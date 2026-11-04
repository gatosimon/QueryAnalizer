using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Odbc;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CapiDL;

namespace QueryAnalyzer
{
    /// <summary>
    /// Un valor binario/LOB de una fila supera el umbral configurado. No se lee completo:
    /// quien llama decide si aborta o si omite la fila (siempre dejando incidencia).
    /// </summary>
    internal sealed class LobExcedeUmbralException : Exception
    {
        public string Columna { get; }
        public long   Bytes   { get; }
        public string Clave   { get; }

        public LobExcedeUmbralException(string columna, long bytes, string clave, long umbral)
            : base($"El valor de la columna {columna} supera el umbral de {TransferenciaStreaming.Mb(umbral)}")
        {
            Columna = columna; Bytes = bytes; Clave = clave;
        }
    }

    /// <summary>Falla al escribir en B, con el contexto de la fila para poder mostrarla.</summary>
    internal sealed class ErrorTransferencia : Exception
    {
        public string Tabla   { get; set; }
        public string Clave   { get; set; }
        public string Sql     { get; set; }

        public ErrorTransferencia(string mensaje, Exception inner) : base(mensaje, inner) { }
    }

    /// <summary>
    /// Lector de una tabla en modo "solo hacia adelante": mantiene una única fila en memoria.
    /// Reemplaza al <c>DataAdapter.Fill(DataTable)</c> que cargaba la tabla completa (y de ahí el
    /// OutOfMemoryException con tablas grandes en un proceso de 32 bits).
    /// </summary>
    internal sealed class LectorTabla : IDisposable
    {
        private readonly DataBase       _db;
        private readonly OdbcCommand    _cmd;
        private readonly OdbcDataReader _rd;
        private readonly int[]          _indices;
        private readonly bool[]         _esBinaria;
        private readonly long           _umbral;

        public string[] Columnas { get; }
        public string   Tabla    { get; }

        /// <summary>Filas leídas del cursor (incluye las omitidas por LOB sobre el umbral).</summary>
        public long NumFila { get; private set; }

        private LectorTabla(DataBase db, OdbcCommand cmd, OdbcDataReader rd, string tabla,
                            HashSet<string> excluidas, long umbral)
        {
            _db = db; _cmd = cmd; _rd = rd; _umbral = umbral; Tabla = tabla;

            var idx = new List<int>();
            var nom = new List<string>();
            var bin = new List<bool>();
            for (int i = 0; i < rd.FieldCount; i++)
            {
                string n = rd.GetName(i);
                if (excluidas != null && excluidas.Contains(n)) continue;
                idx.Add(i);
                nom.Add(n);
                // Algunos drivers (p. ej. SQLite) informan los BLOB como texto: el nombre del tipo lo delata
                bin.Add(rd.GetFieldType(i) == typeof(byte[])
                        || string.Equals(rd.GetDataTypeName(i), "BLOB", StringComparison.OrdinalIgnoreCase));
            }
            _indices = idx.ToArray();
            Columnas = nom.ToArray();
            _esBinaria = bin.ToArray();
        }

        public static LectorTabla Abrir(string connStr, TablaTransfer tabla, HashSet<string> excluidas, long umbralLob)
        {
            var db = new DataBase(connStr);
            try
            {
                var cmd = new OdbcCommand("SELECT * FROM " + tabla.NombreCompleto, db.Connection);
                cmd.CommandTimeout = TransferenciaStreaming.TimeoutSegundos();
                var rd = cmd.ExecuteReader();
                return new LectorTabla(db, cmd, rd, tabla.NombreCompleto, excluidas, umbralLob);
            }
            catch
            {
                try { db.CloseConnection(); } catch { }
                throw;
            }
        }

        /// <summary>Lee la siguiente fila en <paramref name="valores"/>. Devuelve false al terminar.</summary>
        public bool LeerFila(object[] valores)
        {
            if (!_rd.Read()) return false;
            NumFila++;

            for (int k = 0; k < _indices.Length; k++)
            {
                int i = _indices[k];
                if (_esBinaria[k])
                {
                    byte[] b = LeerBinario(i, k, valores);
                    valores[k] = b == null ? (object)DBNull.Value : b;
                }
                else
                {
                    valores[k] = _rd.IsDBNull(i) ? DBNull.Value : _rd.GetValue(i);
                }
            }
            return true;
        }

        private byte[] LeerBinario(int col, int k, object[] valores)
        {
            // IMPORTANTE: no llamar IsDBNull antes. System.Data.Odbc, para saber si es NULL, lee el valor
            // completo y lo deja en caché (un BLOB de cientos de MB entero en memoria) y además GetBytes
            // después falla al convertir ese valor cacheado. Se lee directo por trozos.
            var primero = new byte[16384];
            long n;
            try { n = _rd.GetBytes(col, 0, primero, 0, primero.Length); }
            catch (InvalidCastException)
            {
                // Un NULL hace fallar GetBytes. Si no es NULL el driver informa el tipo mal: se lee el valor tal cual.
                if (_rd.IsDBNull(col)) return null;
                return ConvertirABinario(_rd.GetValue(col));
            }

            if (n <= 0)
            {
                bool esNulo = false;
                try { esNulo = _rd.IsDBNull(col); } catch { }
                return esNulo ? null : new byte[0];
            }

            if (n < primero.Length)
            {
                // Puede haber más: se sigue hasta que el driver devuelva 0
                long m0 = _rd.GetBytes(col, n, primero, (int)n, primero.Length - (int)n);
                if (m0 <= 0)
                {
                    var chico = new byte[n];
                    Buffer.BlockCopy(primero, 0, chico, 0, (int)n);
                    return chico;
                }
                n += m0;
            }

            var trozos = new List<KeyValuePair<byte[], int>>();
            trozos.Add(new KeyValuePair<byte[], int>(primero, (int)n));
            long total = n;

            while (true)
            {
                if (total > _umbral)
                    throw new LobExcedeUmbralException(NombreColumna(k), total,
                        TransferenciaStreaming.ClaveFila(Columnas, valores, k, NumFila), _umbral);

                var trozo = new byte[1 << 20];
                long m = _rd.GetBytes(col, total, trozo, 0, trozo.Length);
                if (m <= 0) break;
                trozos.Add(new KeyValuePair<byte[], int>(trozo, (int)m));
                total += m;
            }

            var resultado = new byte[total];
            long pos = 0;
            foreach (var t in trozos)
            {
                Buffer.BlockCopy(t.Key, 0, resultado, (int)pos, t.Value);
                pos += t.Value;
            }
            return resultado;
        }

        private string NombreColumna(int k) { return Columnas[k]; }

        /// <summary>Respaldo para drivers que entregan el binario como texto "X'HEX'".</summary>
        private static byte[] ConvertirABinario(object valor)
        {
            var b = valor as byte[];
            if (b != null) return b;

            string s = Convert.ToString(valor, CultureInfo.InvariantCulture) ?? "";
            if (s.Length >= 3 && (s[0] == 'X' || s[0] == 'x') && s[1] == '\'' && s[s.Length - 1] == '\''
                && (s.Length - 3) % 2 == 0)
            {
                var r = new byte[(s.Length - 3) / 2];
                for (int i = 0; i < r.Length; i++)
                    r[i] = Convert.ToByte(s.Substring(2 + i * 2, 2), 16);
                return r;
            }
            return Encoding.UTF8.GetBytes(s);
        }

        public void Dispose()
        {
            try { _rd.Close(); } catch { }
            try { _cmd.Dispose(); } catch { }
            try { _db.CloseConnection(); } catch { }
        }
    }

    internal static class TransferenciaStreaming
    {
        /// <summary>Tope de bytes para escribir un binario como literal en la sentencia (más grande: parámetro).</summary>
        public const int LimiteLiteralBinario = 4000;

        /// <summary>Por encima de esto el parámetro va como LONGVARBINARY (BLOB) en vez de VARBINARY.</summary>
        public const int LimiteVarBinario = 32000;

        /// <summary>Aproximado: DB2 no acepta literales hexadecimales mayores (verificar en la versión instalada).</summary>
        public const int LimiteLiteralDb2 = 16000;

        public static int TimeoutSegundos()
        {
            try { return Math.Max(0, ConfigManager.ObtenerConfiguracion().TimeoutConsultaSegundos); }
            catch { return 0; }
        }

        public static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }

        public static string Truncar(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        /// <summary>Texto corto para identificar la fila: nº de fila + los primeros valores no binarios ya leídos.</summary>
        public static string ClaveFila(string[] columnas, object[] valores, int hasta, long numFila)
        {
            var sb = new StringBuilder();
            sb.Append("fila ").Append(numFila.ToString(CultureInfo.InvariantCulture));
            int usados = 0;
            for (int k = 0; k < hasta && k < valores.Length && usados < 2; k++)
            {
                object v = valores[k];
                if (v == null || v is DBNull || v is byte[]) continue;
                string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim();
                sb.Append(usados == 0 ? ": " : ", ").Append(columnas[k]).Append('=').Append(Truncar(s, 40));
                usados++;
            }
            return sb.ToString();
        }

        /// <summary>Oculta la contraseña de un connection string antes de escribirlo en un archivo.</summary>
        public static string EnmascararConexion(string connStr)
        {
            if (string.IsNullOrEmpty(connStr)) return connStr;
            return System.Text.RegularExpressions.Regex.Replace(
                connStr, @"(?i)(pwd|password)\s*=\s*[^;]*", "$1=***");
        }

        /// <summary>Acción sugerida según el texto del error del motor.</summary>
        public static string Sugerir(Exception ex)
        {
            if (ex is OutOfMemoryException)
                return "Memoria insuficiente: excluí las tablas más grandes de esta corrida y transferilas por separado, o cerrá otras aplicaciones.";

            string m = (ex?.Message ?? "").ToUpperInvariant();
            if (m.Contains("SQL0964") || m.Contains("LOG FULL") || m.Contains("TRANSACTION LOG"))
                return "El log de transacciones del motor se llenó. Pedir que se amplíe el log o transferir menos tablas por corrida.";
            if (m.Contains("SESSION_REPLICATION_ROLE") || (m.Contains("42501") && m.Contains("PARAMETER")))
                return "PostgreSQL solo deja desactivar las FK de la sesión (session_replication_role) a un superusuario: " +
                       "usá una conexión con ese rol o incluí en la selección todas las tablas relacionadas. B no fue modificada.";
            if (m.Contains("REFERENCE CONSTRAINT") || m.Contains("SQL0532") || m.Contains("DELETE STATEMENT CONFLICTED"))
                return "Una tabla fuera de la selección referencia a la que se está vaciando: agregala a la lista, o revisá que el " +
                       "usuario tenga permiso ALTER para que se pueda desactivar esa FK durante la carga.";
            if (m.Contains("SQL0668") || m.Contains("CHECK PENDING") || m.Contains("SQL3600"))
                return "Una tabla de DB2 quedó en estado 'check pending' (por un SET INTEGRITY previo): ejecutá " +
                       "SET INTEGRITY FOR <tabla> IMMEDIATE CHECKED y repetí.";
            if (m.Contains("ALTER FOREIGN KEY") || (m.Contains("SQL0104") && m.Contains("ENFORCED")))
                return "Esta versión de DB2 no admite ALTER FOREIGN KEY ... NOT ENFORCED: desactivá las FK a mano antes de transferir.";
            if (m.Contains("SQL0803") || m.Contains("DUPLICATE") || m.Contains("UNIQUE") || m.Contains("23505"))
                return "Clave duplicada: revisá si la tabla de B tiene constraints/índices únicos distintos a A.";
            if (m.Contains("SQL0530") || m.Contains("FOREIGN KEY") || m.Contains("23503"))
                return "Violación de FK: la tabla referencia filas que no existen. Revisá el orden o las tablas relacionadas que quedaron fuera.";
            if (m.Contains("SQL0407") || m.Contains("NOT NULL") || m.Contains("23502"))
                return "Valor nulo en columna NOT NULL de B: la estructura de B difiere de la de A.";
            if (m.Contains("SQL0302") || m.Contains("TRUNCAT") || m.Contains("22001") || m.Contains("TOO LONG"))
                return "El valor no entra en la columna de B: la estructura de B difiere de la de A.";
            if (m.Contains("SQL0204") || m.Contains("SQL0206") || m.Contains("DOES NOT EXIST") || m.Contains("NO SUCH")
                || m.Contains("HAS NO COLUMN") || m.Contains("42S02") || m.Contains("42S22") || m.Contains("42703") || m.Contains("42704"))
                return "La tabla o columna no existe en ese lado: B no tiene la misma estructura que A.";
            if (m.Contains("SQL0551") || m.Contains("PERMISSION") || m.Contains("AUTHORIZ") || m.Contains("42501"))
                return "Falta de permisos del usuario de la conexión sobre esa tabla.";
            if (m.Contains("TIMEOUT") || m.Contains("HYT00"))
                return "Se agotó el tiempo: subir el timeout en Preferencias (0 = sin límite).";
            return "Revisá el mensaje del motor. B no fue modificada si la operación era transaccional.";
        }

        // ── Reporte con límite de frecuencia: evita inundar el Dispatcher de la UI ──────

        public sealed class Avance
        {
            private long _actual;
            private readonly long _total;
            private readonly Action<int, int> _onStep;
            private readonly Action<string> _onSql;
            private readonly Stopwatch _swStep = Stopwatch.StartNew();
            private readonly Stopwatch _swSql  = Stopwatch.StartNew();

            public Avance(long total, Action<int, int> onStep, Action<string> onSql)
            {
                _total = total; _onStep = onStep; _onSql = onSql;
            }

            public void Sumar(long n)
            {
                _actual += n;
                if (_onStep != null && _swStep.ElapsedMilliseconds >= 200)
                {
                    _swStep.Restart();
                    Emitir();
                }
            }

            public void Forzar() { if (_onStep != null) Emitir(); }

            public void Sql(string sql)
            {
                if (_onSql == null || _swSql.ElapsedMilliseconds < 250) return;
                _swSql.Restart();
                _onSql(Truncar(sql, 300));
            }

            private void Emitir()
            {
                int total = (int)Math.Min(int.MaxValue, Math.Max(_total, _actual));
                _onStep((int)Math.Min(int.MaxValue, _actual), total);
            }
        }
    }
}
