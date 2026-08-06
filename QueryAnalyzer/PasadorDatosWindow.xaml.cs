using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace QueryAnalyzer
{
    public class TablaItem : INotifyPropertyChanged
    {
        private bool _seleccionada = true;

        public string Schema        { get; set; }
        public string Nombre        { get; set; }
        public string NombreCompleto => string.IsNullOrEmpty(Schema) ? Nombre : $"{Schema}.{Nombre}";

        public bool Seleccionada
        {
            get => _seleccionada;
            set { _seleccionada = value; OnPropertyChanged(nameof(Seleccionada)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

        public TablaTransfer ToTablaTransfer() => new TablaTransfer { Schema = Schema, Nombre = Nombre };
    }

    public partial class PasadorDatosWindow : Window
    {
        private readonly Dictionary<string, Conexion> _conexiones;
        private bool _sincronizandoMotor;
        private ResultadoPasado _ultimoResultado;
        private string _logFilePath;

        private readonly ObservableCollection<TablaItem> _tablas = new ObservableCollection<TablaItem>();

        private static readonly (string Label, TipoMotor? Motor)[] Motores =
        {
            ("(Todos)",    null),
            ("SQL Server", TipoMotor.MS_SQL),
            ("DB2",        TipoMotor.DB2),
            ("PostgreSQL", TipoMotor.POSTGRES),
            ("SQLite",     TipoMotor.SQLite),
        };

        public PasadorDatosWindow(
            Dictionary<string, Conexion> conexiones,
            Dictionary<string, NodoTablaTag> seleccionPersistente = null,
            Conexion conexionActiva = null)
        {
            InitializeComponent();
            AplicarTemaActual();

            var main = Application.Current.MainWindow;
            if (main != null) Height = Math.Max(600, main.Height - 20);

            _conexiones = conexiones ?? new Dictionary<string, Conexion>();

            lstTablas.ItemsSource = _tablas;

            CargarConexiones();

            if (conexionActiva != null)
                SeleccionarConexion(cmbConexionA, conexionActiva.Nombre);

            if (seleccionPersistente != null)
                CargarTablasDesdeSeleccion(seleccionPersistente);

            ActualizarContador();
            InicializarLogFisico();
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

        // ── Log físico ────────────────────────────────────────────────────────

        private void InicializarLogFisico()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "QueryAnalyzer", "logs");
                Directory.CreateDirectory(dir);
                _logFilePath = Path.Combine(dir, $"transferencia_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                File.WriteAllText(_logFilePath,
                    $"=== Transferencia de Datos — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}",
                    Encoding.UTF8);
                Log($"Log físico: {_logFilePath}");
            }
            catch { _logFilePath = null; }
        }

        // ── Conexiones ────────────────────────────────────────────────────────

        private void CargarConexiones()
        {
            foreach (var m in Motores)
            {
                cmbMotorA.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Motor });
                cmbMotorB.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Motor });
            }
            cmbMotorA.SelectedIndex = 0;
            cmbMotorB.SelectedIndex = 0;
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

            if (selActual != null)
            {
                var match = cmbConexion.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => i.Content.ToString() == selActual);
                if (match != null) cmbConexion.SelectedItem = match;
            }

            if (cmbConexion.SelectedItem == null && cmbConexion.Items.Count > 0)
                cmbConexion.SelectedIndex = 0;
        }

        private void SeleccionarConexion(ComboBox cmb, string nombre)
        {
            var match = cmb.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Content?.ToString(), nombre, StringComparison.OrdinalIgnoreCase));
            if (match != null) cmb.SelectedItem = match;
        }

        private Conexion ObtenerConexion(ComboBox cmbConexion)
            => (cmbConexion.SelectedItem as ComboBoxItem)?.Tag as Conexion;

        // ── Carga de tablas ───────────────────────────────────────────────────

        private void CargarTablasDesdeSeleccion(Dictionary<string, NodoTablaTag> seleccion)
        {
            _tablas.Clear();
            foreach (var kvp in seleccion)
            {
                if (kvp.Value?.Tipo != "TABLE") continue;
                string nombre = kvp.Key;
                string schema = "";
                string tabla  = nombre;
                int dot = nombre.IndexOf('.');
                if (dot >= 0) { schema = nombre.Substring(0, dot); tabla = nombre.Substring(dot + 1); }
                _tablas.Add(new TablaItem { Schema = schema, Nombre = tabla });
            }
            ActualizarContador();
        }

        private void ActualizarContador()
        {
            int total    = _tablas.Count;
            int marcadas = _tablas.Count(t => t.Seleccionada);
            txtContadorTablas.Text = $"{marcadas}/{total} tablas";
        }

        // ── Eventos de lista ──────────────────────────────────────────────────

        private void chkTabla_Changed(object sender, RoutedEventArgs e) => ActualizarContador();

        private void btnMarcarTodas_Click(object sender, RoutedEventArgs e)
        { foreach (var t in _tablas) t.Seleccionada = true; ActualizarContador(); }

        private void btnDesmarcarTodas_Click(object sender, RoutedEventArgs e)
        { foreach (var t in _tablas) t.Seleccionada = false; ActualizarContador(); }

        private void btnQuitarSeleccionadas_Click(object sender, RoutedEventArgs e)
        {
            var aQuitar = lstTablas.SelectedItems.Cast<TablaItem>().ToList();
            foreach (var t in aQuitar) _tablas.Remove(t);
            ActualizarContador();
        }

        private void txtAgregarTabla_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        { if (e.Key == System.Windows.Input.Key.Enter) AgregarTablaDesdeTextBox(); }

        private void btnAgregarTablaManual_Click(object sender, RoutedEventArgs e)
            => AgregarTablaDesdeTextBox();

        private void AgregarTablaDesdeTextBox()
        {
            string input = txtAgregarTabla.Text?.Trim();
            if (string.IsNullOrWhiteSpace(input)) return;
            if (_tablas.Any(t => string.Equals(t.NombreCompleto, input, StringComparison.OrdinalIgnoreCase)))
            { Log($"'{input}' ya está en la lista."); return; }

            string schema = ""; string tabla = input;
            int dot = tabla.IndexOf('.');
            if (dot >= 0) { schema = tabla.Substring(0, dot); tabla = tabla.Substring(dot + 1); }
            _tablas.Add(new TablaItem { Schema = schema, Nombre = tabla });
            txtAgregarTabla.Clear();
            ActualizarContador();
        }

        // ── Filtros de motor ──────────────────────────────────────────────────

        private void cmbMotorA_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            FiltrarConexiones(cmbMotorA, cmbConexionA);
            if (!_sincronizandoMotor)
            { _sincronizandoMotor = true; cmbMotorB.SelectedIndex = cmbMotorA.SelectedIndex; _sincronizandoMotor = false; }
        }

        private void cmbMotorB_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            FiltrarConexiones(cmbMotorB, cmbConexionB);
            if (!_sincronizandoMotor)
            { _sincronizandoMotor = true; cmbMotorA.SelectedIndex = cmbMotorB.SelectedIndex; _sincronizandoMotor = false; }
        }

        private void chkSoloScript_Changed(object sender, RoutedEventArgs e)
        {
            if (btnEjecutar == null) return;
            btnEjecutar.Content = chkSoloScript.IsChecked == true ? "📄 Generar script" : "▶ Ejecutar transferencia";
        }

        // ── Analizar FKs ──────────────────────────────────────────────────────

        private void btnAnalizarFKs_Click(object sender, RoutedEventArgs e)
        {
            var conA = ObtenerConexion(cmbConexionA);
            var conB = ObtenerConexion(cmbConexionB);
            if (conA == null) { txtAdvertencias.Text = "Seleccioná una conexión origen (A)."; return; }

            var marcadas = _tablas.Where(t => t.Seleccionada).Select(t => t.ToTablaTransfer()).ToList();
            if (marcadas.Count == 0) { txtAdvertencias.Text = "No hay tablas marcadas para analizar."; return; }

            txtAdvertencias.Text = "Analizando...";
            btnAnalizarFKs.IsEnabled = false;

            // Extraer strings antes del Task.Run (thread safety)
            string connParaFKs = conB != null ? ConexionesManager.GetConnectionString(conB) : ConexionesManager.GetConnectionString(conA);
            TipoMotor motorParaFKs = conB?.Motor ?? conA.Motor;
            string connStrB = conB != null ? ConexionesManager.GetConnectionString(conB) : null;
            TipoMotor motorB = conB?.Motor ?? conA.Motor;

            Task.Run(() =>
            {
                var fks      = PasadorDatosService.ObtenerFKsEntreTablas(connParaFKs, motorParaFKs, marcadas);
                var externas = PasadorDatosService.DetectarFKsExternas(connParaFKs, motorParaFKs, marcadas);
                var (ordenInsert, ciclicas) = PasadorDatosService.OrdenarPorDependencias(marcadas, fks);

                Dictionary<string, TablaMetadata> metadata = null;
                if (connStrB != null)
                    try { metadata = PasadorDatosService.ObtenerMetadataTablas(connStrB, motorB, marcadas); }
                    catch { }

                Dispatcher.Invoke(() =>
                {
                    var sb = new StringBuilder();
                    string fuenteFKs = conB != null ? "B (destino)" : "A (origen)";
                    sb.AppendLine($"── Orden INSERT (FKs de {fuenteFKs}) ──");
                    for (int i = 0; i < ordenInsert.Count; i++)
                        sb.AppendLine($"  {i + 1}. {ordenInsert[i].NombreCompleto}");

                    if (ciclicas.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("⚠ Ciclos FK (orden parcial):");
                        foreach (var c in ciclicas) sb.AppendLine($"  • {c}");
                    }

                    if (metadata != null)
                    {
                        bool hayEspeciales = metadata.Values.Any(m => m.TieneIdentity || m.ColumnasExcluidas.Count > 0);
                        if (hayEspeciales)
                        {
                            sb.AppendLine();
                            sb.AppendLine("── Columnas especiales en B (manejadas automáticamente) ──");
                            foreach (var kv in metadata)
                            {
                                var m = kv.Value;
                                if (!m.TieneIdentity && m.ColumnasExcluidas.Count == 0) continue;
                                var partes = new List<string>();
                                if (m.TieneIdentity) partes.Add($"IDENTITY: {string.Join(", ", m.ColumnasIdentity)}");
                                if (m.TieneIdentityAlways) partes.Add($"OVERRIDING SYSTEM VALUE: {string.Join(", ", m.ColumnasIdentityAlways)}");
                                if (m.ColumnasExcluidas.Count > 0) partes.Add($"EXCLUIDAS: {string.Join(", ", m.ColumnasExcluidas)}");
                                sb.AppendLine($"  • {kv.Key} → {string.Join("; ", partes)}");
                            }
                        }
                    }

                    if (externas.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("⚠ FK cruzadas con tablas fuera del conjunto:");
                        foreach (var w in externas) sb.AppendLine($"  • {w}");
                        sb.AppendLine("  → Se desactivarán constraints temporalmente durante la transferencia.");
                    }

                    if (ciclicas.Count == 0 && externas.Count == 0)
                        sb.AppendLine("\n✓ Sin conflictos de FK detectados.");

                    txtAdvertencias.Text = sb.ToString();
                    btnAnalizarFKs.IsEnabled = true;
                });
            });
        }

        // ── Ejecutar / Generar script ─────────────────────────────────────────

        private async void btnEjecutar_Click(object sender, RoutedEventArgs e)
        {
            var conA = ObtenerConexion(cmbConexionA);
            var conB = ObtenerConexion(cmbConexionB);
            bool soloScript = chkSoloScript.IsChecked == true;

            if (conA == null)
            { MessageBox.Show("Seleccioná la conexión origen (A).", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (!soloScript && conB == null)
            { MessageBox.Show("Seleccioná la conexión destino (B), o activá 'Solo generar script'.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            var marcadas = _tablas.Where(t => t.Seleccionada).Select(t => t.ToTablaTransfer()).ToList();
            if (marcadas.Count == 0)
            { MessageBox.Show("No hay tablas marcadas.", Title, MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            // Confirmar ejecución
            if (!soloScript)
            {
                var conf = MessageBox.Show(
                    $"Se vaciarán y reemplazarán {marcadas.Count} tabla(s) en la base B.\n¿Confirmás la transferencia?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (conf != MessageBoxResult.Yes) return;
            }

            // Backup previo (en UI thread)
            string backupRutaArchivo = null;
            string backupRutaCarpeta = null;
            bool backupPorTabla     = false;

            if (chkBackup.IsChecked == true && !soloScript)
            {
                var fmtResp = MessageBox.Show(
                    "¿Guardar el backup de B en un único archivo?\n\n" +
                    "Sí = un archivo único\nNo = un archivo por cada tabla\nCancelar = omitir backup",
                    "Formato de backup", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                if (fmtResp == MessageBoxResult.Cancel)
                {
                    // El usuario eligió omitir backup — continuamos sin él
                }
                else if (fmtResp == MessageBoxResult.Yes)
                {
                    var dlg = new Microsoft.Win32.SaveFileDialog
                    {
                        Title           = "Guardar backup de la base B",
                        Filter          = "Script SQL (*.sql)|*.sql|Texto (*.txt)|*.txt",
                        DefaultExt      = "sql",
                        FileName        = $"backup_B_{DateTime.Now:yyyyMMdd_HHmm}.sql",
                        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                    };
                    if (dlg.ShowDialog() != true) return;
                    backupRutaArchivo = dlg.FileName;
                }
                else
                {
                    backupPorTabla = true;
                    var fbd = new System.Windows.Forms.FolderBrowserDialog
                    {
                        Description         = "Seleccioná la carpeta donde guardar los archivos de backup (uno por tabla)",
                        ShowNewFolderButton = true
                    };
                    if (fbd.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                    backupRutaCarpeta = fbd.SelectedPath;
                }
            }

            // Capturar valores UI antes del Task.Run
            SetUiBusy(true);
            txtLog.Clear();
            ActualizarProgreso(0, 0);
            ActualizarSentenciaActual("");
            _ultimoResultado = null;
            btnVerScript.IsEnabled        = false;
            btnVerBackup.IsEnabled        = false;
            btnGuardarUnArchivo.IsEnabled = false;
            btnGuardarPorTabla.IsEnabled  = false;

            string connStrA = ConexionesManager.GetConnectionString(conA);
            string connStrB = conB != null ? ConexionesManager.GetConnectionString(conB) : null;
            TipoMotor motorA = conA.Motor;
            TipoMotor motorB = conB?.Motor ?? conA.Motor;

            var opciones = new OpcionesPasado
            {
                GenerarSoloScript = soloScript,
                HacerBackupDeB    = false,   // lo manejamos aquí
                Transaccional     = chkTransaccional.IsChecked == true,
            };

            var progressLog  = new Progress<string>(msg => Log(msg));
            var progressSql  = new Progress<string>(sql => ActualizarSentenciaActual(sql));
            var progressStep = new Progress<(int c, int t)>(p => ActualizarProgreso(p.c, p.t));

            try
            {
                _ultimoResultado = await Task.Run(() =>
                {
                    void ReportLog(string m)  => ((IProgress<string>)progressLog).Report(m);
                    void ReportSql(string s)  => ((IProgress<string>)progressSql).Report(s);
                    void ReportStep(int c, int t) => ((IProgress<(int, int)>)progressStep).Report((c, t));

                    // FK ordering basado en B (más seguro para DELETE)
                    string connParaFKs  = connStrB ?? connStrA;
                    TipoMotor mParaFKs  = motorB;
                    ReportLog("Resolviendo dependencias FK...");
                    var fks = PasadorDatosService.ObtenerFKsEntreTablas(connParaFKs, mParaFKs, marcadas);
                    var (ordenInsert, _) = PasadorDatosService.OrdenarPorDependencias(marcadas, fks);
                    var ordenDelete = Enumerable.Reverse(ordenInsert).ToList();
                    ReportLog($"Orden FK resuelto: {ordenInsert.Count} tabla(s).");

                    // Backup
                    string backupScript = null;
                    Dictionary<string, string> backupPorTablaDict = null;

                    if (backupRutaArchivo != null)
                    {
                        ReportLog("Generando backup de B...");
                        backupScript = PasadorDatosService.GenerarBackupScript(connStrB, ordenDelete, ReportLog);
                        try
                        {
                            File.WriteAllText(backupRutaArchivo, backupScript, Encoding.UTF8);
                            ReportLog($"✓ Backup guardado: {backupRutaArchivo}");
                        }
                        catch (Exception exB) { ReportLog($"⚠ No se pudo guardar backup: {exB.Message}"); }
                    }
                    else if (backupRutaCarpeta != null)
                    {
                        ReportLog("Generando backup de B (por tabla)...");
                        backupPorTablaDict = PasadorDatosService.GenerarBackupPorTabla(connStrB, ordenDelete, ReportLog);
                        int guardados = 0;
                        foreach (var kv in backupPorTablaDict)
                        {
                            try
                            {
                                string archivo = Path.Combine(backupRutaCarpeta,
                                    kv.Key.Replace(".", "_").Replace(" ", "_") + "_backup.sql");
                                File.WriteAllText(archivo, kv.Value, Encoding.UTF8);
                                guardados++;
                            }
                            catch { }
                        }
                        ReportLog($"✓ {guardados} archivo(s) de backup guardados en: {backupRutaCarpeta}");
                    }

                    // Transferencia
                    var res = PasadorDatosService.TransferirDatos(
                        connStrA,
                        connStrB ?? connStrA,
                        motorA,
                        motorB,
                        ordenInsert,
                        ordenDelete,
                        opciones,
                        ReportLog,
                        ReportSql,
                        ReportStep);

                    if (backupScript != null) res.BackupScript = backupScript;

                    return res;
                });

                if (_ultimoResultado.Exito)
                    Log(soloScript ? "✓ Script generado correctamente." : "✓ Transferencia completada con éxito.");
                else
                    Log($"✗ Error: {_ultimoResultado.Error}");

                foreach (var adv in _ultimoResultado.Advertencias)
                    Log($"⚠ {adv}");

                ActualizarSentenciaActual("");
                ActualizarProgreso(
                    _ultimoResultado.Exito ? 1 : 0,
                    1);

                btnVerScript.IsEnabled        = !string.IsNullOrEmpty(_ultimoResultado?.ScriptTransferencia);
                btnVerBackup.IsEnabled        = !string.IsNullOrEmpty(_ultimoResultado?.BackupScript);
                btnGuardarUnArchivo.IsEnabled = !string.IsNullOrEmpty(_ultimoResultado?.ScriptTransferencia);
                btnGuardarPorTabla.IsEnabled  = _ultimoResultado?.ScriptsPorTabla?.Count > 0;

                if (backupRutaArchivo != null && File.Exists(backupRutaArchivo))
                    Log($"📁 Backup disponible en: {backupRutaArchivo}");
                if (_logFilePath != null)
                    Log($"📋 Log físico guardado en: {_logFilePath}");
            }
            catch (Exception ex)
            {
                Log($"Error inesperado: {ex.Message}");
            }
            finally
            {
                SetUiBusy(false);
            }
        }

        // ── Ver scripts ───────────────────────────────────────────────────────

        private void btnVerScript_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_ultimoResultado?.ScriptTransferencia)) return;
            var conA = ObtenerConexion(cmbConexionA);
            new ScriptResultWindow(_ultimoResultado.ScriptTransferencia, conA?.Motor ?? TipoMotor.MS_SQL)
                { Owner = this }.Show();
        }

        private void btnVerBackup_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_ultimoResultado?.BackupScript)) return;
            var conB = ObtenerConexion(cmbConexionB);
            var win = new ScriptResultWindow(_ultimoResultado.BackupScript, conB?.Motor ?? TipoMotor.MS_SQL)
                { Owner = this };
            win.Title = "Backup de Base B (estado previo)";
            win.Show();
        }

        // ── Guardar a archivo ─────────────────────────────────────────────────

        private void btnGuardarUnArchivo_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_ultimoResultado?.ScriptTransferencia)) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title            = "Guardar script de transferencia",
                Filter           = "Script SQL (*.sql)|*.sql|Texto (*.txt)|*.txt",
                DefaultExt       = "sql",
                FileName         = $"transferencia_{DateTime.Now:yyyyMMdd_HHmm}.sql",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllText(dlg.FileName, _ultimoResultado.ScriptTransferencia, Encoding.UTF8);
            Log($"✓ Script guardado en: {dlg.FileName}");
            System.Diagnostics.Process.Start(dlg.FileName);
        }

        private void btnGuardarPorTabla_Click(object sender, RoutedEventArgs e)
        {
            if (_ultimoResultado?.ScriptsPorTabla == null || _ultimoResultado.ScriptsPorTabla.Count == 0) return;
            var fbd = new System.Windows.Forms.FolderBrowserDialog
            {
                Description         = "Seleccioná la carpeta donde guardar los archivos SQL (uno por tabla)",
                ShowNewFolderButton = true
            };
            if (fbd.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            int guardados = 0;
            foreach (var kv in _ultimoResultado.ScriptsPorTabla)
            {
                string nombreArchivo = kv.Key.Replace(".", "_").Replace(" ", "_") + ".sql";
                File.WriteAllText(Path.Combine(fbd.SelectedPath, nombreArchivo), kv.Value, Encoding.UTF8);
                guardados++;
            }
            Log($"✓ {guardados} archivo(s) guardado(s) en: {fbd.SelectedPath}");
            System.Diagnostics.Process.Start(fbd.SelectedPath);
        }

        private void btnCerrar_Click(object sender, RoutedEventArgs e) => Close();

        // ── Helpers UI ────────────────────────────────────────────────────────

        private void Log(string msg)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(msg)); return; }
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
            txtLog.AppendText(line);
            scrollLog.ScrollToBottom();
            if (_logFilePath != null)
                try { File.AppendAllText(_logFilePath, line, Encoding.UTF8); } catch { }
        }

        private void ActualizarSentenciaActual(string sql)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ActualizarSentenciaActual(sql)); return; }
            if (string.IsNullOrEmpty(sql)) { txtSentenciaActual.Text = ""; return; }
            txtSentenciaActual.Text = sql.Length > 250 ? sql.Substring(0, 250) + "…" : sql;
        }

        private void ActualizarProgreso(int current, int total)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => ActualizarProgreso(current, total)); return; }
            if (total <= 0) { pbProgreso.Value = 0; txtPorcentaje.Text = ""; return; }
            pbProgreso.Maximum = total;
            pbProgreso.Value   = current;
            int pct = (int)((double)current / total * 100);
            txtPorcentaje.Text = $"{pct}%  ({current}/{total})";
        }

        private void SetUiBusy(bool busy)
        {
            btnEjecutar.IsEnabled    = !busy;
            btnAnalizarFKs.IsEnabled = !busy;
            cmbConexionA.IsEnabled   = !busy;
            cmbConexionB.IsEnabled   = !busy;
            lstTablas.IsEnabled      = !busy;
            btnEjecutar.Content = busy
                ? "⏳ Procesando..."
                : (chkSoloScript.IsChecked == true ? "📄 Generar script" : "▶ Ejecutar transferencia");
        }
    }
}
