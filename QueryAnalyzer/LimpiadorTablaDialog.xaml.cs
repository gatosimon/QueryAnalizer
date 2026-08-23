using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace QueryAnalyzer
{
    public partial class LimpiadorTablaDialog : Window
    {
        private readonly TablaConfigLimpiador _cfg;
        private readonly List<string> _columnas;

        private static readonly string[] OperadoresTodos =
            { "IS NOT EMPTY", "IS EMPTY", "IS NOT NULL", "IS NULL", "=", "<>", ">", ">=", "<", "<=" };

        public LimpiadorTablaDialog(TablaConfigLimpiador cfg, LimpiadorBDService svc)
        {
            InitializeComponent();
            AplicarTemaActual();
            _cfg = cfg;
            lblTitulo.Text = cfg.NombreCompleto;

            _columnas = svc.GetColumnas(cfg.Schema, cfg.Nombre);
            cbPK.ItemsSource = _columnas;
            txtPK.Text = cfg.ResumenPK;
            chkReordenar.IsChecked = cfg.ReordenarIds;
            chkIncluir.IsChecked = cfg.Incluir;

            // Poblar filas de condiciones existentes
            if (cfg.CondicionesBaja != null)
                foreach (var c in cfg.CondicionesBaja)
                    AgregarFilaCondicion(c);

            // Si no hay ninguna, agregar una vacía para guiar al usuario
            if (spCondiciones.Children.Count == 0)
                AgregarFilaCondicion(null);
        }

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

        // ── Constructor de filas de condición ─────────────────────────────

        private void AgregarFilaCondicion(CondicionBaja init)
        {
            var fila = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };

            // Campo (ComboBox con columnas de la tabla)
            var cbCampo = new ComboBox { Width = 108, Margin = new Thickness(0, 0, 2, 0), IsEditable = true };
            cbCampo.SetResourceReference(ComboBox.BackgroundProperty, "BrushControlBG");
            cbCampo.SetResourceReference(ComboBox.ForegroundProperty, "BrushFG");
            cbCampo.SetResourceReference(ComboBox.BorderBrushProperty, "BrushBorder");
            foreach (var col in _columnas) cbCampo.Items.Add(col);
            cbCampo.Text = init?.Campo ?? "";
            fila.Children.Add(cbCampo);

            // Operador
            var cbOp = new ComboBox { Width = 108, Margin = new Thickness(0, 0, 2, 0) };
            cbOp.SetResourceReference(ComboBox.BackgroundProperty, "BrushControlBG");
            cbOp.SetResourceReference(ComboBox.ForegroundProperty, "BrushFG");
            cbOp.SetResourceReference(ComboBox.BorderBrushProperty, "BrushBorder");
            foreach (var op in OperadoresTodos) cbOp.Items.Add(op);
            cbOp.SelectedItem = init?.Operador ?? "IS NOT EMPTY";
            fila.Children.Add(cbOp);

            // Valor
            var txtVal = new TextBox { Width = 63, Margin = new Thickness(0, 0, 2, 0), Padding = new Thickness(3, 1, 3, 1) };
            txtVal.SetResourceReference(TextBox.BackgroundProperty, "BrushControlBG");
            txtVal.SetResourceReference(TextBox.ForegroundProperty, "BrushFG");
            txtVal.SetResourceReference(TextBox.BorderBrushProperty, "BrushBorder");
            txtVal.Text = init?.Valor ?? "";
            fila.Children.Add(txtVal);

            // ValorSet
            var txtSet = new TextBox { Width = 68, Margin = new Thickness(0, 0, 2, 0), Padding = new Thickness(3, 1, 3, 1) };
            txtSet.SetResourceReference(TextBox.BackgroundProperty, "BrushControlBG");
            txtSet.SetResourceReference(TextBox.ForegroundProperty, "BrushFG");
            txtSet.SetResourceReference(TextBox.BorderBrushProperty, "BrushBorder");
            ToolTipService.SetToolTip(txtSet, "Valor para SET en baja en cascada (ej: 'SISTEMA', GETDATE())");
            txtSet.Text = init?.ValorSet ?? "";
            fila.Children.Add(txtSet);

            // Combinador
            var cbComb = new ComboBox { Width = 52, Margin = new Thickness(0, 0, 2, 0) };
            cbComb.SetResourceReference(ComboBox.BackgroundProperty, "BrushControlBG");
            cbComb.SetResourceReference(ComboBox.ForegroundProperty, "BrushFG");
            cbComb.SetResourceReference(ComboBox.BorderBrushProperty, "BrushBorder");
            cbComb.Items.Add("AND");
            cbComb.Items.Add("OR");
            cbComb.SelectedItem = init?.Combinador ?? "AND";
            fila.Children.Add(cbComb);

            // Botón quitar
            var btnQ = new Button { Content = "✕", Width = 22, Height = 22, Padding = new Thickness(0), Margin = new Thickness(0) };
            btnQ.SetResourceReference(Button.BackgroundProperty, "BrushBtnBG");
            btnQ.SetResourceReference(Button.ForegroundProperty, "BrushFG");
            btnQ.SetResourceReference(Button.BorderBrushProperty, "BrushBtnBorder");
            btnQ.Click += (s, _) => { spCondiciones.Children.Remove(fila); ActualizarCombinadores(); };
            fila.Children.Add(btnQ);

            // Ocultar valor cuando el operador no lo requiere
            cbOp.SelectionChanged += (s, _) =>
            {
                bool sinVal = CondicionBaja.OperadoresSinValor.Contains(cbOp.SelectedItem as string);
                txtVal.Visibility = sinVal ? Visibility.Collapsed : Visibility.Visible;
            };
            bool sinValInit = CondicionBaja.OperadoresSinValor.Contains(cbOp.SelectedItem as string);
            txtVal.Visibility = sinValInit ? Visibility.Collapsed : Visibility.Visible;

            spCondiciones.Children.Add(fila);
            ActualizarCombinadores();
        }

        private void ActualizarCombinadores()
        {
            for (int i = 0; i < spCondiciones.Children.Count; i++)
            {
                if (spCondiciones.Children[i] is StackPanel fila && fila.Children.Count >= 5)
                {
                    var comb = fila.Children[4] as ComboBox;
                    if (comb != null)
                        comb.Visibility = i < spCondiciones.Children.Count - 1
                            ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        private List<CondicionBaja> GetCondiciones()
        {
            var result = new List<CondicionBaja>();
            for (int i = 0; i < spCondiciones.Children.Count; i++)
            {
                if (!(spCondiciones.Children[i] is StackPanel fila) || fila.Children.Count < 5) continue;
                string campo = (fila.Children[0] as ComboBox)?.Text.Trim() ?? "";
                if (string.IsNullOrEmpty(campo)) continue;
                string op   = (fila.Children[1] as ComboBox)?.SelectedItem as string ?? "=";
                string val  = (fila.Children[2] as TextBox)?.Text.Trim() ?? "";
                string vset = (fila.Children[3] as TextBox)?.Text.Trim() ?? "";
                string comb = (fila.Children[4] as ComboBox)?.SelectedItem as string ?? "AND";
                result.Add(new CondicionBaja
                {
                    Campo      = campo,
                    Operador   = op,
                    Valor      = string.IsNullOrEmpty(val) ? null : val,
                    ValorSet   = string.IsNullOrEmpty(vset) ? null : vset,
                    Combinador = comb
                });
            }
            return result;
        }

        private void btnAgregarCond_Click(object sender, RoutedEventArgs e)
            => AgregarFilaCondicion(null);

        // ── Aceptar / Cancelar ────────────────────────────────────────────

        /// <summary>Columnas separadas por coma → lista limpia, sin vacíos.</summary>
        private static List<string> ParsearCamposPK(string texto)
            => (texto ?? "").Split(',')
                            .Select(s => s.Trim())
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToList();

        private void btnAgregarPK_Click(object sender, RoutedEventArgs e)
        {
            string col = cbPK.SelectedItem as string;
            if (string.IsNullOrEmpty(col)) return;

            var actuales = ParsearCamposPK(txtPK.Text);
            if (actuales.Any(c => string.Equals(c, col, System.StringComparison.OrdinalIgnoreCase))) return;

            actuales.Add(col);
            txtPK.Text = string.Join(", ", actuales);
        }

        private void btnAceptar_Click(object sender, RoutedEventArgs e)
        {
            _cfg.CondicionesBaja = GetCondiciones();
            _cfg.CamposPK   = ParsearCamposPK(txtPK.Text);
            _cfg.ReordenarIds = chkReordenar.IsChecked == true;
            _cfg.Incluir    = chkIncluir.IsChecked == true;
            DialogResult = true;
        }

        private void btnCancelar_Click(object sender, RoutedEventArgs e)
            => DialogResult = false;
    }
}
