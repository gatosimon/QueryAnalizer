using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace QueryAnalyzer
{
    public partial class NombreConjuntoDialog : Window
    {
        public string NombreIngresado { get; private set; }

        public NombreConjuntoDialog()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Aplicar tema activo
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow != null)
            {
                var tema = mainWindow.Resources.MergedDictionaries.FirstOrDefault();
                if (tema != null)
                {
                    var wd = Resources.MergedDictionaries;
                    if (wd.Count > 0) wd[0] = tema;
                    else wd.Add(tema);
                }
            }
            txtNombre.Focus();
        }

        private void BtnAceptar_Click(object sender, RoutedEventArgs e)
        {
            NombreIngresado = txtNombre.Text.Trim();
            if (string.IsNullOrEmpty(NombreIngresado))
            {
                MessageBox.Show("Ingrese un nombre para el conjunto.", "Campo requerido",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
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

        private void TxtNombre_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnAceptar_Click(sender, e);
        }
    }
}
