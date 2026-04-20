using System;
using System.Collections.Generic;
using System.Data.Odbc;
using System.Linq;
using System.Threading.Tasks;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace QueryAnalyzer
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Modelos de datos para documentación
    // ─────────────────────────────────────────────────────────────────────────────

    public class InfoColumnaDoc
    {
        /// <summary>"Pk", "Fk", o vacío</summary>
        public string Indicador { get; set; } = string.Empty;
        public string Nombre { get; set; }
        public string TipoCompleto { get; set; }
        public string Descripcion { get; set; } = string.Empty;
    }

    public class InfoTablaDoc
    {
        public string Schema { get; set; }
        public string Nombre { get; set; }
        /// <summary>"TABLE" o "VIEW"</summary>
        public string Tipo { get; set; }
        public List<InfoColumnaDoc> Columnas { get; set; } = new List<InfoColumnaDoc>();
        /// <summary>
        /// Descripción general de la tabla/vista obtenida desde los metadatos del motor.
        /// Vacío si el motor no soporta comentarios o no tiene ninguno definido.
        /// </summary>
        public string DescripcionTabla { get; set; } = string.Empty;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Servicio de documentación
    // Dependencia NuGet: DocumentFormat.OpenXml v2.7.2 (última compatible con .NET 4.5)
    //   packages.config  → <package id="DocumentFormat.OpenXml" version="2.7.2" targetFramework="net45" />
    //   PackageReference → <PackageReference Include="DocumentFormat.OpenXml" Version="2.7.2" />
    // ─────────────────────────────────────────────────────────────────────────────

    public static class DocumentadorService
    {
        // ── Consulta de info de una tabla ─────────────────────────────────────────

        public static async Task<InfoTablaDoc> GetInfoTablaAsync(
            Conexion conexion, string schema, string tabla, string tipo)
        {
            return await Task.Run(() => GetInfoTabla(conexion, schema, tabla, tipo));
        }

        private static InfoTablaDoc GetInfoTabla(
            Conexion conexion, string schema, string tabla, string tipo)
        {
            var info = new InfoTablaDoc { Schema = schema, Nombre = tabla, Tipo = tipo };

            try
            {
                string connStr = ConexionesManager.GetConnectionString(conexion);
                using (var conn = new OdbcConnection(connStr))
                {
                    conn.Open();

                    var pks = GetPKs(conn, conexion.Motor, schema, tabla);
                    var fks = GetFKs(conn, conexion.Motor, schema, tabla);

                    // Obtener descripciones de columnas y de la tabla/vista
                    Dictionary<string, string> descsColumnas;
                    string descTabla;
                    GetDescripciones(conn, conexion.Motor, schema, tabla,
                                     out descsColumnas, out descTabla);
                    info.DescripcionTabla = descTabla;

                    // GetSchema("Columns") funciona en todos los motores vía ODBC
                    var cols = conn.GetSchema("Columns", new string[] { null, schema, tabla });

                    foreach (System.Data.DataRow row in cols.Rows)
                    {
                        string colName = row["COLUMN_NAME"].ToString();
                        string tipoDB = row["TYPE_NAME"].ToString();
                        string longitud = row.Table.Columns.Contains("COLUMN_SIZE") &&
                                          row["COLUMN_SIZE"] != DBNull.Value
                                          ? row["COLUMN_SIZE"].ToString() : string.Empty;

                        // Construir tipo completo con longitud si corresponde
                        string tipoUpper = tipoDB.ToUpper();
                        bool tieneLogitud = tipoUpper.Contains("CHAR") ||
                                            tipoUpper.Contains("BINARY");
                        string tipoCompleto = (tieneLogitud && !string.IsNullOrEmpty(longitud))
                            ? $"{tipoDB.ToUpper()}({longitud})"
                            : tipoDB.ToUpper();

                        string indicador = string.Empty;
                        if (pks.Contains(colName)) indicador = "Pk";
                        else if (fks.Contains(colName)) indicador = "Fk";

                        string descCol;
                        descsColumnas.TryGetValue(colName, out descCol);

                        info.Columnas.Add(new InfoColumnaDoc
                        {
                            Indicador    = indicador,
                            Nombre       = colName,
                            TipoCompleto = tipoCompleto,
                            Descripcion  = descCol ?? string.Empty
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DocumentadorService] Error en {tabla}: {ex.Message}");
            }

            return info;
        }

        // ── PKs por motor ─────────────────────────────────────────────────────────

        private static HashSet<string> GetPKs(OdbcConnection conn, TipoMotor motor, string schema, string tabla)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string sql = null;
                switch (motor)
                {
                    case TipoMotor.MS_SQL:
                        sql = $@"SELECT c.name
                                 FROM sys.indexes i
                                 JOIN sys.index_columns ic ON i.object_id=ic.object_id AND i.index_id=ic.index_id
                                 JOIN sys.columns c ON ic.object_id=c.object_id AND ic.column_id=c.column_id
                                 JOIN sys.tables t ON i.object_id=t.object_id
                                 WHERE i.is_primary_key=1 AND t.name='{tabla}'";
                        break;
                    case TipoMotor.POSTGRES:
                        sql = $@"SELECT a.attname
                                 FROM pg_index ix
                                 JOIN pg_class t ON t.oid=ix.indrelid
                                 JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=ANY(ix.indkey)
                                 WHERE ix.indisprimary AND t.relname='{tabla.ToLower()}'";
                        break;
                    case TipoMotor.DB2:
                        // SYSIBM.SYSCOLUMNS: KEYSEQ > 0 indica columna de PK
                        sql = $"SELECT NAME FROM SYSIBM.SYSCOLUMNS WHERE TBNAME='{tabla.ToUpper()}' AND KEYSEQ > 0";
                        break;
                    case TipoMotor.SQLite:
                        sql = $"PRAGMA table_info({tabla})";
                        break;
                }
                if (sql == null) return set;

                using (var cmd = new OdbcCommand(sql, conn))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        if (motor == TipoMotor.SQLite)
                        {
                            // PRAGMA table_info: cid | name | type | notnull | dflt_value | pk
                            int pkOrd = r.GetOrdinal("pk");
                            int nameOrd = r.GetOrdinal("name");
                            if (!r.IsDBNull(pkOrd) && r.GetInt32(pkOrd) > 0)
                                set.Add(r.GetString(nameOrd));
                        }
                        else
                        {
                            set.Add(r[0].ToString().Trim());
                        }
                    }
                }
            }
            catch { /* silencioso: PK es informativo */ }
            return set;
        }

        // ── FKs por motor ─────────────────────────────────────────────────────────

        private static HashSet<string> GetFKs(OdbcConnection conn, TipoMotor motor, string schema, string tabla)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string sql = null;
                switch (motor)
                {
                    case TipoMotor.MS_SQL:
                        sql = $@"SELECT c.name
                                 FROM sys.foreign_key_columns fkc
                                 JOIN sys.columns c ON fkc.parent_column_id=c.column_id
                                                   AND fkc.parent_object_id=c.object_id
                                 JOIN sys.tables t ON fkc.parent_object_id=t.object_id
                                 WHERE t.name='{tabla}'";
                        break;
                    case TipoMotor.POSTGRES:
                        sql = $@"SELECT ku.column_name
                                 FROM information_schema.table_constraints tc
                                 JOIN information_schema.key_column_usage ku
                                      ON tc.constraint_name=ku.constraint_name
                                      AND tc.table_name=ku.table_name
                                 WHERE tc.constraint_type='FOREIGN KEY'
                                   AND ku.table_name='{tabla.ToLower()}'";
                        break;
                    case TipoMotor.DB2:
                        // SYSIBM.SYSRELS (DB2 v7): FKCOLNAMES contiene nombres separados por '+'
                        sql = $"SELECT FKCOLNAMES FROM SYSIBM.SYSRELS WHERE TBNAME='{tabla.ToUpper()}'";
                        break;
                    case TipoMotor.SQLite:
                        // PRAGMA foreign_key_list: id | seq | table | from | to | ...
                        sql = $"PRAGMA foreign_key_list({tabla})";
                        break;
                }
                if (sql == null) return set;

                using (var cmd = new OdbcCommand(sql, conn))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        if (motor == TipoMotor.DB2)
                        {
                            // FKCOLNAMES: "+COL1+COL2+..." o "COL1+COL2+..."
                            string colnames = r[0].ToString().Trim();
                            foreach (string c in colnames.Split('+'))
                                if (!string.IsNullOrWhiteSpace(c)) set.Add(c.Trim());
                        }
                        else if (motor == TipoMotor.SQLite)
                        {
                            set.Add(r[3].ToString().Trim()); // columna "from"
                        }
                        else
                        {
                            set.Add(r[0].ToString().Trim());
                        }
                    }
                }
            }
            catch { /* silencioso: FK es informativo */ }
            return set;
        }

        // ── Descripciones / comentarios de metadatos por motor ───────────────────

        /// <summary>
        /// Obtiene las descripciones de columnas y la descripción general de la
        /// tabla/vista desde los metadatos del motor de base de datos.
        /// <para>
        /// • MS SQL Server : sys.extended_properties (MS_Description)
        /// • PostgreSQL    : pg_description
        /// • DB2           : SYSCAT.COLUMNS y SYSCAT.TABLES (REMARKS)
        /// • SQLite        : no soporta comentarios nativos → devuelve vacío
        /// </para>
        /// Opera de forma silenciosa ante errores (las descripciones son informativas).
        /// </summary>
        private static void GetDescripciones(
            OdbcConnection conn,
            TipoMotor motor,
            string schema,
            string tabla,
            out Dictionary<string, string> descripsColumnas,
            out string descripTabla)
        {
            descripsColumnas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            descripTabla     = string.Empty;

            try
            {
                switch (motor)
                {
                    // ── SQL Server ─────────────────────────────────────────────────
                    case TipoMotor.MS_SQL:
                    {
                        // minor_id = 0  → descripción de la tabla/vista
                        // minor_id > 0  → descripción de la columna (c.name)
                        // sys.objects incluye tanto TABLE como VIEW
                        string sql = $@"
SELECT ep.minor_id,
       c.name AS col_name,
       CAST(ep.value AS NVARCHAR(4000)) AS descripcion
FROM sys.extended_properties ep
INNER JOIN sys.objects o
       ON ep.major_id = o.object_id
LEFT  JOIN sys.columns c
       ON ep.major_id = c.object_id
      AND ep.minor_id = c.column_id
WHERE ep.name = 'MS_Description'
  AND o.name  = '{tabla}'
ORDER BY ep.minor_id";

                        using (var cmd = new OdbcCommand(sql, conn))
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                string desc = r.IsDBNull(2) ? string.Empty : r[2].ToString().Trim();
                                if (string.IsNullOrEmpty(desc)) continue;

                                int minorId = r.IsDBNull(0) ? 0 : Convert.ToInt32(r[0]);
                                if (minorId == 0)
                                {
                                    descripTabla = desc;
                                }
                                else
                                {
                                    if (!r.IsDBNull(1))
                                        descripsColumnas[r[1].ToString().Trim()] = desc;
                                }
                            }
                        }
                        break;
                    }

                    // ── PostgreSQL ─────────────────────────────────────────────────
                    case TipoMotor.POSTGRES:
                    {
                        // objsubid = 0  → descripción de tabla/vista
                        // objsubid > 0  → descripción de columna (attname)
                        string schemaWhere = string.IsNullOrEmpty(schema)
                            ? string.Empty
                            : $" AND n.nspname = '{schema.ToLower()}'";

                        string sql = $@"
SELECT pd.objsubid,
       a.attname      AS col_name,
       pd.description AS descripcion
FROM pg_description pd
INNER JOIN pg_class pc ON pd.objoid = pc.oid
INNER JOIN pg_namespace n ON pc.relnamespace = n.oid
LEFT  JOIN pg_attribute a
       ON pd.objoid   = a.attrelid
      AND pd.objsubid = a.attnum
WHERE pc.relname = '{tabla.ToLower()}'{schemaWhere}";

                        using (var cmd = new OdbcCommand(sql, conn))
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                string desc = r.IsDBNull(2) ? string.Empty : r[2].ToString().Trim();
                                if (string.IsNullOrEmpty(desc)) continue;

                                int subId = r.IsDBNull(0) ? 0 : Convert.ToInt32(r[0]);
                                if (subId == 0)
                                {
                                    descripTabla = desc;
                                }
                                else
                                {
                                    if (!r.IsDBNull(1))
                                        descripsColumnas[r[1].ToString().Trim()] = desc;
                                }
                            }
                        }
                        break;
                    }

                    // ── DB2 ────────────────────────────────────────────────────────
                    case TipoMotor.DB2:
                    {
                        string schemaFilter = string.IsNullOrEmpty(schema)
                            ? string.Empty
                            : $" AND TABSCHEMA = '{schema.ToUpper()}'";

                        // Descripciones de columnas
                        string sqlCols = $@"
SELECT COLNAME, REMARKS FROM SYSCAT.COLUMNS
WHERE TABNAME = '{tabla.ToUpper()}'{schemaFilter}
  AND REMARKS IS NOT NULL AND REMARKS <> ''";

                        using (var cmd = new OdbcCommand(sqlCols, conn))
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read())
                            {
                                if (!r.IsDBNull(1))
                                    descripsColumnas[r[0].ToString().Trim()] = r[1].ToString().Trim();
                            }
                        }

                        // Descripción de la tabla/vista
                        string sqlTab = $@"
SELECT REMARKS FROM SYSCAT.TABLES
WHERE TABNAME = '{tabla.ToUpper()}'{schemaFilter}";

                        using (var cmd2 = new OdbcCommand(sqlTab, conn))
                        {
                            var val = cmd2.ExecuteScalar();
                            if (val != null && val != DBNull.Value)
                                descripTabla = val.ToString().Trim();
                        }
                        break;
                    }

                    // SQLite: no tiene sistema de comentarios nativo
                    default:
                        break;
                }
            }
            catch { /* silencioso: las descripciones son informativas */ }
        }

        // ── Generación del .docx ──────────────────────────────────────────────────

        /// <summary>
        /// Genera un archivo .docx con una tabla de documentación por cada InfoTablaDoc.
        /// </summary>
        /// <param name="rutaArchivo">Ruta completa del archivo a crear.</param>
        /// <param name="tablas">Lista de tablas/vistas a documentar.</param>
        /// <param name="tituloDocumento">Texto que aparece como primer párrafo del documento.</param>
        public static void GenerarDocumento(
            string rutaArchivo,
            List<InfoTablaDoc> tablas,
            string tituloDocumento)
        {
            using (var doc = WordprocessingDocument.Create(
                rutaArchivo, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();
                mainPart.Document = new Document();
                var body = mainPart.Document.AppendChild(new Body());

                // ── Párrafo de título ──────────────────────────────────────────────
                body.AppendChild(new Paragraph(
                    new ParagraphProperties(
                        new SpacingBetweenLines { Before = "0", After = "120" }),
                    new Run(
                        new RunProperties(
                            new Bold(),
                            new FontSize { Val = "32" },  // 16pt
                            new Color { Val = "1F3864" }),
                        new Text(tituloDocumento))
                ));

                // ── Párrafo con fecha de generación ───────────────────────────────
                body.AppendChild(new Paragraph(
                    new ParagraphProperties(
                        new SpacingBetweenLines { Before = "0", After = "200" }),
                    new Run(
                        new RunProperties(
                            new FontSize { Val = "18" },
                            new Color { Val = "808080" }),
                        new Text($"Generado: {DateTime.Now:dd/MM/yyyy HH:mm}"))
                ));

                // ── Una tabla por cada objeto documentado ──────────────────────────
                for (int i = 0; i < tablas.Count; i++)
                {
                    body.AppendChild(CrearTablaDocumentacion(tablas[i]));

                    // Separador entre tablas (no al final)
                    if (i < tablas.Count - 1)
                        body.AppendChild(new Paragraph(
                            new ParagraphProperties(
                                new SpacingBetweenLines { Before = "0", After = "160" })));
                }

                // ── Propiedades de página (A4, márgenes 1.5cm) ────────────────────
                body.AppendChild(new SectionProperties(
                    new PageSize { Width = 11906, Height = 16838 },
                    new PageMargin { Top = 851, Right = 851, Bottom = 851, Left = 851 }
                ));

                mainPart.Document.Save();
            }
        }

        // ── Construcción de la tabla de una entidad ───────────────────────────────

        private static Table CrearTablaDocumentacion(InfoTablaDoc info)
        {
            // Ancho útil: 11906 - 1702 (márgenes) = 10204 DXA
            // Columnas: indicador | nombre | tipo | descripción
            int[] anchos = { 600, 2800, 2000, 4804 }; // suma = 10204
            int totalAncho = anchos.Sum();

            var table = new Table();

            // Propiedades generales de la tabla
            table.AppendChild(new TableProperties(
                new TableWidth { Width = totalAncho.ToString(), Type = TableWidthUnitValues.Dxa },
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 6, Color = "2E74B5" },
                    new BottomBorder { Val = BorderValues.Single, Size = 6, Color = "2E74B5" },
                    new LeftBorder { Val = BorderValues.Single, Size = 6, Color = "2E74B5" },
                    new RightBorder { Val = BorderValues.Single, Size = 6, Color = "2E74B5" },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "B4C6E7" },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "B4C6E7" }
                ),
                new TableLook { Val = "04A0" }
            ));

            // ── Fila 1: Esquema ────────────────────────────────────────────────────
            table.AppendChild(CrearFilaFusion(
                $"Esquema: {(string.IsNullOrEmpty(info.Schema) ? "(sin esquema)" : info.Schema)}",
                totalAncho, anchos.Length,
                "D6E4F7", "1F3864", bold: true, fontSize: "20"));

            // ── Fila 2: Nombre + tipo ──────────────────────────────────────────────
            string etiquetaTipo = (info.Tipo == "V" || info.Tipo == "VIEW" ||
                                   info.Tipo == "view") ? "Vista 👁" : "Tabla 📊​";
            table.AppendChild(CrearFilaFusion(
                $"Nombre: {info.Nombre}    [{etiquetaTipo}]",
                totalAncho, anchos.Length,
                "EBF3FB", "1F3864", bold: true, fontSize: "20"));

            // ── Fila 3: Encabezados de columnas ───────────────────────────────────
            string[] headers = { "", "Nombre", "Tipo de dato", "Descripción" };
            table.AppendChild(CrearFilaEncabezados(headers, anchos, "2E74B5", "FFFFFF"));

            // ── Filas de datos ─────────────────────────────────────────────────────
            for (int i = 0; i < info.Columnas.Count; i++)
            {
                string filaBG = (i % 2 == 0) ? "FFFFFF" : "EBF3FB";
                table.AppendChild(CrearFilaColumna(info.Columnas[i], anchos, filaBG));
            }

            // ── Fila de descripción general (si la hay) ────────────────────────────
            if (!string.IsNullOrWhiteSpace(info.DescripcionTabla))
                table.AppendChild(CrearFilaDescripcionTabla(
                    info.DescripcionTabla, totalAncho, anchos.Length));

            return table;
        }

        // ── Fila con todas las celdas fusionadas (para encabezados Esquema/Nombre) ──

        private static TableRow CrearFilaFusion(
            string texto, int anchoTotal, int nCols,
            string bgColor, string fgColor,
            bool bold = false, string fontSize = "18")
        {
            var row = new TableRow();
            var cell = new TableCell();

            var cellProps = new TableCellProperties(
                new TableCellWidth { Width = anchoTotal.ToString(), Type = TableWidthUnitValues.Dxa },
                new GridSpan { Val = nCols },
                new Shading { Fill = bgColor, Val = ShadingPatternValues.Clear },
                new TableCellMargin(
                    new TopMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                    new BottomMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                    new LeftMargin { Width = "120", Type = TableWidthUnitValues.Dxa },
                    new RightMargin { Width = "120", Type = TableWidthUnitValues.Dxa })
            );
            cell.AppendChild(cellProps);

            var runProps = new RunProperties(
                new Color { Val = fgColor },
                new FontSize { Val = fontSize });
            if (bold) runProps.AppendChild(new Bold());

            cell.AppendChild(new Paragraph(
                new ParagraphProperties(
                    new SpacingBetweenLines { Before = "0", After = "0" }),
                new Run(runProps, new Text(texto))
            ));
            row.AppendChild(cell);
            return row;
        }

        // ── Fila de encabezados de columna ────────────────────────────────────────

        private static TableRow CrearFilaEncabezados(
            string[] headers, int[] anchos, string bgColor, string fgColor)
        {
            var row = new TableRow();
            for (int i = 0; i < anchos.Length; i++)
            {
                var cell = new TableCell(
                    new TableCellProperties(
                        new TableCellWidth { Width = anchos[i].ToString(), Type = TableWidthUnitValues.Dxa },
                        new Shading { Fill = bgColor, Val = ShadingPatternValues.Clear },
                        new TableCellMargin(
                            new TopMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                            new BottomMargin { Width = "80", Type = TableWidthUnitValues.Dxa },
                            new LeftMargin { Width = "120", Type = TableWidthUnitValues.Dxa },
                            new RightMargin { Width = "120", Type = TableWidthUnitValues.Dxa })),
                    new Paragraph(
                        new ParagraphProperties(
                            new SpacingBetweenLines { Before = "0", After = "0" },
                            new Justification
                            {
                                Val = i == 0
                                ? JustificationValues.Center
                                : JustificationValues.Left
                            }),
                        new Run(
                            new RunProperties(
                                new Bold(),
                                new Color { Val = fgColor },
                                new FontSize { Val = "18" }),
                            new Text(i < headers.Length ? headers[i] : string.Empty)))
                );
                row.AppendChild(cell);
            }
            return row;
        }

        // ── Fila de descripción general de la tabla/vista ─────────────────────────

        /// <summary>
        /// Crea una fila fusionada con fondo ámbar que muestra la descripción general
        /// de la tabla/vista. Se agrega al final de la tabla del docx.
        /// </summary>
        private static TableRow CrearFilaDescripcionTabla(
            string descripcion, int anchoTotal, int nCols)
        {
            var row  = new TableRow();
            var cell = new TableCell();

            cell.AppendChild(new TableCellProperties(
                new TableCellWidth { Width = anchoTotal.ToString(), Type = TableWidthUnitValues.Dxa },
                new GridSpan { Val = nCols },
                // Fondo ámbar claro para diferenciarlo de las filas de columnas
                new Shading { Fill = "FFF2CC", Val = ShadingPatternValues.Clear },
                new TableCellMargin(
                    new TopMargin    { Width = "100", Type = TableWidthUnitValues.Dxa },
                    new BottomMargin { Width = "100", Type = TableWidthUnitValues.Dxa },
                    new LeftMargin   { Width = "120", Type = TableWidthUnitValues.Dxa },
                    new RightMargin  { Width = "120", Type = TableWidthUnitValues.Dxa })
            ));

            var para = new Paragraph(
                new ParagraphProperties(
                    new SpacingBetweenLines { Before = "0", After = "0" }));

            // Etiqueta "📝 Descripción:" en negrita/color ámbar oscuro
            para.AppendChild(new Run(
                new RunProperties(
                    new Bold(),
                    new Color { Val = "7F6000" },
                    new FontSize { Val = "18" }),
                new Text("📝 Descripción:  ") { Space = SpaceProcessingModeValues.Preserve }
            ));

            // Texto de descripción en color marrón oscuro
            para.AppendChild(new Run(
                new RunProperties(
                    new Color { Val = "3D2B00" },
                    new FontSize { Val = "18" }),
                new Text(descripcion)
            ));

            cell.AppendChild(para);
            row.AppendChild(cell);
            return row;
        }

        // ── Fila de datos de una columna ──────────────────────────────────────────

        private static TableRow CrearFilaColumna(
            InfoColumnaDoc col, int[] anchos, string bgColor)
        {
            var row = new TableRow();
            string[] valores = {
                col.Indicador,
                col.Nombre,
                col.TipoCompleto,
                col.Descripcion
            };

            for (int i = 0; i < anchos.Length; i++)
            {
                string val = i < valores.Length ? valores[i] : string.Empty;

                // Color especial para el indicador (Pk → naranja, Fk → verde)
                string colorTexto = "000000";
                bool bold = false;
                if (i == 0)
                {
                    if (val == "Pk") { colorTexto = "C55A11"; bold = true; }
                    else if (val == "Fk") { colorTexto = "375623"; bold = true; }
                }

                var runProps = new RunProperties(
                    new Color { Val = colorTexto },
                    new FontSize { Val = "18" });
                if (bold) runProps.AppendChild(new Bold());

                var cell = new TableCell(
                    new TableCellProperties(
                        new TableCellWidth { Width = anchos[i].ToString(), Type = TableWidthUnitValues.Dxa },
                        new Shading { Fill = bgColor, Val = ShadingPatternValues.Clear },
                        new TableCellMargin(
                            new TopMargin { Width = "60", Type = TableWidthUnitValues.Dxa },
                            new BottomMargin { Width = "60", Type = TableWidthUnitValues.Dxa },
                            new LeftMargin { Width = "120", Type = TableWidthUnitValues.Dxa },
                            new RightMargin { Width = "120", Type = TableWidthUnitValues.Dxa })),
                    new Paragraph(
                        new ParagraphProperties(
                            new SpacingBetweenLines { Before = "0", After = "0" },
                            new Justification
                            {
                                Val = i == 0
                                ? JustificationValues.Center
                                : JustificationValues.Left
                            }),
                        new Run(runProps, new Text(val)))
                );
                row.AppendChild(cell);
            }
            return row;
        }
    }
}