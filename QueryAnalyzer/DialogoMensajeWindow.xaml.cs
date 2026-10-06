using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace QueryAnalyzer
{
    /// <summary>
    /// Reemplaza a System.Windows.MessageBox dentro de este proyecto (por resolucion de nombres, al estar en el mismo
    /// namespace): todos los avisos y confirmaciones salen con el aspecto y el tema de la aplicacion
    /// sin tener que tocar cada llamada.
    /// </summary>
    internal static class MessageBox
    {
        public static MessageBoxResult Show(string texto)
        { return Mostrar(null, texto, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None); }

        public static MessageBoxResult Show(string texto, string titulo)
        { return Mostrar(null, texto, titulo, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None); }

        public static MessageBoxResult Show(string texto, string titulo, MessageBoxButton botones)
        { return Mostrar(null, texto, titulo, botones, MessageBoxImage.None, MessageBoxResult.None); }

        public static MessageBoxResult Show(string texto, string titulo, MessageBoxButton botones, MessageBoxImage icono)
        { return Mostrar(null, texto, titulo, botones, icono, MessageBoxResult.None); }

        public static MessageBoxResult Show(string texto, string titulo, MessageBoxButton botones, MessageBoxImage icono, MessageBoxResult porDefecto)
        { return Mostrar(null, texto, titulo, botones, icono, porDefecto); }

        public static MessageBoxResult Show(Window propietario, string texto)
        { return Mostrar(propietario, texto, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None); }

        public static MessageBoxResult Show(Window propietario, string texto, string titulo)
        { return Mostrar(propietario, texto, titulo, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None); }

        public static MessageBoxResult Show(Window propietario, string texto, string titulo, MessageBoxButton botones)
        { return Mostrar(propietario, texto, titulo, botones, MessageBoxImage.None, MessageBoxResult.None); }

        public static MessageBoxResult Show(Window propietario, string texto, string titulo, MessageBoxButton botones, MessageBoxImage icono)
        { return Mostrar(propietario, texto, titulo, botones, icono, MessageBoxResult.None); }

        public static MessageBoxResult Show(Window propietario, string texto, string titulo, MessageBoxButton botones, MessageBoxImage icono, MessageBoxResult porDefecto)
        { return Mostrar(propietario, texto, titulo, botones, icono, porDefecto); }

        private static MessageBoxResult Mostrar(Window propietario, string texto, string titulo,
            MessageBoxButton botones, MessageBoxImage icono, MessageBoxResult porDefecto)
        {
            var app = Application.Current;
            // Sin aplicacion WPF (no deberia pasar): cae al cuadro del sistema para no perder el aviso
            if (app == null) return System.Windows.MessageBox.Show(texto, titulo, botones, icono, porDefecto);

            // Los avisos pueden salir desde hilos de fondo: el dialogo se arma siempre en el hilo de la interfaz
            if (!app.Dispatcher.CheckAccess())
                return (MessageBoxResult)app.Dispatcher.Invoke(
                    new Func<MessageBoxResult>(() => Mostrar(propietario, texto, titulo, botones, icono, porDefecto)));

            var owner = ElegirPropietario(propietario);
            var dlg = new DialogoMensajeWindow(texto, titulo, botones, icono, porDefecto);
            if (owner != null) dlg.Owner = owner;
            else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.ShowDialog();
            return dlg.Resultado;
        }

        // Un Owner debe haberse mostrado antes: WPF lanza excepcion si no
        private static Window ElegirPropietario(Window pedido)
        {
            if (EsValido(pedido)) return pedido;
            var ventanas = Application.Current.Windows.OfType<Window>().Where(EsValido).ToList();
            return ventanas.FirstOrDefault(w => w.IsActive)
                   ?? (EsValido(Application.Current.MainWindow) ? Application.Current.MainWindow : ventanas.LastOrDefault());
        }

        private static bool EsValido(Window w)
        {
            return w != null && w.IsVisible && new WindowInteropHelper(w).Handle != IntPtr.Zero;
        }
    }

    public partial class DialogoMensajeWindow : Window
    {
        /// <summary>Boton elegido; si se cierra con la X o Esc es el equivalente a "cancelar".</summary>
        public MessageBoxResult Resultado { get; private set; }

        private readonly MessageBoxResult _alCancelar;
        private readonly string _texto;

        internal DialogoMensajeWindow(string texto, string titulo, MessageBoxButton botones, MessageBoxImage icono, MessageBoxResult porDefecto)
        {
            InitializeComponent();
            AplicarTemaActual();

            _texto = texto ?? string.Empty;
            Title = string.IsNullOrWhiteSpace(titulo) ? "QueryAnalyzer" : titulo;
            txtMensaje.Text = _texto;

            ConfigurarIcono(icono);
            MessageBoxResult primero;
            ConfigurarBotones(botones, porDefecto, out primero, out _alCancelar);
            Resultado = _alCancelar;

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { Resultado = _alCancelar; DialogResult = false; e.Handled = true; }
                else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
                {
                    try { Clipboard.SetText(Title + Environment.NewLine + Environment.NewLine + _texto); } catch { }
                    e.Handled = true;
                }
            };
        }

        private void ConfigurarIcono(MessageBoxImage icono)
        {
            string forma, color;
            switch (icono)
            {
                case MessageBoxImage.Error: forma = "IconError"; color = "BrushDanger"; break;           // Error / Stop / Hand
                case MessageBoxImage.Warning: forma = "IconWarning"; color = "BrushWarning"; break;      // Warning / Exclamation
                case MessageBoxImage.Question: forma = "IconHelp"; color = "BrushAccent"; break;
                case MessageBoxImage.Information: forma = "IconInfo"; color = "BrushAccent"; break;      // Information / Asterisk
                default: vbIcono.Visibility = Visibility.Collapsed; return;
            }
            icono_SetShape(forma);
            vbIcono.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, color);
        }

        private void icono_SetShape(string clave)
        {
            icono.Data = (Geometry)FindResource(clave);
        }

        private void ConfigurarBotones(MessageBoxButton botones, MessageBoxResult porDefecto,
            out MessageBoxResult primero, out MessageBoxResult alCancelar)
        {
            MessageBoxResult[] orden;
            switch (botones)
            {
                case MessageBoxButton.OKCancel: orden = new[] { MessageBoxResult.OK, MessageBoxResult.Cancel }; alCancelar = MessageBoxResult.Cancel; break;
                case MessageBoxButton.YesNo: orden = new[] { MessageBoxResult.Yes, MessageBoxResult.No }; alCancelar = MessageBoxResult.No; break;
                case MessageBoxButton.YesNoCancel: orden = new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel }; alCancelar = MessageBoxResult.Cancel; break;
                default: orden = new[] { MessageBoxResult.OK }; alCancelar = MessageBoxResult.OK; break;
            }
            primero = orden[0];
            var elegido = orden.Contains(porDefecto) ? porDefecto : primero;

            System.Windows.Controls.Button botonPorDefecto = null;
            foreach (var r in orden)
            {
                var resultado = r;
                bool principal = resultado == elegido;
                var b = new System.Windows.Controls.Button
                {
                    Content = Texto(resultado),
                    MinWidth = 92,
                    Margin = new Thickness(panelBotones.Children.Count == 0 ? 0 : 8, 0, 0, 0),
                    Style = (Style)FindResource(principal ? "AccentCompositeButton" : "CompositeButton"),
                    IsDefault = principal
                };
                b.Click += (s, e) => { Resultado = resultado; DialogResult = resultado != MessageBoxResult.Cancel && resultado != MessageBoxResult.No; };
                panelBotones.Children.Add(b);
                if (principal) botonPorDefecto = b;
            }
            Loaded += (s, e) => { if (botonPorDefecto != null) botonPorDefecto.Focus(); };
        }

        private static string Texto(MessageBoxResult r)
        {
            switch (r)
            {
                case MessageBoxResult.Yes: return "Sí";
                case MessageBoxResult.No: return "No";
                case MessageBoxResult.Cancel: return "Cancelar";
                default: return "Aceptar";
            }
        }

        private void AplicarTemaActual()
        {
            var mw = Application.Current.MainWindow;
            if (mw == null) return;
            var tema = mw.Resources.MergedDictionaries.FirstOrDefault();
            if (tema == null) return;
            var wd = Resources.MergedDictionaries;
            if (wd.Count > 0) wd[0] = tema;
            else wd.Add(tema);
        }
    }
}
