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
        private InfoLado      _ultimoLadoA;
        private InfoLado      _ultimoLadoB;
        private bool          _sincronizandoMotor;

        // Colores de estado
        private static readonly SolidColorBrush ColorSoloA    = new SolidColorBrush(Color.FromRgb(70,  130, 210)); // azul
        private static readonly SolidColorBrush ColorSoloB    = new SolidColorBrush(Color.FromRgb(210,  60,  60)); // rojo
        private static readonly SolidColorBrush ColorDif      = new SolidColorBrush(Color.FromRgb(220, 140,  30)); // naranja
        private static readonly SolidColorBrush ColorIgual    = null; // usa el foreground heredado

        public ComparadorWindow(Dictionary<string, Conexion> conexiones)
        {
            InitializeComponent();
            AplicarTemaActual();
            var main = Application.Current.MainWindow;
            if (main != null) Height = Math.Max(600, main.Height - 20);
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
        {
            FiltrarConexiones(cmbMotorA, cmbConexionA);
            if (!_sincronizandoMotor)
            {
                _sincronizandoMotor = true;
                cmbMotorB.SelectedIndex = cmbMotorA.SelectedIndex;
                _sincronizandoMotor = false;
            }
        }

        private void cmbMotorB_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            FiltrarConexiones(cmbMotorB, cmbConexionB);
            if (!_sincronizandoMotor)
            {
                _sincronizandoMotor = true;
                cmbMotorA.SelectedIndex = cmbMotorB.SelectedIndex;
                _sincronizandoMotor = false;
            }
        }

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

        // ── Cargar / filtrar lista de objetos (tablas+vistas) ─────────────────────

        private void btnCargarObjetosA_Click(object sender, RoutedEventArgs e)
            => CargarObjetos(cmbConexionA, cmbBaseA, lstSchemasA, lstObjetosA);

        private void btnCargarObjetosB_Click(object sender, RoutedEventArgs e)
            => CargarObjetos(cmbConexionB, cmbBaseB, lstSchemasB, lstObjetosB);

        private void btnTodosObjetosA_Click(object sender, RoutedEventArgs e)   => SeleccionarTodos(lstObjetosA, true);
        private void btnNingunoObjetosA_Click(object sender, RoutedEventArgs e) => SeleccionarTodos(lstObjetosA, false);
        private void btnTodosObjetosB_Click(object sender, RoutedEventArgs e)   => SeleccionarTodos(lstObjetosB, true);
        private void btnNingunoObjetosB_Click(object sender, RoutedEventArgs e) => SeleccionarTodos(lstObjetosB, false);

        private void CargarObjetos(ComboBox cmbConexion, ComboBox cmbBase, ListBox lstSchemas, ListBox lstObjetos)
        {
            var conexion = ObtenerConexionSeleccionada(cmbConexion);
            if (conexion == null) return;

            string bd = (cmbBase.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (string.IsNullOrEmpty(bd)) return;

            string connStr = ConexionesManager.CambiarBaseDatos(
                ConexionesManager.GetConnectionString(conexion), bd, conexion.Motor);

            string[] schemas = lstSchemas.SelectedItems.Count > 0
                ? lstSchemas.SelectedItems.OfType<ListBoxItem>().Select(i => i.Content.ToString()).ToArray()
                : null;

            SetStatus("Cargando lista de objetos...");
            Task.Run(() =>
            {
                var objetos = ComparadorService.GetNombresObjetos(connStr, conexion.Motor, schemas);
                Dispatcher.Invoke(() =>
                {
                    lstObjetos.Items.Clear();
                    foreach (var (nombre, esVista) in objetos)
                    {
                        string prefijo = esVista ? "[V] " : "[T] ";
                        lstObjetos.Items.Add(new ListBoxItem
                        {
                            Content = prefijo + nombre,
                            Tag     = esVista
                        });
                    }
                    SetStatus($"{objetos.Count(o => !o.EsVista)} tabla(s) · {objetos.Count(o => o.EsVista)} vista(s) cargadas.");
                });
            });
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

            var opciones = BuildOpciones();

            string[] nombresTabA = null, nombresVistA = null;
            string[] nombresTabB = null, nombresVistB = null;
            if (lstObjetosA.SelectedItems.Count > 0)
            {
                nombresTabA  = lstObjetosA.SelectedItems.OfType<ListBoxItem>()
                    .Where(i => !(bool)i.Tag)
                    .Select(i => i.Content.ToString().Substring(4)).ToArray(); // quita "[T] "
                nombresVistA = lstObjetosA.SelectedItems.OfType<ListBoxItem>()
                    .Where(i => (bool)i.Tag)
                    .Select(i => i.Content.ToString().Substring(4)).ToArray(); // quita "[V] "
                if (nombresTabA.Length  == 0) nombresTabA  = null;
                if (nombresVistA.Length == 0) nombresVistA = null;
            }
            if (lstObjetosB.SelectedItems.Count > 0)
            {
                nombresTabB  = lstObjetosB.SelectedItems.OfType<ListBoxItem>()
                    .Where(i => !(bool)i.Tag)
                    .Select(i => i.Content.ToString().Substring(4)).ToArray();
                nombresVistB = lstObjetosB.SelectedItems.OfType<ListBoxItem>()
                    .Where(i => (bool)i.Tag)
                    .Select(i => i.Content.ToString().Substring(4)).ToArray();
                if (nombresTabB.Length  == 0) nombresTabB  = null;
                if (nombresVistB.Length == 0) nombresVistB = null;
            }

            var ladoA = new InfoLado { Conexion = conA, ConnStr = connStrA, Schemas = schemasA, NombresTablas = nombresTabA, NombresVistas = nombresVistA };
            var ladoB = new InfoLado { Conexion = conB, ConnStr = connStrB, Schemas = schemasB, NombresTablas = nombresTabB, NombresVistas = nombresVistB };

            _ultimoLadoA = ladoA;
            _ultimoLadoB = ladoB;
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

                    if (diff.Estado == DiffEstado.SoloEnA || diff.Estado == DiffEstado.SoloEnB)
                    {
                        var colsReales = diff.Estado == DiffEstado.SoloEnA
                            ? diff.LadoA.Columnas
                            : diff.LadoB.Columnas;
                        foreach (var col in colsReales)
                        {
                            string desc = $"{col.Nombre}: {col.ResumenTipo()}{(col.EsPK ? " PK" : "")}";
                            nodo.Items.Add(CrearNodoDiff(diff.Estado, desc, 14));
                        }
                    }
                    else
                    {
                        foreach (var col in diff.Columnas)
                        {
                            if (!mostrarIguales && col.Estado == DiffEstado.Igual) continue;
                            string desc = FormatColumna(col);
                            nodo.Items.Add(CrearNodoDiff(col.Estado, desc, 14));
                        }
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

        // ── Filtros en vivo (re-poblar sin re-consultar) ──────────────────────────

        private OpcionesComp BuildOpciones() => new OpcionesComp
        {
            CompararTablas  = chkTablas.IsChecked  == true,
            CompararVistas  = chkVistas.IsChecked  == true,
            CompararIndices = chkIndices.IsChecked == true,
            CompararDatos   = chkDatos.IsChecked   == true,
            MostrarIguales  = chkIguales.IsChecked == true
        };

        private void chkTipos_Changed(object sender, RoutedEventArgs e)
        {
            if (_ultimoResultado == null) return;
            PoblarResultados(_ultimoResultado, BuildOpciones());
        }

        private void chkIguales_Changed(object sender, RoutedEventArgs e)
        {
            if (_ultimoResultado == null) return;
            PoblarResultados(_ultimoResultado, BuildOpciones());
        }

        // ── Exportar ──────────────────────────────────────────────────────────────

        private void btnExportar_Click(object sender, RoutedEventArgs e)
        {
            if (_ultimoResultado == null) return;

            var dlg = new SaveFileDialog
            {
                Title      = "Exportar resultados",
                Filter     = "Reporte HTML (*.html)|*.html|Archivo de texto (*.txt)|*.txt|CSV (*.csv)|*.csv",
                DefaultExt = "html",
                FileName   = $"comparacion_{DateTime.Now:yyyyMMdd_HHmm}"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                bool html = dlg.FilterIndex == 1;
                bool csv  = dlg.FilterIndex == 3;
                string contenido = html
                    ? GenerarHtml(_ultimoResultado)
                    : GenerarExportacion(_ultimoResultado, csv);
                System.IO.File.WriteAllText(dlg.FileName, contenido, Encoding.UTF8);
                System.Diagnostics.Process.Start(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al exportar:\n" + ex.Message, "Comparador",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Reporte HTML ──────────────────────────────────────────────────────────

        private string GenerarHtml(ResultadoComp res)
        {
            string bdA  = (cmbBaseA.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "-";
            string bdB  = (cmbBaseB.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "-";
            string conA = (cmbConexionA.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "-";
            string conB = (cmbConexionB.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "-";
            string schA = string.Join(", ", lstSchemasA.SelectedItems.OfType<ListBoxItem>().Select(i => i.Content));
            string schB = string.Join(", ", lstSchemasB.SelectedItems.OfType<ListBoxItem>().Select(i => i.Content));
            if (string.IsNullOrEmpty(schA)) schA = "(todos)";
            if (string.IsNullOrEmpty(schB)) schB = "(todos)";

            int totalDif = res.Tablas.Count(t => t.Estado != DiffEstado.Igual)
                         + res.Vistas.Count(v => v.Estado != DiffEstado.Igual)
                         + res.Indices.Count(i => i.Estado != DiffEstado.Igual);

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang='es'><head><meta charset='utf-8'>");
            sb.AppendLine($"<title>Comparación {conA}/{bdA} vs {conB}/{bdB}</title>");
            sb.AppendLine(@"<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:Segoe UI,Arial,sans-serif;font-size:13px;background:#f4f6f9;color:#222;padding:16px}
h1{font-size:18px;margin-bottom:4px}
.meta{color:#555;font-size:12px;margin-bottom:20px}
.meta span{display:inline-block;margin-right:16px}
section{background:#fff;border:1px solid #dde;border-radius:6px;margin-bottom:16px;overflow:hidden}
h2{font-size:14px;padding:10px 14px;background:#eef0f5;border-bottom:1px solid #dde;display:flex;align-items:center;gap:8px}
.counts{font-size:11px;font-weight:normal;color:#555}
.counts span{padding:1px 6px;border-radius:10px;margin-right:4px}
.c-a{background:#dbeafe;color:#1d4ed8}
.c-b{background:#fee2e2;color:#991b1b}
.c-d{background:#ffedd5;color:#9a3412}
.c-i{background:#f0fdf4;color:#166534}
table{width:100%;border-collapse:collapse}
th{text-align:left;padding:7px 10px;background:#f8f9fb;font-size:11px;color:#666;border-bottom:1px solid #dde;font-weight:600;white-space:nowrap}
td{padding:5px 10px;border-bottom:1px solid #f0f0f4;vertical-align:top}
tr:last-child td{border-bottom:none}
tr.solo-a td{background:#eff6ff}tr.solo-a td:first-child{border-left:3px solid #3b82f6;color:#1d4ed8;font-weight:500}
tr.solo-b td{background:#fff1f2}tr.solo-b td:first-child{border-left:3px solid #ef4444;color:#991b1b;font-weight:500}
tr.diferente td{background:#fffbeb}tr.diferente td:first-child{border-left:3px solid #f59e0b;color:#92400e;font-weight:500}
tr.igual td{color:#444}tr.igual td:first-child{border-left:3px solid #d1d5db}
tr.sub td{font-size:12px}
tr.sub td:first-child{padding-left:28px;color:inherit;font-weight:normal}
.badge{display:inline-block;font-size:10px;padding:1px 5px;border-radius:8px;font-weight:600}
.est-A{color:#1d4ed8}.est-B{color:#991b1b}.est-D{color:#92400e}.est-I{color:#166534}
pre{font-size:11px;white-space:pre-wrap;word-break:break-all;background:#f8f8f8;padding:6px 8px;border-radius:4px;max-height:120px;overflow:auto}
.empty{padding:10px 14px;color:#888;font-size:12px}
</style></head><body>");

            sb.AppendLine($"<h1>Comparación de Bases de Datos</h1>");
            sb.AppendLine($@"<div class='meta'>
  <span>🅰 <strong>{H(conA)}</strong> — {H(bdA)} [{H(schA)}]</span>
  <span>🅱 <strong>{H(conB)}</strong> — {H(bdB)} [{H(schB)}]</span>
  <span>📅 {DateTime.Now:dd/MM/yyyy HH:mm}</span>
  <span>⚠ <strong>{totalDif}</strong> diferencia(s)</span>
</div>");

            // ── Tablas
            HtmlSeccion(sb, "📋 Tablas", res.Tablas.Count,
                res.Tablas.Count(t => t.Estado == DiffEstado.Igual),
                res.Tablas.Count(t => t.Estado == DiffEstado.SoloEnA),
                res.Tablas.Count(t => t.Estado == DiffEstado.SoloEnB),
                res.Tablas.Count(t => t.Estado == DiffEstado.Diferente));

            if (res.Tablas.Count == 0) { sb.AppendLine("<div class='empty'>Sin tablas comparadas.</div>"); }
            else
            {
                sb.AppendLine("<table><thead><tr><th>Tabla</th><th>Estado</th><th>Detalle A</th><th>Detalle B</th></tr></thead><tbody>");
                foreach (var d in res.Tablas)
                {
                    string cls = EstadoCls(d.Estado);
                    string est = EstadoBadge(d.Estado);
                    sb.AppendLine($"<tr class='{cls}'><td>{H(d.Nombre)}</td><td>{est}</td><td></td><td></td></tr>");

                    if (d.Estado == DiffEstado.SoloEnA || d.Estado == DiffEstado.SoloEnB)
                    {
                        var cols = d.Estado == DiffEstado.SoloEnA ? d.LadoA.Columnas : d.LadoB.Columnas;
                        bool esA = d.Estado == DiffEstado.SoloEnA;
                        foreach (var col in cols)
                        {
                            string desc = H(ResumenColumna(col));
                            sb.AppendLine($"<tr class='sub {cls}'><td>{H(col.Nombre)}</td><td></td><td>{(esA ? desc : "")}</td><td>{(!esA ? desc : "")}</td></tr>");
                        }
                    }
                    else
                    {
                        foreach (var c in d.Columnas.Where(x => x.Estado != DiffEstado.Igual))
                        {
                            string detA = c.LadoA != null ? H(ResumenColumna(c.LadoA)) : "";
                            string detB = c.LadoB != null ? H(ResumenColumna(c.LadoB)) : "";
                            string cCls = EstadoCls(c.Estado);
                            sb.AppendLine($"<tr class='sub {cCls}'><td>{H(c.Nombre)}</td><td>{EstadoBadge(c.Estado)}</td><td>{detA}</td><td>{detB}</td></tr>");
                        }
                    }
                }
                sb.AppendLine("</tbody></table>");
            }
            sb.AppendLine("</section>");

            // ── Vistas
            HtmlSeccion(sb, "👁 Vistas", res.Vistas.Count,
                res.Vistas.Count(v => v.Estado == DiffEstado.Igual),
                res.Vistas.Count(v => v.Estado == DiffEstado.SoloEnA),
                res.Vistas.Count(v => v.Estado == DiffEstado.SoloEnB),
                res.Vistas.Count(v => v.Estado == DiffEstado.Diferente));

            if (res.Vistas.Count == 0) { sb.AppendLine("<div class='empty'>Sin vistas comparadas.</div>"); }
            else
            {
                sb.AppendLine("<table><thead><tr><th>Vista</th><th>Estado</th><th>Definición A</th><th>Definición B</th></tr></thead><tbody>");
                foreach (var v in res.Vistas)
                {
                    string cls = EstadoCls(v.Estado);
                    sb.AppendLine($"<tr class='{cls}'><td>{H(v.Nombre)}</td><td>{EstadoBadge(v.Estado)}</td>");
                    if (v.Estado != DiffEstado.Igual)
                    {
                        string defA = v.LadoA?.Definicion ?? "";
                        string defB = v.LadoB?.Definicion ?? "";
                        sb.AppendLine($"<td>{(defA.Length > 0 ? $"<pre>{H(defA)}</pre>" : "")}</td>");
                        sb.AppendLine($"<td>{(defB.Length > 0 ? $"<pre>{H(defB)}</pre>" : "")}</td></tr>");
                    }
                    else sb.AppendLine("<td></td><td></td></tr>");
                }
                sb.AppendLine("</tbody></table>");
            }
            sb.AppendLine("</section>");

            // ── Índices
            HtmlSeccion(sb, "🔑 Índices", res.Indices.Count,
                res.Indices.Count(i => i.Estado == DiffEstado.Igual),
                res.Indices.Count(i => i.Estado == DiffEstado.SoloEnA),
                res.Indices.Count(i => i.Estado == DiffEstado.SoloEnB),
                res.Indices.Count(i => i.Estado == DiffEstado.Diferente));

            if (res.Indices.Count == 0) { sb.AppendLine("<div class='empty'>Sin índices comparados.</div>"); }
            else
            {
                sb.AppendLine("<table><thead><tr><th>Índice</th><th>Estado</th><th>Detalle A</th><th>Detalle B</th></tr></thead><tbody>");
                foreach (var idx in res.Indices)
                {
                    string cls = EstadoCls(idx.Estado);
                    string detA = idx.LadoA != null ? H(ResumenIndice(idx.LadoA)) : "";
                    string detB = idx.LadoB != null ? H(ResumenIndice(idx.LadoB)) : "";
                    sb.AppendLine($"<tr class='{cls}'><td>{H(idx.Nombre)}</td><td>{EstadoBadge(idx.Estado)}</td><td>{detA}</td><td>{detB}</td></tr>");
                }
                sb.AppendLine("</tbody></table>");
            }
            sb.AppendLine("</section>");

            // ── Datos
            if (res.Datos.Count > 0)
            {
                HtmlSeccion(sb, "📊 Datos", res.Datos.Count,
                    res.Datos.Count(d => d.Estado == DiffEstado.Igual), 0, 0,
                    res.Datos.Count(d => d.Estado != DiffEstado.Igual));
                sb.AppendLine("<table><thead><tr><th>Tabla</th><th>Estado</th><th>Filas A</th><th>Filas B</th></tr></thead><tbody>");
                foreach (var d in res.Datos)
                    sb.AppendLine($"<tr class='{EstadoCls(d.Estado)}'><td>{H(d.Tabla)}</td><td>{EstadoBadge(d.Estado)}</td><td>{d.ConteoA:N0}</td><td>{d.ConteoB:N0}</td></tr>");
                sb.AppendLine("</tbody></table></section>");
            }

            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        private static void HtmlSeccion(StringBuilder sb, string titulo, int total, int iguales, int soloA, int soloB, int dif)
        {
            sb.Append($"<section><h2>{titulo} <span class='counts'>");
            if (iguales > 0) sb.Append($"<span class='c-i'>{iguales} iguales</span>");
            if (dif > 0)     sb.Append($"<span class='c-d'>{dif} diferentes</span>");
            if (soloA > 0)   sb.Append($"<span class='c-a'>{soloA} solo en A</span>");
            if (soloB > 0)   sb.Append($"<span class='c-b'>{soloB} solo en B</span>");
            sb.AppendLine($"</span></h2>");
        }

        private static string EstadoCls(DiffEstado e)
        {
            switch (e)
            {
                case DiffEstado.SoloEnA:  return "solo-a";
                case DiffEstado.SoloEnB:  return "solo-b";
                case DiffEstado.Diferente: return "diferente";
                default:                   return "igual";
            }
        }

        private static string EstadoBadge(DiffEstado e)
        {
            switch (e)
            {
                case DiffEstado.SoloEnA:   return "<span class='badge est-A'>Solo en A</span>";
                case DiffEstado.SoloEnB:   return "<span class='badge est-B'>Solo en B</span>";
                case DiffEstado.Diferente: return "<span class='badge est-D'>Diferente</span>";
                default:                   return "<span class='badge est-I'>Igual</span>";
            }
        }

        private static string H(string s) => System.Security.SecurityElement.Escape(s ?? "");

        private string GenerarExportacion(ResultadoComp resultado, bool csv)
        {
            var sb = new StringBuilder();

            if (csv)
                sb.AppendLine("Tipo,Objeto,Estado,Lado A,Lado B");

            // ── Tablas ──
            foreach (var d in resultado.Tablas)
            {
                string estado = EstadoTexto(d.Estado);
                if (csv) sb.AppendLine($"Tabla,\"{d.Nombre}\",{estado},,");
                else     sb.AppendLine($"TABLA\t{d.Nombre}\t{estado}");

                if (d.Estado == DiffEstado.SoloEnA || d.Estado == DiffEstado.SoloEnB)
                {
                    // Mostrar columnas reales del lado que existe
                    var cols  = d.Estado == DiffEstado.SoloEnA ? d.LadoA.Columnas : d.LadoB.Columnas;
                    bool esA  = d.Estado == DiffEstado.SoloEnA;
                    foreach (var col in cols)
                    {
                        string desc = ResumenColumna(col);
                        if (csv) sb.AppendLine($"Columna,\"{d.Nombre}.{col.Nombre}\",{estado},{(esA ? $"\"{desc}\"" : "")},{(!esA ? $"\"{desc}\"" : "")}");
                        else     sb.AppendLine($"    COLUMNA\t{col.Nombre}\t{desc}");
                    }
                }
                else
                {
                    foreach (var c in d.Columnas.Where(x => x.Estado != DiffEstado.Igual))
                    {
                        string detA = c.LadoA != null ? ResumenColumna(c.LadoA) : "";
                        string detB = c.LadoB != null ? ResumenColumna(c.LadoB) : "";
                        if (csv)
                            sb.AppendLine($"Columna,\"{d.Nombre}.{c.Nombre}\",{EstadoTexto(c.Estado)},\"{detA}\",\"{detB}\"");
                        else
                        {
                            sb.AppendLine($"    COLUMNA\t{c.Nombre}\t{EstadoTexto(c.Estado)}");
                            if (!string.IsNullOrEmpty(detA)) sb.AppendLine($"        A: {detA}");
                            if (!string.IsNullOrEmpty(detB)) sb.AppendLine($"        B: {detB}");
                        }
                    }
                }
            }

            // ── Vistas ──
            foreach (var v in resultado.Vistas)
            {
                string estado = EstadoTexto(v.Estado);
                if (csv) sb.AppendLine($"Vista,\"{v.Nombre}\",{estado},,");
                else     sb.AppendLine($"VISTA\t{v.Nombre}\t{estado}");

                if (v.Estado != DiffEstado.Igual)
                {
                    string defA = v.LadoA?.Definicion ?? "";
                    string defB = v.LadoB?.Definicion ?? "";
                    if (csv)
                        sb.AppendLine($"Definición,\"{v.Nombre}\",{estado},\"{defA.Replace("\"","\"\"")}\",\"{defB.Replace("\"","\"\"")}\"");
                    else
                    {
                        if (!string.IsNullOrEmpty(defA)) sb.AppendLine($"    A: {defA}");
                        if (!string.IsNullOrEmpty(defB)) sb.AppendLine($"    B: {defB}");
                    }
                }
            }

            // ── Índices ──
            foreach (var idx in resultado.Indices)
            {
                string estado = EstadoTexto(idx.Estado);
                if (csv) sb.AppendLine($"Índice,\"{idx.Nombre}\",{estado},,");
                else     sb.AppendLine($"INDICE\t{idx.Nombre}\t{estado}");

                if (idx.Estado != DiffEstado.Igual)
                {
                    string detA = idx.LadoA != null ? ResumenIndice(idx.LadoA) : "";
                    string detB = idx.LadoB != null ? ResumenIndice(idx.LadoB) : "";
                    if (csv)
                        sb.AppendLine($"Detalle,\"{idx.Nombre}\",{estado},\"{detA}\",\"{detB}\"");
                    else
                    {
                        if (!string.IsNullOrEmpty(detA)) sb.AppendLine($"    A: {detA}");
                        if (!string.IsNullOrEmpty(detB)) sb.AppendLine($"    B: {detB}");
                    }
                }
            }

            // ── Datos ──
            foreach (var d in resultado.Datos)
            {
                if (csv) sb.AppendLine($"Datos,\"{d.Tabla}\",{EstadoTexto(d.Estado)},\"{d.ConteoA:N0} filas\",\"{d.ConteoB:N0} filas\"");
                else     sb.AppendLine($"DATOS\t{d.Tabla}\t{EstadoTexto(d.Estado)}\tA: {d.ConteoA:N0} filas  /  B: {d.ConteoB:N0} filas");
            }

            return sb.ToString();
        }

        private string ResumenColumna(ColumnaComp col)
            => $"{col.ResumenTipo()}{(col.EsPK ? " PK" : "")}{(col.Nullable ? " NULL" : " NOT NULL")}";

        private string ResumenIndice(IndiceComp idx)
            => $"cols: {idx.Columnas}  único: {idx.EsUnico}  PK: {idx.EsPK}";

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
