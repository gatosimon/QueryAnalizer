using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.Odbc;
using System.Text;
using System.Threading.Tasks;

namespace QueryAnalyzer
{
    public class ColumnDesignInfo : INotifyPropertyChanged
    {
        #region Backing fields
        private bool   _esPK;
        private string _nombre;
        private string _tipoDato;
        private int?   _longitud;
        private int?   _precision;
        private int?   _escala;
        private bool   _esNulable;
        private string _valorDefault;
        private bool   _esNueva;
        private bool   _marcarParaEliminar;
        private string _descripcion;
        #endregion

        #region Snapshot (valores originales para detectar cambios)
        private string _nombreOrig;
        private string _tipoDatoCompletoOrig;
        private bool   _esNulableOrig;
        private string _valorDefaultOrig;
        private string _descripcionOrig;
        #endregion

        public bool EsPK
        {
            get => _esPK;
            set { _esPK = value; OnPropertyChanged("EsPK"); }
        }

        public string Nombre
        {
            get => _nombre;
            set { _nombre = value; OnPropertyChanged("Nombre"); }
        }

        public string TipoDato
        {
            get => _tipoDato;
            set { _tipoDato = value; OnPropertyChanged("TipoDato"); OnPropertyChanged("TipoDatoCompleto"); }
        }

        public int? Longitud
        {
            get => _longitud;
            set { _longitud = value; OnPropertyChanged("Longitud"); OnPropertyChanged("TipoDatoCompleto"); }
        }

        public int? Precision
        {
            get => _precision;
            set { _precision = value; OnPropertyChanged("Precision"); OnPropertyChanged("TipoDatoCompleto"); }
        }

        public int? Escala
        {
            get => _escala;
            set { _escala = value; OnPropertyChanged("Escala"); OnPropertyChanged("TipoDatoCompleto"); }
        }

        public bool EsNulable
        {
            get => _esNulable;
            set { _esNulable = value; OnPropertyChanged("EsNulable"); }
        }

        public string ValorDefault
        {
            get => _valorDefault;
            set { _valorDefault = value; OnPropertyChanged("ValorDefault"); }
        }

        public bool EsNueva
        {
            get => _esNueva;
            set { _esNueva = value; OnPropertyChanged("EsNueva"); }
        }

        public bool MarcarParaEliminar
        {
            get => _marcarParaEliminar;
            set { _marcarParaEliminar = value; OnPropertyChanged("MarcarParaEliminar"); }
        }

        public string Descripcion
        {
            get => _descripcion;
            set { _descripcion = value; OnPropertyChanged("Descripcion"); }
        }

        /// <summary>
        /// Tipo de dato como se muestra y edita en la grilla: nvarchar(50), decimal(10,2), int, etc.
        /// El setter parsea el string y actualiza TipoDato/Longitud/Precision/Escala internamente.
        /// </summary>
        public string TipoDatoCompleto
        {
            get
            {
                string t = _tipoDato ?? string.Empty;
                if (_longitud.HasValue)
                    return _longitud == -1 ? string.Format("{0}(MAX)", t) : string.Format("{0}({1})", t, _longitud);
                if (_precision.HasValue && _precision > 0)
                    return (_escala.HasValue && _escala > 0)
                        ? string.Format("{0}({1},{2})", t, _precision, _escala)
                        : string.Format("{0}({1})", t, _precision);
                return t;
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                var v = value.Trim();
                if (v.Contains("("))
                {
                    int p1 = v.IndexOf('(');
                    int p2 = v.LastIndexOf(')');
                    _tipoDato = v.Substring(0, p1).Trim();
                    if (p2 > p1)
                    {
                        string inner = v.Substring(p1 + 1, p2 - p1 - 1).Trim();
                        if (inner.Equals("MAX", StringComparison.OrdinalIgnoreCase))
                        {
                            _longitud = -1; _precision = null; _escala = null;
                        }
                        else if (inner.Contains(","))
                        {
                            var partes = inner.Split(',');
                            _longitud  = null;
                            _precision = int.TryParse(partes[0].Trim(), out int pr) ? pr : (int?)null;
                            _escala    = partes.Length > 1 && int.TryParse(partes[1].Trim(), out int sc) ? sc : (int?)null;
                        }
                        else
                        {
                            _longitud  = int.TryParse(inner, out int ln) ? ln : (int?)null;
                            _precision = null; _escala = null;
                        }
                    }
                }
                else
                {
                    _tipoDato  = v;
                    _longitud  = null; _precision = null; _escala = null;
                }
                OnPropertyChanged("TipoDato");
                OnPropertyChanged("Longitud");
                OnPropertyChanged("Precision");
                OnPropertyChanged("Escala");
                OnPropertyChanged("TipoDatoCompleto");
            }
        }

        // ── Propiedades de solo lectura para el script ────────────────────────────
        public string NombreOriginal          => _nombreOrig;
        public string TipoDatoCompletoOriginal => _tipoDatoCompletoOrig;
        public bool   EsNulableOriginal        => _esNulableOrig;
        public string ValorDefaultOriginal     => _valorDefaultOrig;
        public string DescripcionOriginal      => _descripcionOrig;

        public bool Modificado =>
            _nombre       != _nombreOrig           ||
            TipoDatoCompleto != _tipoDatoCompletoOrig ||
            _esNulable    != _esNulableOrig         ||
            _valorDefault != _valorDefaultOrig      ||
            _descripcion  != _descripcionOrig;

        /// <summary>Guarda el estado actual como snapshot "original" (llamar después de cargar desde BD).</summary>
        public void MarcarComoOriginal()
        {
            _nombreOrig            = _nombre;
            _tipoDatoCompletoOrig  = TipoDatoCompleto;
            _esNulableOrig         = _esNulable;
            _valorDefaultOrig      = _valorDefault;
            _descripcionOrig       = _descripcion;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string prop) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    // ─────────────────────────────────────────────────────────────────────────────

    public static class TableDesignerService
    {
        public static async Task<List<ColumnDesignInfo>> GetColumnasDesignAsync(Conexion conexion, string tabla)
        {
            return await Task.Run(() => GetColumnasDesign(conexion, tabla));
        }

        private static List<ColumnDesignInfo> GetColumnasDesign(Conexion conexion, string tabla)
        {
            var resultado = new List<ColumnDesignInfo>();
            if (conexion == null || string.IsNullOrWhiteSpace(tabla)) return resultado;

            // Separamos esquema si viene "esquema.tabla"
            string nombreTabla = tabla.Contains(".")
                ? tabla.Substring(tabla.LastIndexOf('.') + 1)
                : tabla;

            try
            {
                string connStr = ConexionesManager.GetConnectionString(conexion);
                using (var conn = new OdbcConnection(connStr))
                {
                    conn.Open();
                    resultado = BuildColumnsFromDB(conn, conexion.Motor, nombreTabla);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[TableDesignerService] " + ex.Message);
            }

            return resultado;
        }

        private static List<ColumnDesignInfo> BuildColumnsFromDB(OdbcConnection conn, TipoMotor motor, string tabla)
        {
            var lista = new List<ColumnDesignInfo>();
            string t = tabla.Replace("'", "''");

            switch (motor)
            {
                case TipoMotor.MS_SQL:
                {
                    // Paso 1: cargar descripciones de columnas en un diccionario
                    // (mismo patrón que DocumentadorService, evita subconsulta correlacionada
                    //  que puede fallar con sql_variant via ODBC)
                    var descripciones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    try
                    {
                        string sqlDesc = string.Format(@"
SELECT sc.name AS col_name, CAST(ep.value AS NVARCHAR(4000)) AS descripcion
FROM sys.extended_properties ep
INNER JOIN sys.objects o  ON ep.major_id = o.object_id
INNER JOIN sys.columns sc ON ep.major_id = sc.object_id AND ep.minor_id = sc.column_id
WHERE ep.name = 'MS_Description' AND o.name = '{0}'", t);
                        using (var cmdD = new OdbcCommand(sqlDesc, conn))
                        using (var rd = cmdD.ExecuteReader())
                        {
                            while (rd.Read())
                            {
                                string colName = rd[0].ToString().Trim();
                                string desc    = rd.IsDBNull(1) ? null : rd[1].ToString().Trim();
                                if (!string.IsNullOrEmpty(colName))
                                    descripciones[colName] = desc;
                            }
                        }
                    }
                    catch { /* continuar sin descripciones si falla */ }

                    // Paso 2: cargar columnas (sin subconsulta correlacionada)
                    string sql = string.Format(@"
SELECT
    c.COLUMN_NAME,
    c.DATA_TYPE,
    c.CHARACTER_MAXIMUM_LENGTH,
    c.NUMERIC_PRECISION,
    c.NUMERIC_SCALE,
    c.IS_NULLABLE,
    c.COLUMN_DEFAULT,
    CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END AS IS_PK
FROM INFORMATION_SCHEMA.COLUMNS c
LEFT JOIN (
    SELECT ku.TABLE_NAME, ku.COLUMN_NAME
    FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
    JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
        ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
    WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
) pk ON pk.TABLE_NAME = c.TABLE_NAME AND pk.COLUMN_NAME = c.COLUMN_NAME
WHERE c.TABLE_NAME = '{0}'
ORDER BY c.ORDINAL_POSITION", t);
                    using (var cmd = new OdbcCommand(sql, conn))
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            string colName = r[0].ToString().Trim();
                            var col = new ColumnDesignInfo
                            {
                                Nombre       = colName,
                                TipoDato     = r[1].ToString().Trim(),
                                Longitud     = r.IsDBNull(2) ? (int?)null : Convert.ToInt32(r[2]),
                                Precision    = r.IsDBNull(3) ? (int?)null : Convert.ToInt32(r[3]),
                                Escala       = r.IsDBNull(4) ? (int?)null : Convert.ToInt32(r[4]),
                                EsNulable    = r[5].ToString().Trim().ToUpperInvariant() == "YES",
                                ValorDefault = r.IsDBNull(6) ? null : r[6].ToString().Trim(),
                                EsPK         = Convert.ToInt32(r[7]) == 1,
                                Descripcion  = descripciones.TryGetValue(colName, out string d) ? d : null
                            };
                            col.MarcarComoOriginal();
                            lista.Add(col);
                        }
                    }
                    break;
                }

                case TipoMotor.DB2:
                {
                    string sql = string.Format(@"
SELECT NAME, COLTYPE, LENGTH, SCALE, NULLS, DEFAULT, KEYSEQ, REMARKS
FROM SYSIBM.SYSCOLUMNS
WHERE TBNAME = '{0}'
ORDER BY COLNO", t.ToUpperInvariant());
                    using (var cmd = new OdbcCommand(sql, conn))
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            int len   = r.IsDBNull(2) ? 0 : Convert.ToInt32(r[2]);
                            int scale = r.IsDBNull(3) ? 0 : Convert.ToInt32(r[3]);
                            bool isPK = !r.IsDBNull(6) && Convert.ToInt32(r[6]) > 0;
                            var col = new ColumnDesignInfo
                            {
                                Nombre       = r[0].ToString().Trim(),
                                TipoDato     = r[1].ToString().Trim(),
                                Longitud     = len   > 0 ? len   : (int?)null,
                                Escala       = scale > 0 ? scale : (int?)null,
                                EsNulable    = r[4].ToString().Trim().ToUpperInvariant() == "Y",
                                ValorDefault = r.IsDBNull(5) ? null : r[5].ToString().Trim(),
                                EsPK         = isPK,
                                Descripcion  = r.IsDBNull(7) ? null : r[7].ToString().Trim()
                            };
                            col.MarcarComoOriginal();
                            lista.Add(col);
                        }
                    }
                    break;
                }

                case TipoMotor.POSTGRES:
                {
                    string sql = string.Format(@"
SELECT
    c.column_name,
    c.data_type,
    c.character_maximum_length,
    c.numeric_precision,
    c.numeric_scale,
    c.is_nullable,
    c.column_default,
    CASE WHEN pk.column_name IS NOT NULL THEN 1 ELSE 0 END AS is_pk,
    pg_catalog.col_description(pgc.oid, c.ordinal_position) AS description
FROM information_schema.columns c
LEFT JOIN (
    SELECT ku.column_name
    FROM information_schema.table_constraints tc
    JOIN information_schema.key_column_usage ku
        ON tc.constraint_name = ku.constraint_name
        AND tc.table_name = ku.table_name
    WHERE tc.constraint_type = 'PRIMARY KEY'
      AND ku.table_name = '{0}'
) pk ON pk.column_name = c.column_name
LEFT JOIN pg_class pgc ON pgc.relname = c.table_name
WHERE c.table_name = '{0}'
ORDER BY c.ordinal_position", t.ToLowerInvariant());
                    using (var cmd = new OdbcCommand(sql, conn))
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var col = new ColumnDesignInfo
                            {
                                Nombre       = r[0].ToString().Trim(),
                                TipoDato     = r[1].ToString().Trim(),
                                Longitud     = r.IsDBNull(2) ? (int?)null : Convert.ToInt32(r[2]),
                                Precision    = r.IsDBNull(3) ? (int?)null : Convert.ToInt32(r[3]),
                                Escala       = r.IsDBNull(4) ? (int?)null : Convert.ToInt32(r[4]),
                                EsNulable    = r[5].ToString().Trim().ToUpperInvariant() == "YES",
                                ValorDefault = r.IsDBNull(6) ? null : r[6].ToString().Trim(),
                                EsPK         = Convert.ToInt32(r[7]) == 1,
                                Descripcion  = r.IsDBNull(8) ? null : r[8].ToString().Trim()
                            };
                            col.MarcarComoOriginal();
                            lista.Add(col);
                        }
                    }
                    break;
                }

                case TipoMotor.SQLite:
                {
                    using (var cmd = new OdbcCommand("PRAGMA table_info(" + tabla + ")", conn))
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            string tipoRaw  = r[2].ToString().Trim();
                            string tipoDato = tipoRaw;
                            int?   longitud = null;
                            if (tipoRaw.Contains("("))
                            {
                                int p1 = tipoRaw.IndexOf('(');
                                int p2 = tipoRaw.IndexOf(')');
                                tipoDato = tipoRaw.Substring(0, p1).Trim();
                                if (p2 > p1)
                                {
                                    string inner = tipoRaw.Substring(p1 + 1, p2 - p1 - 1);
                                    if (int.TryParse(inner, out int ln)) longitud = ln;
                                }
                            }
                            var col = new ColumnDesignInfo
                            {
                                Nombre       = r[1].ToString().Trim(),
                                TipoDato     = tipoDato,
                                Longitud     = longitud,
                                EsNulable    = r[3].ToString().Trim() == "0", // notnull=0 → nullable
                                ValorDefault = r.IsDBNull(4) ? null : r[4].ToString().Trim(),
                                EsPK         = r[5].ToString().Trim() != "0"
                            };
                            col.MarcarComoOriginal();
                            lista.Add(col);
                        }
                    }
                    break;
                }
            }

            return lista;
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Descripción de tabla
        // ─────────────────────────────────────────────────────────────────────────

        public static async Task<string> GetDescripcionTablaAsync(Conexion conexion, string tabla)
        {
            return await Task.Run(() => GetDescripcionTabla(conexion, tabla));
        }

        private static string GetDescripcionTabla(Conexion conexion, string tabla)
        {
            if (conexion == null) return null;
            string nombreTabla = tabla.Contains(".") ? tabla.Substring(tabla.LastIndexOf('.') + 1) : tabla;
            string t = nombreTabla.Replace("'", "''");
            try
            {
                string connStr = ConexionesManager.GetConnectionString(conexion);
                using (var conn = new OdbcConnection(connStr))
                {
                    conn.Open();
                    string sql = null;
                    switch (conexion.Motor)
                    {
                        case TipoMotor.MS_SQL:
                            sql = string.Format("SELECT TOP 1 CAST(ep.value AS nvarchar(4000)) FROM sys.extended_properties ep INNER JOIN sys.objects o ON ep.major_id = o.object_id WHERE ep.name = 'MS_Description' AND ep.minor_id = 0 AND o.name = '{0}'", t);
                            break;
                        case TipoMotor.POSTGRES:
                            sql = string.Format("SELECT pg_catalog.obj_description(oid, 'pg_class') FROM pg_class WHERE relname = '{0}'", t.ToLowerInvariant());
                            break;
                        case TipoMotor.DB2:
                            sql = string.Format("SELECT REMARKS FROM SYSCAT.TABLES WHERE TABNAME = '{0}'", t.ToUpperInvariant());
                            break;
                        default:
                            return null;
                    }
                    using (var cmd = new OdbcCommand(sql, conn))
                    {
                        var result = cmd.ExecuteScalar();
                        return result == null || result == DBNull.Value ? null : result.ToString().Trim();
                    }
                }
            }
            catch { return null; }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Generación de script DDL
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Genera el script DDL comparando el estado original vs el actual de cada columna.
        /// Contempla: nuevas columnas, columnas a eliminar, columnas modificadas y renombre de tabla.
        /// </summary>
        /// <param name="motor">Motor de base de datos.</param>
        /// <param name="tabla">Nombre actual de la tabla.</param>
        /// <param name="columnas">Lista de columnas con su estado.</param>
        /// <param name="nuevoNombreTabla">
        ///     Nombre al que se renombra la tabla. Null o vacío = sin renombre.
        ///     La sentencia de renombre se agrega AL FINAL del script, después de todos los ALTER.
        /// </param>
        public static string GenerarScript(
            TipoMotor motor,
            string tabla,
            List<ColumnDesignInfo> columnas,
            string nuevoNombreTabla = null,
            string descripcionTabla = null,
            string descripcionTablaOriginal = null)
        {
            var sb = new StringBuilder();

            string schemaSql = "dbo";
            string tableOnly = tabla;
            if (tabla.Contains("."))
            {
                var partes = tabla.Split(new[] { '.' }, 2);
                schemaSql = partes[0].Trim('[', ']');
                tableOnly  = partes[1].Trim('[', ']');
            }

            // ── Cambios en columnas ───────────────────────────────────────────────
            foreach (var col in columnas)
            {
                // ── ELIMINAR ──────────────────────────────────────────────────────
                if (col.MarcarParaEliminar && !col.EsNueva)
                {
                    switch (motor)
                    {
                        case TipoMotor.DB2:
                            sb.AppendLine(string.Format("ALTER TABLE {0} DROP COLUMN {1};", tabla, col.NombreOriginal));
                            sb.AppendLine(string.Format("CALL SYSPROC.ADMIN_CMD('REORG TABLE {0}');", tabla));
                            break;
                        default:
                            sb.AppendLine(string.Format("ALTER TABLE {0} DROP COLUMN {1};", tabla, col.NombreOriginal));
                            break;
                    }
                    sb.AppendLine();
                    continue;
                }

                // ── NUEVA ─────────────────────────────────────────────────────────
                if (col.EsNueva && !col.MarcarParaEliminar)
                {
                    string nullable = col.EsNulable ? "NULL" : "NOT NULL";
                    string def      = string.IsNullOrWhiteSpace(col.ValorDefault) ? string.Empty
                                      : string.Format(" DEFAULT {0}", col.ValorDefault);
                    string tipo     = col.TipoDatoCompleto;

                    switch (motor)
                    {
                        case TipoMotor.MS_SQL:
                            sb.AppendLine(string.Format("ALTER TABLE {0} ADD {1} {2} {3}{4};", tabla, col.Nombre, tipo, nullable, def));
                            break;
                        case TipoMotor.POSTGRES:
                            sb.AppendLine(string.Format("ALTER TABLE {0} ADD COLUMN {1} {2} {3}{4};", tabla, col.Nombre, tipo, nullable, def));
                            break;
                        case TipoMotor.DB2:
                            sb.AppendLine(string.Format("ALTER TABLE {0} ADD COLUMN {1} {2} {3}{4};", tabla, col.Nombre, tipo, nullable, def));
                            sb.AppendLine(string.Format("CALL SYSPROC.ADMIN_CMD('REORG TABLE {0}');", tabla));
                            break;
                        case TipoMotor.SQLite:
                            sb.AppendLine(string.Format("ALTER TABLE {0} ADD COLUMN {1} {2}{3};", tabla, col.Nombre, tipo, def));
                            break;
                    }
                    sb.AppendLine();
                    continue;
                }

                // ── MODIFICADA ────────────────────────────────────────────────────
                if (!col.EsNueva && !col.MarcarParaEliminar && col.Modificado)
                {
                    // 1. Rename de columna
                    bool renombrada = col.Nombre != col.NombreOriginal;
                    if (renombrada)
                    {
                        switch (motor)
                        {
                            case TipoMotor.MS_SQL:
                                sb.AppendLine(string.Format("EXEC sp_rename '{0}.{1}', '{2}', 'COLUMN';",
                                    tabla, col.NombreOriginal, col.Nombre));
                                break;
                            case TipoMotor.POSTGRES:
                            case TipoMotor.DB2:
                            case TipoMotor.SQLite:
                                sb.AppendLine(string.Format("ALTER TABLE {0} RENAME COLUMN {1} TO {2};",
                                    tabla, col.NombreOriginal, col.Nombre));
                                break;
                        }
                    }

                    // 2. Cambio de tipo y/o nullable
                    bool tipoModificado     = col.TipoDatoCompleto != col.TipoDatoCompletoOriginal;
                    bool nullableModificado = col.EsNulable         != col.EsNulableOriginal;

                    if (tipoModificado || nullableModificado)
                    {
                        string colActual   = col.Nombre;
                        string tipoActual  = col.TipoDatoCompleto;
                        string nullableAct = col.EsNulable ? "NULL" : "NOT NULL";

                        switch (motor)
                        {
                            case TipoMotor.MS_SQL:
                                sb.AppendLine(string.Format("ALTER TABLE {0} ALTER COLUMN {1} {2} {3};",
                                    tabla, colActual, tipoActual, nullableAct));
                                break;

                            case TipoMotor.POSTGRES:
                                if (tipoModificado)
                                    sb.AppendLine(string.Format("ALTER TABLE {0} ALTER COLUMN {1} TYPE {2};",
                                        tabla, colActual, tipoActual));
                                if (nullableModificado)
                                    sb.AppendLine(col.EsNulable
                                        ? string.Format("ALTER TABLE {0} ALTER COLUMN {1} DROP NOT NULL;", tabla, colActual)
                                        : string.Format("ALTER TABLE {0} ALTER COLUMN {1} SET NOT NULL;",  tabla, colActual));
                                break;

                            case TipoMotor.DB2:
                                if (tipoModificado)
                                    sb.AppendLine(string.Format("ALTER TABLE {0} ALTER COLUMN {1} SET DATA TYPE {2};",
                                        tabla, colActual, tipoActual));
                                if (nullableModificado)
                                    sb.AppendLine(col.EsNulable
                                        ? string.Format("ALTER TABLE {0} ALTER COLUMN {1} DROP NOT NULL;", tabla, colActual)
                                        : string.Format("ALTER TABLE {0} ALTER COLUMN {1} SET NOT NULL;",  tabla, colActual));
                                sb.AppendLine(string.Format("CALL SYSPROC.ADMIN_CMD('REORG TABLE {0}');", tabla));
                                break;

                            case TipoMotor.SQLite:
                                sb.AppendLine(string.Format(
                                    "-- SQLite no soporta ALTER COLUMN. Para modificar '{0}' es necesario recrear la tabla.", colActual));
                                break;
                        }
                    }

                    sb.AppendLine();
                }
            }

            // ── DESCRIPCIONES DE COLUMNAS ─────────────────────────────────────────
            foreach (var col in columnas)
            {
                if (col.MarcarParaEliminar && !col.EsNueva) continue;
                bool esNuevaConDesc = col.EsNueva && !string.IsNullOrWhiteSpace(col.Descripcion);
                bool descCambio = !col.EsNueva && (col.Descripcion ?? "") != (col.DescripcionOriginal ?? "");
                if (!esNuevaConDesc && !descCambio) continue;

                string colNom = col.Nombre;
                string desc   = (col.Descripcion ?? "").Replace("'", "''");

                switch (motor)
                {
                    case TipoMotor.MS_SQL:
                        if (!string.IsNullOrWhiteSpace(col.Descripcion))
                        {
                            sb.AppendLine(string.Format("IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID(N'{0}') AND name = N'MS_Description' AND minor_id = COLUMNPROPERTY(OBJECT_ID(N'{0}'), N'{1}', 'ColumnId'))", tabla, colNom));
                            sb.AppendLine("BEGIN");
                            sb.AppendLine(string.Format("    EXEC sp_updateextendedproperty N'MS_Description', N'{0}', N'SCHEMA', N'{1}', N'TABLE', N'{2}', N'COLUMN', N'{3}'", desc, schemaSql, tableOnly, colNom));
                            sb.AppendLine("END");
                            sb.AppendLine("ELSE");
                            sb.AppendLine("BEGIN");
                            sb.AppendLine(string.Format("    EXEC sp_addextendedproperty N'MS_Description', N'{0}', N'SCHEMA', N'{1}', N'TABLE', N'{2}', N'COLUMN', N'{3}'", desc, schemaSql, tableOnly, colNom));
                            sb.AppendLine("END");
                        }
                        else
                        {
                            sb.AppendLine(string.Format("IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID(N'{0}') AND name = N'MS_Description' AND minor_id = COLUMNPROPERTY(OBJECT_ID(N'{0}'), N'{1}', 'ColumnId'))", tabla, colNom));
                            sb.AppendLine("BEGIN");
                            sb.AppendLine(string.Format("    EXEC sp_dropextendedproperty N'MS_Description', N'SCHEMA', N'{0}', N'TABLE', N'{1}', N'COLUMN', N'{2}'", schemaSql, tableOnly, colNom));
                            sb.AppendLine("END");
                        }
                        break;
                    case TipoMotor.POSTGRES:
                        sb.AppendLine(string.IsNullOrWhiteSpace(col.Descripcion)
                            ? string.Format("COMMENT ON COLUMN {0}.{1} IS NULL;", tabla, colNom)
                            : string.Format("COMMENT ON COLUMN {0}.{1} IS '{2}';", tabla, colNom, desc));
                        break;
                    case TipoMotor.DB2:
                        sb.AppendLine(string.Format("COMMENT ON COLUMN {0}.{1} IS '{2}';", tabla, colNom, desc));
                        break;
                    case TipoMotor.SQLite:
                        sb.AppendLine(string.Format("-- SQLite no admite descripciones de columna a nivel de catálogo (columna '{0}').", colNom));
                        break;
                }
                sb.AppendLine();
            }

            // ── DESCRIPCIÓN DE TABLA ──────────────────────────────────────────────
            bool descTablaCambio = (descripcionTabla ?? "") != (descripcionTablaOriginal ?? "");
            if (descTablaCambio)
            {
                string dt = (descripcionTabla ?? "").Replace("'", "''");
                switch (motor)
                {
                    case TipoMotor.MS_SQL:
                        if (!string.IsNullOrWhiteSpace(descripcionTabla))
                        {
                            sb.AppendLine(string.Format("IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID(N'{0}') AND name = N'MS_Description' AND minor_id = 0)", tabla));
                            sb.AppendLine("BEGIN");
                            sb.AppendLine(string.Format("    EXEC sp_updateextendedproperty N'MS_Description', N'{0}', N'SCHEMA', N'{1}', N'TABLE', N'{2}'", dt, schemaSql, tableOnly));
                            sb.AppendLine("END");
                            sb.AppendLine("ELSE");
                            sb.AppendLine("BEGIN");
                            sb.AppendLine(string.Format("    EXEC sp_addextendedproperty N'MS_Description', N'{0}', N'SCHEMA', N'{1}', N'TABLE', N'{2}'", dt, schemaSql, tableOnly));
                            sb.AppendLine("END");
                        }
                        else
                        {
                            sb.AppendLine(string.Format("IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID(N'{0}') AND name = N'MS_Description' AND minor_id = 0)", tabla));
                            sb.AppendLine("BEGIN");
                            sb.AppendLine(string.Format("    EXEC sp_dropextendedproperty N'MS_Description', N'SCHEMA', N'{0}', N'TABLE', N'{1}'", schemaSql, tableOnly));
                            sb.AppendLine("END");
                        }
                        break;
                    case TipoMotor.POSTGRES:
                        sb.AppendLine(string.IsNullOrWhiteSpace(descripcionTabla)
                            ? string.Format("COMMENT ON TABLE {0} IS NULL;", tabla)
                            : string.Format("COMMENT ON TABLE {0} IS '{1}';", tabla, dt));
                        break;
                    case TipoMotor.DB2:
                        sb.AppendLine(string.Format("COMMENT ON TABLE {0} IS '{1}';", tabla, dt));
                        break;
                    case TipoMotor.SQLite:
                        sb.AppendLine("-- SQLite no admite descripciones de tabla a nivel de catálogo.");
                        break;
                }
                sb.AppendLine();
            }

            // ── RENOMBRAR TABLA (al final, después de todos los ALTER) ────────────
            if (!string.IsNullOrWhiteSpace(nuevoNombreTabla) &&
                !nuevoNombreTabla.Equals(tabla, StringComparison.OrdinalIgnoreCase))
            {
                switch (motor)
                {
                    case TipoMotor.MS_SQL:
                        // sp_rename funciona tanto para tabla como para columna; sin tercer parámetro = tabla
                        sb.AppendLine(string.Format("EXEC sp_rename '{0}', '{1}';", tabla, nuevoNombreTabla));
                        break;
                    case TipoMotor.POSTGRES:
                        sb.AppendLine(string.Format("ALTER TABLE {0} RENAME TO {1};", tabla, nuevoNombreTabla));
                        break;
                    case TipoMotor.DB2:
                        sb.AppendLine(string.Format("RENAME TABLE {0} TO {1};", tabla, nuevoNombreTabla));
                        break;
                    case TipoMotor.SQLite:
                        sb.AppendLine(string.Format("ALTER TABLE {0} RENAME TO {1};", tabla, nuevoNombreTabla));
                        break;
                }
                sb.AppendLine();
            }

            string resultado = sb.ToString().Trim();
            return string.IsNullOrEmpty(resultado) ? "-- Sin cambios detectados." : resultado;
        }

        // ── Tipos de dato por motor ───────────────────────────────────────────────

        /// <summary>
        /// Devuelve la lista de tipos de dato más comunes para el motor indicado.
        /// Se usa para poblar el ComboBox editable de la columna "Data Type"
        /// en el diseñador de tablas.
        /// </summary>
        public static IReadOnlyList<string> GetTiposDato(TipoMotor motor)
        {
            switch (motor)
            {
                case TipoMotor.MS_SQL:
                    return new List<string>
                    {
                        // Enteros
                        "int", "bigint", "smallint", "tinyint",
                        // Decimales / numéricos
                        "decimal(18,2)", "decimal(10,4)", "numeric(18,2)",
                        "float", "real", "money", "smallmoney",
                        // Texto fijo
                        "char(10)", "nchar(10)",
                        // Texto variable
                        "varchar(50)", "varchar(100)", "varchar(255)", "varchar(max)",
                        "nvarchar(50)", "nvarchar(100)", "nvarchar(255)", "nvarchar(max)",
                        // Texto legacy
                        "text", "ntext",
                        // Lógico
                        "bit",
                        // Fecha / hora
                        "date", "time", "datetime", "datetime2(7)",
                        "datetimeoffset(7)", "smalldatetime",
                        // Identificador
                        "uniqueidentifier",
                        // Binario
                        "varbinary(max)", "binary(8)",
                        // Otros
                        "xml", "timestamp", "rowversion",
                    };

                case TipoMotor.DB2:
                    return new List<string>
                    {
                        // Enteros
                        "INTEGER", "BIGINT", "SMALLINT",
                        // Decimales
                        "DECIMAL(10,2)", "DECIMAL(18,4)", "NUMERIC(10,2)",
                        "FLOAT", "DOUBLE", "REAL", "DECFLOAT",
                        // Texto fijo
                        "CHAR(10)", "GRAPHIC(10)",
                        // Texto variable
                        "VARCHAR(50)", "VARCHAR(100)", "VARCHAR(255)",
                        "VARGRAPHIC(50)",
                        // LOB
                        "CLOB", "DBCLOB", "BLOB",
                        // Fecha / hora
                        "DATE", "TIME", "TIMESTAMP",
                        // Otros
                        "XML", "BOOLEAN",
                    };

                case TipoMotor.POSTGRES:
                    return new List<string>
                    {
                        // Enteros
                        "integer", "bigint", "smallint",
                        "serial", "bigserial", "smallserial",
                        // Decimales
                        "numeric(10,2)", "numeric(18,4)",
                        "float", "double precision", "real",
                        // Texto fijo
                        "char(10)",
                        // Texto variable
                        "varchar(50)", "varchar(100)", "varchar(255)", "text",
                        // Lógico
                        "boolean",
                        // Fecha / hora
                        "date", "time", "timestamp", "timestamptz", "interval",
                        // Identificador
                        "uuid",
                        // Binario / JSON
                        "bytea", "json", "jsonb",
                    };

                case TipoMotor.SQLite:
                default:
                    return new List<string>
                    {
                        "TEXT", "INTEGER", "REAL", "BLOB", "NUMERIC",
                    };
            }
        }
    }
}
