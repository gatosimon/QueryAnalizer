using System;
using System.Collections.Generic;
using CapiDL;
using System.Linq;
using System.Threading;
using Microsoft.Win32;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Controls;
using QueryAnalyzer.Models;

namespace QueryAnalyzer
{
    public partial class DatosConexion : Window
    {
        private Dictionary<TipoMotor, string> motores;
        private List<ServidorPreset> _todosLosPresets = new List<ServidorPreset>();

        /// <summary>
        /// Evita que los eventos de cambio (motor, servidor) disparen el cascadeo
        /// mientras se están cargando los datos de una conexión existente.
        /// </summary>
        private bool _inicializando = false;

        /// <summary>
        /// Token para cancelar la carga async de bases de datos cuando el usuario
        /// cambia de motor o de preset antes de que termine la operación anterior.
        /// </summary>
        private CancellationTokenSource _ctsCargaBases;

        public Conexion ConexionActual = null;

        public DatosConexion()
        {
            InitializeComponent();
            AplicarTemaActual();
            InicializarMotores();
            CargarServidoresPreset();
        }

        public DatosConexion(Conexion conexion)
        {
            InitializeComponent();
            AplicarTemaActual();
            InicializarMotores();
            CargarServidoresPreset();
            ConexionActual = conexion;
            InicializarDatosConexion();
        }

        // ══════════════════════════════════════════════════════════════════════
        // PRESETS
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Carga todos los presets en memoria y aplica el filtro por motor activo.</summary>
        private void CargarServidoresPreset()
        {
            try
            {
                _todosLosPresets = ServidorPresetManager.Cargar();
                AplicarFiltroPresets();
            }
            catch { /* no interrumpir si falla */ }
        }

        /// <summary>
        /// Filtra _todosLosPresets por el motor actualmente seleccionado y actualiza
        /// el ItemsSource del ComboBox sin perder el texto que ya había escrito el usuario.
        /// </summary>
        private void AplicarFiltroPresets()
        {
            if (cmbMotor.SelectedValue == null) return;
            TipoMotor motorActual = (TipoMotor)cmbMotor.SelectedValue;
            string textoActual = cmbServidor.Text;

            var filtrados = _todosLosPresets
                .Where(p => p.Motor == motorActual)
                .ToList();

            // Bloquear temporalmente SelectionChanged para que el cambio de ItemsSource
            // no dispare un autofill no deseado
            cmbServidor.SelectionChanged -= cmbServidor_SelectionChanged;
            cmbServidor.ItemsSource       = filtrados;
            cmbServidor.Text              = textoActual;
            cmbServidor.SelectionChanged += cmbServidor_SelectionChanged;
        }

        // ══════════════════════════════════════════════════════════════════════
        // CASCADEO: MOTOR → limpia todo hacia abajo
        // ══════════════════════════════════════════════════════════════════════

        private void cmbMotor_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbMotor.SelectedValue == null) return;
            TipoMotor motor = (TipoMotor)cmbMotor.SelectedValue;

            if (!_inicializando)
            {
                // Cancelar cualquier carga de bases de datos en curso
                _ctsCargaBases?.Cancel();

                // Limpiar todo lo que está debajo del motor
                cmbServidor.SelectionChanged -= cmbServidor_SelectionChanged;
                cmbServidor.SelectedIndex     = -1;
                cmbServidor.Text              = "";
                cmbServidor.SelectionChanged += cmbServidor_SelectionChanged;

                txtPuerto.Text         = "";
                chkEsWeb.IsChecked     = false;
                txtUsuario.Text        = "";
                txtContrasena.Password = "";

                // Si la contraseña estaba en modo "revelada", volver al modo oculto
                if (txtContrasenaRevelada.Visibility == Visibility.Visible)
                {
                    txtContrasenaRevelada.Text       = "";
                    txtContrasenaRevelada.Visibility = Visibility.Collapsed;
                    txtContrasena.Visibility         = Visibility.Visible;
                    btnTogglePass.Content            = "👁";
                }

                txtBaseDatos.Text        = "";
                cmbBaseDatos.ItemsSource = null;  // liberar antes de limpiar Items
                cmbBaseDatos.Items.Clear();
            }

            AplicarFiltroPresets();
            AjustarVisibilidadPorMotor(motor);
        }

        /// <summary>
        /// Adapta la visibilidad de los controles según el motor seleccionado.
        /// Para DB2 carga la lista fija de bases; para los demás deja el textbox
        /// listo para entrada manual (o para que lo llene el preset).
        /// </summary>
        private void AjustarVisibilidadPorMotor(TipoMotor motor)
        {
            if (motor == TipoMotor.SQLite)
            {
                // Collapsed (no Hidden) para que los rows se achiquen y la ventana se reduzca
                btnBuscarBase.IsEnabled  = true;
                lblPuerto.Visibility     = Visibility.Collapsed;
                pnlPuerto.Visibility     = Visibility.Collapsed;
                lblUsuario.Visibility    = Visibility.Collapsed;
                txtUsuario.Visibility    = Visibility.Collapsed;
                lblContrasena.Visibility = Visibility.Collapsed;
                txtContrasena.Visibility = Visibility.Collapsed;
                btnTogglePass.Visibility = Visibility.Collapsed;
                lblBaseDatos.Visibility  = Visibility.Collapsed;
                cmbBaseDatos.Visibility  = Visibility.Collapsed;
                txtBaseDatos.Visibility  = Visibility.Collapsed;
                return;
            }

            // Motores con usuario y contraseña
            btnBuscarBase.IsEnabled  = false;
            lblPuerto.Visibility     = Visibility.Visible;
            pnlPuerto.Visibility     = Visibility.Visible;
            lblUsuario.Visibility    = Visibility.Visible;
            txtUsuario.Visibility    = Visibility.Visible;
            lblContrasena.Visibility = Visibility.Visible;
            txtContrasena.Visibility = Visibility.Visible;
            btnTogglePass.Visibility = Visibility.Visible;
            lblBaseDatos.Visibility  = Visibility.Visible;

            if (motor == TipoMotor.DB2)
            {
                // Lista fija: no requiere conexión
                cmbBaseDatos.ItemsSource = ConexionesManager.BasesDB2;
                if (!_inicializando)
                    cmbBaseDatos.SelectedIndex = 0;
                cmbBaseDatos.Visibility = Visibility.Visible;
                cmbBaseDatos.IsEnabled  = true;
                txtBaseDatos.Visibility = Visibility.Hidden;
                txtBaseDatos.IsEnabled  = false;
            }
            else
            {
                // MS_SQL / POSTGRES: entrada manual hasta que se conecte con éxito
                txtBaseDatos.Visibility = Visibility.Visible;
                txtBaseDatos.IsEnabled  = true;
                cmbBaseDatos.Visibility = Visibility.Hidden;
                cmbBaseDatos.IsEnabled  = false;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // CASCADEO: SERVIDOR (preset) → llena todo hacia abajo
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Cuando el usuario elige un preset del desplegable, se autocompletan todos
        /// los campos y se dispara la carga de bases de datos en segundo plano.
        /// </summary>
        private void cmbServidor_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            if (!(e.AddedItems[0] is ServidorPreset preset)) return;

            txtPuerto.Text     = preset.Puerto;
            chkEsWeb.IsChecked = preset.EsWeb;
            txtUsuario.Text    = preset.Usuario;

            if (!string.IsNullOrEmpty(preset.Contrasena))
                txtContrasena.Password = preset.Contrasena;

            // Cargar lista de bases de datos en segundo plano
            CargarBasesDatosAsync(
                preset.Motor, preset.Servidor, preset.Puerto,
                preset.Usuario, preset.Contrasena, preset.EsWeb,
                preset.BaseDatos ?? "");
        }

        // ══════════════════════════════════════════════════════════════════════
        // CARGA ASYNC DE BASES DE DATOS
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Carga la lista de bases de datos disponibles para el servidor dado.
        /// Cancela automáticamente cualquier carga anterior que esté pendiente.
        /// DB2 → lista fija hardcodeada (sin intento de conexión).
        /// MS_SQL / POSTGRES → intento async con timeout de 15 s.
        /// SQLite → no aplica.
        /// </summary>
        private void CargarBasesDatosAsync(
            TipoMotor motor, string servidor, string puerto,
            string usuario, string contrasena, bool esWeb,
            string baseDatosPref = "")
        {
            // Cancelar operación previa si aún está corriendo
            _ctsCargaBases?.Cancel();
            _ctsCargaBases = new CancellationTokenSource();
            var token = _ctsCargaBases.Token;

            // ── DB2: lista fija ──────────────────────────────────────────────
            if (motor == TipoMotor.DB2)
            {
                cmbBaseDatos.ItemsSource = ConexionesManager.BasesDB2;
                cmbBaseDatos.SelectedValue = !string.IsNullOrWhiteSpace(baseDatosPref)
                    ? baseDatosPref
                    : null;
                if (cmbBaseDatos.SelectedItem == null)
                    cmbBaseDatos.SelectedIndex = 0;
                cmbBaseDatos.Visibility = Visibility.Visible;
                cmbBaseDatos.IsEnabled  = true;
                txtBaseDatos.Visibility = Visibility.Hidden;
                txtBaseDatos.IsEnabled  = false;
                return;
            }

            // ── SQLite: no aplica lista de bases ─────────────────────────────
            if (motor == TipoMotor.SQLite)
                return;

            // ── MS_SQL / POSTGRES: mostrar indicador y conectar en background ─
            txtBaseDatos.Text       = "Conectando…";
            txtBaseDatos.Visibility = Visibility.Visible;
            txtBaseDatos.IsEnabled  = false;
            cmbBaseDatos.Visibility = Visibility.Hidden;
            cmbBaseDatos.IsEnabled  = false;

            Task.Run(() =>
            {
                try
                {
                    if (token.IsCancellationRequested) return;

                    string connStr = ConexionesManager.GetConnectionString(
                        motor, servidor, puerto, "", usuario, contrasena, esWeb);

                    if (motor == TipoMotor.MS_SQL)
                        connStr += "LoginTimeout=15;";
                    else if (motor == TipoMotor.POSTGRES)
                        connStr += "connect_timeout=15;";

                    string query = motor == TipoMotor.MS_SQL
                        ? "SELECT UPPER(name) FROM sys.databases WHERE state = 0 ORDER BY name ASC"
                        : "SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname";

                    DataBase DB = new DataBase(connStr);
                    DB.CommandText = query;

                    var bases = new List<string>();
                    while (DB.Read())
                    {
                        if (token.IsCancellationRequested) { DB.CloseConnection(); return; }
                        bases.Add(DB.Reader.GetString(0));
                    }
                    DB.CloseConnection();

                    if (token.IsCancellationRequested) return;

                    Dispatcher.Invoke(() =>
                    {
                        if (token.IsCancellationRequested) return;

                        cmbBaseDatos.ItemsSource = null;  // liberar ItemsSource (puede venir de DB2)
                        cmbBaseDatos.Items.Clear();
                        foreach (var b in bases)
                            cmbBaseDatos.Items.Add(b);

                        if (!string.IsNullOrWhiteSpace(baseDatosPref))
                            cmbBaseDatos.SelectedValue = baseDatosPref.ToUpper();
                        else if (cmbBaseDatos.Items.Count > 0)
                            cmbBaseDatos.SelectedIndex = 0;

                        cmbBaseDatos.Visibility = Visibility.Visible;
                        cmbBaseDatos.IsEnabled  = true;
                        txtBaseDatos.Visibility = Visibility.Hidden;
                        txtBaseDatos.IsEnabled  = false;
                    });
                }
                catch
                {
                    if (token.IsCancellationRequested) return;

                    Dispatcher.Invoke(() =>
                    {
                        if (token.IsCancellationRequested) return;

                        txtBaseDatos.Text       = baseDatosPref;
                        txtBaseDatos.Visibility = Visibility.Visible;
                        txtBaseDatos.IsEnabled  = true;
                        cmbBaseDatos.Visibility = Visibility.Hidden;
                        cmbBaseDatos.IsEnabled  = false;
                    });
                }
            }, token);
        }

        // ══════════════════════════════════════════════════════════════════════
        // INICIALIZACIÓN
        // ══════════════════════════════════════════════════════════════════════

        private void AplicarTemaActual()
        {
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow == null) return;
            var mainMerged = mainWindow.Resources.MergedDictionaries;
            if (mainMerged.Count == 0) return;

            var temaActual = mainMerged[0];
            var misMerged  = this.Resources.MergedDictionaries;
            if (misMerged.Count > 0)
                misMerged[0] = temaActual;
            else
                misMerged.Add(temaActual);
        }

        private void InicializarMotores()
        {
            motores = Enum.GetValues(typeof(TipoMotor))
                .Cast<TipoMotor>()
                .ToDictionary(m => m, m => m.ToString().Replace("_", " "));

            cmbMotor.ItemsSource   = motores;
            cmbMotor.SelectedIndex = 0;
        }

        private void InicializarDatosConexion()
        {
            if (ConexionActual == null) return;

            _inicializando = true;
            try
            {
                txtNombre.Text = ConexionActual.Nombre;

                // Motor primero → dispara cmbMotor_SelectionChanged (sin cascadeo por el flag)
                cmbMotor.SelectedValue = ConexionActual.Motor;

                // Restaurar servidor explícitamente
                cmbServidor.Text       = ConexionActual.Servidor;

                txtPuerto.Text         = ConexionActual.Puerto;
                chkEsWeb.IsChecked     = ConexionActual.EsWeb;
                txtUsuario.Text        = ConexionActual.Usuario;
                txtContrasena.Password = ConexionActual.Contrasena;
            }
            finally
            {
                _inicializando = false;
            }

            // Cargar bases de datos (fuera del flag: opera en async)
            CargarBasesDatosAsync(
                ConexionActual.Motor, ConexionActual.Servidor, ConexionActual.Puerto,
                ConexionActual.Usuario, ConexionActual.Contrasena, ConexionActual.EsWeb,
                ConexionActual.BaseDatos ?? "");
        }

        // ══════════════════════════════════════════════════════════════════════
        // EVENTOS Y BOTONES
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Al salir del campo contraseña, si hay servidor y usuario completos,
        /// intenta cargar la lista de bases de datos en segundo plano.
        /// </summary>
        private void txtContrasena_LostFocus(object sender, RoutedEventArgs e)
        {
            if (cmbMotor.SelectedValue == null) return;
            TipoMotor motor = (TipoMotor)cmbMotor.SelectedValue;

            if (motor != TipoMotor.MS_SQL && motor != TipoMotor.POSTGRES) return;
            if (string.IsNullOrWhiteSpace(cmbServidor.Text)) return;

            CargarBasesDatosAsync(
                motor,
                cmbServidor.Text.Trim(),
                txtPuerto.Text,
                txtUsuario.Text.Trim(),
                txtContrasena.Password,
                chkEsWeb.IsChecked.GetValueOrDefault());
        }

        private void btnBuscarBase_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Seleccionar archivo SQLite",
                Filter = "Base de datos SQLite (*.db)|*.db|Todos los archivos (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true)
                cmbServidor.Text = dlg.FileName;
        }

        private void btnProbar_Click(object sender, RoutedEventArgs e)
        {
            TipoMotor motor = (TipoMotor)cmbMotor.SelectedValue;

            string baseDatos = cmbBaseDatos.Visibility == Visibility.Visible
                               ? cmbBaseDatos.Text.Trim()
                               : txtBaseDatos.Text.Trim();
            string stringConnection = ConexionesManager.GetConnectionString(
                motor, cmbServidor.Text, txtPuerto.Text,
                baseDatos, txtUsuario.Text, txtContrasena.Password,
                chkEsWeb.IsChecked.GetValueOrDefault());

            if (string.IsNullOrWhiteSpace(stringConnection))
            {
                MessageBox.Show("El string de conexión está vacío.", "Atención");
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    DataBase DB = new DataBase(stringConnection);
                    MessageBox.Show("Conexión exitosa.");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Conexión fallida: " + ex.Message);
                }
            });
        }

        private void btnGuardar_Click(object sender, RoutedEventArgs e)
        {
            if (ConexionActual == null)
                ConexionActual = new Conexion();

            ConexionActual.Nombre     = txtNombre.Text.Trim();
            ConexionActual.Motor      = (TipoMotor)cmbMotor.SelectedValue;
            ConexionActual.Servidor   = cmbServidor.Text.Trim();
            ConexionActual.BaseDatos  = cmbBaseDatos.Visibility == Visibility.Visible
                                        ? cmbBaseDatos.Text.Trim()
                                        : txtBaseDatos.Text.Trim();
            ConexionActual.Usuario    = txtUsuario.Text.Trim();
            ConexionActual.Contrasena = txtContrasena.Password;
            ConexionActual.Puerto     = txtPuerto.Text;
            ConexionActual.EsWeb      = chkEsWeb.IsChecked.GetValueOrDefault();

            var conexiones = ConexionesManager.Cargar();
            conexiones[ConexionActual.Nombre] = ConexionActual;
            ConexionesManager.Guardar(conexiones);

            MainWindow.conexionActual = ConexionActual;
            MessageBox.Show("Conexión guardada correctamente.", "Éxito",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }

        private void btnTogglePass_Click(object sender, RoutedEventArgs e)
        {
            if (txtContrasenaRevelada.Visibility == Visibility.Collapsed)
            {
                txtContrasenaRevelada.Text       = txtContrasena.Password;
                txtContrasena.Visibility         = Visibility.Collapsed;
                txtContrasenaRevelada.Visibility = Visibility.Visible;
                btnTogglePass.Content            = "🙈";
                txtContrasenaRevelada.Focus();
            }
            else
            {
                txtContrasena.Password           = txtContrasenaRevelada.Text;
                txtContrasenaRevelada.Visibility = Visibility.Collapsed;
                txtContrasena.Visibility         = Visibility.Visible;
                btnTogglePass.Content            = "👁";
                txtContrasena.Focus();
            }
        }
    }
}
