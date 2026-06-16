using System.Linq;
using System.Windows;

namespace QueryAnalyzer
{
    public enum FormatoExportacion { Excel, Csv, SqlInsert }

    public partial class ExportarDialog : Window
    {
        public FormatoExportacion FormatoSeleccionado { get; private set; }
        public bool               IncluirEncabezados  { get; private set; }
        public string             NombreTabla         { get; private set; }
        public bool               IncluirDelete       { get; private set; }

        public ExportarDialog(string nombreTablaDefecto = null)
        {
            InitializeComponent();
            AplicarTemaActual();
            if (!string.IsNullOrWhiteSpace(nombreTablaDefecto))
                txtNombreTabla.Text = nombreTablaDefecto;
        }

        private void AplicarTemaActual()
        {
            var mw = Application.Current.MainWindow;
            if (mw == null) return;
            var tema = mw.Resources.MergedDictionaries.FirstOrDefault();
            if (tema == null) return;
            var wd = Resources.MergedDictionaries;
            if (wd.Count > 0) wd[0] = tema; else wd.Add(tema);
        }

        private void rbSql_Checked(object sender, RoutedEventArgs e)
        {
            if (pnlOpcionesSql == null) return;
            pnlOpcionesSql.Visibility      = Visibility.Visible;
            lblOpcionesArchivo.Visibility  = Visibility.Collapsed;
            chkEncabezados.Visibility      = Visibility.Collapsed;
            txtNombreTabla.Focus();
        }

        private void rbArchivo_Checked(object sender, RoutedEventArgs e)
        {
            if (pnlOpcionesSql == null) return;
            pnlOpcionesSql.Visibility      = Visibility.Collapsed;
            lblOpcionesArchivo.Visibility  = Visibility.Visible;
            chkEncabezados.Visibility      = Visibility.Visible;
        }

        private void btnGenerar_Click(object sender, RoutedEventArgs e)
        {
            if (rbSql.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(txtNombreTabla.Text))
                {
                    MessageBox.Show("Ingresá el nombre de la tabla destino.", "Falta el nombre",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    txtNombreTabla.Focus();
                    return;
                }
                FormatoSeleccionado = FormatoExportacion.SqlInsert;
                NombreTabla         = txtNombreTabla.Text.Trim();
                IncluirDelete       = chkDeletePrevio.IsChecked == true;
            }
            else
            {
                FormatoSeleccionado = rbCsv.IsChecked == true
                    ? FormatoExportacion.Csv
                    : FormatoExportacion.Excel;
                IncluirEncabezados = chkEncabezados.IsChecked == true;
            }

            DialogResult = true;
            Close();
        }

        private void btnCancelar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
