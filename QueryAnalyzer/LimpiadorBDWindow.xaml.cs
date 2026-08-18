using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QueryAnalyzer
{
    public partial class LimpiadorBDWindow : Window
    {
        private readonly Conexion _conn;
        private readonly string _connStr;
        private LimpiadorBDService _svc;
        private List<TablaConfigLimpiador> _configs = new List<TablaConfigLimpiador>();
        private AnalisisResultLimpiador _analisis;
        private string _scriptGenerado;
        private string _schemaFiltro;

        private static readonly string[] OperadoresTodos =
            { "IS NOT EMPTY", "IS EMPTY", "IS NOT NULL", "IS NULL", "=", "<>", ">", ">=", "<", "<=" };

        private class AnalisisVM
        {
            public string NombreCompleto  { get; set; }
            public int    RegistrosBaja   { get; set; }
            public int    RegistrosActivos { get; set; }
            public string Estado          { get; set; }
            public string ConflictosTexto { get; set; }
        }

        public LimpiadorBDWindow(Conexion conn, string connStr)
        {
            InitializeComponent();
            AplicarTemaActual();
            _conn = conn;
            _connStr = connStr;
            _svc = new LimpiadorBDService(conn, connStr);
            Title = $"Limpiador de BD — {conn.Nombre}";
            cbModoConflicto.SelectedIndex = 0;
            Loaded += (s, e) => CargarTablas();
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

        // ── Carga inicial ─────────────────────────────────────────────────

        private void CargarTablas()
        {
            SetEstado("Cargando tablas...");
            Task.Run(() => _svc.GetTablas()).ContinueWith(t =>
            {
                _configs = t.Result;

                var esquemas = _configs
                    .Select(c => c.Schema)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s)
                    .ToList();

                cbEsquema.Items.Clear();
                cbEsquema.Items.Add("(Todos)");
                foreach (var s in esquemas) cbEsquema.Items.Add(s);
                cbEsquema.SelectedIndex = 0;

                AplicarFiltro();
                SetEstado($"{_configs.Count} tabla(s) cargadas.");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void AplicarFiltro()
        {
            lvTablas.ItemsSource = string.IsNullOrEmpty(_schemaFiltro)
                ? _configs
                : _configs.Where(c => string.Equals(c.Schema, _schemaFiltro, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private void cbEsquema_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var sel = cbEsquema.SelectedItem as string;
            _schemaFiltro = (sel == null || sel == "(Todos)") ? null : sel;
            AplicarFiltro();
        }

        // ── Constructor de condiciones (genérico) ─────────────────────────

        /// <summary>
        /// Agrega una fila de condición al StackPanel dado.
        /// columnas=null → TextBox libre para el campo (uso global).
        /// columnas con valores → ComboBox de columnas (uso por tabla).
        /// </summary>
        private void AgregarFilaCondicion(StackPanel sp, IList<string> columnas, CondicionBaja init = null)
        {
            var fila = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };

            // Campo
            FrameworkElement ctrlCampo;
            if (columnas == null)
            {
                var tb = new TextBox { Width = 108, Margin = new Thickness(0, 0, 2, 0), Padding = new Thickness(3, 1, 3, 1) };
                tb.SetResourceReference(TextBox.BackgroundProperty, "BrushControlBG");
                tb.SetResourceReference(TextBox.ForegroundProperty, "BrushFG");
                tb.SetResourceReference(TextBox.BorderBrushProperty, "BrushBorder");
                if (init != null) tb.Text = init.Campo ?? "";
                ctrlCampo = tb;
            }
            else
            {
                var cb = new ComboBox { Width = 108, Margin = new Thickness(0, 0, 2, 0), IsEditable = true };
                cb.SetResourceReference(ComboBox.BackgroundProperty, "BrushControlBG");
                cb.SetResourceReference(ComboBox.ForegroundProperty, "BrushFG");
                cb.SetResourceReference(ComboBox.BorderBrushProperty, "BrushBorder");
                foreach (var col in columnas) cb.Items.Add(col);
                if (init != null) cb.Text = init.Campo ?? "";
                ctrlCampo = cb;
            }
            fila.Children.Add(ctrlCampo);

            // Operador
            var cbOp = new ComboBox { Width = 108, Margin = new Thickness(0, 0, 2, 0) };
            cbOp.SetResourceReference(ComboBox.BackgroundProperty, "BrushControlBG");
            cbOp.SetResourceReference(ComboBox.ForegroundProperty, "BrushFG");
            cbOp.SetResourceReference(ComboBox.BorderBrushProperty, "BrushBorder");
            foreach (var op in OperadoresTodos) cbOp.Items.Add(op);
            cbOp.SelectedItem = init?.Operador ?? "IS NOT EMPTY";
            fila.Children.Add(cbOp);

            // Valor
            var txtVal = new TextBox { Width = 63, Margin = new Thickness(0, 0, 2, 0), Padding = new Thickness(3, 1, 3, 1) };
            txtVal.SetResourceReference(TextBox.BackgroundProperty, "BrushControlBG");
            txtVal.SetResourceReference(TextBox.ForegroundProperty, "BrushFG");
            txtVal.SetResourceReference(TextBox.BorderBrushProperty, "BrushBorder");
            if (init != null) txtVal.Text = init.Valor ?? "";
            fila.Children.Add(txtVal);

            // ValorSet (para cascada)
            var txtSet = new TextBox { Width = 68, Margin = new Thickness(0, 0, 2, 0), Padding = new Thickness(3, 1, 3, 1) };
            txtSet.SetResourceReference(TextBox.BackgroundProperty, "BrushControlBG");
            txtSet.SetResourceReference(TextBox.ForegroundProperty, "BrushFG");
            txtSet.SetResourceReference(TextBox.BorderBrushProperty, "BrushBorder");
            ToolTipService.SetToolTip(txtSet, "Valor para SET en baja en cascada (ej: 'SISTEMA', GETDATE())");
            if (init != null) txtSet.Text = init.ValorSet ?? "";
            fila.Children.Add(txtSet);

            // Combinador AND/OR
            var cbComb = new ComboBox { Width = 52, Margin = new Thickness(0, 0, 2, 0) };
            cbComb.SetResourceReference(ComboBox.BackgroundProperty, "BrushControlBG");
            cbComb.SetResourceReference(ComboBox.ForegroundProperty, "BrushFG");
            cbComb.SetResourceReference(ComboBox.BorderBrushProperty, "BrushBorder");
            cbComb.Items.Add("AND");
            cbComb.Items.Add("OR");
            cbComb.SelectedItem = init?.Combinador ?? "AND";
            fila.Children.Add(cbComb);

            // Botón quitar
            var btnQ = new Button { Content = "✕", Width = 22, Height = 22, Padding = new Thickness(0), Margin = new Thickness(0) };
            btnQ.SetResourceReference(Button.BackgroundProperty, "BrushBtnBG");
            btnQ.SetResourceReference(Button.ForegroundProperty, "BrushFG");
            btnQ.SetResourceReference(Button.BorderBrushProperty, "BrushBtnBorder");
            btnQ.Click += (s, _) => { sp.Children.Remove(fila); ActualizarVisibilidadCombinadores(sp); };
            fila.Children.Add(btnQ);

            // Ocultar valor cuando el operador no lo requiere
            cbOp.SelectionChanged += (s, _) =>
            {
                bool sinVal = CondicionBaja.OperadoresSinValor.Contains(cbOp.SelectedItem as string);
                txtVal.Visibility = sinVal ? Visibility.Collapsed : Visibility.Visible;
            };
            // Aplicar estado inicial
            bool sinValInit = CondicionBaja.OperadoresSinValor.Contains(cbOp.SelectedItem as string);
            txtVal.Visibility = sinValInit ? Visibility.Collapsed : Visibility.Visible;

            sp.Children.Add(fila);
            ActualizarVisibilidadCombinadores(sp);
        }

        private void ActualizarVisibilidadCombinadores(StackPanel sp)
        {
            // El combinador de la última fila se oculta (no tiene con qué conectarse)
            for (int i = 0; i < sp.Children.Count; i++)
            {
                if (sp.Children[i] is StackPanel fila && fila.Children.Count >= 5)
                {
                    var comb = fila.Children[4] as ComboBox;
                    if (comb != null)
                        comb.Visibility = i < sp.Children.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        private List<CondicionBaja> GetCondiciones(StackPanel sp)
        {
            var result = new List<CondicionBaja>();
            for (int i = 0; i < sp.Children.Count; i++)
            {
                if (!(sp.Children[i] is StackPanel fila) || fila.Children.Count < 5) continue;

                string campo = fila.Children[0] is TextBox tb ? tb.Text.Trim()
                             : fila.Children[0] is ComboBox cbC ? cbC.Text.Trim() : "";
                if (string.IsNullOrEmpty(campo)) continue;

                string op   = (fila.Children[1] as ComboBox)?.SelectedItem as string ?? "=";
                string val  = (fila.Children[2] as TextBox)?.Text.Trim() ?? "";
                string vset = (fila.Children[3] as TextBox)?.Text.Trim() ?? "";
                string comb = (fila.Children[4] as ComboBox)?.SelectedItem as string ?? "AND";

                result.Add(new CondicionBaja
                {
                    Campo      = campo,
                    Operador   = op,
                    Valor      = string.IsNullOrEmpty(val) ? null : val,
                    ValorSet   = string.IsNullOrEmpty(vset) ? null : vset,
                    Combinador = comb
                });
            }
            return result;
        }

        private void btnAgregarCondGlobal_Click(object sender, RoutedEventArgs e)
            => AgregarFilaCondicion(spCondicionesGlobal, columnas: null);

        // ── Botones de configuración ──────────────────────────────────────

        private void btnDetectar_Click(object sender, RoutedEventArgs e)
        {
            int detectadas = 0;
            foreach (var cfg in _configs)
            {
                var cols = _svc.GetColumnas(cfg.Schema, cfg.Nombre);
                var nuevas = DetectarCondiciones(cols);
                if (nuevas.Count > 0)
                {
                    cfg.CondicionesBaja = nuevas;
                    detectadas++;
                }
            }
            lvTablas.Items.Refresh();
            SetEstado($"Detección completada: {detectadas} tabla(s) con condiciones configuradas.");
        }

        private List<CondicionBaja> DetectarCondiciones(List<string> cols)
        {
            var conds = new List<CondicionBaja>();

            // BajaUsuario → IS NOT EMPTY
            var colBajaUsr = cols.FirstOrDefault(c => string.Equals(c, "BajaUsuario", StringComparison.OrdinalIgnoreCase));
            if (colBajaUsr != null)
                conds.Add(new CondicionBaja { Campo = colBajaUsr, Operador = "IS NOT EMPTY", ValorSet = "'SISTEMA'", Combinador = "AND" });

            // BajaFecha → <> fecha cero
            var colBajaFecha = cols.FirstOrDefault(c => string.Equals(c, "BajaFecha", StringComparison.OrdinalIgnoreCase)
                                                      || string.Equals(c, "FechaBaja", StringComparison.OrdinalIgnoreCase));
            if (colBajaFecha != null)
                conds.Add(new CondicionBaja { Campo = colBajaFecha, Operador = "<>", Valor = "'19000101'", ValorSet = "GETDATE()", Combinador = "AND" });

            // Si no hay usuario/fecha, buscar campos genéricos
            if (conds.Count == 0)
            {
                var genericoBaja = cols.FirstOrDefault(c =>
                    new[] { "Baja", "IsDeleted", "Eliminado", "Deleted" }
                    .Any(x => string.Equals(c, x, StringComparison.OrdinalIgnoreCase)));
                if (genericoBaja != null)
                    conds.Add(new CondicionBaja { Campo = genericoBaja, Operador = "=", Valor = "1", ValorSet = "1", Combinador = "AND" });
                else
                {
                    var campoActivo = cols.FirstOrDefault(c =>
                        new[] { "Activo", "Vigente", "Active" }
                        .Any(x => string.Equals(c, x, StringComparison.OrdinalIgnoreCase)));
                    if (campoActivo != null)
                        conds.Add(new CondicionBaja { Campo = campoActivo, Operador = "=", Valor = "0", ValorSet = "0", Combinador = "AND" });
                }
            }
            return conds;
        }

        private void btnSelTodas_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in _configs) c.Incluir = true;
            lvTablas.Items.Refresh();
        }

        private void btnSelNinguna_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in _configs) c.Incluir = false;
            lvTablas.Items.Refresh();
        }

        private void btnAplicarSeleccionadas_Click(object sender, RoutedEventArgs e)
        {
            var condsGlobal = GetCondiciones(spCondicionesGlobal);
            if (!condsGlobal.Any())
            {
                MessageBox.Show("Agregá al menos una condición de baja global antes de aplicar.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var seleccionadas = lvTablas.SelectedItems.Cast<TablaConfigLimpiador>().ToList();
            if (!seleccionadas.Any()) seleccionadas = _configs;

            foreach (var cfg in seleccionadas)
            {
                cfg.CondicionesBaja = condsGlobal.Select(c => new CondicionBaja
                {
                    Campo = c.Campo, Operador = c.Operador, Valor = c.Valor,
                    ValorSet = c.ValorSet, Combinador = c.Combinador
                }).ToList();
                cfg.ReordenarIds = chkReordenarGlobal.IsChecked == true;
            }
            lvTablas.Items.Refresh();
        }

        private void chkIncluir_Changed(object sender, RoutedEventArgs e) { }

        private void lvTablas_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (lvTablas.SelectedItem is TablaConfigLimpiador cfg)
                AbrirEditorTabla(cfg);
        }

        private void AbrirEditorTabla(TablaConfigLimpiador cfg)
        {
            var dlg = new LimpiadorTablaDialog(cfg, _svc);
            dlg.Owner = this;
            if (dlg.ShowDialog() == true)
                lvTablas.Items.Refresh();
        }

        // ── Analizar ──────────────────────────────────────────────────────

        private void btnAnalizar_Click(object sender, RoutedEventArgs e)
        {
            AplicarConfigGlobal();
            var modo = GetModo();
            SetEstado("Analizando...");
            btnAnalizar.IsEnabled = false;

            Task.Run(() => _svc.Analizar(_configs, modo)).ContinueWith(t =>
            {
                _analisis = t.Result;
                MostrarAnalisis(_analisis);
                btnAnalizar.IsEnabled = true;
                btnGenerarScript.IsEnabled = !_analisis.HayConflictosBloquantes;
                btnEjecutar.IsEnabled = false;
                tabResultado.SelectedIndex = 0;
                string adv = _analisis.HayConflictosBloquantes
                    ? "⚠ Hay conflictos bloqueantes. Cambiá el modo FK para poder generar el script."
                    : $"Análisis completado. {_analisis.Tablas.Count(x => x.Estado == "OK")} tabla(s) listas.";
                SetEstado(adv);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void MostrarAnalisis(AnalisisResultLimpiador result)
        {
            var vms = result.Tablas.Select(t => new AnalisisVM
            {
                NombreCompleto  = t.NombreCompleto,
                RegistrosBaja   = t.RegistrosBaja,
                RegistrosActivos = t.RegistrosActivos,
                Estado          = t.Estado,
                ConflictosTexto = t.Conflictos.Any() ? string.Join(" | ", t.Conflictos) : ""
            }).ToList();
            dgAnalisis.ItemsSource = vms;
            txtAdvertencias.Text = result.Advertencias.Any()
                ? string.Join("\n", result.Advertencias)
                : "Sin advertencias.";
        }

        // ── Generar Script ────────────────────────────────────────────────

        private void btnGenerarScript_Click(object sender, RoutedEventArgs e)
        {
            if (_analisis == null) { btnAnalizar_Click(sender, e); return; }
            AplicarConfigGlobal();
            var modo = GetModo();
            SetEstado("Generando script...");
            Task.Run(() => _svc.GenerarScript(_configs, _analisis, modo)).ContinueWith(t =>
            {
                _scriptGenerado = t.Result;
                txtScript.Text = _scriptGenerado;
                tabResultado.SelectedIndex = 1;
                btnEjecutar.IsEnabled = true;
                SetEstado("Script generado. Revisá el contenido antes de ejecutar.");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ── Ejecutar Script ───────────────────────────────────────────────

        private void btnEjecutar_Click(object sender, RoutedEventArgs e)
        {
            string script = txtScript.Text.Trim();
            if (string.IsNullOrEmpty(script))
            {
                MessageBox.Show("El script está vacío.", "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var confirm = MessageBox.Show(
                "Se ejecutará el script en una transacción.\nPodrás confirmar o revertir al finalizar.\n\n¿Continuar?",
                "Ejecutar Script", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            tabProgreso.Visibility = Visibility.Visible;
            tabResultado.SelectedItem = tabProgreso;
            pbar.Value = 0;
            txtLog.Clear();
            txtEstadoEjecucion.Text = "";
            btnEjecutar.IsEnabled = false;
            btnGenerarScript.IsEnabled = false;
            btnAnalizar.IsEnabled = false;

            Task.Run(() => _svc.EjecutarScript(script, progreso =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (progreso.Total > 0)
                        pbar.Value = (double)progreso.Completadas / progreso.Total * 100;
                    if (!string.IsNullOrEmpty(progreso.UltimaSentencia))
                        txtLog.AppendText($"[{progreso.Completadas}/{progreso.Total}] {progreso.UltimaSentencia}\n");
                });
            })).ContinueWith(t =>
            {
                var prog = t.Result;
                btnAnalizar.IsEnabled = true;

                if (!prog.Exitoso)
                {
                    txtEstadoEjecucion.Text = $"❌ Error: {prog.Error}";
                    txtLog.AppendText($"\n❌ ROLLBACK automático. Error: {prog.Error}\n");
                    btnGenerarScript.IsEnabled = true;
                    btnEjecutar.IsEnabled = true;
                    return;
                }

                txtLog.AppendText("\n✅ Ejecución completada. Transacción pendiente.");
                pbar.Value = 100;

                var respuesta = MessageBox.Show(
                    "El script se ejecutó correctamente.\n\n¿Confirmar COMMIT? Esta acción no se puede deshacer.",
                    "Confirmar COMMIT", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (respuesta == MessageBoxResult.Yes)
                {
                    _svc.ConfirmarCommit();
                    txtEstadoEjecucion.Text = "✅ COMMIT confirmado.";
                    txtLog.AppendText("✅ COMMIT realizado.\n");
                    SetEstado("Operación completada con éxito.");
                    btnGenerarScript.IsEnabled = false;
                    btnEjecutar.IsEnabled = false;
                }
                else
                {
                    _svc.CancelarRollback();
                    txtEstadoEjecucion.Text = "↩ ROLLBACK realizado. No se modificaron datos.";
                    txtLog.AppendText("↩ ROLLBACK realizado.\n");
                    SetEstado("Operación revertida.");
                    btnGenerarScript.IsEnabled = true;
                    btnEjecutar.IsEnabled = true;
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ── Copiar ────────────────────────────────────────────────────────

        private void btnCopiar_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(txtScript.Text))
            {
                Clipboard.SetText(txtScript.Text);
                txtMensajeCopia.Text = "¡Copiado!";
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (s, _) => { txtMensajeCopia.Text = ""; timer.Stop(); };
                timer.Start();
            }
        }

        private void btnCerrar_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_Closed(object sender, EventArgs e)
        {
            try { _svc?.CancelarRollback(); } catch { }
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private void AplicarConfigGlobal()
        {
            var condsGlobal = GetCondiciones(spCondicionesGlobal);
            bool reordenar = chkReordenarGlobal.IsChecked == true;

            foreach (var cfg in _configs.Where(c => c.Incluir))
            {
                if (!cfg.TieneCondiciones && condsGlobal.Any())
                    cfg.CondicionesBaja = condsGlobal.Select(c => new CondicionBaja
                    {
                        Campo = c.Campo, Operador = c.Operador, Valor = c.Valor,
                        ValorSet = c.ValorSet, Combinador = c.Combinador
                    }).ToList();
                if (reordenar) cfg.ReordenarIds = true;
            }
        }

        private ModoConflicto GetModo()
        {
            var tag = (cbModoConflicto.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Bloquear";
            return (ModoConflicto)Enum.Parse(typeof(ModoConflicto), tag);
        }

        private void SetEstado(string msg)
        {
            if (Dispatcher.CheckAccess()) txtEstadoBarra.Text = msg;
            else Dispatcher.Invoke(() => txtEstadoBarra.Text = msg);
        }
    }
}
