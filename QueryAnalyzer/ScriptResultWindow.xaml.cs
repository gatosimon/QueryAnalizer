using System.Linq;
using System.Windows;

namespace QueryAnalyzer
{
    public partial class ScriptResultWindow : Window
    {
        public ScriptResultWindow(string script, TipoMotor motor)
        {
            InitializeComponent();
            // Aplicar el tema activo de MainWindow antes de mostrar contenido
            AplicarTemaActual();
            Title          = string.Format("Script generado  [{0}]", motor);
            txtScript.Text = script;
        }

        /// <summary>
        /// Copia el ResourceDictionary de tema activo desde MainWindow a esta ventana.
        /// </summary>
        private void AplicarTemaActual()
        {
            var mainWindow = System.Windows.Application.Current.MainWindow;
            if (mainWindow == null) return;
            var tema = mainWindow.Resources.MergedDictionaries.FirstOrDefault();
            if (tema == null) return;
            var wd = this.Resources.MergedDictionaries;
            if (wd.Count > 0) wd[0] = tema;
            else wd.Add(tema);
        }

        private void Copiar_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(txtScript.Text))
                Clipboard.SetText(txtScript.Text);

            MessageBox.Show("Script copiado al portapapeles.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Cerrar_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
