using System.Linq;
using System.Windows;

namespace QueryAnalyzer
{
    public partial class AyudaWindow : Window
    {
        public AyudaWindow()
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
    }
}
