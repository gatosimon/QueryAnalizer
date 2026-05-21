using System.Linq;
using System.Windows;

namespace QueryAnalyzer
{
    public enum FormatoExportacion { Excel, Csv }

    public partial class ExportarDialog : Window
    {
        /// <summary>Formato elegido por el usuario.</summary>
        public FormatoExportacion FormatoSeleccionado { get; private set; }

        /// <summary>Si se debe incluir la fila de encabezados.</summary>
        public bool IncluirEncabezados { get; private set; }

        public ExportarDialog()
        {
            InitializeComponent();
            AplicarTemaActual();
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

        private void btnGenerar_Click(object sender, RoutedEventArgs e)
        {
            FormatoSeleccionado = rbCsv.IsChecked == true
                ? FormatoExportacion.Csv
                : FormatoExportacion.Excel;

            IncluirEncabezados = chkEncabezados.IsChecked == true;

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
