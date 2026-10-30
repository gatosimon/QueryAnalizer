using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net;
using System.Text;

namespace QueryAnalyzer
{
    /// <summary>
    /// Formatos de texto de un DataTable que no existían en ExcelService: TSV (texto
    /// tabulado, se pega directo en Excel), HTML y Markdown. Los valores se escriben como
    /// los muestra la grilla (fechas dd/MM/yyyy HH:mm:ss, cultura actual en los números).
    /// </summary>
    internal static class FormatosTexto
    {
        private const int MaxBytesHex = 32;

        // ────────────────────────────────────────────────────────────────────
        // Valores
        // ────────────────────────────────────────────────────────────────────

        public static string FormatearValor(object valor)
        {
            if (valor == null || valor == DBNull.Value) return "";
            if (valor is DateTime fecha) return fecha.ToString("dd/MM/yyyy HH:mm:ss");
            if (valor is byte[] bytes) return BytesAHex(bytes);
            return Convert.ToString(valor);
        }

        private static string BytesAHex(byte[] bytes)
        {
            if (bytes.Length == 0) return "";
            int n = Math.Min(bytes.Length, MaxBytesHex);
            string hex = "0x" + BitConverter.ToString(bytes, 0, n).Replace("-", "");
            return bytes.Length > n ? hex + "..." : hex;
        }

        private static bool EsNumerica(Type t)
        {
            return t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) ||
                   t == typeof(int)  || t == typeof(uint)  || t == typeof(long)  || t == typeof(ulong)  ||
                   t == typeof(float) || t == typeof(double) || t == typeof(decimal);
        }

        // ────────────────────────────────────────────────────────────────────
        // TSV
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Texto tabulado con la convención de Excel: el campo que contiene tab, salto de
        /// línea o comillas va entre comillas (y las comillas se duplican).
        /// </summary>
        public static string CrearTsv(DataTable dt, bool incluyeEncabezados)
        {
            var sb = new StringBuilder();

            if (incluyeEncabezados)
                sb.AppendLine(string.Join("\t", dt.Columns.Cast<DataColumn>().Select(c => EscaparCampoTsv(c.ColumnName))));

            foreach (DataRow fila in dt.Rows)
            {
                var campos = new List<string>(dt.Columns.Count);
                foreach (DataColumn col in dt.Columns)
                    campos.Add(EscaparCampoTsv(FormatearValor(fila[col])));
                sb.AppendLine(string.Join("\t", campos));
            }

            return sb.ToString();
        }

        private static string EscaparCampoTsv(string campo)
        {
            if (string.IsNullOrEmpty(campo)) return "";
            bool comillas = campo.IndexOfAny(new[] { '\t', '\n', '\r', '"' }) >= 0;
            return comillas ? '"' + campo.Replace("\"", "\"\"") + '"' : campo;
        }

        // ────────────────────────────────────────────────────────────────────
        // HTML
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Documento HTML autocontenido (CSS en línea, claro u oscuro según el sistema del
        /// lector) con una sección por hoja.
        /// </summary>
        public static string CrearHtml(IDictionary<string, DataTable> hojas, bool incluyeEncabezados)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"es\">");
            sb.AppendLine("<head>");
            sb.AppendLine("<meta charset=\"utf-8\">");
            sb.AppendLine("<title>Resultados</title>");
            sb.AppendLine("<style>");
            sb.AppendLine(":root { --fondo: #ffffff; --texto: #1f2328; --borde: #c9ced6; --encabezado: #eef1f5; --alterna: #f6f8fa; --tenue: #5b6470; }");
            sb.AppendLine("@media (prefers-color-scheme: dark) { :root { --fondo: #1e1f22; --texto: #e3e5e8; --borde: #444851; --encabezado: #2b2d31; --alterna: #25272b; --tenue: #9aa0a8; } }");
            sb.AppendLine("body { background: var(--fondo); color: var(--texto); font-family: Segoe UI, Arial, sans-serif; font-size: 14px; margin: 24px; }");
            sb.AppendLine("h2 { font-size: 16px; margin: 24px 0 8px; }");
            sb.AppendLine("p.nota { color: var(--tenue); margin: 0 0 8px; }");
            sb.AppendLine("table { border-collapse: collapse; }");
            sb.AppendLine("th, td { border: 1px solid var(--borde); padding: 4px 10px; vertical-align: top; }");
            sb.AppendLine("th { background: var(--encabezado); text-align: left; }");
            sb.AppendLine("tbody tr:nth-child(even) td { background: var(--alterna); }");
            sb.AppendLine("td.num { text-align: right; }");
            sb.AppendLine("</style>");
            sb.AppendLine("</head>");
            sb.AppendLine("<body>");

            foreach (var hoja in hojas)
            {
                sb.AppendLine("<h2>" + WebUtility.HtmlEncode(hoja.Key) + "</h2>");
                sb.AppendLine("<p class=\"nota\">" + hoja.Value.Rows.Count + " filas, " + hoja.Value.Columns.Count + " columnas</p>");
                sb.AppendLine(CrearTablaHtml(hoja.Value, incluyeEncabezados, estiloEnLinea: false));
            }

            sb.AppendLine("</body>");
            sb.AppendLine("</html>");
            return sb.ToString();
        }

        /// <summary>
        /// Solo el elemento &lt;table&gt;. Con estiloEnLinea los bordes van en cada celda,
        /// para que el pegado en Word o en un correo conserve el aspecto de tabla.
        /// </summary>
        public static string CrearTablaHtml(DataTable dt, bool incluyeEncabezados, bool estiloEnLinea)
        {
            const string estiloTabla = " style=\"border-collapse:collapse\"";
            const string estiloCelda = " style=\"border:1px solid #999999;padding:2px 8px\"";
            const string estiloCeldaNum = " style=\"border:1px solid #999999;padding:2px 8px;text-align:right\"";
            const string estiloTh = " style=\"border:1px solid #999999;padding:2px 8px;background:#eeeeee;text-align:left\"";

            var sb = new StringBuilder();
            sb.AppendLine("<table" + (estiloEnLinea ? estiloTabla : "") + ">");

            if (incluyeEncabezados)
            {
                sb.AppendLine("<thead><tr>");
                foreach (DataColumn col in dt.Columns)
                    sb.AppendLine("<th" + (estiloEnLinea ? estiloTh : "") + ">" + WebUtility.HtmlEncode(col.ColumnName) + "</th>");
                sb.AppendLine("</tr></thead>");
            }

            sb.AppendLine("<tbody>");
            foreach (DataRow fila in dt.Rows)
            {
                sb.Append("<tr>");
                foreach (DataColumn col in dt.Columns)
                {
                    bool num = EsNumerica(col.DataType);
                    string atributos = estiloEnLinea
                        ? (num ? estiloCeldaNum : estiloCelda)
                        : (num ? " class=\"num\"" : "");
                    string texto = WebUtility.HtmlEncode(FormatearValor(fila[col])).Replace("\n", "<br>");
                    sb.Append("<td" + atributos + ">" + texto + "</td>");
                }
                sb.AppendLine("</tr>");
            }
            sb.AppendLine("</tbody>");
            sb.Append("</table>");
            return sb.ToString();
        }

        /// <summary>
        /// Envuelve un fragmento HTML con el encabezado del formato CF_HTML del portapapeles
        /// de Windows. Los offsets son en bytes UTF-8.
        /// </summary>
        public static string ConPrefijoCfHtml(string fragmento)
        {
            const string plantillaEncabezado =
                "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
            const string antes = "<html><body>\r\n<!--StartFragment-->";
            const string despues = "<!--EndFragment-->\r\n</body></html>";

            var utf8 = Encoding.UTF8;
            int largoEncabezado = string.Format(plantillaEncabezado, 0, 0, 0, 0).Length;   // largo fijo: D10
            int startHtml = largoEncabezado;
            int startFragment = startHtml + utf8.GetByteCount(antes);
            int endFragment = startFragment + utf8.GetByteCount(fragmento);
            int endHtml = endFragment + utf8.GetByteCount(despues);

            return string.Format(plantillaEncabezado, startHtml, endHtml, startFragment, endFragment)
                   + antes + fragmento + despues;
        }

        // ────────────────────────────────────────────────────────────────────
        // Markdown
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Tablas Markdown (una sección "##" por hoja). Markdown exige fila de encabezado:
        /// sin encabezados se deja vacía.
        /// </summary>
        public static string CrearMarkdown(IDictionary<string, DataTable> hojas, bool incluyeEncabezados)
        {
            var sb = new StringBuilder();
            bool primera = true;
            foreach (var hoja in hojas)
            {
                if (!primera) sb.AppendLine();
                primera = false;
                if (hojas.Count > 1)
                {
                    sb.AppendLine("## " + hoja.Key);
                    sb.AppendLine();
                }
                sb.Append(CrearTablaMarkdown(hoja.Value, incluyeEncabezados));
            }
            return sb.ToString();
        }

        public static string CrearTablaMarkdown(DataTable dt, bool incluyeEncabezados)
        {
            var sb = new StringBuilder();
            var cols = dt.Columns.Cast<DataColumn>().ToList();
            if (cols.Count == 0) return "";

            sb.AppendLine("| " + string.Join(" | ", cols.Select(c => incluyeEncabezados ? EscaparMarkdown(c.ColumnName) : " ")) + " |");
            sb.AppendLine("| " + string.Join(" | ", cols.Select(c => EsNumerica(c.DataType) ? "---:" : "---")) + " |");

            foreach (DataRow fila in dt.Rows)
                sb.AppendLine("| " + string.Join(" | ", cols.Select(c => EscaparMarkdown(FormatearValor(fila[c])))) + " |");

            return sb.ToString();
        }

        private static string EscaparMarkdown(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("|", "\\|").Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");
        }
    }
}
