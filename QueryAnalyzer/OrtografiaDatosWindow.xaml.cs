using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace QueryAnalyzer
{
    /// <summary>
    /// Busca posibles errores ortográficos en los datos de las columnas de texto de la base conectada.
    /// Solo lee: nunca modifica datos.
    /// </summary>
    public partial class OrtografiaDatosWindow : Window
    {
        private readonly Conexion _conn;
        private readonly OrtografiaService _svc;
        private readonly Action<string> _verEnEditor;

        private readonly ObservableCollection<TablaOrtografia> _tablas = new ObservableCollection<TablaOrtografia>();
        private readonly ObservableCollection<ColumnaOrtografia> _columnas = new ObservableCollection<ColumnaOrtografia>();
        private readonly ObservableCollection<HallazgoOrtografico> _hallazgos = new ObservableCollection<HallazgoOrtografico>();
        private readonly ICollectionView _vistaTablas;
        private readonly ICollectionView _vistaColumnas;

        private CancellationTokenSource _cts;
        private bool _ocupado;
        private bool _analizando;

        private class ProgresoColumna
        {
            public ColumnaOrtografia Columna;
            public int Indice;
            public int Total;
            public string Estado;
            public ResultadoColumnaOrtografia Resultado;
        }

        /// <param name="verEnEditor">Pone una consulta en el editor SQL de la ventana principal.</param>
        public OrtografiaDatosWindow(Conexion conn, string connStr, Action<string> verEnEditor)
        {
            InitializeComponent();
            AplicarTemaActual();
            _conn = conn;
            _svc = new OrtografiaService(conn, connStr);
            _verEnEditor = verEnEditor;
            Title = $"Revisión ortográfica — {conn.Nombre}";

            _vistaTablas = CollectionViewSource.GetDefaultView(_tablas);
            dgTablas.ItemsSource = _vistaTablas;
            _vistaColumnas = CollectionViewSource.GetDefaultView(_columnas);
            dgColumnas.ItemsSource = _vistaColumnas;
            dgHallazgos.ItemsSource = _hallazgos;
            AplicarFiltroColumnas();

            Loaded += async (s, e) => await CargarTablasAsync();
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

        // ── Estado de la interfaz ─────────────────────────────────────────

        private void SetOcupado(bool ocupado, string estado = null)
        {
            _ocupado = ocupado;
            if (estado != null) txtEstado.Text = estado;
            ActualizarBotones();
        }

        private void ActualizarBotones()
        {
            btnCargarColumnas.IsEnabled = !_ocupado;
            btnAnalizar.IsEnabled = !_ocupado && ColumnasVisibles().Count > 0;
            btnQuitarColumnas.IsEnabled = !_ocupado && _columnas.Count > 0;
            btnCancelar.IsEnabled = _analizando;
            btnExportar.IsEnabled = !_ocupado && _hallazgos.Count > 0;
            dgTablas.IsEnabled = !_ocupado;
            dgColumnas.IsEnabled = !_ocupado;
            txtFiltro.IsEnabled = !_ocupado;
        }

        private void ActualizarResumen()
        {
            if (_hallazgos.Count == 0) { txtResumen.Text = "HALLAZGOS"; return; }
            var partes = _hallazgos.GroupBy(h => h.Categoria).Select(g => g.Key + " " + g.Count());
            txtResumen.Text = $"HALLAZGOS: {_hallazgos.Count}  ({string.Join(", ", partes)})";
        }

        // ── Tablas ────────────────────────────────────────────────────────

        private async Task CargarTablasAsync()
        {
            SetOcupado(true, "Cargando tablas...");
            try
            {
                var lista = await Task.Run(() => _svc.GetTablas());
                foreach (var t in lista) _tablas.Add(t);

                var esquemas = lista.Select(t => t.Schema).Where(s => !string.IsNullOrEmpty(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
                cbEsquema.Items.Clear();
                cbEsquema.Items.Add("(Todos)");
                foreach (var s in esquemas) cbEsquema.Items.Add(s);
                cbEsquema.SelectedIndex = 0;
                panelEsquema.Visibility = esquemas.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

                txtEstado.Text = $"{_tablas.Count} tabla(s). Tildá las que querés revisar y cargá sus columnas de texto.";
            }
            catch (Exception ex)
            {
                txtEstado.Text = "No se pudieron cargar las tablas.";
                MessageBox.Show(this, "No se pudieron cargar las tablas:\n" + ex.Message, "Revisión ortográfica",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            SetOcupado(false);
        }

        private void txtFiltro_TextChanged(object sender, TextChangedEventArgs e)
        {
            AplicarFiltroTablas();
        }

        private void cbEsquema_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AplicarFiltroTablas();
        }

        /// <summary>Esquema y texto se combinan: se ven las tablas que cumplen las dos cosas.</summary>
        private void AplicarFiltroTablas()
        {
            // Los primeros eventos llegan durante InitializeComponent, antes de armar la vista
            if (_vistaTablas == null) return;

            string f = txtFiltro.Text.Trim();
            string esquema = cbEsquema.SelectedIndex > 0 ? cbEsquema.SelectedItem as string : null;
            if (string.IsNullOrEmpty(f) && esquema == null)
            {
                _vistaTablas.Filter = null;
                return;
            }
            _vistaTablas.Filter = o =>
            {
                var t = (TablaOrtografia)o;
                if (esquema != null && !string.Equals(t.Schema, esquema, StringComparison.OrdinalIgnoreCase)) return false;
                return string.IsNullOrEmpty(f) || t.NombreCompleto.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0;
            };
        }

        private void btnSelTodas_Click(object sender, RoutedEventArgs e)
        {
            foreach (TablaOrtografia t in _vistaTablas) t.Incluir = true;
        }

        private void btnSelNinguna_Click(object sender, RoutedEventArgs e)
        {
            foreach (TablaOrtografia t in _vistaTablas) t.Incluir = false;
        }

        // ── Columnas ──────────────────────────────────────────────────────

        private async void btnCargarColumnas_Click(object sender, RoutedEventArgs e)
        {
            var elegidas = _tablas.Where(t => t.Incluir).ToList();
            if (elegidas.Count == 0)
            {
                MessageBox.Show(this, "Tildá al menos una tabla.", "Revisión ortográfica",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SetOcupado(true, "Buscando columnas de texto...");
            _columnas.Clear();
            string conexion = _conn.Nombre;
            try
            {
                var resultado = await Task.Run(() =>
                {
                    var cols = new List<ColumnaOrtografia>();
                    var errores = new List<string>();
                    foreach (var t in elegidas)
                    {
                        try { cols.AddRange(_svc.GetColumnasTexto(t)); }
                        catch (Exception ex) { errores.Add(t.NombreCompleto + ": " + ex.Message); }
                    }
                    return Tuple.Create(cols, errores);
                });

                foreach (var c in resultado.Item1)
                {
                    bool ignorada = OrtografiaManager.EsColumnaIgnorada(conexion, c.Clave);
                    c.EsAuditoria = MainWindow.EsColumnaAuditoria(c.Nombre);
                    c.Revisar = !ignorada && !c.EsAuditoria && c.Puntaje >= 50;
                    if (ignorada) c.Estado = "Ignorada";
                    _columnas.Add(c);
                }

                var visibles = ColumnasVisibles();
                int ocultas = _columnas.Count - visibles.Count;
                txtEstado.Text = $"{visibles.Count} columna(s) de texto" +
                    (ocultas > 0 ? $" ({ocultas} de auditoría oculta(s))" : "") +
                    $", {visibles.Count(c => c.Revisar)} tildada(s). Ajustá la selección y tocá Analizar.";
                if (resultado.Item2.Count > 0)
                    MessageBox.Show(this, "No se pudieron leer las columnas de:\n" + string.Join("\n", resultado.Item2),
                        "Revisión ortográfica", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                txtEstado.Text = "No se pudieron cargar las columnas.";
                MessageBox.Show(this, "No se pudieron cargar las columnas:\n" + ex.Message, "Revisión ortográfica",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            SetOcupado(false);
        }

        /// <summary>Columnas que se ven en la grilla; las ocultas (auditoría) no se tildan ni se analizan.</summary>
        private List<ColumnaOrtografia> ColumnasVisibles()
        {
            return _vistaColumnas.Cast<ColumnaOrtografia>().ToList();
        }

        private void chkOcultarAuditoria_Changed(object sender, RoutedEventArgs e)
        {
            AplicarFiltroColumnas();
            if (_columnas.Count == 0) return;
            int ocultas = _columnas.Count - ColumnasVisibles().Count;
            txtEstado.Text = ocultas > 0
                ? $"{ocultas} columna(s) de auditoría oculta(s): no se analizan."
                : "Columnas de auditoría visibles.";
        }

        private void AplicarFiltroColumnas()
        {
            // El primer Checked llega durante InitializeComponent, antes de armar la vista
            if (_vistaColumnas == null) return;
            _vistaColumnas.Filter = chkOcultarAuditoria.IsChecked == true
                ? (Predicate<object>)(o => !((ColumnaOrtografia)o).EsAuditoria)
                : null;
            ActualizarBotones();
        }

        private void btnColTodas_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in ColumnasVisibles()) c.Revisar = true;
        }

        private void btnColNinguna_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in ColumnasVisibles()) c.Revisar = false;
        }

        private void btnColProbables_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in ColumnasVisibles()) c.Revisar = c.Puntaje >= 35 && c.Estado != "Ignorada";
        }

        /// <summary>Saca de la lista las columnas seleccionadas. No toca la base ni lo que se ignoró: al volver a cargar las columnas reaparecen.</summary>
        private void QuitarColumnasSeleccionadas()
        {
            if (_ocupado) return;
            var elegidas = dgColumnas.SelectedItems.Cast<ColumnaOrtografia>().ToList();
            if (elegidas.Count == 0)
            {
                txtEstado.Text = "Seleccioná en la lista las columnas que querés quitar.";
                return;
            }
            foreach (var c in elegidas) _columnas.Remove(c);
            txtEstado.Text = $"{elegidas.Count} columna(s) quitada(s) de la lista. Para recuperarlas, cargá las columnas de nuevo.";
            ActualizarBotones();
        }

        private void btnQuitarColumnas_Click(object sender, RoutedEventArgs e)
        {
            QuitarColumnasSeleccionadas();
        }

        private void menuQuitarColumnas_Click(object sender, RoutedEventArgs e)
        {
            QuitarColumnasSeleccionadas();
        }

        private void dgColumnas_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Con el foco en el casillero de una fila, Supr también cuenta como quitar la selección
            if (e.Key != Key.Delete) return;
            QuitarColumnasSeleccionadas();
            e.Handled = true;
        }

        // ── Análisis ──────────────────────────────────────────────────────

        private async void btnAnalizar_Click(object sender, RoutedEventArgs e)
        {
            // Solo las visibles: las de auditoría ocultas no se analizan aunque hubieran quedado tildadas
            var columnas = ColumnasVisibles().Where(c => c.Revisar).ToList();
            if (columnas.Count == 0)
            {
                MessageBox.Show(this, "Tildá al menos una columna.", "Revisión ortográfica",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int max;
            if (!int.TryParse(txtMaxValores.Text.Trim(), out max) || max < 100 || max > 5000000)
            {
                MessageBox.Show(this, "Los valores por columna tienen que ser un número entre 100 y 5.000.000.",
                    "Revisión ortográfica", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Tildar a mano una columna ignorada es pedir que se vuelva a revisar
            foreach (var c in columnas)
                if (OrtografiaManager.EsColumnaIgnorada(_conn.Nombre, c.Clave))
                    OrtografiaManager.QuitarColumnaIgnorada(_conn.Nombre, c.Clave);

            var op = new OpcionesOrtografia
            {
                MaxValoresPorColumna = max,
                TildeEsError = chkTilde.IsChecked == true,
                IgnorarSiglas = chkSiglas.IsChecked == true
            };

            _hallazgos.Clear();
            ActualizarResumen();
            foreach (var c in columnas) c.Estado = "En espera";
            pbProgreso.Minimum = 0;
            pbProgreso.Maximum = columnas.Count;
            pbProgreso.Value = 0;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _analizando = true;
            SetOcupado(true, "Cargando el diccionario...");

            IProgress<ProgresoColumna> progreso = new Progress<ProgresoColumna>(AplicarProgreso);
            bool cancelado = false;
            try
            {
                await Task.Run(() =>
                {
                    // Si el diccionario no está en el .exe falla acá, antes de tocar la base
                    var corrector = CorrectorOrtografico.Instancia;
                    for (int i = 0; i < columnas.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var col = columnas[i];
                        progreso.Report(new ProgresoColumna { Columna = col, Indice = i, Total = columnas.Count, Estado = "Leyendo..." });
                        try
                        {
                            var res = _svc.AnalizarColumna(col, op, ct);
                            progreso.Report(new ProgresoColumna { Columna = col, Indice = i, Total = columnas.Count, Resultado = res });
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            progreso.Report(new ProgresoColumna { Columna = col, Indice = i, Total = columnas.Count, Estado = "Error: " + ex.Message });
                        }
                    }
                }, ct);
            }
            catch (OperationCanceledException) { cancelado = true; }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo completar el análisis:\n" + ex.Message, "Revisión ortográfica",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _analizando = false;
                _cts.Dispose();
                _cts = null;
            }

            foreach (var c in columnas)
                if (c.Estado == "En espera" || c.Estado == "Leyendo...") c.Estado = cancelado ? "Cancelada" : "";

            ActualizarResumen();
            SetOcupado(false, cancelado
                ? $"Análisis cancelado. Hallazgos hasta ahora: {_hallazgos.Count}."
                : $"Análisis terminado: {columnas.Count} columna(s), {_hallazgos.Count} hallazgo(s).");
        }

        private void AplicarProgreso(ProgresoColumna p)
        {
            var col = p.Columna;
            if (p.Resultado != null)
            {
                var r = p.Resultado;
                col.Estado = $"{r.ValoresLeidos} valores" + (r.Truncado ? " (tope)" : "") + $", {r.Hallazgos.Count} hallazgo(s)";
                foreach (var h in r.Hallazgos) _hallazgos.Add(h);
                pbProgreso.Value = p.Indice + 1;
                ActualizarResumen();
            }
            else
            {
                col.Estado = p.Estado;
                if (p.Estado != null && p.Estado.StartsWith("Error")) pbProgreso.Value = p.Indice + 1;
            }
            txtEstado.Text = $"Revisando {col.Clave} ({p.Indice + 1} de {p.Total})";
        }

        private void btnCancelar_Click(object sender, RoutedEventArgs e)
        {
            var cts = _cts;
            if (cts != null) { txtEstado.Text = "Cancelando..."; cts.Cancel(); }
        }

        // ── Hallazgos ─────────────────────────────────────────────────────

        private List<HallazgoOrtografico> Seleccionados()
        {
            return dgHallazgos.SelectedItems.Cast<HallazgoOrtografico>().ToList();
        }

        private void VerEnEditor(HallazgoOrtografico h)
        {
            if (h == null || _verEnEditor == null) return;
            _verEnEditor(_svc.ConsultaVerFilas(h));
        }

        private void menuVerEditor_Click(object sender, RoutedEventArgs e)
        {
            VerEnEditor(Seleccionados().FirstOrDefault());
        }

        private void dgHallazgos_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Solo sobre una fila: un doble click en el encabezado o en la barra no debe abrir nada
            for (var o = e.OriginalSource as DependencyObject; o != null; o = VisualTreeHelper.GetParent(o))
            {
                if (o is DataGridRow) { VerEnEditor(dgHallazgos.SelectedItem as HallazgoOrtografico); return; }
                if (o is DataGridColumnHeader) return;
            }
        }

        private void menuIgnorarPalabra_Click(object sender, RoutedEventArgs e)
        {
            var palabras = Seleccionados().Select(h => h.Palabra)
                .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (palabras.Count == 0) return;

            foreach (var p in palabras) OrtografiaManager.IgnorarPalabra(p);
            QuitarHallazgos(h => palabras.Contains(h.Palabra, StringComparer.OrdinalIgnoreCase));
            txtEstado.Text = $"{palabras.Count} palabra(s) ignorada(s): no se vuelven a informar.";
        }

        private void menuIgnorarColumna_Click(object sender, RoutedEventArgs e)
        {
            var claves = Seleccionados().Select(h => h.ClaveColumna).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (claves.Count == 0) return;

            foreach (var k in claves) OrtografiaManager.IgnorarColumna(_conn.Nombre, k);
            foreach (var c in _columnas.Where(c => claves.Contains(c.Clave, StringComparer.OrdinalIgnoreCase)))
            {
                c.Revisar = false;
                c.Estado = "Ignorada";
            }
            QuitarHallazgos(h => claves.Contains(h.ClaveColumna, StringComparer.OrdinalIgnoreCase));
            txtEstado.Text = $"{claves.Count} columna(s) ignorada(s). Si querés revisarlas de nuevo, tildalas a mano y analizá.";
        }

        private void QuitarHallazgos(Func<HallazgoOrtografico, bool> criterio)
        {
            foreach (var h in _hallazgos.Where(criterio).ToList()) _hallazgos.Remove(h);
            ActualizarResumen();
            ActualizarBotones();
        }

        private void menuCopiar_Click(object sender, RoutedEventArgs e)
        {
            var h = Seleccionados().FirstOrDefault();
            if (h == null) return;
            try { Clipboard.SetText(h.Palabra); } catch { /* el portapapeles puede estar tomado por otra aplicación */ }
        }

        // ── Exportar ──────────────────────────────────────────────────────

        private void btnExportar_Click(object sender, RoutedEventArgs e)
        {
            if (_hallazgos.Count == 0) return;

            var dt = new DataTable("Ortografía");
            foreach (var c in new[] { "Tipo", "Tabla", "Columna", "Palabra", "Sugerencias", "Valores", "Ejemplo" })
                dt.Columns.Add(c, c == "Valores" ? typeof(int) : typeof(string));
            foreach (var h in _hallazgos)
                dt.Rows.Add(h.Categoria, h.TablaCompleta, h.Columna, h.Palabra, h.Sugerencias, h.Valores, h.Ejemplo);

            var sfd = new System.Windows.Forms.SaveFileDialog
            {
                Title = "Exportar hallazgos",
                Filter = "Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
                FileName = "RevisionOrtografica_" + _conn.Nombre,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (sfd.ShowDialog(this.OwnerWin32()) != System.Windows.Forms.DialogResult.OK) return;

            try
            {
                var svc = new ExcelService();
                if (sfd.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    var utf8Bom = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                    System.IO.File.WriteAllText(sfd.FileName, svc.CrearCsv(dt, true), utf8Bom);
                }
                else
                {
                    var hojas = new Dictionary<string, DataTable> { { "Ortografía", dt } };
                    svc.GuardarArchivo(svc.CrearExcelMultiplesHojas(hojas, true), sfd.FileName);
                }
                txtEstado.Text = "Informe guardado en: " + sfd.FileName;
                System.Diagnostics.Process.Start(sfd.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "No se pudo guardar el informe:\n" + ex.Message, "Revisión ortográfica",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Cierre ────────────────────────────────────────────────────────

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!_analizando) return;
            var r = MessageBox.Show(this, "Hay un análisis en curso. ¿Cancelarlo y cerrar la ventana?", "Revisión ortográfica",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
            var cts = _cts;
            if (cts != null) cts.Cancel();
        }

        private void btnCerrar_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
