using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace QueryAnalyzer
{
    public class ExcelService
    {
        // --------------------------------------------------------------------
        // 🔹 MÉTODO PRINCIPAL PARA MULTIHOJAS
        // --------------------------------------------------------------------
        public byte[] CrearExcelMultiplesHojas(Dictionary<string, System.Data.DataTable> hojas)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                using (SpreadsheetDocument document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
                {
                    WorkbookPart workbookPart = document.AddWorkbookPart();
                    workbookPart.Workbook = new Workbook();

                    // Estilos comunes
                    WorkbookStylesPart stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
                    stylesPart.Stylesheet = CrearEstilos();
                    stylesPart.Stylesheet.Save();

                    Sheets sheets = workbookPart.Workbook.AppendChild(new Sheets());
                    uint sheetId = 1;

                    foreach (var hoja in hojas)
                    {
                        string nombreHoja = hoja.Key;
                        var dt = hoja.Value;

                        WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                        SheetData sheetData = new SheetData();

                        worksheetPart.Worksheet = new Worksheet(sheetData);
                        Sheet sheet = new Sheet()
                        {
                            Id = workbookPart.GetIdOfPart(worksheetPart),
                            SheetId = sheetId++,
                            Name = SanearNombreHoja(nombreHoja)
                        };
                        sheets.Append(sheet);

                        // Si no hay filas, igual agregamos encabezado vacío
                        if (dt.Columns.Count == 0)
                        {
                            sheetData.Append(new Row());
                            continue;
                        }

                        // Encabezados
                        Row headerRow = new Row();
                        foreach (System.Data.DataColumn col in dt.Columns)
                            headerRow.Append(CreateCell(col.ColumnName, 1));
                        sheetData.Append(headerRow);

                        // Filas de datos
                        uint rowIndex = 0;
                        foreach (System.Data.DataRow row in dt.Rows)
                        {
                            Row newRow = new Row();
                            foreach (System.Data.DataColumn col in dt.Columns)
                            {
                                string value = row[col] != DBNull.Value ? Convert.ToString(row[col]) : "";

                                // estilo 3 = fila par, estilo 2 = fila impar
                                uint style = (rowIndex % 2 == 0) ? 2U : 3U;
                                newRow.Append(CreateCell(value, style));
                            }
                            sheetData.Append(newRow);
                            rowIndex++;
                        }

                        // Ajuste de anchos
                        Columns cols = AutoSizeColumns(dt);
                        worksheetPart.Worksheet.InsertAt(cols, 0);
                    }

                    workbookPart.Workbook.Save();
                }
                return stream.ToArray();
            }
        }

        // --------------------------------------------------------------------
        // 🔹 FUNCIONES AUXILIARES
        // --------------------------------------------------------------------

        private string SanearNombreHoja(string nombre)
        {
            if (string.IsNullOrEmpty(nombre)) return "Hoja";
            string limpio = new string(nombre.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '_').ToArray());
            return limpio.Length > 31 ? limpio.Substring(0, 31) : limpio;
        }

        private Cell CreateCell(string text, uint styleIndex)
        {
            return new Cell()
            {
                DataType = CellValues.String,
                CellValue = new CellValue(text ?? ""),
                StyleIndex = styleIndex
            };
        }

        private Columns AutoSizeColumns(System.Data.DataTable dt)
        {
            Columns cols = new Columns();
            for (int i = 0; i < dt.Columns.Count; i++)
            {
                double maxLength = dt.Columns[i].ColumnName.Length;

                foreach (System.Data.DataRow row in dt.Rows)
                {
                    if (row[i] != DBNull.Value)
                    {
                        double len = Convert.ToString(row[i]).Length;
                        if (len > maxLength) maxLength = len;
                    }
                }

                cols.Append(new Column()
                {
                    Min = (UInt32)(i + 1),
                    Max = (UInt32)(i + 1),
                    Width = Math.Max(10, maxLength + 2),
                    CustomWidth = true
                });
            }
            return cols;
        }

        private Stylesheet CrearEstilos()
        {
            Fonts fonts = new Fonts(
                new Font( // 0 - default Roboto
                    new FontName() { Val = "Calibri,Roboto,Arial" },
                    new FontSize() { Val = 10 },
                    new Color() { Rgb = "000000" }
                ),
                new Font( // 1 - encabezado Roboto Bold
                    new FontName() { Val = "Calibri,Roboto,Arial" },
                    new FontSize() { Val = 10 },
                    new Bold(),
                    new Color() { Rgb = "000000" }
                ),
                new Font( // 2 - texto normal Roboto
                    new FontName() { Val = "Calibri,Roboto,Arial" },
                    new FontSize() { Val = 10 },
                    new Color() { Rgb = "000000" }
                )
        );


            Fills fills = new Fills(
                new Fill(new PatternFill() { PatternType = PatternValues.None }), // 0
                new Fill(new PatternFill() { PatternType = PatternValues.Gray125 }), // 1
                new Fill(new PatternFill(new ForegroundColor { Rgb = "FFA7C7FF" }) { PatternType = PatternValues.Solid }), // 2 encabezado azul
                new Fill(new PatternFill(new ForegroundColor { Rgb = "FFA7D7F0" }) { PatternType = PatternValues.Solid }) // 3 filas alternas
            );

            Borders borders = new Borders(
                new Border(),
                new Border(
                    new LeftBorder() { Style = BorderStyleValues.Thin },
                    new RightBorder() { Style = BorderStyleValues.Thin },
                    new TopBorder() { Style = BorderStyleValues.Thin },
                    new BottomBorder() { Style = BorderStyleValues.Thin },
                    new DiagonalBorder())
            );

            CellFormats cellFormats = new CellFormats(
                new CellFormat(), // 0 - default
                new CellFormat() // 1 - encabezado
                {
                    FontId = 1,
                    FillId = 2,
                    BorderId = 1,
                    ApplyFont = true,
                    ApplyFill = true,
                    ApplyBorder = true,
                    Alignment = new Alignment()
                    {
                        Horizontal = HorizontalAlignmentValues.Center,
                        Vertical = VerticalAlignmentValues.Center,
                        WrapText = true
                    }
                },
                new CellFormat() // 2 - fila impar
                {
                    FontId = 2,
                    FillId = 0,
                    BorderId = 1,
                    ApplyFont = true,
                    ApplyBorder = true,
                    Alignment = new Alignment()
                    {
                        Horizontal = HorizontalAlignmentValues.Left,
                        Vertical = VerticalAlignmentValues.Center
                    }
                },
                new CellFormat() // 3 - fila par (color alterno)
                {
                    FontId = 2,
                    FillId = 3,
                    BorderId = 1,
                    ApplyFont = true,
                    ApplyBorder = true,
                    Alignment = new Alignment()
                    {
                        Horizontal = HorizontalAlignmentValues.Left,
                        Vertical = VerticalAlignmentValues.Center
                    }
                }
            );


            return new Stylesheet(fonts, fills, borders, cellFormats);
        }

        public void GuardarArchivo(byte[] bytes, string ruta)
        {
            File.WriteAllBytes(ruta, bytes);
        }
    }
}




//using DocumentFormat.OpenXml;
//using DocumentFormat.OpenXml.Packaging;
//using DocumentFormat.OpenXml.Spreadsheet;
//using System;
//using System.Collections.Generic;
//using System.IO;
//using System.Linq;
//using System.Reflection;

//namespace QueryAnalyzer
//{
//    public class ExcelService
//    {
//        public byte[] CrearExcelGenerico<T>(IEnumerable<T> data, string[] columnHeaders = null, string sheetName = "Datos")
//        {
//            using (MemoryStream stream = new MemoryStream())
//            {
//                using (SpreadsheetDocument document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
//                {
//                    WorkbookPart workbookPart = document.AddWorkbookPart();
//                    workbookPart.Workbook = new Workbook();

//                    // Estilos
//                    WorkbookStylesPart stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
//                    stylesPart.Stylesheet = CrearEstilos();
//                    stylesPart.Stylesheet.Save();

//                    WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
//                    SheetData sheetData = new SheetData();
//                    worksheetPart.Worksheet = new Worksheet(sheetData);

//                    Sheets sheets = workbookPart.Workbook.AppendChild(new Sheets());
//                    Sheet sheet = new Sheet() { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = sheetName };
//                    sheets.Append(sheet);

//                    // Determinar columnas
//                    List<string> headers = new List<string>();
//                    if (columnHeaders != null && columnHeaders.Length > 0)
//                        headers.AddRange(columnHeaders);
//                    else
//                        headers.AddRange(GetHeadersFromType(typeof(T)));

//                    // Agregar encabezado (con estilo 1)
//                    Row headerRow = new Row();
//                    foreach (var header in headers)
//                        headerRow.Append(CreateCell(header, 1)); // estilo de encabezado
//                    sheetData.Append(headerRow);

//                    // Agregar datos (con estilo 2)
//                    foreach (var item in data)
//                    {
//                        Row newRow = new Row();
//                        var values = GetValuesFromObject(item);
//                        foreach (var value in values)
//                            newRow.Append(CreateCell(value, 2));
//                        sheetData.Append(newRow);
//                    }

//                    // Ajustar ancho de columnas
//                    Columns columns = AutoSizeColumns(headers.Count, headers);
//                    worksheetPart.Worksheet.InsertAt(columns, 0);

//                    workbookPart.Workbook.Save();
//                }
//                return stream.ToArray();
//            }
//        }

//        private Stylesheet CrearEstilos()
//        {
//            // Fuentes
//            Fonts fonts = new Fonts(
//                new Font(), // 0 - default
//                new Font(new Bold(), new Color() { Rgb = "000000" }, new FontSize() { Val = 11 }), // 1 - bold para encabezado
//                new Font(new Color() { Rgb = "000000" }, new FontSize() { Val = 11 }) // 2 - texto normal
//            );

//            // Rellenos
//            Fills fills = new Fills(
//                new Fill(new PatternFill() { PatternType = PatternValues.None }),  // 0
//                new Fill(new PatternFill() { PatternType = PatternValues.Gray125 }), // 1
//                new Fill(new PatternFill(new ForegroundColor { Rgb = "FFD9D9D9" }) { PatternType = PatternValues.Solid }) // 2 - gris claro
//            );

//            // Bordes
//            Borders borders = new Borders(
//                new Border(), // 0 default
//                new Border( // 1 borde fino
//                    new LeftBorder(new Color() { Auto = true }) { Style = BorderStyleValues.Thin },
//                    new RightBorder(new Color() { Auto = true }) { Style = BorderStyleValues.Thin },
//                    new TopBorder(new Color() { Auto = true }) { Style = BorderStyleValues.Thin },
//                    new BottomBorder(new Color() { Auto = true }) { Style = BorderStyleValues.Thin },
//                    new DiagonalBorder())
//            );

//            // Alineaciones y formato de celdas
//            CellFormats cellFormats = new CellFormats(
//                new CellFormat(), // 0 - default
//                new CellFormat() // 1 - encabezado
//                {
//                    FontId = 1,
//                    FillId = 2,
//                    BorderId = 1,
//                    ApplyFont = true,
//                    ApplyFill = true,
//                    ApplyBorder = true,
//                    Alignment = new Alignment() { Horizontal = HorizontalAlignmentValues.Center, Vertical = VerticalAlignmentValues.Center, WrapText = true }
//                },
//                new CellFormat() // 2 - normal con borde
//                {
//                    FontId = 2,
//                    FillId = 0,
//                    BorderId = 1,
//                    ApplyFont = true,
//                    ApplyBorder = true,
//                    Alignment = new Alignment() { Horizontal = HorizontalAlignmentValues.Left, Vertical = VerticalAlignmentValues.Center }
//                }
//            );

//            return new Stylesheet(fonts, fills, borders, cellFormats);
//        }

//        public byte[] CrearExcelMultiplesHojas(Dictionary<string, IEnumerable<object>> hojas)
//        {
//            using (MemoryStream stream = new MemoryStream())
//            {
//                using (SpreadsheetDocument document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
//                {
//                    WorkbookPart workbookPart = document.AddWorkbookPart();
//                    workbookPart.Workbook = new Workbook();

//                    WorkbookStylesPart stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
//                    stylesPart.Stylesheet = CrearEstilos();
//                    stylesPart.Stylesheet.Save();

//                    Sheets sheets = workbookPart.Workbook.AppendChild(new Sheets());
//                    uint sheetId = 1;

//                    foreach (var hoja in hojas)
//                    {
//                        WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
//                        SheetData sheetData = new SheetData();

//                        worksheetPart.Worksheet = new Worksheet(sheetData);
//                        Sheet sheet = new Sheet()
//                        {
//                            Id = workbookPart.GetIdOfPart(worksheetPart),
//                            SheetId = sheetId++,
//                            Name = hoja.Key.Length > 31 ? hoja.Key.Substring(0, 31) : hoja.Key
//                        };
//                        sheets.Append(sheet);

//                        var items = hoja.Value.ToList();
//                        if (items.Count == 0) continue;

//                        var headers = GetHeadersFromType(items[0].GetType());

//                        // Agregar encabezado
//                        Row headerRow = new Row();
//                        foreach (var header in headers)
//                            headerRow.Append(CreateCell(header, 1));
//                        sheetData.Append(headerRow);

//                        // Agregar datos
//                        foreach (var item in items)
//                        {
//                            Row newRow = new Row();
//                            var values = GetValuesFromObject(item);
//                            foreach (var value in values)
//                                newRow.Append(CreateCell(value, 2));
//                            sheetData.Append(newRow);
//                        }

//                        Columns columns = AutoSizeColumns(headers.Count, headers);
//                        worksheetPart.Worksheet.InsertAt(columns, 0);
//                    }

//                    workbookPart.Workbook.Save();
//                }
//                return stream.ToArray();
//            }
//        }

//        private Cell CreateCell(string text, uint styleIndex)
//        {
//            return new Cell()
//            {
//                DataType = CellValues.String,
//                CellValue = new CellValue(text ?? ""),
//                StyleIndex = styleIndex
//            };
//        }

//        private Columns AutoSizeColumns(int count, List<string> headers)
//        {
//            Columns cols = new Columns();
//            for (int i = 0; i < count; i++)
//            {
//                double ancho = Math.Max(10, headers[i].Length + 2);
//                cols.Append(new Column()
//                {
//                    Min = (UInt32)(i + 1),
//                    Max = (UInt32)(i + 1),
//                    Width = ancho,
//                    CustomWidth = true
//                });
//            }
//            return cols;
//        }

//        private List<string> GetHeadersFromType(Type type)
//        {
//            if (type == typeof(string) || type.IsPrimitive)
//                return new List<string>() { "Valor" };

//            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string))
//                return new List<string>() { "Columna1", "Columna2", "Columna3" };

//            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
//                       .Select(p => p.Name).ToList();
//        }

//        private List<string> GetValuesFromObject<T>(T item)
//        {
//            if (item == null) return new List<string>() { "" };

//            if (item is string s) return new List<string>() { s };

//            if (item is IEnumerable<string> list) return list.ToList();

//            //var props = item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
//            //return props.Select(p => Convert.ToString(p.GetValue(item, null) ?? "")).ToList();

//            var props = item.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
//            var valores = new List<string>();

//            foreach (var p in props)
//            {
//                try
//                {
//                    object valor = p.GetValue(item, null);
//                    valores.Add(valor != null ? Convert.ToString(valor) : "");
//                }
//                catch (Exception err)
//                {
//                    string error = err.Message;
//                }            
//            }

//            return valores;
//        }

//        public void GuardarArchivo(byte[] bytes, string ruta)
//        {
//            File.WriteAllBytes(ruta, bytes);
//        }
//    }
//}
