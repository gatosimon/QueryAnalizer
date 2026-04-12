using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace QueryAnalyzer
{
    public partial class PreferenciasWindow : Window
    {
        // Evento que MainWindow escucha para aplicar cambios sin necesidad de reabrir
        public event Action<AppConfig> ConfigGuardada;

        private AppConfig _configOriginal;

        public PreferenciasWindow()
        {
            InitializeComponent();
            AplicarTemaActual();
        }

        // ── Carga inicial ─────────────────────────────────────────────────────────

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _configOriginal = ConfigManager.ObtenerConfiguracion();

            chkTemaOscuro.IsChecked        = _configOriginal.TemaOscuro;
            chkIntellisense.IsChecked      = _configOriginal.IntellisenseActivo;
            chkCargarUltConsulta.IsChecked = _configOriginal.CargarUltimaConsulta;

            txtRutaConfig.Text = "Ruta: " + Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QueryAnalyzer", "config.xml");
        }

        // ── Toggle de tema en tiempo real ─────────────────────────────────────────

        private void chkTemaOscuro_Changed(object sender, RoutedEventArgs e)
        {
            // Vista previa inmediata del tema en esta ventana
            AplicarTemaSegun(chkTemaOscuro.IsChecked == true);
        }

        private void AplicarTemaSegun(bool oscuro)
        {
            string archivo = oscuro ? "ThemeDark.xaml" : "ThemeLight.xaml";
            string ruta    = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QueryAnalyzer", "Themes", archivo);

            ResourceDictionary tema;
            if (File.Exists(ruta))
            {
                using (var stream = File.OpenRead(ruta))
                    tema = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
            }
            else
            {
                // Fallback al recurso embebido
                var uri = new Uri($"pack://application:,,,/{archivo}", UriKind.Absolute);
                tema = new ResourceDictionary { Source = uri };
            }

            var wd = Resources.MergedDictionaries;
            if (wd.Count > 0) wd[0] = tema;
            else wd.Add(tema);
        }

        // ── Botones ───────────────────────────────────────────────────────────────

        private void BtnGuardar_Click(object sender, RoutedEventArgs e)
        {
            var nueva = new AppConfig
            {
                TemaOscuro           = chkTemaOscuro.IsChecked == true,
                IntellisenseActivo   = chkIntellisense.IsChecked == true,
                CargarUltimaConsulta = chkCargarUltConsulta.IsChecked == true
            };

            try
            {
                ConfigManager.GuardarConfiguracion(nueva);
                ConfigGuardada?.Invoke(nueva);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al guardar preferencias:\n" + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            // Revertir vista previa del tema si el usuario cancela
            AplicarTemaSegun(_configOriginal.TemaOscuro);
            DialogResult = false;
            Close();
        }

        private void BtnAbrirCarpeta_Click(object sender, RoutedEventArgs e)
        {
            string carpeta = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QueryAnalyzer");
            if (Directory.Exists(carpeta))
                Process.Start("explorer.exe", carpeta);
        }

        // ── Aplicar tema de MainWindow al abrir ───────────────────────────────────

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
    }
}
