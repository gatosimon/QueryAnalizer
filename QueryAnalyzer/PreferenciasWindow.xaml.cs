using System;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace QueryAnalyzer
{
    /// <summary>
    /// Ventana de Preferencias de Query Analyzer.
    /// Permite cambiar el tema (Claro/Oscuro), activar/desactivar IntelliSense
    /// y configurar el comportamiento al cambiar de conexión.
    /// Los cambios se persisten en config.xml vía ConfiguracionManager.
    /// </summary>
    public partial class PreferenciasWindow : Window
    {
        // Configuración actual cargada al abrir la ventana
        private Configuracion _config;

        public PreferenciasWindow()
        {
            InitializeComponent();
            AplicarTemaActual();
            CargarPreferencias();
        }

        // ── Tema ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Copia el ResourceDictionary de tema activo desde MainWindow para
        /// que esta ventana respete el tema seleccionado por el usuario.
        /// </summary>
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

        /// <summary>
        /// Aplica una vista previa instantánea del tema seleccionado en esta ventana.
        /// </summary>
        private void rdTema_Checked(object sender, RoutedEventArgs e)
        {
            bool oscuro = rdOscuro.IsChecked == true;
            string archivo = oscuro ? "ThemeDark.xaml" : "ThemeLight.xaml";
            string ruta = Path.Combine(App.AppDataFolder, "Themes", archivo);

            if (!File.Exists(ruta)) return;

            try
            {
                using (var stream = File.OpenRead(ruta))
                {
                    var tema = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
                    var merged = this.Resources.MergedDictionaries;
                    if (merged.Count > 0) merged[0] = tema;
                    else merged.Add(tema);
                }
            }
            catch { /* Si falla la previsualización, no interrumpir */ }
        }

        // ── Carga de preferencias ─────────────────────────────────────────────

        private void CargarPreferencias()
        {
            _config = ConfiguracionManager.Cargar();

            // Tema
            rdClaro.IsChecked  = !_config.ModoOscuro;
            rdOscuro.IsChecked = _config.ModoOscuro;

            // IntelliSense
            chkIntelliSense.IsChecked = _config.IntelliSenseHabilitado;

            // Comportamiento
            chkCargarConsulta.IsChecked = _config.CargarUltimaConsultaAlConectar;

            // Ruta de temas en AppData
            txtRutaTemas.Text = Path.Combine(App.AppDataFolder, "Themes");
        }

        // ── Abrir carpeta de temas ─────────────────────────────────────────────

        private void TxtRutaTemas_Click(object sender, MouseButtonEventArgs e)
        {
            string ruta = Path.Combine(App.AppDataFolder, "Themes");
            if (Directory.Exists(ruta))
            {
                try { System.Diagnostics.Process.Start("explorer.exe", ruta); }
                catch { }
            }
        }

        // ── Botones ──────────────────────────────────────────────────────────

        private void BtnGuardar_Click(object sender, RoutedEventArgs e)
        {
            _config.ModoOscuro                    = rdOscuro.IsChecked == true;
            _config.IntelliSenseHabilitado        = chkIntelliSense.IsChecked == true;
            _config.CargarUltimaConsultaAlConectar = chkCargarConsulta.IsChecked == true;

            try
            {
                ConfiguracionManager.Guardar(_config);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al guardar preferencias: " + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
