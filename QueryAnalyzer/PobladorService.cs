using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Odbc;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QueryAnalyzer
{
    // ── Modelos internos del servicio ─────────────────────────────────────────

    /// <summary>Información de una clave foránea para el proceso de poblado.</summary>
    public class InfoFKPoblado
    {
        public string ColumnaLocal       { get; set; }
        public string TablaReferenciada  { get; set; }
        public string ColumnaReferenciada{ get; set; }
    }

    /// <summary>Información de una columna para el proceso de poblado.</summary>
    public class InfoColumnaPoblado
    {
        public string         Nombre      { get; set; }
        public string         Tipo        { get; set; }
        public int            Longitud    { get; set; }
        public bool           EsNulable   { get; set; }
        public bool           EsPK        { get; set; }
        /// <summary>True si la columna es auto-numérica (IDENTITY / AUTOINCREMENT). No se incluye en INSERT.</summary>
        public bool           EsIdentidad { get; set; }
        /// <summary>Información de FK; null si no es clave foránea.</summary>
        public InfoFKPoblado  FK          { get; set; }
    }

    /// <summary>
    /// Servicio que inserta datos de prueba en tablas de base de datos.
    /// Genera valores coherentes por semántica del nombre de columna y respeta
    /// las relaciones de clave foránea (FK) para mantener la integridad referencial.
    /// Compatible con MS SQL Server, DB2, PostgreSQL y SQLite.
    /// </summary>
    public static class PobladorService
    {
        // ── Datos de ejemplo para generación realista ─────────────────────────
        private static readonly Random _rnd = new Random();

        private static readonly string[] _nombres = {
            "Juan", "María", "Carlos", "Ana", "Luis", "Rosa", "Pedro", "Elena",
            "Miguel", "Laura", "Diego", "Patricia", "Andrés", "Carmen", "Sergio",
            "Lucía", "Roberto", "Isabel", "Alejandro", "Sofía", "Tomás", "Valentina"
        };
        private static readonly string[] _apellidos = {
            "García", "Rodríguez", "López", "Martínez", "González", "Pérez",
            "Sánchez", "Ramírez", "Fernández", "Torres", "Álvarez", "Díaz",
            "Muñoz", "Jiménez", "Moreno", "Ruiz", "Hernández", "Flores", "Núñez"
        };
        private static readonly string[] _dominios = {
            "gmail.com", "hotmail.com", "yahoo.com", "outlook.com", "empresa.com", "correo.cl"
        };
        private static readonly string[] _ciudades = {
            "Santiago", "Buenos Aires", "Lima", "Bogotá", "Ciudad de México",
            "Madrid", "Montevideo", "Asunción", "Quito", "La Paz"
        };
        private static readonly string[] _palabrasDescripcion = {
            "Elemento", "Registro", "Ítem", "Proceso", "Módulo",
            "Componente", "Sección", "Categoría", "Unidad", "Servicio"
        };
        private static readonly string[] _estadosGenerales = {
            "Activo", "Inactivo", "Pendiente", "Procesado", "Cancelado"
        };
        private static readonly string[] _estadosCivil = {
            "Soltero", "Casado", "Divorciado", "Viudo", "Conviviente"
        };

        // ── API pública ───────────────────────────────────────────────────────

        /// <summary>
        /// Inserta <paramref name="cantidadRegistros"/> filas de prueba en la tabla indicada.
        /// </summary>
        /// <param name="conexion">Conexión activa.</param>
        /// <param name="schema">Schema de la tabla (puede ser vacío).</param>
        /// <param name="nombreTabla">Nombre de la tabla destino.</param>
        /// <param name="cantidadRegistros">Cantidad de registros a insertar.</param>
        /// <param name="reportar">Callback opcional para mensajes de progreso.</param>
        /// <param name="token">Token de cancelación.</param>
        /// <returns>Número de filas insertadas efectivamente.</returns>
        public static async Task<int> PoblarTablaAsync(
            Conexion conexion,
            string schema,
            string nombreTabla,
            int cantidadRegistros,
            Action<string> reportar = null,
            CancellationToken token = default(CancellationToken))
        {
            return await Task.Run(() =>
            {
                string connStr = ConexionesManager.GetConnectionString(conexion);
                using (var conn = new OdbcConnection(connStr))
                {
                    conn.Open();

                    // 1. Estructura de la tabla
                    var columnas = ObtenerColumnasParaPoblado(conn, conexion.Motor, schema, nombreTabla);
                    if (columnas.Count == 0)
                    {
                        reportar?.Invoke($"No se pudieron obtener columnas de {nombreTabla}.");
                        return 0;
                    }

                    // 2. Precargar valores disponibles para cada FK
                    var valoresFKDisponibles = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var col in columnas.Where(c => c.FK != null))
                    {
                        string key = col.FK.TablaReferenciada + "." + col.FK.ColumnaReferenciada;
                        if (!valoresFKDisponibles.ContainsKey(key))
                        {
                            var vals = ObtenerValoresExistentes(conn, conexion.Motor, schema,
                                col.FK.TablaReferenciada, col.FK.ColumnaReferenciada);
                            valoresFKDisponibles[key] = vals;
                            if (vals.Count == 0)
                                reportar?.Invoke($"Advertencia: '{col.FK.TablaReferenciada}' referenciada por FK está vacía. Se omitirá FK de '{col.Nombre}'.");
                        }
                    }

                    // 3. Columnas insertables (excluye auto-identidades)
                    var colsInsertables = columnas.Where(c => !c.EsIdentidad).ToList();
                    if (colsInsertables.Count == 0)
                    {
                        reportar?.Invoke($"Tabla '{nombreTabla}' no tiene columnas insertables (todas son auto-numéricas).");
                        return 0;
                    }

                    // 4. Preparar INSERT con placeholders ODBC (?)
                    string t = string.IsNullOrEmpty(schema)
                        ? nombreTabla
                        : schema + "." + nombreTabla;
                    string colNames     = string.Join(", ", colsInsertables.Select(c => c.Nombre));
                    string placeholders = string.Join(", ", colsInsertables.Select(c => "?"));
                    string sqlInsert    = $"INSERT INTO {t} ({colNames}) VALUES ({placeholders})";

                    // 5. Insertar filas
                    int insertados = 0;
                    for (int i = 0; i < cantidadRegistros; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            using (var cmd = new OdbcCommand(sqlInsert, conn))
                            {
                                foreach (var col in colsInsertables)
                                {
                                    object valor;
                                    if (col.FK != null)
                                    {
                                        string key = col.FK.TablaReferenciada + "." + col.FK.ColumnaReferenciada;
                                        if (valoresFKDisponibles.TryGetValue(key, out var disponibles) && disponibles.Count > 0)
                                            valor = disponibles[_rnd.Next(disponibles.Count)];
                                        else
                                            valor = DBNull.Value;
                                    }
                                    else
                                    {
                                        valor = GenerarValorParaColumna(col);
                                    }
                                    cmd.Parameters.Add(new OdbcParameter { Value = valor ?? DBNull.Value });
                                }
                                cmd.ExecuteNonQuery();
                                insertados++;
                            }
                        }
                        catch { /* Saltar fila con error (ej. violación de unique) y continuar */ }
                    }

                    reportar?.Invoke($"Insertados {insertados}/{cantidadRegistros} registros en '{nombreTabla}'.");
                    return insertados;
                }
            }, token);
        }

        // ── Obtención de metadatos ────────────────────────────────────────────

        private static List<InfoColumnaPoblado> ObtenerColumnasParaPoblado(
            OdbcConnection conn, TipoMotor motor, string schema, string tabla)
        {
            var resultado = new List<InfoColumnaPoblado>();
            try
            {
                var schemaTabla = conn.GetSchema("Columns",
                    new string[] { null, string.IsNullOrEmpty(schema) ? null : schema, tabla });

                var pks         = ObtenerPKs(conn, motor, tabla);
                var fks         = ObtenerFKsTabla(conn, motor, schema, tabla);
                var identidades = ObtenerIdentidades(conn, motor, schema, tabla);

                foreach (DataRow col in schemaTabla.Rows)
                {
                    string nombre = col["COLUMN_NAME"].ToString();
                    string tipo   = col["TYPE_NAME"].ToString().ToUpper();

                    int longitud = 255;
                    if (col.Table.Columns.Contains("COLUMN_SIZE") && col["COLUMN_SIZE"] != DBNull.Value)
                        int.TryParse(col["COLUMN_SIZE"].ToString(), out longitud);

                    bool esNulable = true;
                    if (col.Table.Columns.Contains("IS_NULLABLE") && col["IS_NULLABLE"] != DBNull.Value)
                        esNulable = !col["IS_NULLABLE"].ToString().Equals("NO", StringComparison.OrdinalIgnoreCase);

                    resultado.Add(new InfoColumnaPoblado
                    {
                        Nombre      = nombre,
                        Tipo        = tipo,
                        Longitud    = longitud > 0 ? longitud : 255,
                        EsNulable   = esNulable,
                        EsPK        = pks.Contains(nombre),
                        EsIdentidad = identidades.Contains(nombre),
                        FK          = fks.ContainsKey(nombre) ? fks[nombre] : null
                    });
                }
            }
            catch { }
            return resultado;
        }

        private static HashSet<string> ObtenerPKs(OdbcConnection conn, TipoMotor motor, string tabla)
        {
            var pks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string sql = null;
                switch (motor)
                {
                    case TipoMotor.MS_SQL:
                        sql = $"SELECT c.name FROM sys.indexes i " +
                              $"JOIN sys.index_columns ic ON i.object_id=ic.object_id AND i.index_id=ic.index_id " +
                              $"JOIN sys.columns c ON ic.object_id=c.object_id AND ic.column_id=c.column_id " +
                              $"JOIN sys.tables t ON i.object_id=t.object_id " +
                              $"WHERE i.is_primary_key=1 AND t.name='{tabla}'";
                        break;
                    case TipoMotor.POSTGRES:
                        sql = $"SELECT a.attname FROM pg_index ix " +
                              $"JOIN pg_class t ON t.oid=ix.indrelid " +
                              $"JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=ANY(ix.indkey) " +
                              $"WHERE ix.indisprimary=true AND t.relname='{tabla}'";
                        break;
                    case TipoMotor.SQLite:
                        sql = $"PRAGMA table_info('{tabla}')";
                        break;
                    case TipoMotor.DB2:
                        sql = $"SELECT COLNAME FROM SYSCAT.KEYCOLUSE WHERE CONSTNAME IN " +
                              $"(SELECT CONSTNAME FROM SYSCAT.TABCONST WHERE TABNAME=UPPER('{tabla}') AND TYPE='P')";
                        break;
                }
                if (sql == null) return pks;

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    using (var rdr = cmd.ExecuteReader())
                    {
                        while (rdr.Read())
                        {
                            if (motor == TipoMotor.SQLite)
                            {
                                int pkOrd = rdr.GetOrdinal("pk");
                                if (!rdr.IsDBNull(pkOrd) && rdr.GetInt32(pkOrd) > 0)
                                    pks.Add(rdr["name"].ToString());
                            }
                            else
                            {
                                pks.Add(rdr.GetString(0));
                            }
                        }
                    }
                }
            }
            catch { }
            return pks;
        }

        private static Dictionary<string, InfoFKPoblado> ObtenerFKsTabla(
            OdbcConnection conn, TipoMotor motor, string schema, string tabla)
        {
            var fks = new Dictionary<string, InfoFKPoblado>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string sql = null;
                switch (motor)
                {
                    case TipoMotor.MS_SQL:
                        sql = $@"SELECT cu.COLUMN_NAME AS FK_COL, pk.TABLE_NAME AS PK_TABLE, pt.COLUMN_NAME AS PK_COL
                                FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
                                JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS fk ON rc.CONSTRAINT_NAME = fk.CONSTRAINT_NAME
                                JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS pk ON rc.UNIQUE_CONSTRAINT_NAME = pk.CONSTRAINT_NAME
                                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE cu ON rc.CONSTRAINT_NAME = cu.CONSTRAINT_NAME
                                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE pt ON rc.UNIQUE_CONSTRAINT_NAME = pt.CONSTRAINT_NAME
                                    AND cu.ORDINAL_POSITION = pt.ORDINAL_POSITION
                                WHERE fk.TABLE_NAME = '{tabla}'";
                        break;
                    case TipoMotor.POSTGRES:
                        sql = $@"SELECT kcu.column_name AS FK_COL, ccu.table_name AS PK_TABLE, ccu.column_name AS PK_COL
                                FROM information_schema.table_constraints tc
                                JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name
                                JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name
                                WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_name = '{tabla}'";
                        break;
                    case TipoMotor.DB2:
                        sql = $@"SELECT K.COLNAME AS FK_COL, R.REFTABNAME AS PK_TABLE, F.COLNAME AS PK_COL
                                FROM SYSCAT.REFERENCES R
                                JOIN SYSCAT.KEYCOLUSE K ON R.CONSTNAME = K.CONSTNAME AND R.TABSCHEMA = K.TABSCHEMA AND R.TABNAME = K.TABNAME
                                JOIN SYSCAT.KEYCOLUSE F ON R.REFKEYNAME = F.CONSTNAME AND R.REFTABSCHEMA = F.TABSCHEMA AND R.REFTABNAME = F.TABNAME
                                    AND K.COLSEQ = F.COLSEQ
                                WHERE R.TABNAME = UPPER('{tabla}')";
                        break;
                    case TipoMotor.SQLite:
                        sql = $"PRAGMA foreign_key_list('{tabla}')";
                        break;
                }
                if (sql == null) return fks;

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    using (var rdr = cmd.ExecuteReader())
                    {
                        while (rdr.Read())
                        {
                            string fkCol, pkTable, pkCol;
                            if (motor == TipoMotor.SQLite)
                            {
                                fkCol   = rdr["from"].ToString();
                                pkTable = rdr["table"].ToString();
                                pkCol   = rdr["to"].ToString();
                            }
                            else
                            {
                                fkCol   = rdr[0].ToString();
                                pkTable = rdr[1].ToString();
                                pkCol   = rdr[2].ToString();
                            }

                            if (!fks.ContainsKey(fkCol))
                            {
                                fks[fkCol] = new InfoFKPoblado
                                {
                                    ColumnaLocal        = fkCol,
                                    TablaReferenciada   = pkTable,
                                    ColumnaReferenciada = pkCol
                                };
                            }
                        }
                    }
                }
            }
            catch { }
            return fks;
        }

        private static HashSet<string> ObtenerIdentidades(
            OdbcConnection conn, TipoMotor motor, string schema, string tabla)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string sql = null;
                string nombreCompleto = string.IsNullOrEmpty(schema) ? tabla : schema + "." + tabla;
                switch (motor)
                {
                    case TipoMotor.MS_SQL:
                        sql = $"SELECT name FROM sys.columns WHERE object_id=OBJECT_ID('{nombreCompleto}') AND is_identity=1";
                        break;
                    case TipoMotor.POSTGRES:
                        sql = $"SELECT column_name FROM information_schema.columns " +
                              $"WHERE table_name='{tabla}' AND (column_default LIKE 'nextval%' OR is_generated='ALWAYS')";
                        break;
                    case TipoMotor.SQLite:
                        // En SQLite, la clave INTEGER PRIMARY KEY es el rowid (auto)
                        sql = $"PRAGMA table_info('{tabla}')";
                        break;
                    // DB2 no expone fácilmente identidades vía ODBC, se omite
                }
                if (sql == null) return ids;

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    using (var rdr = cmd.ExecuteReader())
                    {
                        while (rdr.Read())
                        {
                            if (motor == TipoMotor.SQLite)
                            {
                                int pkOrd = rdr.GetOrdinal("pk");
                                if (!rdr.IsDBNull(pkOrd) && rdr.GetInt32(pkOrd) == 1)
                                {
                                    string tipoCol = rdr["type"].ToString().ToUpper().Trim();
                                    if (tipoCol == "INTEGER")
                                        ids.Add(rdr["name"].ToString());
                                }
                            }
                            else
                            {
                                ids.Add(rdr.GetString(0));
                            }
                        }
                    }
                }
            }
            catch { }
            return ids;
        }

        /// <summary>
        /// Obtiene hasta 500 valores distintos existentes en una columna de una tabla.
        /// Se usan como fuente de valores FK válidos durante el poblado.
        /// </summary>
        private static List<object> ObtenerValoresExistentes(
            OdbcConnection conn, TipoMotor motor, string schema, string tabla, string columna)
        {
            var valores = new List<object>();
            try
            {
                string t = string.IsNullOrEmpty(schema) ? tabla : schema + "." + tabla;
                string sql;
                switch (motor)
                {
                    case TipoMotor.MS_SQL:
                        sql = $"SELECT TOP 500 DISTINCT {columna} FROM {t} WHERE {columna} IS NOT NULL";
                        break;
                    case TipoMotor.POSTGRES:
                        sql = $"SELECT DISTINCT {columna} FROM {t} WHERE {columna} IS NOT NULL LIMIT 500";
                        break;
                    case TipoMotor.DB2:
                        sql = $"SELECT DISTINCT {columna} FROM {t} WHERE {columna} IS NOT NULL FETCH FIRST 500 ROWS ONLY";
                        break;
                    default: // SQLite
                        sql = $"SELECT DISTINCT {columna} FROM {t} WHERE {columna} IS NOT NULL LIMIT 500";
                        break;
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = sql;
                    using (var rdr = cmd.ExecuteReader())
                    {
                        while (rdr.Read())
                            valores.Add(rdr.IsDBNull(0) ? null : rdr.GetValue(0));
                    }
                }
            }
            catch { }
            return valores;
        }

        // ── Generación de valores ─────────────────────────────────────────────

        /// <summary>
        /// Genera un valor de prueba para la columna indicada, basándose en el tipo de dato
        /// y en la semántica del nombre de la columna para producir datos coherentes.
        /// </summary>
        private static object GenerarValorParaColumna(InfoColumnaPoblado col)
        {
            string tipo   = col.Tipo.ToUpper();
            string nombre = col.Nombre.ToUpper();
            int    maxLen = col.Longitud > 0 ? col.Longitud : 255;

            // ── Enteros ────────────────────────────────────────────────────────
            if (tipo == "INT" || tipo == "INTEGER" || tipo == "SMALLINT" ||
                tipo == "BIGINT" || tipo == "TINYINT" || tipo == "BYTEINT" ||
                tipo.StartsWith("INT ") || tipo.StartsWith("INTEGER "))
            {
                if (col.EsPK) return _rnd.Next(1, 999999);
                return _rnd.Next(1, 10000);
            }

            // ── Decimales / Monetarios ─────────────────────────────────────────
            if (tipo.Contains("DECIMAL") || tipo.Contains("NUMERIC") ||
                tipo.Contains("FLOAT")   || tipo.Contains("DOUBLE")  ||
                tipo.Contains("REAL")    || tipo.Contains("MONEY")   ||
                tipo.Contains("NUMBER"))
            {
                return Math.Round(_rnd.NextDouble() * 9999.0 + 1.0, 2);
            }

            // ── Booleanos ──────────────────────────────────────────────────────
            if (tipo == "BIT" || tipo == "BOOLEAN" || tipo == "BOOL")
                return _rnd.Next(0, 2) == 1;

            // ── Fechas / Hora ──────────────────────────────────────────────────
            if (tipo.Contains("DATE") || tipo.Contains("TIME") || tipo.Contains("TIMESTAMP"))
            {
                var inicio = new DateTime(2015, 1, 1);
                int rangoDias = (DateTime.Now - inicio).Days;
                var fecha = inicio.AddDays(_rnd.Next(0, rangoDias > 0 ? rangoDias : 1));
                return fecha.ToString("yyyy-MM-dd HH:mm:ss");
            }

            // ── Binarios (no insertar aleatorios) ──────────────────────────────
            if (tipo.Contains("BINARY") || tipo.Contains("BLOB") ||
                tipo.Contains("IMAGE")  || tipo == "BYTEA")
                return DBNull.Value;

            // ── Texto: generar por semántica del nombre ────────────────────────
            string texto = GenerarTextoSemantico(nombre, maxLen);
            if (maxLen > 0 && texto.Length > maxLen)
                texto = texto.Substring(0, maxLen);
            return texto;
        }

        /// <summary>
        /// Genera un texto de prueba coherente analizando el nombre de la columna.
        /// Detecta campos comunes como nombre, email, teléfono, RUT, etc.
        /// </summary>
        private static string GenerarTextoSemantico(string nombreCol, int maxLen)
        {
            // Nombre propio
            if (Contiene(nombreCol, "NOMBRE", "FIRST_NAME", "PRIMER_NOMBRE", "GIVENNAME"))
                return _nombres[_rnd.Next(_nombres.Length)];

            // Apellido
            if (Contiene(nombreCol, "APELLIDO", "LAST_NAME", "SURNAME", "SEGUNDO_NOMBRE"))
                return _apellidos[_rnd.Next(_apellidos.Length)];

            // Nombre completo / razón social
            if (Contiene(nombreCol, "FULLNAME", "NOMBRE_COMPLETO", "RAZON_SOCIAL", "RAZON", "EMPRESA"))
                return _nombres[_rnd.Next(_nombres.Length)] + " " + _apellidos[_rnd.Next(_apellidos.Length)];

            // Email
            if (Contiene(nombreCol, "EMAIL", "MAIL", "CORREO", "E_MAIL"))
            {
                string n = _nombres[_rnd.Next(_nombres.Length)].ToLower()
                    .Replace("á","a").Replace("é","e").Replace("í","i").Replace("ó","o").Replace("ú","u");
                return $"{n}{_rnd.Next(10, 999)}@{_dominios[_rnd.Next(_dominios.Length)]}";
            }

            // Teléfono / Celular
            if (Contiene(nombreCol, "TELEFONO", "PHONE", "CEL", "CELULAR", "MOVIL", "TEL_"))
                return $"+56 9 {_rnd.Next(10000000, 99999999)}";

            // RUT / DNI / Cédula
            if (Contiene(nombreCol, "RUT", "DNI", "CEDULA", "DOCUMENTO", "PASAPORTE", "NRODOC"))
                return $"{_rnd.Next(10000000, 25000000)}-{(char)('0' + _rnd.Next(0, 10))}";

            // Código
            if (Contiene(nombreCol, "CODIGO", "CODE", "COD_", "CODG") || nombreCol == "COD" || nombreCol.EndsWith("_COD"))
                return $"COD{_rnd.Next(1000, 9999)}";

            // Descripción / Observación / Detalle
            if (Contiene(nombreCol, "DESC", "OBSERV", "NOTA", "DETALLE", "COMENTARIO", "GLOSA"))
            {
                string d = $"{_palabrasDescripcion[_rnd.Next(_palabrasDescripcion.Length)]} #{_rnd.Next(1, 9999)}";
                return maxLen > 0 && d.Length > maxLen ? d.Substring(0, maxLen) : d;
            }

            // Dirección / Calle
            if (Contiene(nombreCol, "DIRECCION", "ADDRESS", "CALLE", "DOMICILIO"))
                return $"Calle {_apellidos[_rnd.Next(_apellidos.Length)]} #{_rnd.Next(100, 9999)}";

            // Ciudad / Región / País
            if (Contiene(nombreCol, "CIUDAD", "CITY", "REGION", "PAIS", "COUNTRY", "LOCALIDAD", "COMUNA"))
                return _ciudades[_rnd.Next(_ciudades.Length)];

            // Estado civil
            if (Contiene(nombreCol, "ESTADO_CIVIL", "ESTADOCIVIL", "CIVIL"))
                return _estadosCivil[_rnd.Next(_estadosCivil.Length)];

            // Estado / Estatus
            if (Contiene(nombreCol, "ESTADO", "STATUS", "ESTATUS", "SITUACION"))
                return _estadosGenerales[_rnd.Next(_estadosGenerales.Length)];

            // Género / Sexo
            if (nombreCol == "GENERO" || nombreCol == "SEXO" || nombreCol == "GENDER" || nombreCol == "SEX")
                return _rnd.Next(2) == 0 ? "M" : "F";

            // URL / Web
            if (Contiene(nombreCol, "URL", "WEB", "SITIO", "WEBSITE", "LINK"))
                return $"https://www.ejemplo{_rnd.Next(1, 999)}.com";

            // Monto / Precio / Valor
            if (Contiene(nombreCol, "MONTO", "PRECIO", "VALOR", "IMPORTE", "COSTO"))
                return $"{_rnd.Next(1000, 99999)}.00";

            // Porcentaje
            if (Contiene(nombreCol, "PORCENT", "TASA", "RATE", "PERCENT"))
                return $"{_rnd.Next(0, 100)}";

            // Año / Mes
            if (Contiene(nombreCol, "ANIO", "YEAR", "ANO_") || nombreCol == "ANIO" || nombreCol == "YEAR")
                return _rnd.Next(2015, 2026).ToString();

            // Genérico por longitud
            if (maxLen <= 1)  return _rnd.Next(2) == 0 ? "S" : "N";
            if (maxLen <= 5)  return $"V{_rnd.Next(100, 999)}";
            if (maxLen <= 20) return $"Dato {_rnd.Next(1000, 9999)}";

            return $"Valor de prueba #{_rnd.Next(1000, 99999)}";
        }

        /// <summary>Verifica si el nombre contiene alguna de las palabras clave.</summary>
        private static bool Contiene(string nombre, params string[] claves)
        {
            foreach (var clave in claves)
            {
                if (nombre.Contains(clave))
                    return true;
            }
            return false;
        }
    }
}
