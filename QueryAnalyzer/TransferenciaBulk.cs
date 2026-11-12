using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace QueryAnalyzer
{
    /// <summary>
    /// Carga masiva hacia SQL Server con <see cref="SqlBulkCopy"/>: en vez de un INSERT por fila (un viaje de red,
    /// un parseo y un registro de log por fila) las filas viajan en un único flujo TDS de carga masiva.
    /// Solo aplica cuando B es SQL Server; los demás motores siguen con el INSERT fila por fila.
    /// </summary>
    internal static class TransferenciaBulk
    {
        /// <summary>
        /// Las mismas garantías que el INSERT fila por fila: dispara triggers, valida CHECK (si no, SQL Server los
        /// marca como "no confiables"), conserva los NULL explícitos (no aplica el DEFAULT) y bloquea la tabla
        /// (permite log mínimo; la tabla acaba de vaciarse en esta misma transacción).
        /// </summary>
        private const SqlBulkCopyOptions OpcionesBase =
            SqlBulkCopyOptions.TableLock | SqlBulkCopyOptions.CheckConstraints |
            SqlBulkCopyOptions.FireTriggers | SqlBulkCopyOptions.KeepNulls;

        /// <summary>
        /// Abre una conexión SqlClient equivalente a la cadena ODBC de B. Null (con el motivo) si no se puede:
        /// quien llama sigue con el INSERT fila por fila.
        /// </summary>
        public static SqlConnection AbrirConexion(string connStrOdbc, out string motivo)
        {
            string cs = ConexionSqlServer.DesdeOdbc(connStrOdbc, out motivo);
            if (cs == null) return null;

            SqlConnection c = null;
            try
            {
                c = new SqlConnection(cs);
                c.Open();
                return c;
            }
            catch (Exception ex)
            {
                motivo = ex.Message;
                try { c?.Dispose(); } catch { }
                return null;
            }
        }

        /// <summary>
        /// Algunos errores de SqlBulkCopy solo traen el número de columna ("colid 7") y no su nombre: se busca en B.
        /// Si no se puede resolver (p. ej. la conexión quedó inutilizable) se devuelve null.
        /// </summary>
        private static string ColumnaDelError(SqlConnection connB, SqlTransaction tx, string nombreB, Exception ex)
        {
            try
            {
                var m = System.Text.RegularExpressions.Regex.Match(ex.Message ?? "", @"colid (\d+)");
                if (!m.Success) return null;
                using (var cmd = connB.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@t) AND column_id = @n";
                    cmd.Parameters.AddWithValue("@t", nombreB);
                    cmd.Parameters.AddWithValue("@n", int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                    return cmd.ExecuteScalar() as string;
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// Escala (decimales) de cada columna decimal/numeric/money de B, alineada con las columnas del mapa (-1 = no
        /// aplica). SqlBulkCopy TRUNCA al bajar de escala y el INSERT con literal REDONDEA: hay que redondear antes.
        /// </summary>
        private static int[] EscalasDestino(SqlConnection connB, SqlTransaction tx, string nombreB, MapaInsert mapa)
        {
            var porNombre = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var cmd = connB.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT name, scale FROM sys.columns " +
                                      "WHERE object_id = OBJECT_ID(@t) AND system_type_id IN (106, 108, 60, 122)";
                    cmd.Parameters.AddWithValue("@t", nombreB);
                    using (var rd = cmd.ExecuteReader())
                        while (rd.Read())
                            porNombre[rd.GetString(0)] = Convert.ToInt32(rd.GetValue(1), CultureInfo.InvariantCulture);
                }
            }
            catch { return null; }

            var r = new int[mapa.Destino.Length];
            for (int j = 0; j < r.Length; j++)
            {
                int e;
                r[j] = porNombre.TryGetValue(mapa.Destino[j].Nombre, out e) ? e : -1;
            }
            return r;
        }

        /// <summary>Copia una tabla de A a B con SqlBulkCopy leyendo A en streaming. Devuelve las filas enviadas a B.</summary>
        public static long CopiarTabla(
            SqlConnection connB, SqlTransaction tx, string connStrA, TablaTransfer tabla,
            TablaMetadata meta, OpcionesPasado op, RegistroIncidencias inc,
            TransferenciaStreaming.Avance avance, long conteoEsperado, Action<string> onProgress)
        {
            string nombreB = tabla.B.NombreCompleto;
            var reloj = Stopwatch.StartNew();
            long leidas;
            long enviadas;

            long msAbrir, msCarga = 0;
            using (var lector = LectorTabla.Abrir(connStrA, tabla, meta?.ColumnasExcluidas, op.UmbralLobBytes))
            {
                msAbrir = reloj.ElapsedMilliseconds;

                // Motores distintos: solo las columnas que existen en B, con su nombre exacto y su tipo
                MapaInsert mapa = (tabla.Destino != null && meta != null && meta.Columnas.Count > 0)
                    ? MapaInsert.Crear(lector, meta, TipoMotor.MS_SQL) : null;

                string[] nombres = mapa != null ? mapa.Destino.Select(c => c.Nombre).ToArray() : lector.Columnas;
                int[] escalas = mapa != null ? EscalasDestino(connB, tx, nombreB, mapa) : null;

                // Con IDENTITY se envían los valores de A (equivale a SET IDENTITY_INSERT ON del INSERT por fila)
                bool keepIdentity = mapa != null ? mapa.CopiaIdentity
                                                 : (tabla.Destino == null && meta?.TieneIdentity == true);

                onProgress?.Invoke($"[BULK]   {tabla.NombreCompleto}: {nombres.Length} columna(s) a copiar: " +
                                   TransferenciaStreaming.Truncar(string.Join(", ", nombres), 400) +
                                   (keepIdentity ? " (con valores IDENTITY)" : ""));
                avance.Sql("SqlBulkCopy -> " + nombreB);

                using (var fuente = new LectorBulk(lector, mapa, nombres, escalas, tabla.NombreCompleto, op, inc, avance))
                using (var bulk = new SqlBulkCopy(connB, keepIdentity ? OpcionesBase | SqlBulkCopyOptions.KeepIdentity : OpcionesBase, tx))
                {
                    bulk.DestinationTableName = nombreB;
                    // El timeout de SqlBulkCopy abarca la tabla ENTERA (no cada fila como el del INSERT): un valor
                    // corto de Preferencias cortaría tablas grandes que están avanzando bien.
                    bulk.BulkCopyTimeout = 0;
                    // Un solo lote: dentro de la transacción de la transferencia no hay nada que confirmar por lote
                    // y los lotes chicos solo agregan viajes de ida y vuelta.
                    bulk.BatchSize = 0;
                    for (int j = 0; j < nombres.Length; j++)
                        bulk.ColumnMappings.Add(j, nombres[j]);

                    var relojCarga = Stopwatch.StartNew();
                    try { bulk.WriteToServer(fuente); msCarga = relojCarga.ElapsedMilliseconds; }
                    catch (Exception ex)
                    {
                        // Si falló la lectura de A o la conversión de un valor, ese error ya trae su contexto
                        if (fuente.ErrorLectura != null) throw fuente.ErrorLectura;

                        throw new ErrorTransferencia(ex.Message, ex)
                        {
                            Tabla   = tabla.NombreCompleto,
                            Columna = ColumnaDelError(connB, tx, nombreB, ex),
                            Clave   = $"carga masiva, filas leídas de A al fallar: {lector.NumFila}",
                            Sql     = "SqlBulkCopy -> " + nombreB
                        };
                    }

                    enviadas = fuente.Entregadas;
                    if (fuente.OmitidasLob > 0)
                        onProgress?.Invoke($"[BULK]   {tabla.NombreCompleto}: {fuente.OmitidasLob} fila(s) omitidas por binario grande.");
                }

                leidas = lector.NumFila;
            }

            long ms = Math.Max(1, reloj.ElapsedMilliseconds);
            onProgress?.Invoke($"[BULK]   {tabla.NombreCompleto}: leídas de A={leidas} (esperadas={(conteoEsperado >= 0 ? conteoEsperado.ToString() : "?")}), " +
                               $"enviadas a B={enviadas}, {ms} ms ({enviadas * 1000 / ms:N0} filas/s; abrir A {msAbrir} ms, carga {msCarga} ms).");

            if (conteoEsperado > 0 && leidas == 0)
                inc.Agregar("Transferencia", tabla.NombreCompleto, SeveridadIncidencia.Error, "No se leyó ninguna fila de A",
                    $"El conteo previo decía {conteoEsperado} fila(s) pero el SELECT * no devolvió ninguna.",
                    "Revisá permisos de lectura / filtros de seguridad de fila en A, o que el usuario de la conexión A sea el mismo que contó las filas.");
            else if (conteoEsperado >= 0 && leidas != conteoEsperado)
                inc.Agregar("Transferencia", tabla.NombreCompleto, SeveridadIncidencia.Aviso, "Filas leídas distinto del conteo previo",
                    $"Esperadas {conteoEsperado}, leídas {leidas}.",
                    "A cambió durante la transferencia o el driver cortó la lectura: repetir y comparar.");

            return enviadas;
        }
    }

    /// <summary>
    /// Convierte una cadena de conexión ODBC (la que usa la app) en una de SqlClient, que es lo que necesita
    /// SqlBulkCopy. Solo conserva lo que importa (servidor, base, credenciales, cifrado); el resto del ODBC
    /// (driver, DSN, opciones propias) no se traduce.
    /// </summary>
    internal static class ConexionSqlServer
    {
        public static string DesdeOdbc(string odbc, out string motivo)
        {
            motivo = null;
            if (string.IsNullOrWhiteSpace(odbc)) { motivo = "cadena de conexión vacía"; return null; }

            var p = Pares(odbc);
            string servidor = Primero(p, "Server", "Address", "Addr", "Network Address");
            if (string.IsNullOrWhiteSpace(servidor))
            {
                motivo = "la cadena de B no tiene 'Server' (¿usa un DSN?)";
                return null;
            }

            string puerto = Primero(p, "Port");
            if (!string.IsNullOrEmpty(puerto) && servidor.IndexOf(',') < 0 && servidor.IndexOf('\\') < 0)
                servidor += "," + puerto;

            var b = new SqlConnectionStringBuilder
            {
                DataSource      = servidor,
                ApplicationName = "QueryAnalyzer",
                Pooling         = false,   // conexión de una sola corrida: al cerrarla se libera de verdad
                PacketSize      = 32767    // paquetes TDS grandes: más rendimiento en cargas masivas
            };

            string bd = Primero(p, "Database", "DBName");
            if (!string.IsNullOrWhiteSpace(bd)) b.InitialCatalog = bd;

            if (EsVerdadero(Primero(p, "Trusted_Connection", "Trusted Connection", "Integrated Security")))
                b.IntegratedSecurity = true;
            else
            {
                string usuario = Primero(p, "Uid", "User Id", "User");
                string clave   = Primero(p, "Pwd", "Password");
                if (!string.IsNullOrEmpty(usuario)) b.UserID = usuario;
                if (clave != null) b.Password = clave;
            }

            string tsc = Primero(p, "TrustServerCertificate");
            if (tsc != null) b.TrustServerCertificate = EsVerdadero(tsc);
            string enc = Primero(p, "Encrypt");
            if (enc != null) b.Encrypt = EsVerdadero(enc);

            return b.ConnectionString;
        }

        private static bool EsVerdadero(string v)
        {
            if (v == null) return false;
            v = v.Trim().ToLowerInvariant();
            return v == "yes" || v == "true" || v == "1" || v == "sspi";
        }

        private static string Primero(Dictionary<string, string> p, params string[] claves)
        {
            foreach (var c in claves)
            {
                string v;
                if (p.TryGetValue(c, out v)) return v;
            }
            return null;
        }

        /// <summary>Pares clave=valor de una cadena ODBC; los valores pueden ir entre llaves ({a;b}).</summary>
        private static Dictionary<string, string> Pares(string cs)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0, n = cs.Length;
            while (i < n)
            {
                int eq = cs.IndexOf('=', i);
                if (eq < 0) break;
                int pc = cs.IndexOf(';', i);
                if (pc >= 0 && pc < eq) { i = pc + 1; continue; }   // fragmento sin '='

                string clave = cs.Substring(i, eq - i).Trim();
                i = eq + 1;
                while (i < n && cs[i] == ' ') i++;

                string valor;
                if (i < n && cs[i] == '{')
                {
                    var sb = new StringBuilder();
                    int j = i + 1;
                    while (j < n)
                    {
                        if (cs[j] == '}')
                        {
                            if (j + 1 < n && cs[j + 1] == '}') { sb.Append('}'); j += 2; continue; }
                            break;
                        }
                        sb.Append(cs[j]);
                        j++;
                    }
                    valor = sb.ToString();
                    int sig = cs.IndexOf(';', Math.Min(j, n));
                    i = sig < 0 ? n : sig + 1;
                }
                else
                {
                    int fin = cs.IndexOf(';', i);
                    if (fin < 0) fin = n;
                    valor = cs.Substring(i, fin - i).Trim();
                    i = fin + 1;
                }

                if (clave.Length > 0) d[clave] = valor;
            }
            return d;
        }
    }

    /// <summary>
    /// Convierte un valor leído de A al tipo CLR que debe recibir la columna de B (SQL Server). Es el equivalente
    /// tipado de <c>ScriptHelper.EscaparValorSql</c>: mismas reglas, pero sin pasar por texto SQL.
    /// </summary>
    internal static class ConversorBulk
    {
        public static object Convertir(object valor, ColumnaDestino col, string tipoA, int escala = -1)
        {
            if (valor == null || valor is DBNull) return DBNull.Value;
            if (col == null) return valor;

            Type t = valor.GetType();
            CategoriaTipo cat = col.Categoria;

            if (t == typeof(bool))
            {
                bool b = (bool)valor;
                if (cat == CategoriaTipo.Bool) return b;
                if (ScriptHelper.EsTexto(cat)) return b ? "1" : "0";
                return b ? 1 : 0;
            }

            if (ScriptHelper.EsNumericoClr(t))
            {
                if (t == typeof(double) && (double.IsNaN((double)valor) || double.IsInfinity((double)valor)) ||
                    t == typeof(float)  && (float.IsNaN((float)valor)   || float.IsInfinity((float)valor)))
                    throw new InvalidOperationException("El valor " + Convert.ToString(valor, CultureInfo.InvariantCulture) +
                        " (no finito) no se puede escribir en SQL Server.");

                if (cat == CategoriaTipo.Bool) return Convert.ToDecimal(valor, CultureInfo.InvariantCulture) != 0m;
                if (ScriptHelper.EsTexto(cat))
                    return t == typeof(double) ? ((double)valor).ToString("R", CultureInfo.InvariantCulture)
                         : t == typeof(float)  ? ((float)valor).ToString("R", CultureInfo.InvariantCulture)
                         : Convert.ToString(valor, CultureInfo.InvariantCulture);

                // Con decimales hacia una columna entera, SQL Server trunca (SqlBulkCopy redondearía)
                bool fraccionario = t == typeof(decimal) || t == typeof(double) || t == typeof(float);
                if (cat == CategoriaTipo.Entero && fraccionario)
                    return decimal.Truncate(Convert.ToDecimal(valor, CultureInfo.InvariantCulture));
                // Hacia una columna decimal con menos decimales, SQL Server redondea (SqlBulkCopy truncaría)
                if (cat == CategoriaTipo.Decimal && fraccionario && escala >= 0)
                    return Redondear(Convert.ToDecimal(valor, CultureInfo.InvariantCulture), escala);
                return valor;
            }

            if (t == typeof(DateTime)) return DeFecha((DateTime)valor, col);

            if (t == typeof(DateTimeOffset))
            {
                var o = (DateTimeOffset)valor;
                return cat == CategoriaTipo.FechaHoraTz ? (object)o : DeFecha(o.DateTime, col);
            }

            if (t == typeof(TimeSpan))
                return cat == CategoriaTipo.Hora ? valor : ((TimeSpan)valor).ToString();

            if (t == typeof(byte[])) return valor;

            if (t == typeof(Guid))
                return cat == CategoriaTipo.Guid ? valor : ((Guid)valor).ToString();

            string s = valor.ToString();

            switch (cat)
            {
                case CategoriaTipo.Bool:
                {
                    bool? bb = ScriptHelper.InterpretarBool(s);
                    return bb.HasValue ? (object)bb.Value : s;
                }

                case CategoriaTipo.Texto:
                case CategoriaTipo.TextoUnicode:
                    // CHAR(n) rellena con espacios: al pasar a una columna de largo variable esos espacios sobran
                    if (ScriptHelper.TipoOrigenEsChar(tipoA) && !ScriptHelper.ColumnaEsChar(col)) s = s.TrimEnd(' ');
                    return s;

                case CategoriaTipo.Entero:
                {
                    // Solo si no pierde decimales: un "12.5" en una columna entera tiene que fallar, no redondearse
                    decimal d;
                    if (decimal.TryParse(s.Trim(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out d)
                        && d == decimal.Truncate(d)) return d;
                    return s;
                }

                case CategoriaTipo.Decimal:
                {
                    decimal d;
                    if (!decimal.TryParse(s.Trim(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out d))
                        return s;
                    return escala >= 0 ? Redondear(d, escala) : d;
                }

                case CategoriaTipo.Real:
                {
                    double d;
                    return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? (object)d : s;
                }

                case CategoriaTipo.Fecha:
                case CategoriaTipo.FechaHora:
                {
                    DateTime dt;
                    return DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)
                        ? DeFecha(dt, col) : (object)s;
                }

                case CategoriaTipo.FechaHoraTz:
                {
                    DateTimeOffset o;
                    return DateTimeOffset.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out o)
                        ? (object)o : s;
                }

                case CategoriaTipo.Hora:
                {
                    TimeSpan ts;
                    if (TimeSpan.TryParse(s.Trim(), CultureInfo.InvariantCulture, out ts)) return ts;
                    DateTime dt;
                    return DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)
                        ? (object)dt.TimeOfDay : s;
                }

                case CategoriaTipo.Guid:
                {
                    Guid g;
                    return Guid.TryParse(s.Trim(), out g) ? (object)g : s;
                }

                default:
                    return s;
            }
        }

        private static decimal Redondear(decimal d, int escala)
        {
            return Math.Round(d, Math.Min(escala, 28), MidpointRounding.AwayFromZero);
        }

        /// <summary>Fecha/hora al tipo y la precisión de la columna de B (mismas reglas que el literal SQL).</summary>
        private static object DeFecha(DateTime d, ColumnaDestino col)
        {
            switch (col.Categoria)
            {
                case CategoriaTipo.Fecha: return d.Date;
                case CategoriaTipo.Hora:  return d.TimeOfDay;

                case CategoriaTipo.Texto:
                case CategoriaTipo.TextoUnicode:
                    return d.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

                case CategoriaTipo.FechaHora:
                case CategoriaTipo.FechaHoraTz:
                {
                    // datetime = milisegundos, smalldatetime = segundos, datetime2 = 100 ns
                    int fracciones = ScriptHelper.FraccionesDeSegundo(TipoMotor.MS_SQL, col);
                    long paso = 1;
                    for (int k = fracciones; k < 7; k++) paso *= 10;
                    var r = new DateTime(d.Ticks - d.Ticks % paso, DateTimeKind.Unspecified);
                    // Sin zona en el origen: se guarda tal cual, con offset +00:00 (como el literal sin zona)
                    return col.Categoria == CategoriaTipo.FechaHoraTz ? (object)new DateTimeOffset(r, TimeSpan.Zero) : r;
                }

                default: return d;
            }
        }
    }

    /// <summary>
    /// Fuente de filas para SqlBulkCopy: lee A en streaming (una fila en memoria), aplica la conversión de tipos
    /// de B y respeta el umbral de binarios grandes. SqlBulkCopy la consume con Read()/GetValue().
    /// </summary>
    internal sealed class LectorBulk : IDataReader
    {
        private readonly LectorTabla _lector;
        private readonly MapaInsert _mapa;          // null = se copian las columnas tal como salen de A
        private readonly string[] _nombres;         // nombre de cada columna en B
        private readonly int[] _escalas;            // escala de cada columna decimal de B (null = desconocida)
        private readonly string _tabla;
        private readonly OpcionesPasado _op;
        private readonly RegistroIncidencias _inc;
        private readonly TransferenciaStreaming.Avance _avance;
        private readonly object[] _crudo;           // fila tal como sale de A
        private readonly object[] _fila;            // fila a escribir en B
        private bool _cerrado;

        /// <summary>Filas entregadas a SqlBulkCopy.</summary>
        public long Entregadas { get; private set; }

        /// <summary>Filas omitidas por tener un binario sobre el umbral.</summary>
        public long OmitidasLob { get; private set; }

        /// <summary>Error de lectura/conversión ocurrido dentro de Read() (SqlBulkCopy puede envolverlo).</summary>
        public Exception ErrorLectura { get; private set; }

        public LectorBulk(LectorTabla lector, MapaInsert mapa, string[] nombres, int[] escalas, string tabla,
                          OpcionesPasado op, RegistroIncidencias inc, TransferenciaStreaming.Avance avance)
        {
            _lector = lector; _mapa = mapa; _nombres = nombres; _escalas = escalas; _tabla = tabla;
            _op = op; _inc = inc; _avance = avance;
            _crudo = new object[lector.Columnas.Length];
            _fila  = mapa != null ? new object[nombres.Length] : _crudo;
        }

        public bool Read()
        {
            while (true)
            {
                bool hay;
                try { hay = _lector.LeerFila(_crudo); }
                catch (LobExcedeUmbralException lx)
                {
                    if (!_op.OmitirFilasLobGrandes) { ErrorLectura = lx; throw; }

                    _inc.Agregar("Transferencia", _tabla, SeveridadIncidencia.Error,
                        "Fila omitida: binario sobre el umbral",
                        $"El valor supera {TransferenciaStreaming.Mb(_op.UmbralLobBytes)} y la fila no se copió a B.",
                        "La tabla quedó incompleta en B. Transferí esa fila por otro medio.",
                        lx.Columna, lx.Clave);
                    _avance.Sumar(1);
                    OmitidasLob++;
                    continue;
                }
                catch (Exception ex) { ErrorLectura = ex; throw; }

                if (!hay) return false;

                if (_mapa != null)
                {
                    try
                    {
                        for (int j = 0; j < _fila.Length; j++)
                            _fila[j] = ConversorBulk.Convertir(_crudo[_mapa.Indices[j]], _mapa.Destino[j], _mapa.TiposA[j],
                                _escalas != null ? _escalas[j] : -1);
                    }
                    catch (Exception ex)
                    {
                        ErrorLectura = new ErrorTransferencia(ex.Message, ex)
                        {
                            Tabla = _tabla,
                            Clave = TransferenciaStreaming.ClaveFila(_lector.Columnas, _crudo, _crudo.Length, _lector.NumFila),
                            Sql   = "SqlBulkCopy"
                        };
                        throw ErrorLectura;
                    }
                }

                Entregadas++;
                _avance.Sumar(1);
                return true;
            }
        }

        // ── IDataRecord ──────────────────────────────────────────────────────

        public int FieldCount { get { return _nombres.Length; } }
        public string GetName(int i) { return _nombres[i]; }
        public object GetValue(int i) { return _fila[i]; }
        public bool IsDBNull(int i) { return _fila[i] == null || _fila[i] is DBNull; }
        public Type GetFieldType(int i) { return typeof(object); }
        public string GetDataTypeName(int i) { return "object"; }
        public object this[int i] { get { return _fila[i]; } }
        public object this[string name] { get { return _fila[GetOrdinal(name)]; } }

        public int GetOrdinal(string name)
        {
            for (int i = 0; i < _nombres.Length; i++)
                if (string.Equals(_nombres[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            throw new IndexOutOfRangeException(name);
        }

        public int GetValues(object[] values)
        {
            int n = Math.Min(values.Length, _fila.Length);
            Array.Copy(_fila, values, n);
            return n;
        }

        public bool     GetBoolean(int i)  { return Convert.ToBoolean(_fila[i], CultureInfo.InvariantCulture); }
        public byte     GetByte(int i)     { return Convert.ToByte(_fila[i], CultureInfo.InvariantCulture); }
        public char     GetChar(int i)     { return Convert.ToChar(_fila[i], CultureInfo.InvariantCulture); }
        public DateTime GetDateTime(int i) { return Convert.ToDateTime(_fila[i], CultureInfo.InvariantCulture); }
        public decimal  GetDecimal(int i)  { return Convert.ToDecimal(_fila[i], CultureInfo.InvariantCulture); }
        public double   GetDouble(int i)   { return Convert.ToDouble(_fila[i], CultureInfo.InvariantCulture); }
        public float    GetFloat(int i)    { return Convert.ToSingle(_fila[i], CultureInfo.InvariantCulture); }
        public short    GetInt16(int i)    { return Convert.ToInt16(_fila[i], CultureInfo.InvariantCulture); }
        public int      GetInt32(int i)    { return Convert.ToInt32(_fila[i], CultureInfo.InvariantCulture); }
        public long     GetInt64(int i)    { return Convert.ToInt64(_fila[i], CultureInfo.InvariantCulture); }
        public string   GetString(int i)   { return Convert.ToString(_fila[i], CultureInfo.InvariantCulture); }
        public Guid     GetGuid(int i)     { return _fila[i] is Guid ? (Guid)_fila[i] : Guid.Parse(GetString(i)); }

        public long GetBytes(int i, long fieldOffset, byte[] buffer, int bufferoffset, int length)
        { throw new NotSupportedException(); }
        public long GetChars(int i, long fieldoffset, char[] buffer, int bufferoffset, int length)
        { throw new NotSupportedException(); }
        public IDataReader GetData(int i) { throw new NotSupportedException(); }

        // ── IDataReader ──────────────────────────────────────────────────────

        public int Depth { get { return 0; } }
        public bool IsClosed { get { return _cerrado; } }
        public int RecordsAffected { get { return -1; } }
        public bool NextResult() { return false; }
        public void Close() { _cerrado = true; }
        public void Dispose() { _cerrado = true; }

        public DataTable GetSchemaTable()
        {
            var t = new DataTable("Esquema");
            t.Columns.Add("ColumnName", typeof(string));
            t.Columns.Add("ColumnOrdinal", typeof(int));
            t.Columns.Add("DataType", typeof(Type));
            t.Columns.Add("AllowDBNull", typeof(bool));
            for (int i = 0; i < _nombres.Length; i++)
                t.Rows.Add(_nombres[i], i, typeof(object), true);
            return t;
        }
    }
}
