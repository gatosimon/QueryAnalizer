using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using CapiDL;

namespace QueryAnalyzer
{
    public static class ComparadorService
    {
        // ── Bases de datos disponibles ────────────────────────────────────────────

        public static List<string> GetDatabases(string connStr, TipoMotor motor)
        {
            var lista = new List<string>();
            try
            {
                var DB = new DataBase(connStr);
                string sql = null;
                switch (motor)
                {
                    case TipoMotor.MS_SQL:
                        sql = "SELECT name FROM sys.databases WHERE state = 0 ORDER BY name";
                        break;
                    case TipoMotor.POSTGRES:
                        sql = "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname";
                        break;
                    case TipoMotor.DB2:
                        sql = "SELECT DISTINCT CURRENT SERVER FROM SYSIBM.SYSDUMMY1";
                        break;
                    case TipoMotor.SQLite:
                        lista.Add("(base actual)");
                        DB.CloseConnection();
                        return lista;
                }
                if (sql != null)
                {
                    DB.CommandText = sql;
                    while (DB.Read()) lista.Add(DB.Reader[0].ToString().Trim());
                    DB.CloseConnection();
                }
            }
            catch { }
            return lista;
        }

        // ── Esquemas disponibles ──────────────────────────────────────────────────

        public static List<string> GetSchemas(string connStr, TipoMotor motor)
        {
            var lista = new List<string>();
            try
            {
                var DB = new DataBase(connStr);
                string sql = null;
                switch (motor)
                {
                    case TipoMotor.MS_SQL:
                        sql = "SELECT SCHEMA_NAME FROM INFORMATION_SCHEMA.SCHEMATA ORDER BY SCHEMA_NAME";
                        break;
                    case TipoMotor.POSTGRES:
                        sql = "SELECT schema_name FROM information_schema.schemata WHERE schema_name NOT LIKE 'pg_%' AND schema_name <> 'information_schema' ORDER BY schema_name";
                        break;
                    case TipoMotor.DB2:
                        sql = "SELECT DISTINCT TABSCHEMA FROM SYSCAT.TABLES ORDER BY TABSCHEMA";
                        break;
                    case TipoMotor.SQLite:
                        lista.Add("main");
                        return lista;
                }
                if (sql != null)
                {
                    DB.CommandText = sql;
                    while (DB.Read()) lista.Add(DB.Reader[0].ToString().Trim());
                    DB.CloseConnection();
                }
            }
            catch { }
            return lista;
        }

        // ── Tablas con columnas ───────────────────────────────────────────────────

        public static List<TablaComp> GetTablas(string connStr, TipoMotor motor, string[] schemas)
        {
            var lista = new List<TablaComp>();
            try
            {
                var DB = new DataBase(connStr);
                DataTable dt = DB.GetSchema("TABLEs");
                DB.CloseConnection();

                var filas = dt.AsEnumerable()
                    .Where(r =>
                    {
                        string tipo = r["TABLE_TYPE"].ToString().Trim().ToUpper();
                        return tipo == "TABLE";
                    })
                    .ToList();

                foreach (var fila in filas)
                {
                    string schema = fila["TABLE_SCHEM"].ToString().Trim();
                    string nombre = fila["TABLE_NAME"].ToString().Trim();

                    if (schemas != null && schemas.Length > 0 &&
                        !schemas.Any(s => s.Equals(schema, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var tabla = new TablaComp { Schema = schema, Nombre = nombre };
                    tabla.Columnas = GetColumnas(connStr, motor, schema, nombre);
                    lista.Add(tabla);
                }
            }
            catch { }
            return lista.OrderBy(t => t.NombreCompleto).ToList();
        }

        private static List<ColumnaComp> GetColumnas(string connStr, TipoMotor motor, string schema, string tabla)
        {
            var lista = new List<ColumnaComp>();
            string t = tabla.Replace("'", "''");
            try
            {
                var DB = new DataBase(connStr);
                string sql = null;
                switch (motor)
                {
                    case TipoMotor.MS_SQL:
                        string schemaFiltro = string.IsNullOrEmpty(schema) ? "" : $"AND c.TABLE_SCHEMA = '{schema.Replace("'", "''")}' ";
                        sql = $@"SELECT c.COLUMN_NAME, c.DATA_TYPE,
                                    c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE,
                                    c.IS_NULLABLE, c.COLUMN_DEFAULT,
                                    CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END AS IS_PK
                                FROM INFORMATION_SCHEMA.COLUMNS c
                                LEFT JOIN (
                                    SELECT ku.TABLE_NAME, ku.COLUMN_NAME, ku.TABLE_SCHEMA
                                    FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                                    JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                                        ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
                                    WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                                ) pk ON pk.TABLE_NAME = c.TABLE_NAME AND pk.COLUMN_NAME = c.COLUMN_NAME
                                    AND pk.TABLE_SCHEMA = c.TABLE_SCHEMA
                                WHERE c.TABLE_NAME = '{t}' {schemaFiltro}
                                ORDER BY c.ORDINAL_POSITION";
                        break;
                    case TipoMotor.POSTGRES:
                        string schemaFiltroP = string.IsNullOrEmpty(schema) ? "" : $"AND c.table_schema = '{schema.Replace("'", "''")}' ";
                        sql = $@"SELECT c.column_name, c.data_type,
                                    c.character_maximum_length, c.numeric_precision, c.numeric_scale,
                                    c.is_nullable, c.column_default,
                                    CASE WHEN pk.column_name IS NOT NULL THEN 1 ELSE 0 END AS is_pk
                                FROM information_schema.columns c
                                LEFT JOIN (
                                    SELECT ku.column_name
                                    FROM information_schema.table_constraints tc
                                    JOIN information_schema.key_column_usage ku
                                        ON tc.constraint_name = ku.constraint_name AND tc.table_name = ku.table_name
                                    WHERE tc.constraint_type = 'PRIMARY KEY' AND ku.table_name = '{t.ToLowerInvariant()}'
                                ) pk ON pk.column_name = c.column_name
                                WHERE c.table_name = '{t.ToLowerInvariant()}' {schemaFiltroP}
                                ORDER BY c.ordinal_position";
                        break;
                    case TipoMotor.DB2:
                        string schemaFiltroD = string.IsNullOrEmpty(schema) ? "" : $"AND TBCREATOR = '{schema.Replace("'", "''").ToUpperInvariant()}' ";
                        sql = $"SELECT NAME, COLTYPE, LENGTH, SCALE, NULLS, DEFAULT, KEYSEQ FROM SYSIBM.SYSCOLUMNS WHERE TBNAME = '{t.ToUpperInvariant()}' {schemaFiltroD} ORDER BY COLNO";
                        break;
                    case TipoMotor.SQLite:
                        sql = null;
                        DB.CommandText = $"PRAGMA table_info({tabla})";
                        while (DB.Read())
                        {
                            string tipoRaw = DB.Reader[2].ToString().Trim();
                            string tipoDato = tipoRaw;
                            int? lon = null;
                            if (tipoRaw.Contains("("))
                            {
                                int p1 = tipoRaw.IndexOf('('), p2 = tipoRaw.IndexOf(')');
                                tipoDato = tipoRaw.Substring(0, p1).Trim();
                                if (p2 > p1 && int.TryParse(tipoRaw.Substring(p1 + 1, p2 - p1 - 1), out int ln))
                                    lon = ln;
                            }
                            lista.Add(new ColumnaComp
                            {
                                Nombre   = DB.Reader[1].ToString().Trim(),
                                TipoDato = tipoDato,
                                Longitud = lon,
                                Nullable = DB.Reader[3].ToString().Trim() == "0",
                                Default  = DB.IsDBNull(4) ? null : DB.Reader[4].ToString().Trim(),
                                EsPK     = DB.Reader[5].ToString().Trim() != "0"
                            });
                        }
                        DB.CloseConnection();
                        return lista;
                }

                if (sql != null)
                {
                    DB.CommandText = sql;
                    while (DB.Read())
                    {
                        var col = new ColumnaComp
                        {
                            Nombre   = DB.Reader[0].ToString().Trim(),
                            TipoDato = DB.Reader[1].ToString().Trim(),
                            Nullable = motor == TipoMotor.DB2
                                ? DB.Reader[4].ToString().Trim().ToUpper() == "Y"
                                : DB.Reader[5].ToString().Trim().ToUpper() == "YES",
                            Default  = DB.IsDBNull(motor == TipoMotor.DB2 ? 5 : 6) ? null
                                     : DB.Reader[motor == TipoMotor.DB2 ? 5 : 6].ToString().Trim(),
                            EsPK     = motor == TipoMotor.DB2
                                ? (!DB.IsDBNull(6) && Convert.ToInt32(DB.Reader[6]) > 0)
                                : Convert.ToInt32(DB.Reader[7]) == 1
                        };
                        if (motor != TipoMotor.DB2)
                        {
                            col.Longitud  = DB.IsDBNull(2) ? (int?)null : Convert.ToInt32(DB.Reader[2]);
                            col.Precision = DB.IsDBNull(3) ? (int?)null : Convert.ToInt32(DB.Reader[3]);
                            col.Escala    = DB.IsDBNull(4) ? (int?)null : Convert.ToInt32(DB.Reader[4]);
                        }
                        else
                        {
                            int len = DB.IsDBNull(2) ? 0 : Convert.ToInt32(DB.Reader[2]);
                            col.Longitud = len > 0 ? len : (int?)null;
                            int scl = DB.IsDBNull(3) ? 0 : Convert.ToInt32(DB.Reader[3]);
                            col.Escala = scl > 0 ? scl : (int?)null;
                        }
                        lista.Add(col);
                    }
                    DB.CloseConnection();
                }
            }
            catch { }
            return lista;
        }

        // ── Vistas ────────────────────────────────────────────────────────────────

        public static List<VistaComp> GetVistas(string connStr, TipoMotor motor, string[] schemas)
        {
            var lista = new List<VistaComp>();
            try
            {
                var DB = new DataBase(connStr);
                DataTable dt = DB.GetSchema("VIEWs");
                DB.CloseConnection();

                foreach (DataRow fila in dt.Rows)
                {
                    string schema = fila["TABLE_SCHEM"].ToString().Trim();
                    string nombre = fila["TABLE_NAME"].ToString().Trim();

                    if (schemas != null && schemas.Length > 0 &&
                        !schemas.Any(s => s.Equals(schema, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    string def = GetDefinicionVista(connStr, motor, schema, nombre);
                    lista.Add(new VistaComp { Schema = schema, Nombre = nombre, Definicion = def ?? "" });
                }
            }
            catch { }
            return lista.OrderBy(v => v.NombreCompleto).ToList();
        }

        private static string GetDefinicionVista(string connStr, TipoMotor motor, string schema, string nombre)
        {
            string t = nombre.Replace("'", "''");
            try
            {
                var DB = new DataBase(connStr);
                string sql = null;
                switch (motor)
                {
                    case TipoMotor.MS_SQL:
                        sql = $"SELECT VIEW_DEFINITION FROM INFORMATION_SCHEMA.VIEWS WHERE TABLE_NAME = '{t}'";
                        break;
                    case TipoMotor.POSTGRES:
                        sql = $"SELECT view_definition FROM information_schema.views WHERE table_name = '{t.ToLowerInvariant()}'";
                        break;
                    case TipoMotor.DB2:
                        sql = $"SELECT TEXT FROM SYSCAT.VIEWS WHERE VIEWNAME = '{t.ToUpperInvariant()}'";
                        break;
                    default:
                        return null;
                }
                DB.CommandText = sql;
                var val = DB.Scalar();
                DB.CloseConnection();
                return val == null || val == DBNull.Value ? null : val.ToString().Trim();
            }
            catch { return null; }
        }

        // ── Índices ───────────────────────────────────────────────────────────────

        public static List<IndiceComp> GetIndices(string connStr, TipoMotor motor, string[] schemas)
        {
            var lista = new List<IndiceComp>();
            try
            {
                var DB = new DataBase(connStr);
                string sql = null;
                switch (motor)
                {
                    case TipoMotor.MS_SQL:
                        string schWhere = (schemas != null && schemas.Length > 0)
                            ? $"WHERE s.name IN ({string.Join(",", schemas.Select(s => $"'{s.Replace("'", "''")}'"))})"
                            : "";
                        sql = $@"SELECT s.name AS schema_name, t.name AS table_name,
                                    i.name AS index_name, i.is_unique, i.is_primary_key,
                                    STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal) AS cols
                                FROM sys.tables t
                                JOIN sys.schemas s ON t.schema_id = s.schema_id
                                JOIN sys.indexes i ON t.object_id = i.object_id AND i.name IS NOT NULL
                                JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                                JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                                {schWhere}
                                GROUP BY s.name, t.name, i.name, i.is_unique, i.is_primary_key
                                ORDER BY s.name, t.name, i.name";
                        break;
                    case TipoMotor.POSTGRES:
                        string schWhereP = (schemas != null && schemas.Length > 0)
                            ? $"AND schemaname IN ({string.Join(",", schemas.Select(s => $"'{s.Replace("'", "''")}'"  ))})"
                            : "";
                        sql = $"SELECT schemaname, tablename, indexname, indexdef FROM pg_indexes WHERE 1=1 {schWhereP} ORDER BY schemaname, tablename, indexname";
                        break;
                    case TipoMotor.DB2:
                        string schWhereD = (schemas != null && schemas.Length > 0)
                            ? $"AND TABSCHEMA IN ({string.Join(",", schemas.Select(s => $"'{s.Replace("'", "''").ToUpperInvariant()}'"))})"
                            : "";
                        sql = $"SELECT TABSCHEMA, TABNAME, INDNAME, UNIQUERULE, COLNAMES FROM SYSCAT.INDEXES WHERE 1=1 {schWhereD} ORDER BY TABSCHEMA, TABNAME, INDNAME";
                        break;
                    case TipoMotor.SQLite:
                        GetIndicesSQLite(connStr, lista);
                        return lista;
                }

                if (sql != null)
                {
                    DB.CommandText = sql;
                    while (DB.Read())
                    {
                        if (motor == TipoMotor.POSTGRES)
                        {
                            string def = DB.Reader[3].ToString();
                            bool esUnico = def.StartsWith("CREATE UNIQUE", StringComparison.OrdinalIgnoreCase);
                            bool esPK   = DB.Reader[2].ToString().ToLower().Contains("pkey");
                            lista.Add(new IndiceComp
                            {
                                Schema  = DB.Reader[0].ToString().Trim(),
                                Tabla   = DB.Reader[1].ToString().Trim(),
                                Nombre  = DB.Reader[2].ToString().Trim(),
                                EsUnico = esUnico,
                                EsPK    = esPK,
                                Columnas = ExtractPgIndexCols(def)
                            });
                        }
                        else if (motor == TipoMotor.DB2)
                        {
                            string uniqueRule = DB.Reader[3].ToString().Trim().ToUpper();
                            lista.Add(new IndiceComp
                            {
                                Schema  = DB.Reader[0].ToString().Trim(),
                                Tabla   = DB.Reader[1].ToString().Trim(),
                                Nombre  = DB.Reader[2].ToString().Trim(),
                                EsPK    = uniqueRule == "P",
                                EsUnico = uniqueRule == "U" || uniqueRule == "P",
                                Columnas = DB.IsDBNull(4) ? "" : DB.Reader[4].ToString().Trim()
                            });
                        }
                        else // MS_SQL
                        {
                            lista.Add(new IndiceComp
                            {
                                Schema  = DB.Reader[0].ToString().Trim(),
                                Tabla   = DB.Reader[1].ToString().Trim(),
                                Nombre  = DB.Reader[2].ToString().Trim(),
                                EsUnico = Convert.ToBoolean(DB.Reader[3]),
                                EsPK    = Convert.ToBoolean(DB.Reader[4]),
                                Columnas = DB.IsDBNull(5) ? "" : DB.Reader[5].ToString().Trim()
                            });
                        }
                    }
                    DB.CloseConnection();
                }
            }
            catch { }
            return lista.OrderBy(i => i.Clave).ToList();
        }

        private static void GetIndicesSQLite(string connStr, List<IndiceComp> lista)
        {
            try
            {
                var DB = new DataBase(connStr);
                var tablas = new List<string>();
                DB.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
                while (DB.Read()) tablas.Add(DB.Reader[0].ToString().Trim());
                DB.CloseConnection();

                foreach (string tabla in tablas)
                {
                    var DB2 = new DataBase(connStr);
                    DB2.CommandText = $"PRAGMA index_list({tabla})";
                    var indices = new List<(string name, bool unique)>();
                    while (DB2.Read())
                        indices.Add((DB2.Reader[1].ToString().Trim(), DB2.Reader[2].ToString().Trim() == "1"));
                    DB2.CloseConnection();

                    foreach (var (idxName, idxUnique) in indices)
                    {
                        var DB3 = new DataBase(connStr);
                        DB3.CommandText = $"PRAGMA index_info({idxName})";
                        var cols = new List<string>();
                        while (DB3.Read()) cols.Add(DB3.Reader[2].ToString().Trim());
                        DB3.CloseConnection();
                        lista.Add(new IndiceComp
                        {
                            Schema   = "main",
                            Tabla    = tabla,
                            Nombre   = idxName,
                            EsUnico  = idxUnique,
                            EsPK     = false,
                            Columnas = string.Join(", ", cols)
                        });
                    }
                }
            }
            catch { }
        }

        private static string ExtractPgIndexCols(string def)
        {
            var m = Regex.Match(def, @"\(([^)]+)\)");
            return m.Success ? m.Groups[1].Value : def;
        }

        // ── Conteo de filas ───────────────────────────────────────────────────────

        public static long GetConteoFilas(string connStr, string tablaCompleta)
        {
            try
            {
                var DB = new DataBase(connStr);
                DB.CommandText = $"SELECT COUNT(*) FROM {tablaCompleta}";
                var val = DB.Scalar();
                DB.CloseConnection();
                return val == null || val == DBNull.Value ? 0 : Convert.ToInt64(val);
            }
            catch { return -1; }
        }

        // ── Diffs ─────────────────────────────────────────────────────────────────

        public static List<DiffTabla> CompararTablas(List<TablaComp> a, List<TablaComp> b)
        {
            var dictA = a.ToDictionary(t => t.NombreCompleto, StringComparer.OrdinalIgnoreCase);
            var dictB = b.ToDictionary(t => t.NombreCompleto, StringComparer.OrdinalIgnoreCase);
            var claves = dictA.Keys.Union(dictB.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k);
            var resultado = new List<DiffTabla>();

            foreach (string clave in claves)
            {
                bool enA = dictA.ContainsKey(clave);
                bool enB = dictB.ContainsKey(clave);

                if (enA && enB)
                {
                    var cols = CompararColumnas(dictA[clave].Columnas, dictB[clave].Columnas);
                    var estado = cols.Any(c => c.Estado != DiffEstado.Igual) ? DiffEstado.Diferente : DiffEstado.Igual;
                    resultado.Add(new DiffTabla { LadoA = dictA[clave], LadoB = dictB[clave], Estado = estado, Columnas = cols });
                }
                else if (enA)
                    resultado.Add(new DiffTabla { LadoA = dictA[clave], Estado = DiffEstado.SoloEnA });
                else
                    resultado.Add(new DiffTabla { LadoB = dictB[clave], Estado = DiffEstado.SoloEnB });
            }
            return resultado;
        }

        public static List<DiffColumna> CompararColumnas(List<ColumnaComp> a, List<ColumnaComp> b)
        {
            var dictA = a.ToDictionary(c => c.Nombre, StringComparer.OrdinalIgnoreCase);
            var dictB = b.ToDictionary(c => c.Nombre, StringComparer.OrdinalIgnoreCase);
            var claves = dictA.Keys.Union(dictB.Keys, StringComparer.OrdinalIgnoreCase);
            var resultado = new List<DiffColumna>();

            foreach (string clave in claves)
            {
                bool enA = dictA.ContainsKey(clave);
                bool enB = dictB.ContainsKey(clave);

                if (enA && enB)
                {
                    var ca = dictA[clave]; var cb = dictB[clave];
                    bool igual = ca.ResumenTipo().Equals(cb.ResumenTipo(), StringComparison.OrdinalIgnoreCase)
                              && ca.Nullable == cb.Nullable
                              && ca.EsPK == cb.EsPK;
                    resultado.Add(new DiffColumna { LadoA = ca, LadoB = cb, Estado = igual ? DiffEstado.Igual : DiffEstado.Diferente });
                }
                else if (enA)
                    resultado.Add(new DiffColumna { LadoA = dictA[clave], Estado = DiffEstado.SoloEnA });
                else
                    resultado.Add(new DiffColumna { LadoB = dictB[clave], Estado = DiffEstado.SoloEnB });
            }
            // orden: PKs primero, luego orden original de A, luego solo-B al final
            return resultado
                .OrderByDescending(c => (c.LadoA ?? c.LadoB).EsPK)
                .ThenBy(c => a.FindIndex(x => x.Nombre.Equals(c.Nombre, StringComparison.OrdinalIgnoreCase)) is int idx && idx >= 0 ? idx : 9999)
                .ToList();
        }

        public static List<DiffVista> CompararVistas(List<VistaComp> a, List<VistaComp> b)
        {
            var dictA = a.ToDictionary(v => v.NombreCompleto, StringComparer.OrdinalIgnoreCase);
            var dictB = b.ToDictionary(v => v.NombreCompleto, StringComparer.OrdinalIgnoreCase);
            var claves = dictA.Keys.Union(dictB.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k);
            var resultado = new List<DiffVista>();

            foreach (string clave in claves)
            {
                bool enA = dictA.ContainsKey(clave);
                bool enB = dictB.ContainsKey(clave);
                if (enA && enB)
                {
                    bool igual = NormalizarSql(dictA[clave].Definicion) == NormalizarSql(dictB[clave].Definicion);
                    resultado.Add(new DiffVista { LadoA = dictA[clave], LadoB = dictB[clave], Estado = igual ? DiffEstado.Igual : DiffEstado.Diferente });
                }
                else if (enA)
                    resultado.Add(new DiffVista { LadoA = dictA[clave], Estado = DiffEstado.SoloEnA });
                else
                    resultado.Add(new DiffVista { LadoB = dictB[clave], Estado = DiffEstado.SoloEnB });
            }
            return resultado;
        }

        public static List<DiffIndice> CompararIndices(List<IndiceComp> a, List<IndiceComp> b)
        {
            var dictA = a.ToDictionary(i => i.Clave, StringComparer.OrdinalIgnoreCase);
            var dictB = b.ToDictionary(i => i.Clave, StringComparer.OrdinalIgnoreCase);
            var claves = dictA.Keys.Union(dictB.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(k => k);
            var resultado = new List<DiffIndice>();

            foreach (string clave in claves)
            {
                bool enA = dictA.ContainsKey(clave);
                bool enB = dictB.ContainsKey(clave);
                if (enA && enB)
                {
                    var ia = dictA[clave]; var ib = dictB[clave];
                    bool igual = ia.EsUnico == ib.EsUnico && ia.EsPK == ib.EsPK
                              && NormalizarSql(ia.Columnas) == NormalizarSql(ib.Columnas);
                    resultado.Add(new DiffIndice { LadoA = ia, LadoB = ib, Estado = igual ? DiffEstado.Igual : DiffEstado.Diferente });
                }
                else if (enA)
                    resultado.Add(new DiffIndice { LadoA = dictA[clave], Estado = DiffEstado.SoloEnA });
                else
                    resultado.Add(new DiffIndice { LadoB = dictB[clave], Estado = DiffEstado.SoloEnB });
            }
            return resultado;
        }

        // ── Orquestador principal ─────────────────────────────────────────────────

        public static ResultadoComp Comparar(
            InfoLado ladoA, InfoLado ladoB, OpcionesComp opciones,
            Action<string> onProgress = null)
        {
            var resultado = new ResultadoComp();

            if (opciones.CompararTablas)
            {
                onProgress?.Invoke("Cargando tablas de A...");
                var tA = GetTablas(ladoA.ConnStr, ladoA.Conexion.Motor, ladoA.Schemas);
                onProgress?.Invoke("Cargando tablas de B...");
                var tB = GetTablas(ladoB.ConnStr, ladoB.Conexion.Motor, ladoB.Schemas);
                onProgress?.Invoke("Comparando tablas...");
                resultado.Tablas = CompararTablas(tA, tB);

                if (opciones.CompararDatos)
                {
                    var tablasEnAmbas = resultado.Tablas
                        .Where(d => d.Estado == DiffEstado.Igual || d.Estado == DiffEstado.Diferente)
                        .ToList();
                    int total = tablasEnAmbas.Count, i = 0;
                    foreach (var diff in tablasEnAmbas)
                    {
                        i++;
                        onProgress?.Invoke($"Comparando datos {i}/{total}: {diff.Nombre}");
                        long conteoA = GetConteoFilas(ladoA.ConnStr, diff.LadoA.NombreCompleto);
                        long conteoB = GetConteoFilas(ladoB.ConnStr, diff.LadoB.NombreCompleto);
                        DiffEstado estadoDatos = conteoA == conteoB ? DiffEstado.Igual : DiffEstado.Diferente;
                        resultado.Datos.Add(new DiffDatos
                        {
                            Tabla   = diff.Nombre,
                            ConteoA = conteoA,
                            ConteoB = conteoB,
                            Estado  = estadoDatos
                        });
                    }
                }
            }

            if (opciones.CompararVistas)
            {
                onProgress?.Invoke("Cargando vistas de A...");
                var vA = GetVistas(ladoA.ConnStr, ladoA.Conexion.Motor, ladoA.Schemas);
                onProgress?.Invoke("Cargando vistas de B...");
                var vB = GetVistas(ladoB.ConnStr, ladoB.Conexion.Motor, ladoB.Schemas);
                onProgress?.Invoke("Comparando vistas...");
                resultado.Vistas = CompararVistas(vA, vB);
            }

            if (opciones.CompararIndices)
            {
                onProgress?.Invoke("Cargando índices de A...");
                var iA = GetIndices(ladoA.ConnStr, ladoA.Conexion.Motor, ladoA.Schemas);
                onProgress?.Invoke("Cargando índices de B...");
                var iB = GetIndices(ladoB.ConnStr, ladoB.Conexion.Motor, ladoB.Schemas);
                onProgress?.Invoke("Comparando índices...");
                resultado.Indices = CompararIndices(iA, iB);
            }

            onProgress?.Invoke("Listo.");
            return resultado;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static string NormalizarSql(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            return Regex.Replace(s.Trim(), @"\s+", " ").ToLowerInvariant();
        }
    }
}
