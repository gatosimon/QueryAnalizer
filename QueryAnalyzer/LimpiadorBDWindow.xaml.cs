using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
            public int    CascadaEstimada { get; set; }
            public int    RetenidasPorExterno { get; set; }
            public int    Huerfanos       { get; set; }
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

        /// <summary>
        /// El freno del 90% no aplica al modo iterativo, que se lleva TODO lo que está de baja: en
        /// cualquier tabla donde eso sea la mayoría, el umbral cortaría una limpieza correcta.
        /// Se deshabilita en vez de ignorarlo en silencio — un checkbox tildado que no hace nada es
        /// peor que ninguno, porque promete una red que no existe.
        /// </summary>
        private void cbModoConflicto_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // El primer disparo llega durante InitializeComponent, antes de que exista el checkbox.
            if (chkFrenoSeguridad == null) return;

            bool iterativo = GetModo() == ModoConflicto.BorradoIterativo;
            chkFrenoSeguridad.IsEnabled = !iterativo;
            chkFrenoSeguridad.ToolTip = iterativo
                ? "No aplica al 'Borrado iterativo': ese modo borra todas las bajas por diseño, así que " +
                  "el umbral del 90% cortaría limpiezas correctas. Revisá la estimación del análisis y el " +
                  "resumen del log, que salen antes de confirmar."
                : _tooltipFreno;
        }

        private const string _tooltipFreno =
            "Antes de borrar, aborta el script si el borrado se lleva más del 90% de las filas de alguna " +
            "tabla. Sirve para atajar una condición de baja mal escrita que se lleve una tabla entera. " +
            "En tablas con pocas filas el 90% se alcanza enseguida, por eso viene apagado.";

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
                SetEstado($"{_configs.Count} tabla(s) cargadas. Elegí el esquema y tildá las tablas a limpiar.");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// Tablas sobre las que operan TODAS las acciones de la ventana: las del esquema
        /// elegido, o todas si está en "(Todos)". El selector de esquema define el alcance
        /// real de la operación, no sólo lo que se ve en la lista.
        /// </summary>
        private List<TablaConfigLimpiador> TablasEnAlcance()
            => string.IsNullOrEmpty(_schemaFiltro)
                ? _configs
                : _configs.Where(c => string.Equals(c.Schema, _schemaFiltro, StringComparison.OrdinalIgnoreCase)).ToList();

        private void AplicarFiltro()
        {
            lvTablas.ItemsSource = TablasEnAlcance();
            ActualizarAlcance();
        }

        /// <summary>Contador vivo de qué queda dentro del alcance y cuánto está incluido.</summary>
        private void ActualizarAlcance()
        {
            var alcance = TablasEnAlcance();
            string esquema = string.IsNullOrEmpty(_schemaFiltro) ? "(Todos)" : _schemaFiltro;
            txtAlcance.Text = $"Alcance: {esquema} — {alcance.Count} tabla(s), {alcance.Count(c => c.Incluir)} incluida(s)";
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
            btnDetectar.IsEnabled = false;
            SetEstado("Detectando condiciones y claves primarias...");

            // Leer los controles acá: dentro del Task.Run estamos fuera del hilo de UI
            string combinador = GetCombinadorDetectar();
            var alcance = TablasEnAlcance();

            Task.Run(() =>
            {
                // La lista completa, no la del alcance: ese parámetro sólo lo usa la rama
                // SQLite, y recortarlo achicaría el grafo de FKs que necesita la detección
                // de conflictos contra tablas de otros esquemas.
                var nombres = _configs.Select(c => c.Nombre).ToList();

                // Una sola consulta al catálogo para todo el esquema
                var pks = _svc.GetPrimaryKeys(nombres);
                // Lo que el catálogo no resolvió, se deduce del grafo de FKs (sin costo extra)
                _svc.CompletarPKsDesdeFKs(pks, _svc.GetRelaciones(nombres));

                int conCondicion = 0;
                foreach (var cfg in alcance)
                {
                    var cols = _svc.GetColumnas(cfg.Schema, cfg.Nombre);
                    var nuevas = DetectarCondiciones(cols, combinador);
                    if (nuevas.Count > 0)
                    {
                        cfg.CondicionesBaja = nuevas;
                        conCondicion++;
                    }

                    // Siempre por nombre calificado: el catálogo y el grafo de FKs lo traen así,
                    // y es lo único que distingue dos tablas homónimas de esquemas distintos.
                    if (pks.TryGetValue(cfg.NombreCompleto, out var camposPK))
                        cfg.CamposPK = new List<string>(camposPK);
                }
                return conCondicion;
            }).ContinueWith(t =>
            {
                lvTablas.Items.Refresh();
                ActualizarAlcance();
                btnDetectar.IsEnabled = true;

                int simples   = alcance.Count(c => c.PKSimple);
                int compuestas = alcance.Count(c => c.TienePK && !c.PKSimple);
                var sinPK     = alcance.Where(c => !c.TienePK).Select(c => c.Nombre).ToList();

                string resumen = $"{alcance.Count} tabla(s) en alcance: {t.Result} con condición (unidas con {combinador}) | PK: {simples} simple(s), {compuestas} compuesta(s)";
                if (sinPK.Any())
                    resumen += $", {sinPK.Count} sin PK ({string.Join(", ", sinPK.Take(5))}{(sinPK.Count > 5 ? "…" : "")})";
                SetEstado(resumen + ".");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// Lee el combinador elegido para la detección automática ("AND" | "OR").
        /// Sólo tiene efecto cuando la tabla aporta más de una condición.
        /// </summary>
        private string GetCombinadorDetectar()
            => (cbCombinadorDetectar.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "AND";

        /// <summary>
        /// Vive en el servicio: el análisis de retenciones necesita la misma deducción para tablas
        /// que el usuario no configuró, y con dos copias una se iría quedando atrás de la otra.
        /// </summary>
        private List<CondicionBaja> DetectarCondiciones(List<string> cols, string combinador)
            => _svc.DetectarCondiciones(cols, combinador);

        private void btnSelTodas_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in TablasEnAlcance()) c.Incluir = true;
            lvTablas.Items.Refresh();
            ActualizarAlcance();
        }

        private void btnSelNinguna_Click(object sender, RoutedEventArgs e)
        {
            foreach (var c in TablasEnAlcance()) c.Incluir = false;
            lvTablas.Items.Refresh();
            ActualizarAlcance();
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
            // Sin selección explícita: todo el alcance, nunca las tablas de otros esquemas
            if (!seleccionadas.Any()) seleccionadas = TablasEnAlcance();

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
            ActualizarAlcance();
        }

        private void chkIncluir_Changed(object sender, RoutedEventArgs e) => ActualizarAlcance();

        private void lvTablas_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (lvTablas.SelectedItem is TablaConfigLimpiador cfg)
                AbrirEditorTabla(cfg);
        }

        private void AbrirEditorTabla(TablaConfigLimpiador cfg)
        {
            var dlg = new LimpiadorTablaDialog(cfg, _svc);
            if (dlg.MostrarModal(this) == true)
            {
                lvTablas.Items.Refresh();
                ActualizarAlcance();
            }
        }

        // ── Analizar ──────────────────────────────────────────────────────

        private void btnAnalizar_Click(object sender, RoutedEventArgs e)
        {
            AplicarConfigGlobal();
            var alcance = TablasEnAlcance();

            if (!alcance.Any(c => c.Incluir))
            {
                MessageBox.Show(
                    $"No hay ninguna tabla incluida dentro del alcance actual ({(string.IsNullOrEmpty(_schemaFiltro) ? "(Todos)" : _schemaFiltro)}).\n\n" +
                    "Tildá las tablas que querés limpiar, o usá '☑ Todas'.",
                    "Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var modo = GetModo();
            // La base entera, no el alcance: un conflicto desde una tabla de otro esquema tiene
            // que seguir detectándose, y el borrado en cascada necesita además el Schema de las
            // tablas que arrastra sin que estén tildadas.
            var todas = _configs;
            var opciones = GetOpcionesBarrido();
            SetEstado("Analizando...");
            btnAnalizar.IsEnabled = false;

            Task.Run(() => _svc.Analizar(alcance, modo, todas, opciones)).ContinueWith(t =>
            {
                _analisis = t.Result;
                MostrarAnalisis(_analisis);
                btnAnalizar.IsEnabled = true;
                btnGenerarScript.IsEnabled = !_analisis.HayConflictosBloquantes;
                btnEjecutar.IsEnabled = false;
                tabResultado.SelectedItem = tabAnalisis;
                string adv;
                if (_analisis.HayConflictosBloquantes)
                    adv = "⚠ Hay conflictos bloqueantes. Revisá las advertencias antes de generar el script.";
                else if (modo == ModoConflicto.BorradoEnCascada)
                {
                    // En este modo no hay estado "OK": las tablas quedan en "Baja" o en "Cascada".
                    int arrastradas = _analisis.Tablas.Count(x => (x.Estado ?? "").StartsWith("Cascada"));
                    adv = $"Análisis completado. {_analisis.Tablas.Sum(x => x.RegistrosBaja)} fila(s) en baja lógica; " +
                          $"la cascada arrastra {arrastradas} tabla(s) más. Revisá las advertencias.";
                }
                else if (modo == ModoConflicto.BorradoSeguro)
                {
                    // El conteo de bajas es un techo, no lo que se va a borrar: las filas que
                    // todavía tengan hijos vivos quedan retenidas y recién el script sabe cuántas.
                    adv = $"Análisis completado. {_analisis.Tablas.Sum(x => x.RegistrosBaja)} fila(s) en baja lógica " +
                          "COMO MÁXIMO: las que todavía tengan hijos vivos se retienen y no se borran.";
                }
                else if (modo == ModoConflicto.BorradoIterativo)
                {
                    // Acá el número que importa no es cuántas bajas hay —se van todas— sino cuántas
                    // filas VIVAS se lleva puestas el barrido. Es lo único que puede sorprender.
                    int activas = _analisis.Tablas.Sum(x => x.CascadaEstimada);
                    adv = $"Análisis completado. Se borran las {_analisis.Tablas.Sum(x => x.RegistrosBaja)} fila(s) en baja lógica" +
                          (activas > 0
                              ? $", y al menos {activas} fila(s) ACTIVAS que quedan desconectadas. Revisá las advertencias."
                              : ". Ninguna fila activa queda desconectada.");
                }
                else
                    adv = $"Análisis completado. {_analisis.Tablas.Count(x => x.Estado == "OK")} tabla(s) listas.";

                if (opciones.DepurarHuerfanos && modo != ModoConflicto.BorradoIterativo)
                {
                    var depurables = _analisis.Truncadas.Where(x => !x.FueraDeAlcance && x.Error == null).ToList();
                    adv += depurables.Any()
                        ? $" | {depurables.Count} relación(es) truncada(s), {depurables.Sum(x => x.FilasRotas)} fila(s) colgando."
                        : " | Sin relaciones truncadas.";
                }
                SetEstado(adv);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>
        /// Lee los checkboxes del barrido. Siempre desde el hilo de UI: los llamadores lo invocan
        /// antes de entrar al Task.Run.
        /// </summary>
        private OpcionesBarrido GetOpcionesBarrido() => new OpcionesBarrido
        {
            DepurarHuerfanos            = chkDepurarHuerfanos.IsChecked == true,
            CentinelasComoSinReferencia = chkCentinelas.IsChecked == true,
            FrenoSeguridad              = chkFrenoSeguridad.IsChecked == true
        };

        private void MostrarAnalisis(AnalisisResultLimpiador result)
        {
            var vms = result.Tablas.Select(t => new AnalisisVM
            {
                NombreCompleto  = t.NombreCompleto,
                RegistrosBaja   = t.RegistrosBaja,
                RegistrosActivos = t.RegistrosActivos,
                CascadaEstimada = t.CascadaEstimada,
                RetenidasPorExterno = t.RetenidasPorExterno,
                Huerfanos       = t.Huerfanos,
                Estado          = t.Estado,
                ConflictosTexto = t.Conflictos.Any() ? string.Join(" | ", t.Conflictos) : ""
            }).ToList();
            dgAnalisis.ItemsSource = vms;
            dgTruncadas.ItemsSource = result.Truncadas;
            txtSinTruncadas.Visibility = result.Truncadas.Any() ? Visibility.Collapsed : Visibility.Visible;
            dgRetenciones.ItemsSource = result.Retenciones;
            btnIncluirRetenedoras.IsEnabled = result.Retenciones.Any(r => r.CadenaIncompleta && r.PuedeIncluirse);
            txtSinRetenciones.Text = ResumirRetenciones(result, out string porQueApagado);
            txtSinRetenciones.Visibility = Visibility.Visible;
            btnIncluirRetenedoras.ToolTip = porQueApagado ?? _tooltipIncluirRetenedoras;
            txtAdvertencias.Text = result.Advertencias.Any()
                ? string.Join("\n", result.Advertencias)
                : "Sin advertencias.";
        }

        // ── Retenciones ───────────────────────────────────────────────────

        /// <summary>Tooltip del botón de incluir cuando está habilitado y no hay nada que explicar.</summary>
        private const string _tooltipIncluirRetenedoras =
            "Tilda las tablas de las filas 'Cadena incompleta' y les detecta los campos de baja. " +
            "Cada tabla sigue borrando sólo sus propias bajas: no se arrastra nada. Después volvé a analizar.";

        /// <summary>
        /// Resumen del informe con el desglose por motivo, y por qué el botón de incluir está como
        /// está. Existe porque un botón apagado se ve igual en cuatro situaciones muy distintas — sin
        /// analizar, todo con error, nada que incluir, o cadenas sin campos de baja detectables — y
        /// la diferencia entre "no hay nada que hacer" y "no se pudo calcular" es la que importa.
        /// </summary>
        /// <param name="porQueApagado">
        /// Explicación del botón deshabilitado, o null si está habilitado.
        /// </param>
        private string ResumirRetenciones(AnalisisResultLimpiador result, out string porQueApagado)
        {
            porQueApagado = null;

            if (result.Retenciones == null || !result.Retenciones.Any())
            {
                porQueApagado = "No hay retenciones que resolver: no hay ninguna tabla que incluir.";

                // En el modo iterativo el informe está vacío por diseño, no por falta de análisis:
                // ese modo no retiene nada. Decir "analizá primero" mandaría a repetir un análisis
                // que ya corrió y que nunca va a llenar esta solapa.
                if (GetModo() == ModoConflicto.BorradoIterativo)
                    return "El modo 'Borrado iterativo' no retiene nada: borra todo lo que está en baja y " +
                           "después todo lo que quede desconectado. Su informe es el contrario y está en la " +
                           "solapa Análisis, en la columna \"Cascada estimada\": cuántas filas ACTIVAS se lleva.";

                return "Sin retenciones. Este informe se llena con el modo 'Borrado seguro': muestra qué filas " +
                       "dadas de baja NO se van a borrar y por qué. Si no analizaste todavía, analizá primero.";
            }

            int conError  = result.Retenciones.Count(r => r.Error != null);
            var validas   = result.Retenciones.Where(r => r.Error == null).ToList();
            int vivos     = validas.Count(r => !r.CadenaIncompleta && !r.RetenidaEnCadena);
            int enCadena  = validas.Count(r => r.RetenidaEnCadena);
            int cadenas   = validas.Count(r => r.CadenaIncompleta);
            int accionables = validas.Count(r => r.EsAccionable);
            int filas     = validas.Sum(r => r.FilasRetenidas);

            var sb = new StringBuilder();
            sb.Append($"{validas.Count} relación(es) retienen {filas} baja(s): ");
            sb.Append($"{vivos} por datos vivos · {enCadena} retenida(s) en cadena · {cadenas} cadena(s) incompleta(s).");
            if (conError > 0)
                sb.Append($" | ⚠ {conError} relación(es) no se pudieron calcular: el mensaje del motor está en la columna 'Qué hacer'.");

            if (accionables == 0)
            {
                if (cadenas > 0)
                    porQueApagado = $"Hay {cadenas} cadena(s) incompleta(s), pero a esas tablas no se les detectan " +
                                    "campos de baja. Configuralas a mano (doble clic en la tabla) y volvé a analizar.";
                else if (validas.Any())
                    porQueApagado = "No hay cadenas incompletas: las tablas que retienen ya están tildadas, " +
                                    "así que no queda ninguna por incluir. Lo que retiene son datos vivos o " +
                                    "filas que a su vez están retenidas.";
                else
                    porQueApagado = "Ninguna relación se pudo calcular, así que no se sabe todavía si hay algo que incluir.";
            }

            if (porQueApagado != null) sb.Append("\n" + porQueApagado);
            return sb.ToString();
        }

        /// <summary>
        /// Cierra las cadenas incompletas: tilda las tablas que retienen y les detecta campos de
        /// baja y PK, para que la corrida siguiente las borre y libere a los padres.
        ///
        /// Es seguro aunque parezca que amplía el borrado: cada tabla incluida sigue borrando
        /// únicamente sus propias filas dadas de baja. No es arrastre — lo único que cambia es que
        /// alguien se ocupa de las hijas que hoy nadie mira.
        ///
        /// Sólo toca las filas "Cadena incompleta": las retenidas por datos vivos no se resuelven
        /// incluyendo nada, y meterlas acá haría creer que el problema se arregló solo.
        /// </summary>
        private void btnIncluirRetenedoras_Click(object sender, RoutedEventArgs e)
        {
            if (_analisis == null) return;

            var aIncluir = _analisis.Retenciones
                .Where(r => r.CadenaIncompleta && r.PuedeIncluirse)
                .Select(r => r.TablaQueRetiene)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!aIncluir.Any()) { SetEstado("No hay cadenas incompletas que se puedan cerrar automáticamente."); return; }

            var objetivo = _configs
                .Where(c => aIncluir.Contains(c.NombreCompleto, StringComparer.OrdinalIgnoreCase))
                .ToList();

            // El alcance lo define el selector de esquema, no el tildado: una tabla de otro esquema
            // se puede tildar pero igual queda afuera, y conviene decirlo antes de que el usuario
            // vuelva a analizar y no entienda por qué nada cambió.
            var fueraDeEsquema = string.IsNullOrEmpty(_schemaFiltro)
                ? new List<TablaConfigLimpiador>()
                : objetivo.Where(c => !string.Equals(c.Schema, _schemaFiltro, StringComparison.OrdinalIgnoreCase)).ToList();

            var noEncontradas = aIncluir
                .Where(n => !_configs.Any(c => string.Equals(c.NombreCompleto, n, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            btnIncluirRetenedoras.IsEnabled = false;
            SetEstado($"Incluyendo {objetivo.Count} tabla(s) que retienen…");

            string combinador = GetCombinadorDetectar();
            Task.Run(() =>
            {
                var nombres = _configs.Select(c => c.Nombre).ToList();
                var pks = _svc.GetPrimaryKeys(nombres);
                _svc.CompletarPKsDesdeFKs(pks, _svc.GetRelaciones(nombres));

                int listas = 0;
                foreach (var cfg in objetivo)
                {
                    cfg.Incluir = true;
                    if (!cfg.TieneCondiciones)
                        cfg.CondicionesBaja = _svc.DetectarCondiciones(_svc.GetColumnas(cfg.Schema, cfg.Nombre), combinador);
                    if (!cfg.TienePK && pks.TryGetValue(cfg.NombreCompleto, out var campos))
                        cfg.CamposPK = new List<string>(campos);
                    if (cfg.TieneCondiciones && cfg.TienePK) listas++;
                }
                return listas;
            }).ContinueWith(t =>
            {
                lvTablas.Items.Refresh();
                ActualizarAlcance();
                btnIncluirRetenedoras.IsEnabled = true;

                var msg = new StringBuilder($"{t.Result} de {objetivo.Count} tabla(s) incluidas y configuradas. Volvé a analizar para ver la cadena ya cerrada.");
                if (fueraDeEsquema.Any())
                    msg.Append($" | ⚠ {fueraDeEsquema.Count} quedan fuera del alcance por el filtro de esquema " +
                               $"({string.Join(", ", fueraDeEsquema.Select(c => c.NombreCompleto).Take(3))}" +
                               $"{(fueraDeEsquema.Count > 3 ? "…" : "")}): poné el selector en '(Todos)'.");
                if (noEncontradas.Any())
                    msg.Append($" | ⚠ {noEncontradas.Count} no están en el catálogo cargado: {string.Join(", ", noEncontradas.Take(3))}.");
                SetEstado(msg.ToString());
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void btnVerQueRetiene_Click(object sender, RoutedEventArgs e) => VerQueRetiene();

        private void dgRetenciones_MouseDoubleClick(object sender, MouseButtonEventArgs e) => VerQueRetiene();

        /// <summary>
        /// Carga en el editor la consulta que devuelve las filas que están reteniendo.
        ///
        /// Los avisos van por MessageBox y no por la barra de estado: un clic que no produce ningún
        /// cambio visible se lee como un botón roto, y eso fue exactamente lo que pasó.
        /// </summary>
        private void VerQueRetiene()
        {
            var sel = dgRetenciones.SelectedItem as RetencionLimpiador;
            if (sel == null)
            {
                MessageBox.Show(this, "Elegí primero una fila del informe.", "Ver qué la retiene",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (string.IsNullOrEmpty(sel.SelectSql))
            {
                MessageBox.Show(this, "Esa fila no tiene una consulta asociada.", "Ver qué la retiene",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string cabecera = sel.Error != null
                ? $"-- El conteo de esta relación falló: {sel.Error}\n" +
                  $"-- La consulta de abajo igual sirve para ver qué hay del otro lado.\n"
                : $"-- Filas de {sel.TablaQueRetiene} que retienen {sel.FilasRetenidas} baja(s) de {sel.TablaRetenida}\n" +
                  $"-- Motivo: {sel.Motivo}. {sel.QueHacer}\n";

            txtScript.Text = cabecera + "-- Es sólo una consulta: no borra nada.\n\n" + sel.SelectSql;
            tabResultado.SelectedItem = tabScript;
            SetEstado("Consulta cargada en 'Script SQL'. Volvé a generar el script cuando termines de revisar.");
        }

        // ── Generar Script ────────────────────────────────────────────────

        private void btnGenerarScript_Click(object sender, RoutedEventArgs e)
        {
            if (_analisis == null) { btnAnalizar_Click(sender, e); return; }
            AplicarConfigGlobal();
            var alcance = TablasEnAlcance();
            var modo = GetModo();
            var todas = _configs;
            var opciones = GetOpcionesBarrido();
            SetEstado("Generando script...");
            Task.Run(() => _svc.GenerarScript(alcance, _analisis, modo, todas, opciones)).ContinueWith(t =>
            {
                _scriptGenerado = t.Result;
                txtScript.Text = _scriptGenerado;
                tabResultado.SelectedItem = tabScript;
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
                    // Durante el barrido iterativo la barra se queda quieta —el bloque se repite y
                    // el total de sentencias deja de medir el trabajo— así que la vuelta se muestra
                    // aparte para que no parezca colgado.
                    if (progreso.Vuelta > 0)
                        txtEstadoEjecucion.Text = $"Barriendo… vuelta {progreso.Vuelta}";
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

                txtLog.AppendText("\n✅ Ejecución completada. Transacción pendiente.\n");
                pbar.Value = 100;

                // El alcance real, medido durante la corrida. Es lo único que permite revisar el
                // borrado antes de confirmarlo: el análisis previo sólo estima un nivel de arrastre.
                string alcance = ResumirFilasEliminadas(prog);
                txtLog.AppendText(alcance);

                if (prog.Vuelta > 0)
                    txtLog.AppendText($"  El barrido convergió en {prog.Vuelta} vuelta(s).\n");

                // Qué quedó roto. Sólo el modo iterativo puede dejar algo —los otros rehabilitan con
                // WITH CHECK y habrían fallado— y se consulta ahora, con la transacción todavía
                // abierta, porque es el dato que falta para decidir si confirmar.
                var violadas = _svc.InformeFKsVioladas();
                string avisoFK = "";
                if (violadas.Any())
                {
                    txtLog.AppendText(ResumirFKsVioladas(violadas));
                    int conFilas = violadas.Count(f => f.Error == null && f.FilasViolando > 0);
                    if (conFilas > 0)
                        avisoFK = $"\n\n⚠ Quedan {conFilas} FK violada(s), " +
                                  $"{violadas.Where(f => f.Error == null).Sum(f => f.FilasViolando):N0} fila(s) en total. " +
                                  "Sus constraints quedan sin verificar. Está en el log.";
                }

                var respuesta = MessageBox.Show(
                    $"El script se ejecutó correctamente.\n\n{DescribirAlcance(prog)}{avisoFK}\n\n" +
                    "Revisá el detalle en el log antes de decidir.\n\n" +
                    "¿Confirmar COMMIT? Esta acción no se puede deshacer.",
                    "Confirmar COMMIT", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                // El cierre de la transacción también puede fallar, y sin este try/catch la excepción
                // corta el continuation antes de escribir el resultado: la ventana quedaba muda,
                // mostrando "Transacción pendiente" como si nada hubiera pasado.
                if (respuesta == MessageBoxResult.Yes)
                {
                    try
                    {
                        _svc.ConfirmarCommit();
                        txtEstadoEjecucion.Text = "✅ COMMIT confirmado.";
                        txtLog.AppendText("✅ COMMIT realizado.\n");
                        SetEstado("Operación completada con éxito.");
                        btnGenerarScript.IsEnabled = false;
                        btnEjecutar.IsEnabled = false;
                    }
                    catch (Exception ex)
                    {
                        txtEstadoEjecucion.Text = $"❌ El COMMIT falló: {ex.Message}";
                        txtLog.AppendText($"\n❌ El COMMIT falló: {ex.Message}\n");
                        SetEstado("El COMMIT falló. Revisá el log.");
                        btnGenerarScript.IsEnabled = true;
                        btnEjecutar.IsEnabled = true;
                    }
                }
                else
                {
                    try
                    {
                        _svc.CancelarRollback();
                        txtEstadoEjecucion.Text = "↩ ROLLBACK realizado. No se modificaron datos.";
                        txtLog.AppendText("↩ ROLLBACK realizado.\n");
                        SetEstado("Operación revertida.");
                    }
                    catch (Exception ex)
                    {
                        txtEstadoEjecucion.Text = $"❌ El ROLLBACK falló: {ex.Message}";
                        txtLog.AppendText($"\n❌ El ROLLBACK falló: {ex.Message}\n");
                        SetEstado("El ROLLBACK falló. Revisá el log.");
                    }
                    btnGenerarScript.IsEnabled = true;
                    btnEjecutar.IsEnabled = true;
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Una línea por tabla, de mayor a menor, más el total. Vacío si no se borró nada.</summary>
        private static string ResumirFilasEliminadas(LimpiadorBDService.EjecucionProgreso prog)
        {
            if (prog.FilasPorTabla.Count == 0)
                return "\n── Filas eliminadas ──────────────────────\n  (ninguna)\n";

            var sb = new StringBuilder();
            sb.AppendLine("\n── Filas eliminadas ──────────────────────");
            int ancho = prog.FilasPorTabla.Keys.Max(t => t.Length);
            foreach (var kv in prog.FilasPorTabla.OrderByDescending(k => k.Value).ThenBy(k => k.Key))
                sb.AppendLine($"  {kv.Key.PadRight(ancho)}  {kv.Value,8:N0}");
            sb.AppendLine($"  ── {DescribirAlcance(prog)}");
            return sb.ToString();
        }

        /// <summary>
        /// Las FK que quedaron violadas, para el log. No es una lista de errores: es la contrapartida
        /// declarada del modo iterativo, que conserva la fila rota por un dato pero sana por otro.
        /// Lo que importa comunicar es la consecuencia práctica —la constraint queda sin verificar—
        /// porque es la que no se nota hasta mucho después.
        /// </summary>
        private static string ResumirFKsVioladas(List<FKVioladaLimpiador> violadas)
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n── FK que quedaron violadas ──────────────");
            sb.AppendLine("  Son filas que siguen conectadas por otro dato, así que el barrido las conservó.");
            sb.AppendLine("  Sus constraints quedan activas pero SIN verificar (untrusted): mientras sigan así,");
            sb.AppendLine("  el motor las ignora al armar los planes de ejecución.");

            int ancho = violadas.Max(f => f.Detalle.Length);
            foreach (var f in violadas)
                sb.AppendLine(f.Error != null
                    ? $"  {f.Detalle.PadRight(ancho)}  (no se pudo contar: {f.Error})"
                    : $"  {f.Detalle.PadRight(ancho)}  {f.FilasViolando,8:N0}");

            var reales = violadas.Where(f => f.Error == null).ToList();
            if (reales.Any())
                sb.AppendLine($"  ── {reales.Sum(f => f.FilasViolando):N0} fila(s) en {reales.Count} relación(es).");
            return sb.ToString();
        }

        private static string DescribirAlcance(LimpiadorBDService.EjecucionProgreso prog)
            => prog.FilasPorTabla.Count == 0
                ? "No se eliminó ninguna fila."
                : $"Se eliminaron {prog.FilasTotales:N0} fila(s) en {prog.FilasPorTabla.Count} tabla(s).";

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

            // Sólo el alcance: si no, la condición global se filtra a tablas de otros
            // esquemas y vuelven a entrar al análisis aunque el resto esté acotado.
            foreach (var cfg in TablasEnAlcance().Where(c => c.Incluir))
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
