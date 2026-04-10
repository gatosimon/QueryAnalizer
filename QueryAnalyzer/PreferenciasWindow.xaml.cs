using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;

namespace QueryAnalyzer
{
    public partial class PreferenciasWindow : Window
    {
        // Evento que MainWindow escucha para aplicar cambios sin necesidad de reabrir
        public event Action<AppConfig> ConfigGuardada;

        private AppConfig _configOriginal;

        // ── Colores predeterminados ───────────────────────────────────────────

        private static readonly Dictionary<string, string> DefaultClaro =
            new Dictionary<string, string>
            {
                { "BrushWindowBG",   "#FFFFFF" },
                { "BrushPanelBG",    "#FFFFFF" },
                { "BrushControlBG",  "#FFFFFF" },
                { "BrushAltRowBG",   "#F0F8FF" },
                { "BrushBorder",     "#CCCCCC" },
                { "BrushSplitter",   "#DDDDDD" },
                { "BrushFG",         "#1A1A1A" },
                { "BrushFGMuted",    "#555555" },
                { "BrushHover",      "#1E90FF" },
                { "BrushSelected",   "#A7C7FF" },
                { "BrushSelectedFG", "#000000" },
                { "BrushAccent",     "#0078D4" },
                { "BrushHeaderBG",   "#696969" },
                { "BrushHeaderFG",   "#FFFFFF" },
                { "BrushBtnBG",      "#F0F0F0" },
                { "BrushBtnBorder",  "#AAAAAA" },
                { "BrushTreeHover",  "#E8F0FE" },
                { "BrushTreeSel",    "#A7C7FF" },
                { "BrushTabSelBG",   "#0078D4" },
                { "BrushTabSelBdr",  "#0078D4" },
                { "BrushTabSelFG",   "#F0F0F0" },
                { "BrushMenuBG",     "#FFFFFF" },
                { "BrushMenuHover",  "#A7C7FF" },
                { "BrushSeparator",  "#DDDDDD" },
                { "BrushEditor",     "#FFFFFF" },
                { "BrushEditorFG",   "#1A1A1A" },
                { "BrushRowHover",   "#FFD800" },
            };

        private static readonly Dictionary<string, string> DefaultOscuro =
            new Dictionary<string, string>
            {
                { "BrushWindowBG",   "#1E1E1E" },
                { "BrushPanelBG",    "#252526" },
                { "BrushControlBG",  "#696969" },
                { "BrushAltRowBG",   "#2D2D30" },
                { "BrushBorder",     "#3F3F46" },
                { "BrushSplitter",   "#3F3F46" },
                { "BrushFG",         "#D4D4D4" },
                { "BrushFGMuted",    "#9CDCFE" },
                { "BrushHover",      "#FF00DC" },
                { "BrushSelected",   "#6495ED" },
                { "BrushSelectedFG", "#000000" },
                { "BrushAccent",     "#007ACC" },
                { "BrushHeaderBG",   "#4169E1" },
                { "BrushHeaderFG",   "#DCDCAA" },
                { "BrushBtnBG",      "#4827FF" },
                { "BrushBtnBorder",  "#3F3F46" },
                { "BrushTreeHover",  "#2A2D2E" },
                { "BrushTreeSel",    "#264F78" },
                { "BrushTabSelBG",   "#252526" },
                { "BrushTabSelBdr",  "#007ACC" },
                { "BrushTabSelFG",   "#1E90FF" },
                { "BrushMenuBG",     "#2D2D30" },
                { "BrushMenuHover",  "#264F78" },
                { "BrushSeparator",  "#3F3F46" },
                { "BrushEditor",     "#1E1E1E" },
                { "BrushEditorFG",   "#D4D4D4" },
                { "BrushRowHover",   "#FFD800" },
            };

        // ── Estado ───────────────────────────────────────────────────────────

        private List<BrushEntrada> _entradasClaro;
        private List<BrushEntrada> _entradasOscuro;

        private static readonly string ThemesFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "QueryAnalyzer", "Themes");

        // ── Constructor ──────────────────────────────────────────────────────

        public PreferenciasWindow()
        {
            InitializeComponent();
            AplicarTemaActual();
        }

        // ── Carga inicial ─────────────────────────────────────────────────────

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _configOriginal = ConfigManager.ObtenerConfiguracion();

            chkTemaOscuro.IsChecked        = _configOriginal.TemaOscuro;
            chkIntellisense.IsChecked      = _configOriginal.IntellisenseActivo;
            chkCargarUltConsulta.IsChecked = _configOriginal.CargarUltimaConsulta;

            txtRutaConfig.Text = "Ruta: " + Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QueryAnalyzer", "config.xml");

            CargarColoresTemas();
        }

        // ── Toggle de tema en tiempo real ─────────────────────────────────────

        private void chkTemaOscuro_Changed(object sender, RoutedEventArgs e)
        {
            AplicarTemaSegun(chkTemaOscuro.IsChecked == true);
        }

        private void AplicarTemaSegun(bool oscuro)
        {
            string archivo = oscuro ? "ThemeDark.xaml" : "ThemeLight.xaml";
            string ruta    = Path.Combine(ThemesFolder, archivo);

            ResourceDictionary tema;
            if (File.Exists(ruta))
            {
                using (var stream = File.OpenRead(ruta))
                    tema = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
            }
            else
            {
                var uri = new Uri($"pack://application:,,,/{archivo}", UriKind.Absolute);
                tema = new ResourceDictionary { Source = uri };
            }

            var wd = Resources.MergedDictionaries;
            if (wd.Count > 0) wd[0] = tema;
            else wd.Add(tema);
        }

        // ── Colores de temas ──────────────────────────────────────────────────

        private void CargarColoresTemas()
        {
            _entradasClaro  = CargarEntradasDesdeArchivo("ThemeLight.xaml", DefaultClaro);
            _entradasOscuro = CargarEntradasDesdeArchivo("ThemeDark.xaml",  DefaultOscuro);

            listClaro.ItemsSource  = _entradasClaro;
            listOscuro.ItemsSource = _entradasOscuro;
        }

        private List<BrushEntrada> CargarEntradasDesdeArchivo(
            string nombreArchivo,
            Dictionary<string, string> defaults)
        {
            var resultado     = new List<BrushEntrada>();
            string ruta       = Path.Combine(ThemesFolder, nombreArchivo);
            var coloresLeidos = new Dictionary<string, string>();

            if (File.Exists(ruta))
            {
                try
                {
                    var doc = XDocument.Load(ruta);
                    XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                    XNamespace x  = "http://schemas.microsoft.com/winfx/2006/xaml";

                    foreach (var elem in doc.Root.Elements(ns + "SolidColorBrush"))
                    {
                        string key   = (string)elem.Attribute(x + "Key");
                        string color = (string)elem.Attribute("Color");
                        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(color))
                            coloresLeidos[key] = NormalizarHex(color);
                    }
                }
                catch { }
            }

            foreach (var kvp in defaults)
            {
                string hex = coloresLeidos.ContainsKey(kvp.Key)
                    ? coloresLeidos[kvp.Key]
                    : kvp.Value;
                resultado.Add(new BrushEntrada(kvp.Key, hex));
            }

            return resultado;
        }

        // ── Color picker ──────────────────────────────────────────────────────

        private void ColorSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            var border  = sender as System.Windows.Controls.Border;
            var entrada = border?.Tag as BrushEntrada;
            if (entrada == null) return;
            AbrirColorPicker(entrada);
        }

        private void HexBox_LostFocus(object sender, RoutedEventArgs e)
        {
            var txtBox  = sender as System.Windows.Controls.TextBox;
            var entrada = txtBox?.Tag as BrushEntrada;
            if (entrada == null) return;
            entrada.AplicarHex(txtBox.Text.Trim());
        }

        private void AbrirColorPicker(BrushEntrada entrada)
        {
            System.Drawing.Color colorInicial = System.Drawing.Color.White;
            try
            {
                var wpfColor = (Color)ColorConverter.ConvertFromString(entrada.Hex);
                colorInicial = System.Drawing.Color.FromArgb(
                    wpfColor.A, wpfColor.R, wpfColor.G, wpfColor.B);
            }
            catch { }

            using (var dlg = new System.Windows.Forms.ColorDialog())
            {
                dlg.Color         = colorInicial;
                dlg.FullOpen      = true;
                dlg.AnyColor      = true;
                dlg.AllowFullOpen = true;

                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    var c = dlg.Color;
                    entrada.AplicarHex($"#{c.R:X2}{c.G:X2}{c.B:X2}");
                }
            }
        }

        // ── Restaurar predeterminados ─────────────────────────────────────────

        private void BtnRestaurarClaro_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(
                    "¿Restaurar todos los colores del Tema Claro a sus valores predeterminados?",
                    "Restaurar Tema Claro", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;

            foreach (var entrada in _entradasClaro)
                if (DefaultClaro.ContainsKey(entrada.Key))
                    entrada.AplicarHex(DefaultClaro[entrada.Key]);
        }

        private void BtnRestaurarOscuro_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(
                    "¿Restaurar todos los colores del Tema Oscuro a sus valores predeterminados?",
                    "Restaurar Tema Oscuro", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;

            foreach (var entrada in _entradasOscuro)
                if (DefaultOscuro.ContainsKey(entrada.Key))
                    entrada.AplicarHex(DefaultOscuro[entrada.Key]);
        }

        // ── Botones principales ───────────────────────────────────────────────

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
                GuardarColoresTema("ThemeLight.xaml", _entradasClaro,  esOscuro: false);
                GuardarColoresTema("ThemeDark.xaml",  _entradasOscuro, esOscuro: true);
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

        // ── Escritura de archivos de tema ─────────────────────────────────────

        private void GuardarColoresTema(
            string nombreArchivo,
            List<BrushEntrada> entradas,
            bool esOscuro)
        {
            string ruta = Path.Combine(ThemesFolder, nombreArchivo);
            Directory.CreateDirectory(ThemesFolder);

            string comentario = esOscuro
                ? "── PALETA OSCURA ────────────────────────────────────────── "
                : "── PALETA CLARA ─────────────────────────────────────────── ";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"");
            sb.AppendLine("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">");
            sb.AppendLine();
            sb.AppendLine($"    <!-- {comentario}-->");

            int maxLen = entradas.Max(en => en.Key.Length);
            foreach (var entrada in entradas)
            {
                string padding = new string(' ', maxLen - entrada.Key.Length);
                sb.AppendLine($"    <SolidColorBrush x:Key=\"{entrada.Key}\"{padding}   Color=\"{entrada.Hex}\"/>");
            }

            if (esOscuro)
                sb.AppendLine("    <x:String x:Key=\"ThemeToggleIcon\">&#x2600;&#xFE0F;</x:String>");

            sb.AppendLine();
            sb.AppendLine("</ResourceDictionary>");

            File.WriteAllText(ruta, sb.ToString(), System.Text.Encoding.UTF8);
        }

        // ── Tema de MainWindow al abrir ───────────────────────────────────────

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

        // ── Helper ───────────────────────────────────────────────────────────

        private static string NormalizarHex(string valor)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(valor);
                return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            }
            catch { return valor; }
        }
    }

    // ── Modelo de ítem ────────────────────────────────────────────────────────

    /// <summary>
    /// Representa un SolidColorBrush editable del archivo de tema.
    /// INotifyPropertyChanged hace que el swatch se actualice en tiempo real.
    /// </summary>
    public class BrushEntrada : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private string _hex;
        private SolidColorBrush _colorActual;

        public string Key { get; }

        public string Hex
        {
            get => _hex;
            set { if (_hex != value) { _hex = value; Notify(nameof(Hex)); } }
        }

        public SolidColorBrush ColorActual
        {
            get => _colorActual;
            private set { _colorActual = value; Notify(nameof(ColorActual)); }
        }

        public BrushEntrada(string key, string hex)
        {
            Key = key;
            AplicarHex(hex);
        }

        /// <summary>
        /// Valida y aplica un hex. Si es inválido guarda el texto sin actualizar el swatch.
        /// </summary>
        public void AplicarHex(string hex)
        {
            if (!hex.StartsWith("#")) hex = "#" + hex;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                _hex        = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                ColorActual = new SolidColorBrush(c);
                Notify(nameof(Hex));
            }
            catch
            {
                _hex = hex;
                Notify(nameof(Hex));
            }
        }

        private void Notify(string prop) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
