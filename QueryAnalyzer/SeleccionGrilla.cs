using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace QueryAnalyzer
{
    /// <summary>Formato en el que se copia al portapapeles un rango de celdas.</summary>
    internal enum FormatoCopia { Json, Tsv, Html, Markdown }

    /// <summary>
    /// Selección de rangos de celdas tipo planilla en la grilla de resultados: recorta lo
    /// seleccionado a un DataTable (que luego consumen los generadores de formatos) y
    /// ofrece los helpers para seleccionar filas y columnas completas desde la celda Nº y
    /// el encabezado.
    /// </summary>
    internal static class SeleccionGrilla
    {
        /// <summary>
        /// Nombre del DataColumn al que está enlazada una columna autogenerada. Se asigna en
        /// AutoGeneratingColumn; no se deduce del Binding ni de SortMemberPath porque ahí WPF
        /// puede escapar el nombre.
        /// </summary>
        public static readonly DependencyProperty NombreColumnaProperty =
            DependencyProperty.RegisterAttached("NombreColumna", typeof(string),
                typeof(SeleccionGrilla), new PropertyMetadata(null));

        public static void RegistrarColumna(DataGridColumn columna, string nombre)
        {
            columna.SetValue(NombreColumnaProperty, nombre);
        }

        /// <summary>
        /// Columna de datos = la autogenerada. La Nº es una DataGridTemplateColumn virtual
        /// que no pertenece al DataTable.
        /// </summary>
        private static bool EsColumnaDatos(DataGridColumn c)
        {
            return c != null && c.GetValue(NombreColumnaProperty) is string;
        }

        // ────────────────────────────────────────────────────────────────────
        // Consulta de la selección
        // ────────────────────────────────────────────────────────────────────

        /// <summary>Hay más de una celda de datos seleccionada (un clic simple no cuenta).</summary>
        public static bool HaySeleccionMultiple(DataGrid grid)
        {
            int n = 0;
            foreach (var celda in grid.SelectedCells)
                if (EsColumnaDatos(celda.Column) && celda.Item is DataRowView && ++n > 1)
                    return true;
            return false;
        }

        /// <summary>
        /// Recorta la selección a un DataTable. Filas en el orden en que se ven en la grilla
        /// (respeta el orden que aplicó el usuario) y columnas en orden visual. Si la selección
        /// no es contigua (Ctrl+clic) las celdas que quedan dentro del rectángulo y no están
        /// seleccionadas salen vacías (DBNull).
        /// </summary>
        public static DataTable ConstruirTabla(DataGrid grid, out int filas, out int columnas)
        {
            filas = 0;
            columnas = 0;

            var vista = grid.ItemsSource as DataView;
            if (vista == null) return null;

            // fila -> columnas seleccionadas en esa fila
            var porFila = new Dictionary<object, HashSet<DataGridColumn>>();
            var columnasSel = new HashSet<DataGridColumn>();
            foreach (var celda in grid.SelectedCells)
            {
                if (!EsColumnaDatos(celda.Column) || !(celda.Item is DataRowView)) continue;
                HashSet<DataGridColumn> set;
                if (!porFila.TryGetValue(celda.Item, out set))
                    porFila[celda.Item] = set = new HashSet<DataGridColumn>();
                set.Add(celda.Column);
                columnasSel.Add(celda.Column);
            }
            if (porFila.Count == 0) return null;

            var columnasOrdenadas = columnasSel.OrderBy(c => c.DisplayIndex).ToList();
            var resultado = new DataTable("Seleccion");
            var nombres = new List<string>();
            foreach (var col in columnasOrdenadas)
            {
                string nombre = (string)col.GetValue(NombreColumnaProperty);
                DataColumn origen = vista.Table.Columns[nombre];
                if (origen == null) continue;
                resultado.Columns.Add(new DataColumn(nombre, origen.DataType));
                nombres.Add(nombre);
            }
            if (nombres.Count == 0) return null;

            var columnaPorNombre = columnasOrdenadas
                .Where(c => nombres.Contains((string)c.GetValue(NombreColumnaProperty)))
                .ToList();

            foreach (object item in grid.Items)
            {
                HashSet<DataGridColumn> sel;
                var drv = item as DataRowView;
                if (drv == null || !porFila.TryGetValue(item, out sel)) continue;

                var nueva = resultado.NewRow();
                for (int i = 0; i < nombres.Count; i++)
                    nueva[i] = sel.Contains(columnaPorNombre[i]) ? drv[nombres[i]] : DBNull.Value;
                resultado.Rows.Add(nueva);
            }

            filas = resultado.Rows.Count;
            columnas = resultado.Columns.Count;
            return resultado;
        }

        // ────────────────────────────────────────────────────────────────────
        // Selección de filas y columnas completas
        // ────────────────────────────────────────────────────────────────────

        private static IEnumerable<DataGridColumn> ColumnasDatosVisibles(DataGrid grid)
        {
            return grid.Columns
                .Where(c => EsColumnaDatos(c) && c.Visibility == Visibility.Visible)
                .OrderBy(c => c.DisplayIndex);
        }

        /// <summary>
        /// Selecciona las filas completas entre desde y hasta (inclusive). Con agregar se
        /// suman a la selección actual; si no, la reemplazan.
        /// </summary>
        public static void SeleccionarFilas(DataGrid grid, object desde, object hasta, bool agregar)
        {
            int i1 = grid.Items.IndexOf(desde);
            int i2 = grid.Items.IndexOf(hasta);
            if (i1 < 0 || i2 < 0) return;
            if (i1 > i2) { int t = i1; i1 = i2; i2 = t; }

            var columnas = ColumnasDatosVisibles(grid).ToList();
            var existentes = PrepararSeleccion(grid, agregar);

            for (int i = i1; i <= i2; i++)
            {
                object item = grid.Items[i];
                if (!(item is DataRowView)) continue;
                foreach (var col in columnas)
                {
                    var info = new DataGridCellInfo(item, col);
                    if (existentes.Add(info))
                        grid.SelectedCells.Add(info);
                }
            }
        }

        /// <summary>
        /// Vacía la selección (salvo que se agregue) y devuelve el conjunto de celdas ya
        /// seleccionadas, para no duplicarlas sin recorrer SelectedCells en cada alta.
        /// </summary>
        private static HashSet<DataGridCellInfo> PrepararSeleccion(DataGrid grid, bool agregar)
        {
            if (!agregar)
            {
                grid.SelectedCells.Clear();
                return new HashSet<DataGridCellInfo>();
            }
            return new HashSet<DataGridCellInfo>(grid.SelectedCells);
        }

        /// <summary>Selecciona todas las celdas de una columna.</summary>
        public static void SeleccionarColumna(DataGrid grid, DataGridColumn columna, bool agregar)
        {
            if (!EsColumnaDatos(columna)) return;
            var existentes = PrepararSeleccion(grid, agregar);

            foreach (object item in grid.Items)
            {
                if (!(item is DataRowView)) continue;
                var info = new DataGridCellInfo(item, columna);
                if (existentes.Add(info))
                    grid.SelectedCells.Add(info);
            }
        }

        // ────────────────────────────────────────────────────────────────────
        // Portapapeles
        // ────────────────────────────────────────────────────────────────────

        public static FormatoCopia FormatoPorDefecto(AppConfig config)
        {
            return config != null && string.Equals(config.FormatoCopiaSeleccion, "Tsv", StringComparison.OrdinalIgnoreCase)
                ? FormatoCopia.Tsv
                : FormatoCopia.Json;
        }

        public static string NombreFormato(FormatoCopia formato)
        {
            switch (formato)
            {
                case FormatoCopia.Tsv:      return "TSV";
                case FormatoCopia.Html:     return "HTML";
                case FormatoCopia.Markdown: return "Markdown";
                default:                    return "JSON";
            }
        }

        /// <summary>Copia la tabla al portapapeles en el formato pedido.</summary>
        public static void Copiar(DataTable tabla, FormatoCopia formato)
        {
            switch (formato)
            {
                case FormatoCopia.Tsv:
                    Clipboard.SetText(FormatosTexto.CrearTsv(tabla, true));
                    break;

                case FormatoCopia.Markdown:
                    Clipboard.SetText(FormatosTexto.CrearTablaMarkdown(tabla, true));
                    break;

                case FormatoCopia.Html:
                    // HTML para pegar en Word/correo y, como respaldo, TSV para editores de texto.
                    var datos = new DataObject();
                    datos.SetData(DataFormats.Html,
                        FormatosTexto.ConPrefijoCfHtml(FormatosTexto.CrearTablaHtml(tabla, true, estiloEnLinea: true)));
                    datos.SetText(FormatosTexto.CrearTsv(tabla, true));
                    Clipboard.SetDataObject(datos, true);
                    break;

                default:
                    Clipboard.SetText(new ExcelService().CrearJson(tabla));
                    break;
            }
        }
    }
}
