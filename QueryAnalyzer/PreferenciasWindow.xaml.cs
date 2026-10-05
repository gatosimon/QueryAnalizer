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

        // Version de la paleta embebida (ThemeLight.xaml / ThemeDark.xaml). Subirla al cambiar los colores
        // por defecto: MainWindow reemplaza (con backup .bak) los archivos de tema guardados con una version menor.
        public const int VersionTemas = 5;

        private static readonly Dictionary<string, string> DefaultClaro =
            new Dictionary<string, string>
            {
                { "BrushWindowBG",   "#F3F3F3" },
                { "BrushPanelBG",    "#FFFFFF" },
                { "BrushControlBG",  "#FFFFFF" },
                { "BrushAltRowBG",   "#F7F7F7" },
                { "BrushBorder",     "#D1D1D1" },
                { "BrushSplitter",   "#E0E0E0" },
                { "BrushFG",         "#1B1B1B" },
                { "BrushFGMuted",    "#616161" },
                { "BrushHover",      "#E9E9E9" },
                { "BrushSelected",   "#CCE4F7" },
                { "BrushSelectedFG", "#1B1B1B" },
                { "BrushAccent",     "#0067C0" },
                { "BrushHeaderBG",   "#F0F0F0" },
                { "BrushHeaderFG",   "#1B1B1B" },
                { "BrushBtnBG",      "#FBFBFB" },
                { "BrushBtnBorder",  "#D1D1D1" },
                { "BrushTreeHover",  "#EEEEEE" },
                { "BrushTreeSel",    "#CCE4F7" },
                { "BrushTabSelBG",   "#FFFFFF" },
                { "BrushTabSelBdr",  "#0067C0" },
                { "BrushTabSelFG",   "#0067C0" },
                { "BrushMenuBG",     "#FFFFFF" },
                { "BrushMenuHover",  "#EAEAEA" },
                { "BrushSeparator",  "#E0E0E0" },
                { "BrushEditor",     "#FFFFFF" },
                { "BrushEditorFG",   "#1B1B1B" },
                { "BrushRowHover",   "#E8F1FA" },
                { "BrushNroFilaBG",  "#EDEDED" },
                { "BrushNroFilaFG",  "#616161" },
                { "BrushDanger",     "#C42B1C" },
                { "BrushSuccess",    "#0F7B0F" },
                { "BrushWarning",    "#9D5D00" },
                { "BrushAccentFG",   "#FFFFFF" },
                { "BrushDangerBG",   "#FDE7E9" },
                { "BrushSuccessBG",  "#DFF6DD" },
                { "BrushWarningBG",  "#FFF4CE" },
                { "BrushInputBG",      "#FFFFFF" },
                { "BrushInputHoverBG", "#F7F7F7" },
                { "BrushInputFocusBG", "#FFFFFF" },
                { "BrushControlBorder",      "#DCDCDC" },
                { "BrushControlBorderHover", "#C8C8C8" },
            };

        private static readonly Dictionary<string, string> DefaultOscuro =
            new Dictionary<string, string>
            {
                { "BrushWindowBG",   "#202020" },
                { "BrushPanelBG",    "#2B2B2B" },
                { "BrushControlBG",  "#2D2D2D" },
                { "BrushAltRowBG",   "#343434" },
                { "BrushBorder",     "#454545" },
                { "BrushSplitter",   "#383838" },
                { "BrushFG",         "#F0F0F0" },
                { "BrushFGMuted",    "#A6A6A6" },
                { "BrushHover",      "#3A3A3A" },
                { "BrushSelected",   "#264F78" },
                { "BrushSelectedFG", "#FFFFFF" },
                { "BrushAccent",     "#4CC2FF" },
                { "BrushHeaderBG",   "#343434" },
                { "BrushHeaderFG",   "#F0F0F0" },
                { "BrushBtnBG",      "#333333" },
                { "BrushBtnBorder",  "#4A4A4A" },
                { "BrushTreeHover",  "#333333" },
                { "BrushTreeSel",    "#264F78" },
                { "BrushTabSelBG",   "#2B2B2B" },
                { "BrushTabSelBdr",  "#4CC2FF" },
                { "BrushTabSelFG",   "#4CC2FF" },
                { "BrushMenuBG",     "#2B2B2B" },
                { "BrushMenuHover",  "#3A3A3A" },
                { "BrushSeparator",  "#454545" },
                { "BrushEditor",     "#1E1E1E" },
                { "BrushEditorFG",   "#D4D4D4" },
                { "BrushRowHover",   "#2F3E4D" },
                { "BrushNroFilaBG",  "#333333" },
                { "BrushNroFilaFG",  "#A6A6A6" },
                { "BrushDanger",     "#FF99A4" },
                { "BrushSuccess",    "#6CCB5F" },
                { "BrushWarning",    "#FCE100" },
                { "BrushAccentFG",   "#00263D" },
                { "BrushDangerBG",   "#442726" },
                { "BrushSuccessBG",  "#1F3A1F" },
                { "BrushWarningBG",  "#433519" },
                { "BrushInputBG",      "#383838" },
                { "BrushInputHoverBG", "#404040" },
                { "BrushInputFocusBG", "#303030" },
                { "BrushControlBorder",      "#3E3E3E" },
                { "BrushControlBorderHover", "#505050" },
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

            chkTemaOscuro.IsChecked            = _configOriginal.TemaOscuro;
            chkIntellisense.IsChecked          = _configOriginal.IntellisenseActivo;
            chkCargarUltConsulta.IsChecked     = _configOriginal.CargarUltimaConsulta;
            chkEjecutarSelectDirecto.IsChecked = _configOriginal.EjecutarSelectDirecto;
            chkResultadosEditables.IsChecked   = _configOriginal.ResultadosEditables;
            chkMostrarNroFila.IsChecked        = _configOriginal.MostrarNumeroFila;
            txtMaxFilas.Text                   = _configOriginal.MaxFilasResultado.ToString();

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

                if (dlg.ShowDialog(this.OwnerWin32()) == System.Windows.Forms.DialogResult.OK)
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
            int maxFilas = 100000;
            if (int.TryParse(txtMaxFilas.Text.Trim(), out int parsedMax) && parsedMax >= 0)
                maxFilas = parsedMax;

            var nueva = new AppConfig
            {
                TemaOscuro              = chkTemaOscuro.IsChecked == true,
                IntellisenseActivo      = chkIntellisense.IsChecked == true,
                CargarUltimaConsulta    = chkCargarUltConsulta.IsChecked == true,
                EjecutarSelectDirecto   = chkEjecutarSelectDirecto.IsChecked == true,
                ResultadosEditables     = chkResultadosEditables.IsChecked == true,
                MostrarNumeroFila       = chkMostrarNroFila.IsChecked == true,
                MaxFilasResultado       = maxFilas,
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

            sb.AppendLine($"    <x:String x:Key=\"ThemeVersion\">{VersionTemas}</x:String>");

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
