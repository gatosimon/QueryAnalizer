using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Odbc;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using CapiDL;

namespace QueryAnalyzer
{
    public static class PasadorDatosService
    {
        // ─────────────────────────────────────────────────────────────────────
        // FK graph
        // ─────────────────────────────────────────────────────────────────────

        public static List<FkEdge> ObtenerFKsEntreTablas(
            string connStr, TipoMotor motor, List<TablaTransfer> tablas)
        {
            // Las FK se consultan en B: se usa el nombre que la tabla tiene en B
            var nombres = new HashSet<string>(
                tablas.Select(t => t.B.Nombre),
                StringComparer.OrdinalIgnoreCase);

            return ObtenerTodasLasFKs(connStr, motor, tablas)
                .Where(e => nombres.Contains(NombreSimple(e.TablaOrigen))
                         && nombres.Contains(NombreSimple(e.TablaDestino)))
                .ToList();
        }

        public static List<string> DetectarFKsExternas(
            string connStr, TipoMotor motor, List<TablaTransfer> tablas)
        {
            var nombres = new HashSet<string>(
                tablas.Select(t => t.B.Nombre),
                StringComparer.OrdinalIgnoreCase);

            var avisos = new List<string>();
            foreach (var e in ObtenerTodasLasFKs(connStr, motor, tablas))
            {
                bool origenDentro  = nombres.Contains(NombreSimple(e.TablaOrigen));
                bool destinoDentro = nombres.Contains(NombreSimple(e.TablaDestino));

                if (origenDentro && !destinoDentro)
                    avisos.Add($"'{e.TablaOrigen}' tiene FK hacia '{e.TablaDestino}' (fuera del conjunto). " +
                               "Datos insertados podrían quedar con referencias inválidas.");

                if (!origenDentro && destinoDentro)
                    avisos.Add($"'{e.TablaDestino}' es referenciada por '{e.TablaOrigen}' (fuera del conjunto). " +
                               "El DELETE puede fallar si hay filas apuntando a ella.");
            }
            return avisos.Distinct().ToList();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Topological sort (Kahn)
        // ─────────────────────────────────────────────────────────────────────

        public static (List<TablaTransfer> OrdenInsert, List<string> Ciclicas) OrdenarPorDependencias(
            List<TablaTransfer> tablas, List<FkEdge> edges)
        {
            // Las FK vienen de B: las tablas se identifican por el nombre que tienen en B (igual al de A si no hay destino resuelto)
            var inDegree  = tablas.ToDictionary(t => t.B.Nombre, _ => 0, StringComparer.OrdinalIgnoreCase);
            var dependeDe = tablas.ToDictionary(t => t.B.Nombre, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            var requiereMe= tablas.ToDictionary(t => t.B.Nombre, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var e in edges)
            {
                string origen  = NombreSimple(e.TablaOrigen);
                string destino = NombreSimple(e.TablaDestino);
                if (!inDegree.ContainsKey(origen) || !inDegree.ContainsKey(destino)) continue;
                if (dependeDe[origen].Add(destino))
                {
                    inDegree[origen]++;
                    requiereMe[destino].Add(origen);
                }
            }

            var cola = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
            var ordenNombres = new List<string>();
            while (cola.Count > 0)
            {
                string actual = cola.Dequeue();
                ordenNombres.Add(actual);
                foreach (string dep in requiereMe[actual])
                    if (--inDegree[dep] == 0) cola.Enqueue(dep);
            }

            var ciclicas = inDegree.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
            ordenNombres.AddRange(ciclicas);

            var mapa = tablas.ToDictionary(t => t.B.Nombre, t => t, StringComparer.OrdinalIgnoreCase);
            return (ordenNombres.Where(n => mapa.ContainsKey(n)).Select(n => mapa[n]).ToList(), ciclicas);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Metadata de columnas (por motor)
        // ─────────────────────────────────────────────────────────────────────

        public static Dictionary<string, TablaMetadata> ObtenerMetadataTablas(
            string connStrB, TipoMotor motorB, List<TablaTransfer> tablas,
            RegistroIncidencias incidencias = null)
        {
            var resultado = new Dictionary<string, TablaMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tablas)
                resultado[t.NombreCompleto] = t.Meta ?? new TablaMetadata();

            // Las tablas con metadata ya resuelta (transferencia entre motores) no se vuelven a consultar
            tablas = tablas.Where(t => t.Meta == null).ToList();
            if (tablas.Count == 0) return resultado;

            if (string.IsNullOrEmpty(connStrB)) return resultado;

            DataBase db = null;
            try { db = new DataBase(connStrB, false); }
            catch (Exception exCon)
            {
                incidencias?.Agregar("Metadata", null, SeveridadIncidencia.Aviso, "Sin metadata de columnas",
                    "No se pudo conectar a B para leer columnas identity/calculadas: " + exCon.Message,
                    "La carga sigue sin esas optimizaciones; si B tiene columnas identity GENERATED ALWAYS o calculadas, el INSERT fallará y se hará rollback.");
                return resultado;
            }

            try
            {
                foreach (var tabla in tablas)
                {
                    var meta = resultado[tabla.NombreCompleto];
                    try
                    {
                        switch (motorB)
                        {
                            case TipoMotor.MS_SQL:
                                CargarMetadataMsSql(db, tabla, meta);
                                break;
                            case TipoMotor.POSTGRES:
                                CargarMetadataPostgres(db, tabla, meta);
                                break;
                            case TipoMotor.DB2:
                                CargarMetadataDb2(db, tabla, meta);
                                break;
                            case TipoMotor.SQLite:
                                CargarMetadataSqlite(db, tabla, meta);
                                break;
                        }
                    }
                    catch (Exception exMeta)
                    {
                        // metadata vacía — sin optimizaciones para esta tabla, pero se deja constancia
                        incidencias?.Agregar("Metadata", tabla.NombreCompleto, SeveridadIncidencia.Aviso,
                            "Sin metadata de columnas", exMeta.Message,
                            "Si la tabla tiene columnas identity GENERATED ALWAYS o calculadas, el INSERT fallará y se hará rollback.");
                    }
                }
            }
            finally { try { db.CloseConnection(); } catch { } }

            return resultado;
        }

        private static void CargarMetadataMsSql(DataBase db, TablaTransfer tabla, TablaMetadata meta)
        {
            db.CommandText =
                // CAST a int: el tipo bit llega como Boolean ("True") por ODBC y no coincidía con "1"
                $"SELECT c.name, CAST(c.is_identity AS int) AS is_identity, CAST(c.is_computed AS int) AS is_computed, " +
                $"CASE WHEN tp.name IN ('timestamp','rowversion') THEN 1 ELSE 0 END AS es_ts " +
                $"FROM sys.columns c JOIN sys.types tp ON c.user_type_id = tp.user_type_id " +
                $"WHERE c.object_id = OBJECT_ID('{tabla.NombreCompleto}')";
            var dt = new DataTable();
            db.DataAdapter.Fill(dt);
            // Sin columnas: la tabla no existe en B o el usuario no la ve. Callar acá escondía el problema hasta el INSERT.
            if (dt.Rows.Count == 0)
                throw new InvalidOperationException($"B no devolvió columnas para {tabla.NombreCompleto} (¿no existe o falta permiso?).");
            foreach (DataRow r in dt.Rows)
            {
                string col = r["name"].ToString();
                if (EsVerdadero(r["is_identity"])) meta.ColumnasIdentity.Add(col);
                if (EsVerdadero(r["is_computed"]) || EsVerdadero(r["es_ts"]))
                    meta.ColumnasExcluidas.Add(col);
            }
        }

        /// <summary>1, "1", true y "True" son verdaderos; el driver ODBC puede entregar cualquiera según el tipo.</summary>
        private static bool EsVerdadero(object valor)
        {
            if (valor == null || valor is DBNull) return false;
            if (valor is bool) return (bool)valor;
            string s = valor.ToString().Trim();
            return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static void CargarMetadataPostgres(DataBase db, TablaTransfer tabla, TablaMetadata meta)
        {
            string schema = string.IsNullOrEmpty(tabla.Schema) ? "public" : tabla.Schema;
            db.CommandText =
                $"SELECT column_name, is_identity, identity_generation, is_generated " +
                $"FROM information_schema.columns " +
                $"WHERE table_schema = '{schema}' AND table_name = '{tabla.Nombre}'";
            var dt = new DataTable();
            db.DataAdapter.Fill(dt);
            if (dt.Rows.Count == 0)
                throw new InvalidOperationException($"B no devolvió columnas para {schema}.{tabla.Nombre} (¿no existe, otro esquema, otras mayúsculas o falta permiso?).");
            foreach (DataRow r in dt.Rows)
            {
                string col = r["column_name"].ToString();
                if ("YES".Equals(r["is_identity"].ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    meta.ColumnasIdentity.Add(col);
                    if ("ALWAYS".Equals(r["identity_generation"].ToString(), StringComparison.OrdinalIgnoreCase))
                        meta.ColumnasIdentityAlways.Add(col);
                }
                if ("ALWAYS".Equals(r["is_generated"].ToString(), StringComparison.OrdinalIgnoreCase))
                    meta.ColumnasExcluidas.Add(col);
            }
        }

        private static void CargarMetadataDb2(DataBase db, TablaTransfer tabla, TablaMetadata meta)
        {
            string schema = (tabla.Schema ?? "").ToUpper();
            string nombre = tabla.Nombre.ToUpper();
            db.CommandText =
                $"SELECT COLNAME, IDENTITY, GENERATED FROM SYSCAT.COLUMNS " +
                $"WHERE TABSCHEMA = '{schema}' AND TABNAME = '{nombre}'";
            var dt = new DataTable();
            db.DataAdapter.Fill(dt);
            if (dt.Rows.Count == 0)
                throw new InvalidOperationException($"B no devolvió columnas para {tabla.NombreCompleto} (¿no existe, otro esquema o falta permiso?).");
            foreach (DataRow r in dt.Rows)
            {
                string col  = r["COLNAME"].ToString();
                string iden = r["IDENTITY"].ToString();
                string gen  = r["GENERATED"].ToString();
                if ("Y".Equals(iden, StringComparison.OrdinalIgnoreCase))
                {
                    meta.ColumnasIdentity.Add(col);
                    if ("A".Equals(gen, StringComparison.OrdinalIgnoreCase))
                        meta.ColumnasIdentityAlways.Add(col);
                }
                else if ("A".Equals(gen, StringComparison.OrdinalIgnoreCase))
                    meta.ColumnasExcluidas.Add(col);
            }
        }

        private static void CargarMetadataSqlite(DataBase db, TablaTransfer tabla, TablaMetadata meta)
        {
            db.CommandText = $"PRAGMA table_xinfo('{tabla.Nombre}')";
            var dt = new DataTable();
            db.DataAdapter.Fill(dt);
            foreach (DataRow r in dt.Rows)
            {
                string hidden = r["hidden"].ToString();
                if (hidden == "2" || hidden == "3")
                    meta.ColumnasExcluidas.Add(r["name"].ToString());
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Transferencia entre motores distintos: resolver la tabla y las columnas en B
        //
        // Con el mismo motor, A y B comparten nombres y la tabla se escribe tal cual. Con motores distintos
        // (p. ej. DB2 -> SQL Server) los nombres cambian (esquema dbo / public / el de DB2, mayúsculas) y los
        // tipos no se llaman igual, así que antes de tocar B se lee el catálogo de B: nombre exacto de la tabla
        // y de cada columna, su tipo, si admite NULL, si es identity. Con eso se entrecomillan los
        // identificadores y se convierte cada valor al tipo de la columna destino (ver ScriptHelper).
        // Todo lo que no coincida queda como incidencia ANTES del primer DELETE.
        // ─────────────────────────────────────────────────────────────────────

        private sealed class TablaCatalogo
        {
            public string Schema;
            public string Nombre;
            public List<ColumnaDestino> Columnas = new List<ColumnaDestino>();
        }

        private sealed class ColumnaOrigen
        {
            public string Nombre;
            public Type   Clr;
            public string TipoNombre;
        }

        /// <summary>Familia de un tipo de dato según el catálogo del motor.</summary>
        internal static CategoriaTipo ClasificarTipo(TipoMotor motor, string tipo)
        {
            string t = (tipo ?? "").Trim().ToLowerInvariant();
            int p = t.IndexOf('(');
            if (p >= 0) t = t.Substring(0, p).Trim();

            switch (t)
            {
                case "boolean": case "bool":
                    return (motor == TipoMotor.POSTGRES || motor == TipoMotor.DB2) ? CategoriaTipo.Bool : CategoriaTipo.Entero;
                case "bit":
                    return motor == TipoMotor.MS_SQL ? CategoriaTipo.Entero : CategoriaTipo.Otro;
                case "tinyint": case "smallint": case "int": case "integer": case "bigint": case "mediumint":
                case "int2": case "int4": case "int8": case "serial": case "bigserial": case "smallserial":
                    return CategoriaTipo.Entero;
                case "decimal": case "numeric": case "money": case "smallmoney": case "number": case "dec": case "decfloat":
                    return CategoriaTipo.Decimal;
                case "float": case "real": case "double": case "double precision": case "float4": case "float8":
                    return CategoriaTipo.Real;
                case "char": case "varchar": case "text": case "character": case "character varying": case "bpchar":
                case "clob": case "long varchar": case "longvarchar": case "tinytext": case "mediumtext": case "longtext":
                case "name": case "citext": case "varchar2":
                    return CategoriaTipo.Texto;
                case "nchar": case "nvarchar": case "ntext": case "graphic": case "vargraphic": case "dbclob":
                case "nvarchar2": case "nclob":
                    return CategoriaTipo.TextoUnicode;
                case "date":
                    return CategoriaTipo.Fecha;
                case "timestamp":
                    return motor == TipoMotor.MS_SQL ? CategoriaTipo.Binario : CategoriaTipo.FechaHora;   // en SQL Server es rowversion
                case "datetime": case "datetime2": case "smalldatetime": case "timestamp without time zone":
                    return CategoriaTipo.FechaHora;
                case "datetimeoffset": case "timestamp with time zone": case "timestamptz":
                    return CategoriaTipo.FechaHoraTz;
                case "time": case "time without time zone": case "timetz": case "time with time zone":
                    return CategoriaTipo.Hora;
                case "binary": case "varbinary": case "image": case "bytea": case "blob": case "long varbinary":
                case "longblob": case "mediumblob": case "tinyblob": case "raw": case "long raw":
                    return CategoriaTipo.Binario;
                case "uniqueidentifier": case "uuid":
                    return CategoriaTipo.Guid;
                case "xml":
                    return CategoriaTipo.Xml;
                default:
                    return CategoriaTipo.Otro;
            }
        }

        private static string ConsultaCatalogo(TipoMotor motor, string nombreEscapado)
        {
            switch (motor)
            {
                case TipoMotor.MS_SQL:
                    return
                        "SELECT s.name AS sch, t.name AS tbl, c.name AS col, TYPE_NAME(c.system_type_id) AS tipo, " +
                        "  CAST(c.is_nullable AS int) AS nulo, " +
                        "  CAST(CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END AS int) AS tdef, " +
                        "  CAST(c.is_identity AS int) AS ident, CAST(0 AS int) AS alw, " +
                        "  CAST(CASE WHEN c.is_computed = 1 OR TYPE_NAME(c.system_type_id) = 'timestamp' THEN 1 ELSE 0 END AS int) AS excl " +
                        "FROM sys.columns c " +
                        "JOIN sys.tables t ON t.object_id = c.object_id " +
                        "JOIN sys.schemas s ON s.schema_id = t.schema_id " +
                        $"WHERE LOWER(t.name) = LOWER('{nombreEscapado}') ORDER BY s.name, t.name, c.column_id";

                case TipoMotor.POSTGRES:
                    return
                        "SELECT table_schema AS sch, table_name AS tbl, column_name AS col, data_type AS tipo, " +
                        "  CASE WHEN is_nullable = 'YES' THEN 1 ELSE 0 END AS nulo, " +
                        "  CASE WHEN column_default IS NOT NULL THEN 1 ELSE 0 END AS tdef, " +
                        "  CASE WHEN is_identity = 'YES' THEN 1 ELSE 0 END AS ident, " +
                        "  CASE WHEN is_identity = 'YES' AND identity_generation = 'ALWAYS' THEN 1 ELSE 0 END AS alw, " +
                        "  CASE WHEN is_generated = 'ALWAYS' THEN 1 ELSE 0 END AS excl " +
                        "FROM information_schema.columns " +
                        $"WHERE LOWER(table_name) = LOWER('{nombreEscapado}') " +
                        "  AND table_schema NOT IN ('pg_catalog', 'information_schema') " +
                        "ORDER BY table_schema, table_name, ordinal_position";

                case TipoMotor.DB2:
                    return
                        "SELECT TRIM(TABSCHEMA) AS sch, TRIM(TABNAME) AS tbl, COLNAME AS col, TYPENAME AS tipo, " +
                        "  CASE WHEN NULLS = 'Y' THEN 1 ELSE 0 END AS nulo, " +
                        "  CASE WHEN \"DEFAULT\" IS NOT NULL THEN 1 ELSE 0 END AS tdef, " +
                        "  CASE WHEN IDENTITY = 'Y' THEN 1 ELSE 0 END AS ident, " +
                        "  CASE WHEN IDENTITY = 'Y' AND GENERATED = 'A' THEN 1 ELSE 0 END AS alw, " +
                        "  CASE WHEN IDENTITY <> 'Y' AND GENERATED = 'A' THEN 1 ELSE 0 END AS excl " +
                        "FROM SYSCAT.COLUMNS " +
                        $"WHERE UPPER(TABNAME) = UPPER('{nombreEscapado}') AND TABSCHEMA NOT LIKE 'SYS%' " +
                        "ORDER BY TABSCHEMA, TABNAME, COLNO";

                default:
                    return null;
            }
        }

        private static ColumnaDestino NuevaColumnaDestino(TipoMotor motor, DataRow r)
        {
            string tipo = Convert.ToString(r["tipo"]).Trim().ToLowerInvariant();
            return new ColumnaDestino
            {
                Nombre           = Convert.ToString(r["col"]),
                Tipo             = tipo,
                Categoria        = ClasificarTipo(motor, tipo),
                Nulable          = EsVerdadero(r["nulo"]),
                TieneDefault     = EsVerdadero(r["tdef"]),
                EsIdentity       = EsVerdadero(r["ident"]),
                EsIdentityAlways = EsVerdadero(r["alw"]),
                Excluida         = EsVerdadero(r["excl"])
            };
        }

        /// <summary>Todas las tablas de B que se llamen <paramref name="nombre"/> (sin distinguir mayúsculas), con sus columnas.</summary>
        private static List<TablaCatalogo> LeerCatalogoTabla(DataBase db, TipoMotor motor, string nombre)
        {
            string n = nombre.Replace("'", "''");
            var res = new List<TablaCatalogo>();

            if (motor == TipoMotor.SQLite)
            {
                db.CommandText = $"SELECT name FROM sqlite_master WHERE type IN ('table', 'view') AND LOWER(name) = LOWER('{n}')";
                var dtT = new DataTable();
                db.DataAdapter.Fill(dtT);
                foreach (DataRow rt in dtT.Rows)
                {
                    string real = Convert.ToString(rt[0]);
                    var tc = new TablaCatalogo { Schema = "", Nombre = real };
                    db.CommandText = $"PRAGMA table_xinfo('{real.Replace("'", "''")}')";
                    var dtC = new DataTable();
                    db.DataAdapter.Fill(dtC);
                    foreach (DataRow rc in dtC.Rows)
                    {
                        string tipo = Convert.ToString(rc["type"]).Trim().ToLowerInvariant();
                        string hidden = Convert.ToString(rc["hidden"]);
                        bool pk = Convert.ToString(rc["pk"]) != "0" && !string.IsNullOrEmpty(Convert.ToString(rc["pk"]));
                        var col = new ColumnaDestino
                        {
                            Nombre       = Convert.ToString(rc["name"]),
                            Tipo         = tipo,
                            Categoria    = ClasificarTipo(motor, tipo),
                            Nulable      = !EsVerdadero(rc["notnull"]),
                            // INTEGER PRIMARY KEY es alias del rowid: SQLite lo genera si no se informa
                            TieneDefault = !(rc["dflt_value"] is DBNull) || (pk && tipo.StartsWith("int")),
                            Excluida     = hidden == "2" || hidden == "3"
                        };
                        tc.Columnas.Add(col);
                    }
                    res.Add(tc);
                }
                return res;
            }

            db.CommandText = ConsultaCatalogo(motor, n);
            var dt = new DataTable();
            db.DataAdapter.Fill(dt);
            foreach (DataRow r in dt.Rows)
            {
                string sch = Convert.ToString(r["sch"]).Trim();
                string tbl = Convert.ToString(r["tbl"]).Trim();
                TablaCatalogo tc = null;
                foreach (var x in res)
                    if (x.Schema == sch && x.Nombre == tbl) { tc = x; break; }
                if (tc == null) { tc = new TablaCatalogo { Schema = sch, Nombre = tbl }; res.Add(tc); }
                tc.Columnas.Add(NuevaColumnaDestino(motor, r));
            }
            return res;
        }

        /// <summary>Elige en qué esquema de B está la tabla. Devuelve null y el motivo si no se puede decidir.</summary>
        private static TablaCatalogo ElegirTablaB(
            List<TablaCatalogo> cands, string nombreA, string esquemaA, string esquemaForzado,
            TipoMotor motorB, out string motivo)
        {
            motivo = null;
            if (cands.Count == 0) { motivo = "no existe en B"; return null; }

            // Si hay dos que difieren solo en mayúsculas, gana la que se llama igual que en A
            cands = cands.OrderByDescending(c => c.Nombre == nombreA).ToList();
            Func<TablaCatalogo, string, bool> mismoEsquema =
                (c, e) => string.Equals(c.Schema ?? "", (e ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            string donde = string.Join(", ", cands.Select(c => string.IsNullOrEmpty(c.Schema) ? "(sin esquema)" : c.Schema).Distinct());

            if (!string.IsNullOrWhiteSpace(esquemaForzado))
            {
                var f = cands.FirstOrDefault(c => mismoEsquema(c, esquemaForzado));
                if (f == null) motivo = $"no existe en el esquema '{esquemaForzado.Trim()}' de B (está en: {donde})";
                return f;
            }

            if (cands.Count == 1) return cands[0];

            var porA = string.IsNullOrEmpty(esquemaA) ? null : cands.FirstOrDefault(c => mismoEsquema(c, esquemaA));
            if (porA != null) return porA;

            string def = motorB == TipoMotor.MS_SQL ? "dbo" : motorB == TipoMotor.POSTGRES ? "public" : null;
            var porDef = def == null ? null : cands.FirstOrDefault(c => mismoEsquema(c, def));
            if (porDef != null) return porDef;

            motivo = $"está en varios esquemas de B ({donde}) y no se puede elegir solo";
            return null;
        }

        private static List<ColumnaOrigen> LeerColumnasOrigen(DataBase dbA, string nombreCompleto)
        {
            var res = new List<ColumnaOrigen>();
            using (var cmd = new OdbcCommand("SELECT * FROM " + nombreCompleto + " WHERE 1 = 0", dbA.Connection))
            {
                cmd.CommandTimeout = TransferenciaStreaming.TimeoutSegundos();
                using (var rd = cmd.ExecuteReader())
                    for (int i = 0; i < rd.FieldCount; i++)
                    {
                        Type ty = null; string tn = "";
                        try { ty = rd.GetFieldType(i); } catch { }
                        try { tn = rd.GetDataTypeName(i) ?? ""; } catch { }
                        res.Add(new ColumnaOrigen { Nombre = rd.GetName(i), Clr = ty, TipoNombre = tn });
                    }
            }
            return res;
        }

        private static CategoriaTipo CategoriaDeOrigen(ColumnaOrigen a)
        {
            Type t = a.Clr;
            if (t == null) return CategoriaTipo.Otro;
            if (t == typeof(byte[])) return CategoriaTipo.Binario;
            // Algunos drivers (SQLite) informan los BLOB como texto: el nombre del tipo lo delata
            string tn = (a.TipoNombre ?? "").Trim().ToUpperInvariant();
            if (tn == "BLOB" || tn.Contains("BINARY") || tn == "BYTEA" || tn == "IMAGE") return CategoriaTipo.Binario;
            if (t == typeof(string)) return CategoriaTipo.Texto;
            if (t == typeof(bool)) return CategoriaTipo.Bool;
            if (t == typeof(decimal)) return CategoriaTipo.Decimal;
            if (t == typeof(float) || t == typeof(double)) return CategoriaTipo.Real;
            if (t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) ||
                t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong)) return CategoriaTipo.Entero;
            if (t == typeof(DateTime))
                return string.Equals((a.TipoNombre ?? "").Trim(), "date", StringComparison.OrdinalIgnoreCase)
                    ? CategoriaTipo.Fecha : CategoriaTipo.FechaHora;
            if (t == typeof(DateTimeOffset)) return CategoriaTipo.FechaHoraTz;
            if (t == typeof(TimeSpan)) return CategoriaTipo.Hora;
            if (t == typeof(Guid)) return CategoriaTipo.Guid;
            return CategoriaTipo.Otro;
        }

        private static string GrupoTipo(CategoriaTipo c)
        {
            switch (c)
            {
                case CategoriaTipo.Entero: case CategoriaTipo.Decimal: case CategoriaTipo.Real: case CategoriaTipo.Bool: return "numérico";
                case CategoriaTipo.Fecha: case CategoriaTipo.FechaHora: case CategoriaTipo.FechaHoraTz: return "fecha";
                case CategoriaTipo.Texto: case CategoriaTipo.TextoUnicode: return "texto";
                default: return c.ToString();
            }
        }

        /// <summary>Null si el valor de A suele poder escribirse en la columna de B; si no, una descripción del choque.</summary>
        private static string TipoDudoso(CategoriaTipo a, CategoriaTipo b)
        {
            if (a == CategoriaTipo.Otro || b == CategoriaTipo.Otro) return null;
            string ga = GrupoTipo(a), gb = GrupoTipo(b);
            if (ga == gb) return null;
            if (ga == "texto" && b != CategoriaTipo.Binario) return null;   // el motor convierte el texto
            if (gb == "texto" && a != CategoriaTipo.Binario) return null;   // todo se puede escribir como texto
            return $"un valor de tipo {ga} hacia una columna de tipo {gb}";
        }

        private static void ValidarColumnas(
            List<ColumnaOrigen> colsA, TablaTransfer t, TablaMetadata meta, RegistroIncidencias inc)
        {
            string tabla = t.NombreCompleto;
            string destino = t.Destino.NombreCompleto;
            var nombresA = new HashSet<string>(colsA.Select(c => c.Nombre), StringComparer.OrdinalIgnoreCase);

            foreach (var a in colsA)
            {
                var b = meta.Buscar(a.Nombre);
                if (b == null)
                {
                    inc.Agregar("Estructura", tabla, SeveridadIncidencia.Error, "Columna de A sin equivalente en B",
                        $"La columna {a.Nombre} existe en A pero no en {destino}.",
                        "Si continuás, esa columna NO se copia y sus datos se pierden en B. Agregala en B o abortá.",
                        a.Nombre);
                    continue;
                }
                if (b.Excluida) continue;

                string dudoso = TipoDudoso(CategoriaDeOrigen(a), b.Categoria);
                if (dudoso != null)
                    inc.Agregar("Estructura", tabla, SeveridadIncidencia.Aviso, "Tipos posiblemente incompatibles",
                        $"{a.Nombre}: A es {a.TipoNombre}, B es {b.Tipo}: {dudoso}.",
                        "Si B rechaza los valores, el INSERT falla con la fila afectada en el detalle.",
                        a.Nombre);
            }

            foreach (var b in meta.Columnas)
            {
                if (b.Excluida || b.EsIdentity || b.Nulable || b.TieneDefault) continue;
                if (nombresA.Contains(b.Nombre)) continue;
                inc.Agregar("Estructura", tabla, SeveridadIncidencia.Error, "Columna obligatoria de B sin dato en A",
                    $"{b.Nombre} ({b.Tipo}) de {destino} no admite NULL ni tiene valor por defecto, y A no tiene esa columna.",
                    "El INSERT va a fallar en la primera fila. Agregá la columna en A, o dale un valor por defecto en B.",
                    b.Nombre);
            }
        }

        /// <summary>
        /// Resuelve para cada tabla de A cuál es su equivalente en B (nombre exacto, columnas, tipos) y la valida.
        /// Completa <see cref="TablaTransfer.Destino"/>. Devuelve las tablas que no existen en B (o no se pudieron
        /// leer): quedan registradas como incidencia y no se pueden transferir.
        /// </summary>
        public static List<TablaTransfer> PrepararDestinos(
            string connStrA, string connStrB, TipoMotor motorB, List<TablaTransfer> tablas,
            string esquemaB, RegistroIncidencias inc, Action<string> onProgress)
        {
            var sinDestino = new List<TablaTransfer>();

            DataBase dbB;
            try { dbB = new DataBase(connStrB, false); }
            catch (Exception ex)
            {
                inc.Agregar("Estructura", null, SeveridadIncidencia.Error, "No se pudo leer la estructura de B",
                    "No se pudo conectar a B: " + ex.Message, "Revisá la conexión de destino.");
                sinDestino.AddRange(tablas);
                return sinDestino;
            }

            DataBase dbA = null;
            bool origenLegible = true;
            try
            {
                try { dbA = new DataBase(connStrA); }
                catch (Exception exA)
                {
                    origenLegible = false;
                    inc.Agregar("Estructura", null, SeveridadIncidencia.Aviso, "No se pudieron comparar las columnas de A",
                        "No se pudo conectar a A para leer sus columnas: " + exA.Message,
                        "Se resolvieron las tablas de B pero no se validó que las columnas coincidan.");
                }

                int n = 0;
                foreach (var t in tablas)
                {
                    n++;
                    onProgress?.Invoke($"[B] ({n}/{tablas.Count}) Resolviendo {t.NombreCompleto} en B...");
                    try
                    {
                        var cands = LeerCatalogoTabla(dbB, motorB, t.Nombre);
                        string motivo;
                        var tc = ElegirTablaB(cands, t.Nombre, t.Schema, esquemaB, motorB, out motivo);
                        if (tc == null)
                        {
                            inc.Agregar("Estructura", t.NombreCompleto, SeveridadIncidencia.Error, "Tabla sin equivalente en B",
                                $"{t.NombreCompleto} {motivo}.",
                                "Creá la tabla en B (esta versión no la crea), indicá el esquema de destino, o seguí sin ella: se omite y B conserva su contenido.");
                            sinDestino.Add(t);
                            continue;
                        }

                        var meta = new TablaMetadata();
                        meta.Columnas.AddRange(tc.Columnas);
                        meta.DerivarConjuntos();

                        t.Destino = new TablaTransfer
                        {
                            Schema        = motorB == TipoMotor.SQLite ? "" : tc.Schema,
                            Nombre        = tc.Nombre,
                            ComillasMotor = motorB,
                            Meta          = meta
                        };
                        onProgress?.Invoke($"  A {t.NombreCompleto}  ->  B {t.Destino.NombreCompleto} ({tc.Columnas.Count} columna(s))");

                        if (origenLegible)
                        {
                            try { ValidarColumnas(LeerColumnasOrigen(dbA, t.NombreCompleto), t, meta, inc); }
                            catch (Exception exCols)
                            {
                                inc.Agregar("Estructura", t.NombreCompleto, SeveridadIncidencia.Aviso,
                                    "No se pudieron comparar las columnas de A", exCols.Message,
                                    "Se leerá de A igual; si la tabla no se puede leer, la prevalidación del origen lo informa.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        inc.Agregar("Estructura", t.NombreCompleto, SeveridadIncidencia.Error,
                            "No se pudo leer la estructura de la tabla en B", ex.Message,
                            "Revisá permisos de lectura del catálogo en B. Si seguís, la tabla se omite.");
                        sinDestino.Add(t);
                    }
                }
            }
            finally
            {
                try { dbB.CloseConnection(); } catch { }
                try { dbA?.CloseConnection(); } catch { }
            }
            return sinDestino;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Prevalidación del origen (A)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Cuenta las filas de cada tabla de A. Sirve para verificar que se puedan leer antes de
        /// tocar B y para dimensionar la barra de progreso. Las tablas que fallan quedan fuera del
        /// diccionario devuelto y registradas como incidencia.
        /// </summary>
        public static Dictionary<string, long> PrevalidarOrigen(
            string connStrA, List<TablaTransfer> tablas, RegistroIncidencias incidencias,
            Action<string> onProgress)
        {
            var conteos = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var t in tablas)
            {
                n++;
                onProgress?.Invoke($"[A] ({n}/{tablas.Count}) Verificando {t.NombreCompleto}...");
                try { conteos[t.NombreCompleto] = ContarFilas(connStrA, t.NombreCompleto); }
                catch (Exception ex)
                {
                    incidencias.Agregar("Origen", t.NombreCompleto, SeveridadIncidencia.Error,
                        "Tabla ilegible en A", ex.Message,
                        "Omitir la tabla (no se vaciará ni se cargará en B) o abortar y revisar que exista y que haya permisos en A.");
                }
            }
            return conteos;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Backup de B — en streaming: una sola fila en memoria, directo a disco
        // ─────────────────────────────────────────────────────────────────────

        public class ResultadoBackup
        {
            public bool Completo { get; set; } = true;
            public int  Tablas   { get; set; }
            public long Filas    { get; set; }
            public int  TablasConIncidencias { get; set; }
        }

        private static StreamWriter NuevoWriter(string ruta, bool conBom = true)
        {
            return new StreamWriter(ruta, false, new UTF8Encoding(conBom), 65536);
        }

        internal static string NombreArchivoTabla(string nombreCompleto)
        {
            return nombreCompleto.Replace(".", "_").Replace(" ", "_");
        }

        /// <summary>
        /// Genera el backup del estado actual de B. Si <paramref name="rutaArchivo"/> no es null escribe
        /// un único archivo; si <paramref name="carpetaPorTabla"/> no es null, un archivo por tabla.
        /// Nunca traga una falla: todo lo que no se pudo respaldar queda como incidencia y el
        /// resultado queda marcado como incompleto para que se decida antes de vaciar B.
        /// </summary>
        public static ResultadoBackup GenerarBackup(
            string connStrB, List<TablaTransfer> ordenDelete, TipoMotor motorB,
            string rutaArchivo, string carpetaPorTabla,
            OpcionesPasado opciones, RegistroIncidencias incidencias, Action<string> onProgress)
        {
            var res = new ResultadoBackup();
            int inicio = incidencias.Count;
            StreamWriter unico = null;
            try
            {
                if (!string.IsNullOrEmpty(rutaArchivo))
                {
                    unico = NuevoWriter(rutaArchivo);
                    unico.WriteLine("-- ╔═══════════════════════════════════════════════════╗");
                    unico.WriteLine("-- ║  BACKUP DE BASE DE DATOS B (estado previo)        ║");
                    unico.WriteLine($"-- ║  Generado: {DateTime.Now:yyyy-MM-dd HH:mm:ss}               ║");
                    unico.WriteLine("-- ╚═══════════════════════════════════════════════════╝");
                    unico.WriteLine();
                }

                int n = 0;
                foreach (var tabla in ordenDelete)
                {
                    n++;
                    onProgress?.Invoke($"[Backup] ({n}/{ordenDelete.Count}) Leyendo {tabla.NombreCompleto}...");
                    StreamWriter propio = null;
                    int antes = incidencias.Count;
                    try
                    {
                        TextWriter w = unico;
                        if (!string.IsNullOrEmpty(carpetaPorTabla))
                        {
                            propio = NuevoWriter(Path.Combine(carpetaPorTabla,
                                NombreArchivoTabla(tabla.NombreCompleto) + "_backup.sql"));
                            propio.WriteLine($"-- BACKUP {tabla.NombreCompleto} — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                            w = propio;
                        }
                        res.Filas += BackupTabla(w, connStrB, tabla, motorB, opciones, incidencias, onProgress);
                        res.Tablas++;
                    }
                    catch (IOException ioEx)
                    {
                        // Disco lleno, archivo bloqueado, ruta inválida: no tiene sentido seguir
                        incidencias.Agregar("Backup", tabla.NombreCompleto, SeveridadIncidencia.Critica,
                            "Error de escritura del backup", ioEx.Message,
                            "Liberar espacio o elegir otra ubicación y repetir. No se vació nada de B.");
                        res.Completo = false;
                        break;
                    }
                    catch (Exception ex)
                    {
                        res.Completo = false;
                        incidencias.Agregar("Backup", tabla.NombreCompleto, SeveridadIncidencia.Error,
                            ex is OutOfMemoryException ? "Memoria insuficiente al respaldar" : "No se pudo respaldar la tabla",
                            ex.Message,
                            TransferenciaStreaming.Sugerir(ex) + " La sección de esta tabla quedó incompleta en el archivo: no la restaures.");
                        try { (propio ?? unico)?.WriteLine("-- TABLA INCOMPLETA (no restaurar esta sección): " + ex.Message); } catch { }
                    }
                    finally { try { propio?.Dispose(); } catch { } }

                    if (incidencias.Count > antes) res.TablasConIncidencias++;
                }

                if (incidencias.Hay(SeveridadIncidencia.Error, inicio)) res.Completo = false;

                if (unico != null)
                {
                    try
                    {
                        unico.WriteLine(res.Completo
                            ? $"-- FIN BACKUP: {res.Tablas} tablas, {res.Filas} filas"
                            : $"-- BACKUP INCOMPLETO: {res.Tablas} de {ordenDelete.Count} tablas, {res.Filas} filas, " +
                              $"{res.TablasConIncidencias} tabla(s) con incidencias (ver incidencias_*.csv)");
                    }
                    catch { }
                }
            }
            finally { try { unico?.Dispose(); } catch { } }

            return res;
        }

        private static long BackupTabla(
            TextWriter w, string connStr, TablaTransfer tabla, TipoMotor motor,
            OpcionesPasado op, RegistroIncidencias inc, Action<string> onProgress)
        {
            long esperadas = -1;
            try { esperadas = ContarFilas(connStr, tabla.NombreCompleto); }
            catch (Exception exC)
            {
                inc.Agregar("Backup", tabla.NombreCompleto, SeveridadIncidencia.Aviso, "Sin conteo previo",
                    "No se pudo contar las filas antes del backup: " + exC.Message,
                    "No se podrá verificar que el backup tenga todas las filas.");
            }

            w.WriteLine($"-- ═══ {tabla.NombreCompleto}  ({(esperadas >= 0 ? esperadas.ToString() : "?")} filas) ═══");

            long filas, leidas;
            using (var lector = LectorTabla.Abrir(connStr, tabla, null, op.UmbralLobBytes))
            {
                filas = EscribirFilasScript(w, lector, tabla.NombreCompleto, motor, false, op, inc, "Backup",
                    n => onProgress?.Invoke($"[Backup]   {tabla.NombreCompleto}: {n} filas..."));
                leidas = lector.NumFila;
            }

            w.WriteLine($"-- FIN {tabla.NombreCompleto}: {filas} filas");
            w.WriteLine();

            if (esperadas >= 0 && leidas != esperadas)
                inc.Agregar("Backup", tabla.NombreCompleto, SeveridadIncidencia.Error,
                    "Conteo del backup distinto al de B",
                    $"B tenía {esperadas} filas al contar y se leyeron {leidas}.",
                    "Si otro usuario modifica B durante el backup, repetilo. Si no, no continúes con el vaciado.");
            return filas;
        }

        /// <summary>
        /// Escribe los INSERT de todas las filas del lector. Las filas con un binario sobre el umbral
        /// no se incluyen y quedan registradas (el script queda incompleto y se decide aparte).
        /// </summary>
        private static long EscribirFilasScript(
            TextWriter w, LectorTabla lector, string nombreTabla, TipoMotor motor, bool overriding,
            OpcionesPasado op, RegistroIncidencias inc, string fase, Action<long> onFilas,
            MapaInsert mapa = null)
        {
            string cols = mapa != null ? mapa.ColsSql : string.Join(", ", lector.Columnas);
            string[] nombresCols = mapa != null ? mapa.Nombres : lector.Columnas;
            var vals = new object[lector.Columnas.Length];
            long filas = 0;
            var grandes = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase); // col → [cantidad, máximo]

            while (true)
            {
                bool hay;
                try { hay = lector.LeerFila(vals); }
                catch (LobExcedeUmbralException lx)
                {
                    inc.Agregar(fase, nombreTabla, SeveridadIncidencia.Error, "Fila con binario sobre el umbral",
                        $"El valor supera {TransferenciaStreaming.Mb(op.UmbralLobBytes)} (se leyeron al menos {TransferenciaStreaming.Mb(lx.Bytes)}) y la fila no se incluyó en el script.",
                        "Esa fila NO está en el script/backup. Respaldala o transferila por otro medio antes de continuar.",
                        lx.Columna, lx.Clave);
                    continue;
                }
                if (!hay) break;

                object[] fila = vals;
                if (mapa != null)
                {
                    fila = mapa.Filtrar(vals);
                    ScriptHelper.EscribirInsert(w, nombreTabla, cols, fila, overriding, motor, mapa.Destino, mapa.TiposA);
                }
                else ScriptHelper.EscribirInsert(w, nombreTabla, cols, vals, overriding, motor);
                filas++;

                if (motor == TipoMotor.DB2)
                    for (int k = 0; k < fila.Length; k++)
                    {
                        var b = fila[k] as byte[];
                        if (b == null || b.Length <= TransferenciaStreaming.LimiteLiteralDb2) continue;
                        long[] acc;
                        if (!grandes.TryGetValue(nombresCols[k], out acc)) grandes[nombresCols[k]] = acc = new long[2];
                        acc[0]++;
                        if (b.Length > acc[1]) acc[1] = b.Length;
                    }

                if (onFilas != null && filas % 50000 == 0) onFilas(filas);
            }

            foreach (var kv in grandes)
                inc.Agregar(fase, nombreTabla, SeveridadIncidencia.Aviso, "Literales binarios muy grandes para DB2",
                    $"{kv.Value[0]} valor(es) de la columna {kv.Key} superan ~16 KB (máximo {TransferenciaStreaming.Mb(kv.Value[1])}).",
                    "DB2 limita el tamaño de los literales hexadecimales (verificar en tu versión): ese script podría no ejecutarse. La transferencia directa A→B usa parámetros y no tiene este límite.",
                    kv.Key);

            return filas;
        }

        // ─────────────────────────────────────────────────────────────────────
        // "Backup de esquema" (script restaurable de las tablas elegidas), en streaming
        // ─────────────────────────────────────────────────────────────────────

        public class ResultadoBackupEsquema
        {
            public int  Tablas   { get; set; }
            public int  Errores  { get; set; }
            public long Filas    { get; set; }
            public bool Completo { get; set; } = true;
        }

        /// <summary>
        /// Escribe en <paramref name="rutaDestino"/> un script que deja la base como estaba: FASE 1 (FK),
        /// transacción con DELETE + INSERT, FASE 3 (FK). Mismo formato que reconoce el restaurador,
        /// pero escrito fila a fila. Una tabla que no se pudo leer queda fuera del script <b>y de sus
        /// DELETE</b>, para que restaurar nunca vacíe una tabla que el backup no puede devolver.
        /// </summary>
        public static ResultadoBackupEsquema GenerarBackupEsquema(
            string connStr, TipoMotor motor, string nombreConexion, string rutaDestino,
            List<TablaTransfer> ordenInsert, List<TablaTransfer> ordenDelete, List<string> ciclicas,
            OpcionesPasado op, RegistroIncidencias inc, Action<string> onProgress)
        {
            var res = new ResultadoBackupEsquema();
            int inicio = inc.Count;

            onProgress?.Invoke("Leyendo metadata de las tablas...");
            var metadata = ObtenerMetadataTablas(connStr, motor, ordenInsert, inc);

            // Conteo previo: arma el índice del encabezado y detecta tablas ilegibles antes de escribir
            var conteos  = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var fallidas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var estados  = new List<Tuple<string, string>>();
            int k = 0;
            foreach (var t in ordenInsert)
            {
                k++;
                onProgress?.Invoke($"[Backup] ({k}/{ordenInsert.Count}) Contando {t.NombreCompleto}...");
                try
                {
                    long c = ContarFilas(connStr, t.NombreCompleto);
                    conteos[t.NombreCompleto] = c;
                    estados.Add(Tuple.Create(t.NombreCompleto, $"{c} filas"));
                    res.Filas += c;
                }
                catch (Exception ex)
                {
                    fallidas.Add(t.NombreCompleto);
                    res.Errores++;
                    estados.Add(Tuple.Create(t.NombreCompleto, "ERROR → " + ex.Message));
                    inc.Agregar("Backup", t.NombreCompleto, SeveridadIncidencia.Error, "Tabla ilegible",
                        ex.Message,
                        "La tabla quedó fuera del script y de sus DELETE: restaurar no la toca. Revisá que exista y los permisos.");
                }
            }

            var ordenInsertOk = ordenInsert.Where(t => !fallidas.Contains(t.NombreCompleto)).ToList();
            var ordenDeleteOk = ordenDelete.Where(t => !fallidas.Contains(t.NombreCompleto)).ToList();
            res.Filas = 0;   // se recalcula con lo realmente escrito

            using (var w = NuevoWriter(rutaDestino))
            {
                w.WriteLine("-- ══════════════════════════════════════════════════════════");
                w.WriteLine("--  BACKUP DE ESQUEMA — script restaurable");
                w.WriteLine("-- ══════════════════════════════════════════════════════════");
                w.WriteLine($"--  Conexión : {nombreConexion}");
                w.WriteLine($"--  Generado : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                w.WriteLine($"--  Tablas   : {ordenInsert.Count} ({res.Errores} con error) — {conteos.Values.Sum()} filas");
                w.WriteLine("--");
                w.WriteLine("--  Ejecutar este script contra la MISMA base la deja tal como");
                w.WriteLine("--  estaba: vacía las tablas y reinserta todo el contenido.");
                w.WriteLine("-- ──────────────────────────────────────────────────────────");
                w.WriteLine("--  Orden de restauración (padres primero):");
                for (int i = 0; i < estados.Count; i++)
                    w.WriteLine($"--    {i + 1,3}. {estados[i].Item1}  ({estados[i].Item2})");
                if (ciclicas != null && ciclicas.Count > 0)
                {
                    w.WriteLine("--");
                    w.WriteLine("--  Estas tablas forman un ciclo de FK entre sí, así que no existe un");
                    w.WriteLine("--  orden válido entre ellas y quedaron al final. NO afecta a este");
                    w.WriteLine("--  backup: el script deshabilita las FK antes de los DELETE/INSERT.");
                    w.WriteLine("--  Solo importa si ejecutás el script por partes o sin la FASE 1.");
                    foreach (string c in ciclicas)
                        w.WriteLine($"--    • {c}");
                }
                w.WriteLine("-- ══════════════════════════════════════════════════════════");
                w.WriteLine();

                var fase1 = new StringBuilder();
                AgregarScriptFaseDeshabilitarFKs(fase1, motor, ordenDeleteOk);
                w.Write(fase1.ToString());

                w.WriteLine();
                w.WriteLine("-- ═══ FASE 2: DML ═══");
                w.WriteLine("BEGIN TRAN");
                w.WriteLine();
                w.WriteLine("  -- DELETE en orden reverso FK (hijas primero)");
                foreach (var t in ordenDeleteOk)
                    w.WriteLine($"  DELETE FROM {t.NombreCompleto};");

                w.WriteLine();
                w.WriteLine("  -- INSERT en orden FK (padres primero)");
                k = 0;
                foreach (var t in ordenInsertOk)
                {
                    k++;
                    string nombre = t.NombreCompleto;
                    long esperadas = conteos[nombre];
                    if (esperadas == 0) { w.WriteLine($"  -- {nombre}: sin filas."); res.Tablas++; continue; }

                    onProgress?.Invoke($"[Backup] ({k}/{ordenInsertOk.Count}) {nombre} ({esperadas} filas)...");
                    metadata.TryGetValue(nombre, out TablaMetadata meta);
                    bool overriding = (motor == TipoMotor.POSTGRES || motor == TipoMotor.DB2) && meta?.TieneIdentityAlways == true;
                    bool identityInsert = motor == TipoMotor.MS_SQL && meta?.TieneIdentity == true;

                    w.WriteLine();
                    w.WriteLine($"  -- {nombre} ({esperadas} filas)");
                    if (identityInsert) w.WriteLine($"  SET IDENTITY_INSERT {nombre} ON;");
                    try
                    {
                        long leidas;
                        using (var lector = LectorTabla.Abrir(connStr, t, meta?.ColumnasExcluidas, op.UmbralLobBytes))
                        {
                            res.Filas += EscribirFilasScript(w, lector, nombre, motor, overriding, op, inc, "Backup",
                                n => onProgress?.Invoke($"[Backup]   {nombre}: {n} filas..."));
                            leidas = lector.NumFila;
                        }
                        res.Tablas++;
                        if (leidas != esperadas)
                            inc.Agregar("Backup", nombre, SeveridadIncidencia.Error, "Conteo del backup distinto al de la base",
                                $"Al contar había {esperadas} filas y se leyeron {leidas}.",
                                "Si otro usuario modificó la tabla durante el backup, repetilo.");
                    }
                    catch (IOException) { throw; }
                    catch (Exception ex)
                    {
                        res.Errores++;
                        w.WriteLine($"-- TABLA INCOMPLETA (no restaurar este script): {ex.Message}");
                        inc.Agregar("Backup", nombre, SeveridadIncidencia.Error,
                            ex is OutOfMemoryException ? "Memoria insuficiente al respaldar" : "No se pudo respaldar la tabla",
                            ex.Message, TransferenciaStreaming.Sugerir(ex) + " La tabla quedó incompleta en el script: no lo restaures tal cual.");
                    }
                    if (identityInsert) w.WriteLine($"  SET IDENTITY_INSERT {nombre} OFF;");
                }

                w.WriteLine();
                w.WriteLine("COMMIT");
                w.WriteLine();
                var fase3 = new StringBuilder();
                AgregarScriptFaseRehabilitarFKs(fase3, motor, ordenInsertOk);
                w.Write(fase3.ToString());
            }

            res.Completo = !inc.Hay(SeveridadIncidencia.Error, inicio);
            return res;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Transferencia principal (streaming: una fila en memoria por vez)
        // ─────────────────────────────────────────────────────────────────────

        public static ResultadoPasado TransferirDatos(
            string connStrA,
            string connStrB,
            TipoMotor motorA,
            TipoMotor motorB,
            List<TablaTransfer> ordenInsert,
            List<TablaTransfer> ordenDelete,
            OpcionesPasado opciones,
            RegistroIncidencias incidencias,
            Action<string> onProgress,
            Action<string> onSql = null,
            Action<int, int> onStep = null)
        {
            var resultado = new ResultadoPasado { Incidencias = incidencias };

            // Con motores distintos cada tabla de A tiene su equivalente en B (Destino); con el mismo motor B == A.
            // Todo lo que se ejecuta en B (DELETE, INSERT, FK, metadata) usa estas listas con los nombres de B.
            var ordenInsertB = ordenInsert.Select(t => t.B).ToList();
            var ordenDeleteB = ordenDelete.Select(t => t.B).ToList();

            // 1. Metadata de B (identity / columnas no insertables)
            onProgress?.Invoke("Obteniendo metadata de columnas de B...");
            var metadata = ObtenerMetadataTablas(connStrB, motorB, ordenInsertB, incidencias);

            // 2. Modo "solo script": se escribe a disco, tabla por tabla
            if (opciones.GenerarSoloScript)
            {
                GenerarScriptEnArchivos(connStrA, connStrB, motorB, ordenInsert, ordenDelete, metadata,
                    opciones, incidencias, resultado, onProgress, onStep);
                resultado.Exito = !incidencias.Hay(SeveridadIncidencia.Error);
                if (!resultado.Exito) resultado.Error = "El script quedó incompleto: revisá las incidencias.";
                return resultado;
            }

            // 3. Total para la barra de progreso (filas de A + un paso por cada DELETE)
            long totalFilas = ordenDelete.Count;
            if (opciones.ConteosA != null)
                foreach (var t in ordenInsert)
                {
                    long c;
                    if (opciones.ConteosA.TryGetValue(t.NombreCompleto, out c)) totalFilas += c;
                }
            var avance = new TransferenciaStreaming.Avance(totalFilas, onStep, onSql);

            // 4. Ejecutar en B
            onProgress?.Invoke("Conectando a la base B...");
            DataBase dbB = null;
            OdbcTransaction tx = null;
            bool commitOk = false;
            bool modificoDatos = false;
            string tablaActual = null;
            string faseActual = "Transferencia";
            int tablasCargadas = 0;
            // FKs de tablas fuera de la selección que apuntan a las que se vacían (MS_SQL): se apagan y se vuelven a prender
            var fkExternas = new List<FkExterna>();

            // Metadata de B que cambia el INSERT: sirve para entender por qué una tabla se comporta distinto
            foreach (var kv in metadata)
            {
                var m = kv.Value;
                if (!m.TieneIdentity && m.ColumnasExcluidas.Count == 0) continue;
                onProgress?.Invoke($"[Metadata B] {kv.Key}: identity=[{string.Join(", ", m.ColumnasIdentity)}] " +
                                   $"identityAlways=[{string.Join(", ", m.ColumnasIdentityAlways)}] " +
                                   $"excluidas=[{string.Join(", ", m.ColumnasExcluidas)}]");
            }

            try
            {
                dbB = new DataBase(connStrB);

                // FASE 1: Deshabilitar FKs (fuera de tx)
                faseActual = "FK";
                onProgress?.Invoke("Deshabilitando FK constraints en B...");
                EjecutarFaseDeshabilitarFKs(dbB.Connection, motorB, ordenDeleteB, onProgress, avance.Sql, fkExternas);

                // FASE 2: Transacción única (si está activada)
                faseActual = "Transferencia";
                if (opciones.Transaccional)
                {
                    onProgress?.Invoke("Iniciando transacción...");
                    tx = dbB.Connection.BeginTransaction();
                }
                else
                {
                    incidencias.Agregar("Transferencia", null, SeveridadIncidencia.Aviso, "Sin transacción",
                        "La opción Transaccional está desactivada: cada sentencia se confirma sola.",
                        "Si algo falla, B queda con una parte de los datos. Usá el backup para volver atrás.");
                }

                // DELETEs
                onProgress?.Invoke("Ejecutando DELETEs en B...");
                foreach (var tabla in ordenDelete)
                {
                    tablaActual = tabla.NombreCompleto;
                    string sql = $"DELETE FROM {tabla.B.NombreCompleto}";
                    avance.Sql(sql);
                    onProgress?.Invoke($"[DELETE] {tabla.B.NombreCompleto}");
                    modificoDatos = true;
                    EjecutarDml(dbB.Connection, tx, sql);
                    avance.Sumar(1);
                }

                // INSERTs, en streaming desde A
                onProgress?.Invoke("Ejecutando INSERTs en B...");
                foreach (var tabla in ordenInsert)
                {
                    tablaActual = tabla.NombreCompleto;

                    long conteo = -1;
                    if (opciones.ConteosA != null && !opciones.ConteosA.TryGetValue(tabla.NombreCompleto, out conteo))
                        conteo = -1;
                    if (conteo == 0)
                    {
                        onProgress?.Invoke($"[INSERT] {tabla.NombreCompleto} — sin datos, omitida.");
                        continue;
                    }

                    metadata.TryGetValue(tabla.B.NombreCompleto, out TablaMetadata meta);
                    onProgress?.Invoke(conteo >= 0
                        ? $"[INSERT] {tabla.NombreCompleto} ({conteo} filas)..."
                        : $"[INSERT] {tabla.NombreCompleto}...");

                    resultado.FilasTransferidas += InsertarTabla(
                        dbB.Connection, tx, connStrA, tabla, meta, motorB, opciones, incidencias, avance,
                        conteo, onProgress);
                    tablasCargadas++;
                }

                onProgress?.Invoke($"Resumen de la carga: {tablasCargadas} tabla(s) cargada(s) de {ordenInsert.Count}, " +
                                   $"{resultado.FilasTransferidas} fila(s) insertada(s) en B.");

                tx?.Commit();
                commitOk = true;
                avance.Forzar();
                onProgress?.Invoke(opciones.Transaccional
                    ? "✓ Transacción completada (COMMIT)."
                    : "✓ Carga completada.");
            }
            catch (Exception ex)
            {
                RegistrarFalla(incidencias, ex, tablaActual, faseActual);
                // Excepción completa (tipo, pila y causas internas) para diagnosticar sin reproducir
                onProgress?.Invoke($"DETALLE TÉCNICO (fase {faseActual}, tabla {tablaActual ?? "-"}): {ex}");
                resultado.Error = (ex is ErrorTransferencia && ex.InnerException != null) ? ex.InnerException.Message : ex.Message;

                if (tx != null)
                {
                    try
                    {
                        tx.Rollback();
                        onProgress?.Invoke("✗ Se realizó ROLLBACK. B queda intacta.");
                    }
                    catch (Exception exRb)
                    {
                        incidencias.Agregar("Transferencia", tablaActual, SeveridadIncidencia.Critica, "Falló el ROLLBACK",
                            exRb.Message, "B puede haber quedado con datos parciales. Restaurá el backup de B antes de seguir.");
                    }
                }
                else if (modificoDatos)
                {
                    incidencias.Agregar("Transferencia", tablaActual, SeveridadIncidencia.Critica,
                        "B quedó parcialmente modificada",
                        "La carga se interrumpió sin transacción: hay tablas vaciadas o cargadas a medias.",
                        "Restaurá el backup de B o repetí la transferencia completa.");
                }
            }
            finally
            {
                // FASE 3: Rehabilitar FKs — siempre, haya commit o rollback
                if (dbB != null)
                {
                    try
                    {
                        onProgress?.Invoke("Rehabilitando FK constraints en B...");
                        EjecutarFaseRehabilitarFKs(dbB.Connection, motorB, ordenInsertB, onProgress, null, fkExternas, incidencias);
                    }
                    catch (Exception exFk)
                    {
                        incidencias.Agregar("FK", null, SeveridadIncidencia.Error, "No se pudieron rehabilitar las FK",
                            exFk.Message,
                            "Las FK de B pueden haber quedado deshabilitadas: ejecutá la FASE 3 manualmente (SET INTEGRITY ... IMMEDIATE CHECKED / WITH CHECK CHECK CONSTRAINT).");
                    }
                    try { dbB.CloseConnection(); } catch { }
                }
            }

            resultado.Exito = commitOk;

            // 5. Verificación de consistencia A vs B
            if (commitOk)
            {
                onProgress?.Invoke("Verificando consistencia entre A y B...");
                VerificarConsistencia(connStrA, connStrB, ordenInsert, incidencias, onProgress);
            }

            return resultado;
        }

        /// <summary>Copia una tabla de A a B leyendo fila por fila y ejecutando un INSERT por fila.</summary>
        private static long InsertarTabla(
            OdbcConnection connB, OdbcTransaction tx, string connStrA, TablaTransfer tabla,
            TablaMetadata meta, TipoMotor motorB, OpcionesPasado op, RegistroIncidencias inc,
            TransferenciaStreaming.Avance avance, long conteoEsperado, Action<string> onProgress)
        {
            string nombreB = tabla.B.NombreCompleto;

            // Mismo motor: lo que dice la metadata de B. Con motores distintos se recalcula al conocer las columnas que se copian.
            bool overriding = (motorB == TipoMotor.POSTGRES || motorB == TipoMotor.DB2)
                              && meta?.TieneIdentityAlways == true;
            bool identityInsert = motorB == TipoMotor.MS_SQL && meta?.TieneIdentity == true;
            if (tabla.Destino != null) { overriding = false; identityInsert = false; }

            long insertadas = 0, leidas = 0, sinFilasAfectadas = 0, omitidasLob = 0;
            var reloj = Stopwatch.StartNew();
            using (var lector = LectorTabla.Abrir(connStrA, tabla, meta?.ColumnasExcluidas, op.UmbralLobBytes))
            {
                // Motores distintos: solo se copian las columnas que existen en B, con su nombre exacto y su tipo
                MapaInsert mapa = (tabla.Destino != null && meta != null && meta.Columnas.Count > 0)
                    ? MapaInsert.Crear(lector, meta, motorB) : null;
                if (mapa != null)
                {
                    overriding     = (motorB == TipoMotor.POSTGRES || motorB == TipoMotor.DB2) && mapa.CopiaIdentityAlways;
                    identityInsert = motorB == TipoMotor.MS_SQL && mapa.CopiaIdentity;
                }
                if (identityInsert)
                    EjecutarDml(connB, tx, $"SET IDENTITY_INSERT {nombreB} ON");

                string cols = mapa != null ? mapa.ColsSql : string.Join(", ", lector.Columnas);
                onProgress?.Invoke($"[INSERT]   {tabla.NombreCompleto}: {(mapa != null ? mapa.Indices.Length : lector.Columnas.Length)} columna(s) a copiar: {TransferenciaStreaming.Truncar(cols, 400)}");
                var vals = new object[lector.Columnas.Length];
                var sb = new StringBuilder(1024);

                while (true)
                {
                    bool hay;
                    try { hay = lector.LeerFila(vals); }
                    catch (LobExcedeUmbralException lx)
                    {
                        if (!op.OmitirFilasLobGrandes) throw;

                        inc.Agregar("Transferencia", tabla.NombreCompleto, SeveridadIncidencia.Error,
                            "Fila omitida: binario sobre el umbral",
                            $"El valor supera {TransferenciaStreaming.Mb(op.UmbralLobBytes)} y la fila no se copió a B.",
                            "La tabla quedó incompleta en B. Transferí esa fila por otro medio.",
                            lx.Columna, lx.Clave);
                        avance.Sumar(1);
                        omitidasLob++;
                        continue;
                    }
                    if (!hay) break;

                    var pars = new List<OdbcParameter>();
                    string sql = null;

                    try
                    {
                        // Armar el literal también puede fallar (p. ej. un NaN hacia un motor sin ese valor): se informa con la fila
                        sql = mapa != null
                            ? ConstruirInsert(sb, nombreB, cols, mapa.Filtrar(vals), overriding, motorB, pars, mapa.Destino, mapa.TiposA)
                            : ConstruirInsert(sb, nombreB, cols, vals, overriding, motorB, pars);
                        avance.Sql(sql);

                        // 0 = el motor no insertó nada; -1 = el driver no informa (no cuenta como falla)
                        if (EjecutarDml(connB, tx, sql, null, pars) == 0) sinFilasAfectadas++;
                    }
                    catch (Exception ex)
                    {
                        throw new ErrorTransferencia(ex.Message, ex)
                        {
                            Tabla = tabla.NombreCompleto,
                            Clave = TransferenciaStreaming.ClaveFila(lector.Columnas, vals, vals.Length, lector.NumFila),
                            Sql   = TransferenciaStreaming.Truncar(sql, 300)
                        };
                    }

                    insertadas++;
                    avance.Sumar(1);
                }
                leidas = lector.NumFila;
            }

            if (identityInsert)
                EjecutarDml(connB, tx, $"SET IDENTITY_INSERT {nombreB} OFF");

            onProgress?.Invoke($"[INSERT]   {tabla.NombreCompleto}: leídas de A={leidas} (esperadas={(conteoEsperado >= 0 ? conteoEsperado.ToString() : "?")}), " +
                               $"insertadas en B={insertadas}, omitidas por binario grande={omitidasLob}, " +
                               $"insert que afectó 0 filas={sinFilasAfectadas}, {reloj.ElapsedMilliseconds} ms.");

            if (conteoEsperado > 0 && leidas == 0)
                inc.Agregar("Transferencia", tabla.NombreCompleto, SeveridadIncidencia.Error, "No se leyó ninguna fila de A",
                    $"El conteo previo decía {conteoEsperado} fila(s) pero el SELECT * no devolvió ninguna.",
                    "Revisá permisos de lectura / filtros de seguridad de fila en A, o que el usuario de la conexión A sea el mismo que contó las filas.");
            else if (conteoEsperado >= 0 && leidas != conteoEsperado)
                inc.Agregar("Transferencia", tabla.NombreCompleto, SeveridadIncidencia.Aviso, "Filas leídas distinto del conteo previo",
                    $"Esperadas {conteoEsperado}, leídas {leidas}.",
                    "A cambió durante la transferencia o el driver cortó la lectura: repetir y comparar.");

            if (sinFilasAfectadas > 0)
                inc.Agregar("Transferencia", tabla.NombreCompleto, SeveridadIncidencia.Aviso, "INSERT sin filas afectadas",
                    $"{sinFilasAfectadas} INSERT(s) terminaron sin error pero el motor informó 0 filas afectadas.",
                    "Revisar triggers INSTEAD OF / reglas en B que descarten la fila.");

            return insertadas;
        }

        private static string ConstruirInsert(
            StringBuilder sb, string nombreTabla, string cols, object[] vals,
            bool overriding, TipoMotor motor, List<OdbcParameter> pars,
            ColumnaDestino[] destino = null, string[] tiposA = null)
        {
            sb.Clear();
            sb.Append("INSERT INTO ").Append(nombreTabla).Append(" (").Append(cols).Append(')');
            if (overriding) sb.Append(" OVERRIDING SYSTEM VALUE");
            sb.Append(" VALUES (");
            for (int i = 0; i < vals.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                var b = vals[i] as byte[];
                if (b != null && b.Length > TransferenciaStreaming.LimiteLiteralBinario)
                {
                    // Binarios grandes: por parámetro. Un literal hexadecimal de ese tamaño
                    // se duplica en memoria varias veces y los motores lo rechazan.
                    sb.Append('?');
                    pars.Add(new OdbcParameter
                    {
                        OdbcType = b.Length > TransferenciaStreaming.LimiteVarBinario ? OdbcType.Image : OdbcType.VarBinary,
                        Value = b,
                        Size = b.Length
                    });
                }
                else if (destino != null) sb.Append(ScriptHelper.EscaparValorSql(vals[i], motor, destino[i], tiposA != null ? tiposA[i] : null));
                else sb.Append(ScriptHelper.EscaparValorSql(vals[i], motor));
            }
            sb.Append(')');
            return sb.ToString();
        }

        /// <summary>Convierte una excepción en una incidencia con el contexto disponible.</summary>
        private static void RegistrarFalla(RegistroIncidencias inc, Exception ex, string tablaActual, string fase)
        {
            var et = ex as ErrorTransferencia;
            var lx = ex as LobExcedeUmbralException;
            Exception raiz = et?.InnerException ?? ex;

            string tipo = lx != null ? "Binario sobre el umbral"
                        : raiz is OutOfMemoryException ? "Memoria insuficiente"
                        : et != null ? "Fila rechazada por B"
                        : "Falla en " + fase;

            string mensaje = raiz.Message;
            if (et != null && !string.IsNullOrEmpty(et.Sql)) mensaje += " | SQL: " + et.Sql;

            string accion = lx != null
                ? "La transferencia se abortó y B quedó intacta. Activá \"Omitir filas con binarios grandes\" (la tabla quedará incompleta) o transferí esa fila por otro medio."
                : TransferenciaStreaming.Sugerir(raiz);

            inc.Agregar(fase, et?.Tabla ?? tablaActual, SeveridadIncidencia.Error, tipo, mensaje, accion,
                lx?.Columna, et?.Clave ?? lx?.Clave);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Script de transferencia en archivos (modo "solo script")
        // ─────────────────────────────────────────────────────────────────────

        private static void GenerarScriptEnArchivos(
            string connStrA, string connStrB, TipoMotor motorB,
            List<TablaTransfer> ordenInsert, List<TablaTransfer> ordenDelete,
            Dictionary<string, TablaMetadata> metadata, OpcionesPasado op, RegistroIncidencias inc,
            ResultadoPasado resultado, Action<string> onProgress, Action<int, int> onStep)
        {
            string carpeta = op.CarpetaScript;
            Directory.CreateDirectory(carpeta);
            resultado.CarpetaScript = carpeta;

            var pre = new StringBuilder();
            pre.AppendLine($"-- ═══ SCRIPT DE TRANSFERENCIA — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══");
            pre.AppendLine($"-- Origen: {TransferenciaStreaming.EnmascararConexion(connStrA)}");
            pre.AppendLine($"-- Destino: {TransferenciaStreaming.EnmascararConexion(connStrB)}");
            pre.AppendLine();
            // El script se escribe en el dialecto de B: nombres de B, FK de B, transacción de B
            AgregarScriptFaseDeshabilitarFKs(pre, motorB, ordenDelete.Select(t => t.B).ToList());
            pre.AppendLine();
            pre.AppendLine("-- ═══ FASE 2: DML ═══");
            pre.AppendLine(SqlIniciarTransaccion(motorB));
            pre.AppendLine();
            pre.AppendLine("  -- DELETE en orden reverso FK");
            foreach (var tabla in ordenDelete)
                pre.AppendLine($"  DELETE FROM {tabla.B.NombreCompleto};");
            pre.AppendLine();
            pre.AppendLine("  -- INSERT en orden FK");
            resultado.PrefijoScript = pre.ToString();

            var suf = new StringBuilder();
            suf.AppendLine("COMMIT");
            suf.AppendLine();
            AgregarScriptFaseRehabilitarFKs(suf, motorB, ordenInsert.Select(t => t.B).ToList());
            resultado.SufijoScript = suf.ToString();

            int n = 0;
            foreach (var tabla in ordenInsert)
            {
                n++;
                onProgress?.Invoke($"[Script] ({n}/{ordenInsert.Count}) {tabla.NombreCompleto}...");
                onStep?.Invoke(n, ordenInsert.Count);

                string nombreB = tabla.B.NombreCompleto;
                metadata.TryGetValue(nombreB, out TablaMetadata meta);
                bool overriding = (motorB == TipoMotor.POSTGRES || motorB == TipoMotor.DB2)
                                  && meta?.TieneIdentityAlways == true;
                bool identity = motorB == TipoMotor.MS_SQL && meta?.TieneIdentity == true;
                if (tabla.Destino != null) { overriding = false; identity = false; }   // se recalcula con las columnas que se copian

                string archivo = Path.Combine(carpeta, n.ToString("000") + "_" + NombreArchivoTabla(tabla.NombreCompleto) + ".sql");
                long filas = 0;
                bool ok = true;
                try
                {
                    using (var w = NuevoWriter(archivo, false))
                    using (var lector = LectorTabla.Abrir(connStrA, tabla, meta?.ColumnasExcluidas, op.UmbralLobBytes))
                    {
                        MapaInsert mapa = (tabla.Destino != null && meta != null && meta.Columnas.Count > 0)
                            ? MapaInsert.Crear(lector, meta, motorB) : null;
                        if (mapa != null)
                        {
                            overriding = (motorB == TipoMotor.POSTGRES || motorB == TipoMotor.DB2) && mapa.CopiaIdentityAlways;
                            identity   = motorB == TipoMotor.MS_SQL && mapa.CopiaIdentity;
                        }
                        filas = EscribirFilasScript(w, lector, nombreB, motorB, overriding, op, inc, "Script",
                            c => onProgress?.Invoke($"[Script]   {tabla.NombreCompleto}: {c} filas..."), mapa);
                    }
                }
                catch (Exception ex)
                {
                    ok = false;
                    inc.Agregar("Script", tabla.NombreCompleto, SeveridadIncidencia.Error,
                        ex is OutOfMemoryException ? "Memoria insuficiente al generar el script" : "No se pudo generar el script de la tabla",
                        ex.Message, TransferenciaStreaming.Sugerir(ex) + " La tabla no está en el script.");
                }

                if (!ok || filas == 0)
                {
                    try { File.Delete(archivo); } catch { }
                    continue;
                }

                resultado.PartesScript.Add(new ParteScript
                {
                    Tabla   = tabla.NombreCompleto,
                    Archivo = archivo,
                    Filas   = filas,
                    Antes   = identity ? $"  SET IDENTITY_INSERT {nombreB} ON;" : null,
                    Despues = identity ? $"  SET IDENTITY_INSERT {nombreB} OFF;" : null
                });
            }
        }

        /// <summary>Sentencia que abre la transacción en el script de transferencia, según el motor de B.</summary>
        private static string SqlIniciarTransaccion(TipoMotor motor)
        {
            switch (motor)
            {
                case TipoMotor.POSTGRES: return "BEGIN";
                case TipoMotor.SQLite:   return "BEGIN TRANSACTION";
                case TipoMotor.DB2:      return "-- DB2: no hay BEGIN; la transacción empieza con la primera sentencia (ejecutar con autocommit desactivado)";
                default:                 return "BEGIN TRAN";
            }
        }

        /// <summary>Arma el script completo (un solo archivo) a partir de las partes ya escritas.</summary>
        public static void EnsamblarScript(ResultadoPasado r, string destino)
        {
            using (var w = NuevoWriter(destino, true))
            {
                w.Write(r.PrefijoScript);
                foreach (var p in r.PartesScript)
                {
                    if (!string.IsNullOrEmpty(p.Antes)) w.WriteLine(p.Antes);
                    w.Flush();
                    using (var fs = File.OpenRead(p.Archivo)) fs.CopyTo(w.BaseStream);
                    if (!string.IsNullOrEmpty(p.Despues)) w.WriteLine(p.Despues);
                }
                w.Write(r.SufijoScript);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Verificación post-transferencia
        // ─────────────────────────────────────────────────────────────────────

        public static void VerificarConsistencia(
            string connStrA, string connStrB,
            List<TablaTransfer> tablas,
            RegistroIncidencias incidencias,
            Action<string> onProgress)
        {
            bool todoOk = true;
            foreach (var tabla in tablas)
            {
                try
                {
                    long cntA = ContarFilas(connStrA, tabla.NombreCompleto);
                    long cntB = ContarFilas(connStrB, tabla.B.NombreCompleto);
                    if (cntA == cntB)
                        onProgress?.Invoke($"  ✓ {tabla.NombreCompleto}: {cntA} filas (A = B)");
                    else
                    {
                        onProgress?.Invoke($"  ✗ {tabla.NombreCompleto}: A={cntA} filas / B={cntB} filas — DIFERENCIA DETECTADA");
                        incidencias.Agregar("Verificación", tabla.NombreCompleto, SeveridadIncidencia.Error,
                            "Conteo distinto entre A y B", $"A tiene {cntA} filas, B tiene {cntB}.",
                            "Si no se omitieron filas a propósito, A pudo cambiar durante la transferencia o B tiene triggers/restricciones: revisar y repetir.");
                        todoOk = false;
                    }
                }
                catch (Exception ex)
                {
                    onProgress?.Invoke($"  {tabla.NombreCompleto}: no se pudo verificar ({ex.Message})");
                    incidencias.Agregar("Verificación", tabla.NombreCompleto, SeveridadIncidencia.Aviso,
                        "No se pudo verificar", ex.Message, "Comparar el conteo de filas de A y B manualmente.");
                }
            }
            onProgress?.Invoke(todoOk
                ? "✓ Verificación completada: A y B están consistentes."
                : "Verificación completada con diferencias. Revisar incidencias.");
        }

        private static long ContarFilas(string connStr, string nombreCompleto)
        {
            var DB = new DataBase(connStr, false);
            try
            {
                DB.CommandText = $"SELECT COUNT(*) FROM {nombreCompleto}";
                var dt = new DataTable();
                DB.DataAdapter.Fill(dt);
                return Convert.ToInt64(dt.Rows[0][0]);
            }
            finally { try { DB.CloseConnection(); } catch { } }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Fase FK disable / enable — por motor
        // ─────────────────────────────────────────────────────────────────────

        public static void AgregarScriptFaseDeshabilitarFKs(
            StringBuilder sb, TipoMotor motor, List<TablaTransfer> tablas)
        {
            sb.AppendLine("-- ═══ FASE 1: Deshabilitar FK constraints ═══");
            switch (motor)
            {
                case TipoMotor.MS_SQL:
                    foreach (var t in tablas) sb.AppendLine($"ALTER TABLE {t.NombreCompleto} NOCHECK CONSTRAINT ALL;");
                    break;
                case TipoMotor.POSTGRES:
                    sb.AppendLine("SET session_replication_role = replica;");
                    break;
                case TipoMotor.DB2:
                    foreach (var t in tablas) sb.AppendLine($"SET INTEGRITY FOR {t.NombreCompleto} OFF CASCADE DEFERRED;");
                    break;
                case TipoMotor.SQLite:
                    sb.AppendLine("PRAGMA foreign_keys = OFF;");
                    break;
            }
        }

        public static void AgregarScriptFaseRehabilitarFKs(
            StringBuilder sb, TipoMotor motor, List<TablaTransfer> tablas)
        {
            sb.AppendLine("-- ═══ FASE 3: Rehabilitar FK constraints ═══");
            switch (motor)
            {
                case TipoMotor.MS_SQL:
                    foreach (var t in tablas) sb.AppendLine($"ALTER TABLE {t.NombreCompleto} WITH CHECK CHECK CONSTRAINT ALL;");
                    break;
                case TipoMotor.POSTGRES:
                    sb.AppendLine("SET session_replication_role = DEFAULT;");
                    break;
                case TipoMotor.DB2:
                    foreach (var t in tablas) sb.AppendLine($"SET INTEGRITY FOR {t.NombreCompleto} IMMEDIATE CHECKED;");
                    break;
                case TipoMotor.SQLite:
                    sb.AppendLine("PRAGMA foreign_keys = ON;");
                    break;
            }
        }

        /// <summary>
        /// Una FK que hay que apagar durante la carga y volver a encender al final. Lleva sus propias sentencias
        /// para que cada motor use el mecanismo que le corresponde y la reactivación sea común.
        /// </summary>
        internal sealed class FkExterna
        {
            public string Tabla;                  // tabla hija (para el log), con comillas del motor
            public string Fk;                     // nombre de la constraint
            public string Referencia;             // tabla padre (para el log)
            public string SqlReactivarValidando;  // reactiva y revisa las filas existentes (puede fallar si hay huérfanas)
            public string SqlReactivarSinValidar; // reactiva sin revisar lo existente (null = el motor no distingue)
        }

        // ─────────────────────────────────────────────────────────────────────
        // Cómo se evita que una FK frene el DELETE/INSERT, motor por motor
        //
        //  MS_SQL    NOCHECK CONSTRAINT actúa sobre UNA tabla. Las FK de tablas fuera de la selección que apuntan a
        //            las que se vacían siguen activas (error 547) → se buscan en sys.foreign_keys y se apagan una por una.
        //  DB2 LUW   SET INTEGRITY ... OFF deja la tabla en "check pending" y en ese estado NO admite INSERT/DELETE
        //            (SQL0668N): sirve para LOAD, no para DML. Lo correcto es ALTER FOREIGN KEY ... NOT ENFORCED sobre
        //            cada FK que toque las tablas (como padre o como hija), buscadas en SYSCAT.REFERENCES.
        //  Postgres  session_replication_role = replica apaga los triggers de FK de TODA la sesión (también las de
        //            tablas externas), pero exige superusuario: sin él falla antes de tocar B y se avisa cómo seguir.
        //  SQLite    PRAGMA foreign_keys = OFF vale para toda la conexión, pero es un no-op dentro de una transacción:
        //            por eso se ejecuta ANTES de BeginTransaction.
        // Si apagar las FK falla, ocurre antes del primer DELETE: B queda intacta.
        // ─────────────────────────────────────────────────────────────────────

        private static void DeshabilitarFKsExternasMsSql(
            OdbcConnection conn, List<TablaTransfer> tablas, List<FkExterna> externas,
            Action<string> onProgress, Action<string> onSql)
        {
            if (tablas.Count == 0) return;

            // Se consulta después del NOCHECK de la selección: lo que sigue habilitado es lo que bloquea el DELETE
            var ids = string.Join(", ", tablas.Select(t => "OBJECT_ID('" + t.NombreCompleto.Replace("'", "''") + "')"));
            string consulta =
                "SELECT QUOTENAME(SCHEMA_NAME(h.schema_id)) + '.' + QUOTENAME(h.name), QUOTENAME(fk.name), " +
                "       QUOTENAME(SCHEMA_NAME(p.schema_id)) + '.' + QUOTENAME(p.name) " +
                "FROM sys.foreign_keys fk " +
                "JOIN sys.tables h ON h.object_id = fk.parent_object_id " +
                "JOIN sys.tables p ON p.object_id = fk.referenced_object_id " +
                "WHERE fk.is_disabled = 0 AND fk.referenced_object_id IN (" + ids + ")";

            var halladas = new List<FkExterna>();
            using (var cmd = new OdbcCommand(consulta, conn))
            using (var rd = cmd.ExecuteReader())
                while (rd.Read())
                {
                    string tabla = rd.GetString(0), fk = rd.GetString(1);
                    halladas.Add(new FkExterna
                    {
                        Tabla = tabla, Fk = fk, Referencia = rd.GetString(2),
                        SqlReactivarValidando  = $"ALTER TABLE {tabla} WITH CHECK CHECK CONSTRAINT {fk}",
                        SqlReactivarSinValidar = $"ALTER TABLE {tabla} WITH NOCHECK CHECK CONSTRAINT {fk}"
                    });
                }

            if (halladas.Count == 0) return;

            onProgress?.Invoke($"Hay {halladas.Count} FK de tablas fuera de la selección que apuntan a las que se vacían: se desactivan durante la carga y se reactivan al final.");
            foreach (var f in halladas)
            {
                string sql = $"ALTER TABLE {f.Tabla} NOCHECK CONSTRAINT {f.Fk}";
                onSql?.Invoke(sql);
                EjecutarDml(conn, null, sql);
                externas.Add(f);   // recién ahora: solo se reactiva lo que realmente se apagó
                onProgress?.Invoke($"  FK externa desactivada: {f.Fk} ({f.Tabla} → {f.Referencia})");
            }
        }

        private static string ComillasDb2(string s) { return "\"" + s.Replace("\"", "\"\"") + "\""; }

        /// <summary>DB2 LUW: todas las FK que tocan las tablas (como padre o como hija) pasan a NOT ENFORCED.</summary>
        private static void DeshabilitarFKsDb2(
            OdbcConnection conn, List<TablaTransfer> tablas, List<FkExterna> externas,
            Action<string> onProgress, Action<string> onSql)
        {
            if (tablas.Count == 0) return;

            var cond = new StringBuilder();
            foreach (var t in tablas)
            {
                string nom = t.Nombre.ToUpperInvariant().Replace("'", "''");
                string sch = string.IsNullOrEmpty(t.Schema) ? null : t.Schema.ToUpperInvariant().Replace("'", "''");
                Func<string, string> esquema = col => sch == null ? col + " = CURRENT SCHEMA" : col + " = '" + sch + "'";
                if (cond.Length > 0) cond.Append(" OR ");
                cond.Append($"({esquema("TABSCHEMA")} AND TABNAME = '{nom}') OR ({esquema("REFTABSCHEMA")} AND REFTABNAME = '{nom}')");
            }

            const string cols = "SELECT TABSCHEMA, TABNAME, CONSTNAME, REFTABSCHEMA, REFTABNAME FROM SYSCAT.REFERENCES WHERE ";
            var halladas = new List<FkExterna>();
            Action<string> leer = consulta =>
            {
                halladas.Clear();
                using (var cmd = new OdbcCommand(consulta, conn))
                using (var rd = cmd.ExecuteReader())
                    while (rd.Read())
                    {
                        string tabla = ComillasDb2(rd.GetString(0).Trim()) + "." + ComillasDb2(rd.GetString(1).Trim());
                        string fk    = ComillasDb2(rd.GetString(2).Trim());
                        halladas.Add(new FkExterna
                        {
                            Tabla = tabla, Fk = fk,
                            Referencia = rd.GetString(3).Trim() + "." + rd.GetString(4).Trim(),
                            SqlReactivarValidando = $"ALTER TABLE {tabla} ALTER FOREIGN KEY {fk} ENFORCED"
                        });
                    }
            };

            // Solo las que hoy se hacen cumplir: así la reactivación no vuelve "obligatoria" una FK que era informativa
            try { leer(cols + "ENFORCED = 'Y' AND (" + cond + ")"); }
            catch (OdbcException) { leer(cols + "(" + cond + ")"); }   // versiones sin la columna ENFORCED

            if (halladas.Count == 0) return;

            onProgress?.Invoke($"DB2: {halladas.Count} FK relacionadas con las tablas; se pasan a NOT ENFORCED durante la carga y vuelven a ENFORCED al final.");
            foreach (var f in halladas)
            {
                string sql = $"ALTER TABLE {f.Tabla} ALTER FOREIGN KEY {f.Fk} NOT ENFORCED";
                onSql?.Invoke(sql);
                EjecutarDml(conn, null, sql);
                externas.Add(f);
            }
        }

        /// <summary>Reactiva las FK que se apagaron. Nunca lanza: cada falla queda como incidencia.</summary>
        private static void RehabilitarFKsExternas(
            OdbcConnection conn, List<FkExterna> externas, Action<string> onProgress, RegistroIncidencias incidencias)
        {
            foreach (var f in externas)
            {
                try
                {
                    EjecutarDml(conn, null, f.SqlReactivarValidando);
                    onProgress?.Invoke($"  FK reactivada: {f.Fk} ({f.Tabla})");
                }
                catch (Exception exCheck)
                {
                    if (f.SqlReactivarSinValidar == null)
                    {
                        incidencias?.Agregar("FK", f.Tabla, SeveridadIncidencia.Error, "No se pudo reactivar una FK",
                            $"{f.Fk}: {exCheck.Message}", $"Reactivala a mano: {f.SqlReactivarValidando}");
                        continue;
                    }

                    // Quedaron filas de esa tabla apuntando a filas que ya no existen en el padre: se reactiva sin validar
                    try
                    {
                        EjecutarDml(conn, null, f.SqlReactivarSinValidar);
                        incidencias?.Agregar("FK", f.Tabla, SeveridadIncidencia.Aviso, "FK reactivada sin validar",
                            $"{f.Fk} ({f.Tabla} → {f.Referencia}): hay filas huérfanas. {exCheck.Message}",
                            "Esa tabla no estaba en la selección y conserva filas que apuntan a datos que se reemplazaron. Incluila en la transferencia o limpiá esas filas.");
                    }
                    catch (Exception exNo)
                    {
                        incidencias?.Agregar("FK", f.Tabla, SeveridadIncidencia.Error, "No se pudo reactivar una FK",
                            $"{f.Fk}: {exNo.Message}", $"Reactivala a mano: {f.SqlReactivarSinValidar}");
                    }
                }
            }
        }

        private static void EjecutarFaseDeshabilitarFKs(
            OdbcConnection conn, TipoMotor motor, List<TablaTransfer> tablas,
            Action<string> onProgress, Action<string> onSql, List<FkExterna> externas = null)
        {
            switch (motor)
            {
                case TipoMotor.MS_SQL:
                    foreach (var t in tablas)
                    {
                        string sql = $"ALTER TABLE {t.NombreCompleto} NOCHECK CONSTRAINT ALL";
                        onSql?.Invoke(sql); EjecutarDml(conn, null, sql);
                    }
                    if (externas != null)
                        DeshabilitarFKsExternasMsSql(conn, tablas, externas, onProgress, onSql);
                    break;
                case TipoMotor.POSTGRES:
                {
                    string sql = "SET session_replication_role = replica";
                    onSql?.Invoke(sql); EjecutarDml(conn, null, sql);
                    break;
                }
                case TipoMotor.DB2:
                    if (externas != null)
                        DeshabilitarFKsDb2(conn, tablas, externas, onProgress, onSql);
                    break;
                case TipoMotor.SQLite:
                {
                    string sql = "PRAGMA foreign_keys = OFF";
                    onSql?.Invoke(sql); EjecutarDml(conn, null, sql);
                    break;
                }
            }
        }

        private static void EjecutarFaseRehabilitarFKs(
            OdbcConnection conn, TipoMotor motor, List<TablaTransfer> tablas,
            Action<string> onProgress, Action<string> onSql,
            List<FkExterna> externas = null, RegistroIncidencias incidencias = null)
        {
            // Primero las FK apagadas una por una: nunca lanza, así no quedan apagadas si falla algo más abajo
            if (externas != null && externas.Count > 0)
                RehabilitarFKsExternas(conn, externas, onProgress, incidencias);

            switch (motor)
            {
                case TipoMotor.MS_SQL:
                {
                    // Se intenta con TODAS aunque alguna falle, y recién al final se informa
                    var errores = new List<string>();
                    foreach (var t in tablas)
                    {
                        string sql = $"ALTER TABLE {t.NombreCompleto} WITH CHECK CHECK CONSTRAINT ALL";
                        try { onSql?.Invoke(sql); EjecutarDml(conn, null, sql); }
                        catch (Exception ex) { errores.Add(t.NombreCompleto + ": " + ex.Message); }
                    }
                    if (errores.Count > 0)
                        throw new InvalidOperationException(
                            $"{errores.Count} tabla(s) no pudieron revalidar sus FK:\n" + string.Join("\n", errores));
                    break;
                }
                case TipoMotor.POSTGRES:
                {
                    string sql = "SET session_replication_role = DEFAULT";
                    onSql?.Invoke(sql); EjecutarDml(conn, null, sql);
                    break;
                }
                case TipoMotor.DB2:
                    // Ya reactivadas arriba (ALTER FOREIGN KEY ... ENFORCED)
                    break;
                case TipoMotor.SQLite:
                {
                    string sql = "PRAGMA foreign_keys = ON";
                    onSql?.Invoke(sql); EjecutarDml(conn, null, sql);
                    break;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers internos
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ejecuta una sentencia DML. <paramref name="onComando"/> permite al llamador
        /// quedarse con el OdbcCommand en curso para poder cancelarlo desde otro hilo.
        /// <paramref name="parametros"/> son los valores de los '?' de la sentencia (binarios grandes).
        /// </summary>
        internal static int EjecutarDml(OdbcConnection conn, OdbcTransaction tx, string sql,
                                        Action<OdbcCommand> onComando = null,
                                        IList<OdbcParameter> parametros = null)
        {
            using (var cmd = new OdbcCommand(sql, conn))
            {
                if (tx != null) cmd.Transaction = tx;
                cmd.CommandTimeout = TransferenciaStreaming.TimeoutSegundos();
                if (parametros != null)
                    foreach (var p in parametros) cmd.Parameters.Add(p);
                onComando?.Invoke(cmd);
                try { return cmd.ExecuteNonQuery(); }
                finally { onComando?.Invoke(null); }
            }
        }

        private static List<FkEdge> ObtenerTodasLasFKs(
            string connStr, TipoMotor motor, List<TablaTransfer> tablas)
        {
            var resultado = new List<FkEdge>();
            try
            {
                var conn = new Conexion { Motor = motor };
                var svc  = new EsquematizadorService(conn);
                var db   = new DataBase(connStr);
                var rels = svc.ObtenerRelacionesFK(db, tablas.Select(t => t.B.Nombre).ToList());
                db.CloseConnection();
                foreach (var r in rels)
                    resultado.Add(new FkEdge
                    {
                        TablaOrigen    = r.TablaOrigen,
                        TablaDestino   = r.TablaDestino,
                        ColumnaOrigen  = r.ColumnaOrigen,
                        ColumnaDestino = r.ColumnaDestino,
                    });
            }
            catch { }
            return resultado;
        }

        private static string NombreSimple(string nombreCompleto)
        {
            int dot = nombreCompleto.LastIndexOf('.');
            return dot >= 0 ? nombreCompleto.Substring(dot + 1) : nombreCompleto;
        }
    }
}
