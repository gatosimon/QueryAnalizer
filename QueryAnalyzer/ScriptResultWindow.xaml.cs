using System.Windows;

namespace QueryAnalyzer
{
    public partial class ScriptResultWindow : Window
    {
        public ScriptResultWindow(string script, TipoMotor motor)
        {
            InitializeComponent();
            Title        = string.Format("Script generado  [{0}]", motor);
            txtScript.Text = script;
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
