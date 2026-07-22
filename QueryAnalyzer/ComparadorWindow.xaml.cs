using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace QueryAnalyzer
{
    public partial class ComparadorWindow : Window
    {
        private readonly Dictionary<string, Conexion> _conexiones;
        private ResultadoComp _ultimoResultado;

        // Colores de estado
        private static readonly SolidColorBrush ColorSoloA    = new SolidColorBrush(Color.FromRgb(70,  130, 210)); // azul
        private static readonly SolidColorBrush ColorSoloB    = new SolidColorBrush(Color.FromRgb(210,  60,  60)); // rojo
        private static readonly SolidColorBrush ColorDif      = new SolidColorBrush(Color.FromRgb(220, 140,  30)); // naranja
        private static readonly SolidColorBrush ColorIgual    = null; // usa el foreground heredado

        public ComparadorWindow(Dictionary<string, Conexion> conexiones)
        {
            InitializeComponent();
            AplicarTemaActual();
            _conexiones = conexiones ?? new Dictionary<string, Conexion>();
            CargarConexiones();
        }

        private void AplicarTemaActual()
        {
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow == null) return;
            var tema = mainWindow.Resources.MergedDictionaries.FirstOrDefault();
            if (tema == null) return;
            var wd = Resources.MergedDictionaries;
            if (wd.Count > 0) wd[0] = tema;
            else wd.Add(tema);
        }

        // ── Inicialización ────────────────────────────────────────────────────────

        private static readonly (string Label, TipoMotor? Motor)[] Motores =
        {
            ("(Todos)",    null),
            ("SQL Server", TipoMotor.MS_SQL),
            ("DB2",        TipoMotor.DB2),
            ("PostgreSQL", TipoMotor.POSTGRES),
            ("SQLite",     TipoMotor.SQLite),
        };

        private void CargarFiltrosMotor()
        {
            foreach (var m in Motores)
            {
                cmbMotorA.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Motor });
                cmbMotorB.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Motor });
            }
            cmbMotorA.SelectedIndex = 0;
            cmbMotorB.SelectedIndex = 0;
        }

        private void CargarConexiones()
        {
            CargarFiltrosMotor();
            FiltrarConexiones(cmbMotorA, cmbConexionA);
            FiltrarConexiones(cmbMotorB, cmbConexionB);
        }

        private void FiltrarConexiones(ComboBox cmbMotor, ComboBox cmbConexion)
        {
            TipoMotor? motorFiltro = (cmbMotor.SelectedItem as ComboBoxItem)?.Tag as TipoMotor?;

            string selActual = (cmbConexion.SelectedItem as ComboBoxItem)?.Content?.ToString();
            cmbConexion.Items.Clear();

            var filtradas = motorFiltro.HasValue
                ? _conexiones.Where(kv => kv.Value.Motor == motorFiltro.Value)
                : _conexiones.AsEnumerable();

            foreach (var kv in filtradas.OrderBy(k => k.Key))
                cmbConexion.Items.Add(new ComboBoxItem { Content = kv.Key, Tag = kv.Value });

            // Restaurar selección anterior si sigue disponible
            if (selActual != null)
            {
                var match = cmbConexion.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => i.Content.ToString() == selActual);
                if (match != null) cmbConexion.SelectedItem = match;
            }
        }

        // ── Filtro por motor ──────────────────────────────────────────────────────

        private void cmbMotorA_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => FiltrarConexiones(cmbMotorA, cmbConexionA);

        private void cmbMotorB_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => FiltrarConexiones(cmbMotorB, cmbConexionB);

        // ── Cambio de conexión ────────────────────────────────────────────────────

        private void cmbConexionA_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => CargarBases(cmbConexionA, cmbBaseA, lstSchemasA);

        private void cmbConexionB_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => CargarBases(cmbConexionB, cmbBaseB, lstSchemasB);

        private void CargarBases(ComboBox cmbConexion, ComboBox cmbBase, ListBox lstSchemas)
        {
            cmbBase.Items.Clear();
            lstSchemas.Items.Clear();

            var conexion = ObtenerConexionSeleccionada(cmbConexion);
            if (conexion == null) return;

            string connStr = ConexionesManager.GetConnectionString(conexion);
            SetStatus($"Cargando bases de datos...");

            Task.Run(() =>
            {
                var bases = ComparadorService.GetDatabases(connStr, conexion.Motor);
                Dispatcher.Invoke(() =>
                {
                    cmbBase.Items.Clear();
                    foreach (string bd in bases)
                        cmbBase.Items.Add(new ComboBoxItem { Content = bd });

                    // Si la conexión ya tiene una base configurada, preseleccionarla
                    if (!string.IsNullOrWhiteSpace(conexion.BaseDatos))
                    {
                        var match = cmbBase.Items.OfType<ComboBoxItem>()
                            .FirstOrDefault(i => i.Content.ToString().Equals(conexion.BaseDatos, StringComparison.OrdinalIgnoreCase));
                        if (match != null) cmbBase.SelectedItem = match;
                        else if (cmbBase.Items.Count > 0) cmbBase.SelectedIndex = 0;
                    }
                    else if (cmbBase.Items.Count > 0)
                        cmbBase.SelectedIndex = 0;

                    SetStatus("");
                });
            });
        }

        private void cmbBaseA_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => CargarSchemas(cmbConexionA, cmbBaseA, lstSchemasA);

        private void cmbBaseB_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => CargarSchemas(cmbConexionB, cmbBaseB, lstSchemasB);

        private void CargarSchemas(ComboBox cmbConexion, ComboBox cmbBase, ListBox lstSchemas)
        {
            lstSchemas.Items.Clear();

            var conexion = ObtenerConexionSeleccionada(cmbConexion);
            if (conexion == null) return;

            string bdSeleccionada = (cmbBase.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (string.IsNullOrEmpty(bdSeleccionada)) return;

            string connStr = ConexionesManager.CambiarBaseDatos(
                ConexionesManager.GetConnectionString(conexion), bdSeleccionada, conexion.Motor);

            SetStatus("Cargando esquemas...");
            Task.Run(() =>
            {
                var schemas = ComparadorService.GetSchemas(connStr, conexion.Motor);
                Dispatcher.Invoke(() =>
                {
                    lstSchemas.Items.Clear();
                    foreach (string s in schemas)
                        lstSchemas.Items.Add(new ListBoxItem { Content = s });

                    // Preseleccionar "dbo" / "public" / "main" si existe; sino el primero
                    string[] defaults = { "dbo", "public", "main" };
                    bool preselected = false;
                    foreach (ListBoxItem item in lstSchemas.Items)
                    {
                        if (defaults.Contains(item.Content.ToString(), StringComparer.OrdinalIgnoreCase))
                        {
                            lstSchemas.SelectedItem = item;
                            preselected = true;
                            break; // solo uno por defecto
                        }
                    }
                    if (!preselected && lstSchemas.Items.Count > 0)
                        lstSchemas.SelectedIndex = 0;

                    SetStatus("");
                });
            });
        }

        // ── Botones Todos / Ninguno de esquemas ───────────────────────────────────

        private void btnTodosA_Click(object sender, RoutedEventArgs e)   => SeleccionarTodos(lstSchemasA, true);
        private void btnNingunoA_Click(object sender, RoutedEventArgs e) => SeleccionarTodos(lstSchemasA, false);
        private void btnTodosB_Click(object sender, RoutedEventArgs e)   => SeleccionarTodos(lstSchemasB, true);
        private void btnNingunoB_Click(object sender, RoutedEventArgs e) => SeleccionarTodos(lstSchemasB, false);

        private void SeleccionarTodos(ListBox lst, bool seleccionar)
        {
            if (seleccionar)
                foreach (ListBoxItem item in lst.Items)
                    if (!lst.SelectedItems.Contains(item))
                        lst.SelectedItems.Add(item);
            else
                lst.SelectedItems.Clear();
        }

        // ── Comparar ──────────────────────────────────────────────────────────────

        private async void btnComparar_Click(object sender, RoutedEventArgs e)
        {
            var conA = ObtenerConexionSeleccionada(cmbConexionA);
            var conB = ObtenerConexionSeleccionada(cmbConexionB);
            if (conA == null || conB == null)
            {
                MessageBox.Show("Seleccioná una conexión para cada lado.", "Comparador",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string bdA = (cmbBaseA.SelectedItem as ComboBoxItem)?.Content?.ToString();
            string bdB = (cmbBaseB.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (string.IsNullOrEmpty(bdA) || string.IsNullOrEmpty(bdB))
            {
                MessageBox.Show("Seleccioná la base de datos para cada lado.", "Comparador",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string connStrA = ConexionesManager.CambiarBaseDatos(
                ConexionesManager.GetConnectionString(conA), bdA, conA.Motor);
            string connStrB = ConexionesManager.CambiarBaseDatos(
                ConexionesManager.GetConnectionString(conB), bdB, conB.Motor);

            // Si no hay ningún esquema seleccionado → null = todos los esquemas
            string[] schemasA = lstSchemasA.SelectedItems.Count > 0
                ? lstSchemasA.SelectedItems.OfType<ListBoxItem>().Select(i => i.Content.ToString()).ToArray()
                : null;
            string[] schemasB = lstSchemasB.SelectedItems.Count > 0
                ? lstSchemasB.SelectedItems.OfType<ListBoxItem>().Select(i => i.Content.ToString()).ToArray()
                : null;

            var opciones = new OpcionesComp
            {
                CompararTablas  = chkTablas.IsChecked  == true,
                CompararVistas  = chkVistas.IsChecked  == true,
                CompararIndices = chkIndices.IsChecked == true,
                CompararDatos   = chkDatos.IsChecked   == true,
                MostrarIguales  = chkIguales.IsChecked == true
            };

            var ladoA = new InfoLado { Conexion = conA, ConnStr = connStrA, Schemas = schemasA };
            var ladoB = new InfoLado { Conexion = conB, ConnStr = connStrB, Schemas = schemasB };

            btnComparar.IsEnabled = false;
            btnExportar.IsEnabled = false;
            tvResultados.Items.Clear();
            pbProgreso.Visibility = Visibility.Visible;
            pbProgreso.IsIndeterminate = true;

            try
            {
                _ultimoResultado = await Task.Run(() =>
                    ComparadorService.Comparar(ladoA, ladoB, opciones, msg => Dispatcher.Invoke(() => SetStatus(msg))));

                PoblarResultados(_ultimoResultado, opciones);
                btnExportar.IsEnabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error durante la comparación:\n" + ex.Message,
                    "Comparador", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Error en la comparación.");
            }
            finally
            {
                btnComparar.IsEnabled = true;
                pbProgreso.IsIndeterminate = false;
                pbProgreso.Visibility = Visibility.Collapsed;
            }
        }

        // ── Poblar TreeView ───────────────────────────────────────────────────────

        private void PoblarResultados(ResultadoComp resultado, OpcionesComp opciones)
        {
            tvResultados.Items.Clear();
            bool mostrarIguales = opciones.MostrarIguales;

            // ── Tablas
            if (opciones.CompararTablas)
            {
                int nIgual = resultado.Tablas.Count(t => t.Estado == DiffEstado.Igual);
                int nSoloA = resultado.Tablas.Count(t => t.Estado == DiffEstado.SoloEnA);
                int nSoloB = resultado.Tablas.Count(t => t.Estado == DiffEstado.SoloEnB);
                int nDif   = resultado.Tablas.Count(t => t.Estado == DiffEstado.Diferente);

                var nodoTablas = CrearNodoCategoria(
                    $"Tablas  ({nIgual} iguales · {nDif} diferentes · {nSoloA} solo en A · {nSoloB} solo en B)");

                foreach (var diff in resultado.Tablas)
                {
                    if (!mostrarIguales && diff.Estado == DiffEstado.Igual) continue;
                    var nodo = CrearNodoDiff(diff.Estado, FormatNombreTabla(diff), null);

                    foreach (var col in diff.Columnas)
                    {
                        if (!mostrarIguales && col.Estado == DiffEstado.Igual) continue;
                        string desc = FormatColumna(col);
                        nodo.Items.Add(CrearNodoDiff(col.Estado, desc, 14));
                    }

                    nodoTablas.Items.Add(nodo);
                }
                nodoTablas.IsExpanded = nDif > 0 || nSoloA > 0 || nSoloB > 0;
                tvResultados.Items.Add(nodoTablas);
            }

            // ── Vistas
            if (opciones.CompararVistas)
            {
                int nIgual = resultado.Vistas.Count(v => v.Estado == DiffEstado.Igual);
                int nSoloA = resultado.Vistas.Count(v => v.Estado == DiffEstado.SoloEnA);
                int nSoloB = resultado.Vistas.Count(v => v.Estado == DiffEstado.SoloEnB);
                int nDif   = resultado.Vistas.Count(v => v.Estado == DiffEstado.Diferente);

                var nodoVistas = CrearNodoCategoria(
                    $"Vistas  ({nIgual} iguales · {nDif} diferentes · {nSoloA} solo en A · {nSoloB} solo en B)");

                foreach (var diff in resultado.Vistas)
                {
                    if (!mostrarIguales && diff.Estado == DiffEstado.Igual) continue;
                    string label = diff.Nombre;
                    if (diff.Estado == DiffEstado.Diferente)
                        label += "  (definición diferente)";
                    nodoVistas.Items.Add(CrearNodoDiff(diff.Estado, label, null));
                }
                nodoVistas.IsExpanded = nDif > 0 || nSoloA > 0 || nSoloB > 0;
                tvResultados.Items.Add(nodoVistas);
            }

            // ── Índices
            if (opciones.CompararIndices)
            {
                int nIgual = resultado.Indices.Count(i => i.Estado == DiffEstado.Igual);
                int nSoloA = resultado.Indices.Count(i => i.Estado == DiffEstado.SoloEnA);
                int nSoloB = resultado.Indices.Count(i => i.Estado == DiffEstado.SoloEnB);
                int nDif   = resultado.Indices.Count(i => i.Estado == DiffEstado.Diferente);

                var nodoIndices = CrearNodoCategoria(
                    $"Índices  ({nIgual} iguales · {nDif} diferentes · {nSoloA} solo en A · {nSoloB} solo en B)");

                foreach (var diff in resultado.Indices)
                {
                    if (!mostrarIguales && diff.Estado == DiffEstado.Igual) continue;
                    string label = $"{diff.Nombre}  [{(diff.LadoA ?? diff.LadoB).Tabla}]";
                    if (diff.Estado == DiffEstado.Diferente)
                        label += $"  A: {diff.LadoA?.Columnas}  /  B: {diff.LadoB?.Columnas}";
                    nodoIndices.Items.Add(CrearNodoDiff(diff.Estado, label, null));
                }
                nodoIndices.IsExpanded = nDif > 0 || nSoloA > 0 || nSoloB > 0;
                tvResultados.Items.Add(nodoIndices);
            }

            // ── Datos
            if (opciones.CompararDatos && resultado.Datos.Count > 0)
            {
                int nIgual = resultado.Datos.Count(d => d.Estado == DiffEstado.Igual);
                int nDif   = resultado.Datos.Count(d => d.Estado != DiffEstado.Igual);

                var nodoDatos = CrearNodoCategoria(
                    $"Datos  ({nIgual} con igual conteo · {nDif} con diferente conteo)");

                foreach (var diff in resultado.Datos)
                {
                    if (!mostrarIguales && diff.Estado == DiffEstado.Igual) continue;
                    string label = $"{diff.Tabla}  (A: {diff.ConteoA:N0} filas  /  B: {diff.ConteoB:N0} filas)";
                    nodoDatos.Items.Add(CrearNodoDiff(diff.Estado, label, null));
                }
                nodoDatos.IsExpanded = nDif > 0;
                tvResultados.Items.Add(nodoDatos);
            }

            int totalDiffs = resultado.Tablas.Count(t => t.Estado != DiffEstado.Igual)
                           + resultado.Vistas.Count(v => v.Estado != DiffEstado.Igual)
                           + resultado.Indices.Count(i => i.Estado != DiffEstado.Igual);
            SetStatus(totalDiffs == 0 ? "Las bases de datos son idénticas en los aspectos comparados."
                                      : $"Comparación completada. {totalDiffs} diferencias encontradas.");
        }

        private TreeViewItem CrearNodoCategoria(string texto)
        {
            return new TreeViewItem
            {
                Header     = new TextBlock { Text = texto, FontWeight = FontWeights.SemiBold },
                IsExpanded = true
            };
        }

        private TreeViewItem CrearNodoDiff(DiffEstado estado, string texto, double? fontSize)
        {
            string prefijo;
            SolidColorBrush color;
            switch (estado)
            {
                case DiffEstado.SoloEnA:
                    prefijo = "→ "; color = ColorSoloA; break;
                case DiffEstado.SoloEnB:
                    prefijo = "← "; color = ColorSoloB; break;
                case DiffEstado.Diferente:
                    prefijo = "⚠ "; color = ColorDif;   break;
                default:
                    prefijo = "✓ "; color = ColorIgual; break;
            }

            var tb = new TextBlock { Text = prefijo + texto };
            if (fontSize.HasValue) tb.FontSize = fontSize.Value;
            if (color != null) tb.Foreground = color;

            return new TreeViewItem { Header = tb };
        }

        private string FormatNombreTabla(DiffTabla diff)
        {
            switch (diff.Estado)
            {
                case DiffEstado.Diferente:
                    int nCols = diff.Columnas.Count(c => c.Estado != DiffEstado.Igual);
                    return $"{diff.Nombre}  ({nCols} columna(s) diferente(s))";
                default:
                    return diff.Nombre;
            }
        }

        private string FormatColumna(DiffColumna col)
        {
            switch (col.Estado)
            {
                case DiffEstado.Igual:
                    return $"{col.Nombre}: {col.LadoA.ResumenTipo()}";
                case DiffEstado.SoloEnA:
                    return $"{col.Nombre}: {col.LadoA.ResumenTipo()}{(col.LadoA.EsPK ? " PK" : "")}";
                case DiffEstado.SoloEnB:
                    return $"{col.Nombre}: {col.LadoB.ResumenTipo()}{(col.LadoB.EsPK ? " PK" : "")}";
                default: // Diferente
                    var sb = new StringBuilder();
                    sb.Append(col.Nombre).Append(": ");
                    if (col.LadoA.ResumenTipo() != col.LadoB.ResumenTipo())
                        sb.Append($"tipo {col.LadoA.ResumenTipo()} → {col.LadoB.ResumenTipo()}");
                    if (col.LadoA.Nullable != col.LadoB.Nullable)
                        sb.Append($"  nullable {col.LadoA.Nullable} → {col.LadoB.Nullable}");
                    if (col.LadoA.EsPK != col.LadoB.EsPK)
                        sb.Append($"  PK {col.LadoA.EsPK} → {col.LadoB.EsPK}");
                    return sb.ToString();
            }
        }

        // ── Filtro mostrar iguales (re-poblar sin re-consultar) ───────────────────

        private void chkIguales_Changed(object sender, RoutedEventArgs e)
        {
            if (_ultimoResultado == null) return;
            PoblarResultados(_ultimoResultado, new OpcionesComp
            {
                CompararTablas  = chkTablas.IsChecked  == true,
                CompararVistas  = chkVistas.IsChecked  == true,
                CompararIndices = chkIndices.IsChecked == true,
                CompararDatos   = chkDatos.IsChecked   == true,
                MostrarIguales  = chkIguales.IsChecked == true
            });
        }

        // ── Exportar ──────────────────────────────────────────────────────────────

        private void btnExportar_Click(object sender, RoutedEventArgs e)
        {
            if (_ultimoResultado == null) return;

            var dlg = new SaveFileDialog
            {
                Title            = "Exportar resultados",
                Filter           = "Archivo de texto (*.txt)|*.txt|CSV (*.csv)|*.csv",
                DefaultExt       = "txt",
                FileName         = $"comparacion_{DateTime.Now:yyyyMMdd_HHmm}"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                bool csv = dlg.FilterIndex == 2;
                string contenido = GenerarExportacion(_ultimoResultado, csv);
                System.IO.File.WriteAllText(dlg.FileName, contenido, Encoding.UTF8);
                MessageBox.Show("Exportación completada.", "Comparador",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al exportar:\n" + ex.Message, "Comparador",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string GenerarExportacion(ResultadoComp resultado, bool csv)
        {
            var sb = new StringBuilder();
            string sep = csv ? "," : "\t";

            if (csv)
                sb.AppendLine("Tipo,Objeto,Estado,Detalle");

            foreach (var d in resultado.Tablas)
            {
                string estado = EstadoTexto(d.Estado);
                if (csv) sb.AppendLine($"Tabla,\"{d.Nombre}\",{estado},");
                else     sb.AppendLine($"TABLA\t{d.Nombre}\t{estado}");

                foreach (var c in d.Columnas.Where(x => x.Estado != DiffEstado.Igual))
                {
                    if (csv) sb.AppendLine($"  Columna,\"{d.Nombre}.{c.Nombre}\",{EstadoTexto(c.Estado)},\"{FormatColumna(c)}\"");
                    else     sb.AppendLine($"  COLUMNA\t{d.Nombre}.{c.Nombre}\t{EstadoTexto(c.Estado)}\t{FormatColumna(c)}");
                }
            }
            foreach (var v in resultado.Vistas)
            {
                if (csv) sb.AppendLine($"Vista,\"{v.Nombre}\",{EstadoTexto(v.Estado)},");
                else     sb.AppendLine($"VISTA\t{v.Nombre}\t{EstadoTexto(v.Estado)}");
            }
            foreach (var i in resultado.Indices)
            {
                if (csv) sb.AppendLine($"Índice,\"{i.Nombre}\",{EstadoTexto(i.Estado)},");
                else     sb.AppendLine($"INDICE\t{i.Nombre}\t{EstadoTexto(i.Estado)}");
            }
            foreach (var d in resultado.Datos)
            {
                string detalle = $"A:{d.ConteoA} B:{d.ConteoB}";
                if (csv) sb.AppendLine($"Datos,\"{d.Tabla}\",{EstadoTexto(d.Estado)},{detalle}");
                else     sb.AppendLine($"DATOS\t{d.Tabla}\t{EstadoTexto(d.Estado)}\t{detalle}");
            }
            return sb.ToString();
        }

        private string EstadoTexto(DiffEstado e)
        {
            switch (e)
            {
                case DiffEstado.Igual:    return "Igual";
                case DiffEstado.SoloEnA:  return "Solo en A";
                case DiffEstado.SoloEnB:  return "Solo en B";
                default:                  return "Diferente";
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private Conexion ObtenerConexionSeleccionada(ComboBox cmb)
            => (cmb.SelectedItem as ComboBoxItem)?.Tag as Conexion;

        private void SetStatus(string msg)
        {
            if (!Dispatcher.CheckAccess())
            { Dispatcher.Invoke(() => SetStatus(msg)); return; }
            txtStatus.Text = msg;
        }
    }
}
