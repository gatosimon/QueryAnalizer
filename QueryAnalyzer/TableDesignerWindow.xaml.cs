using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace QueryAnalyzer
{
    public partial class TableDesignerWindow : Window
    {
        private readonly Conexion _conexion;
        private readonly string   _tabla;

        private ObservableCollection<ColumnDesignInfo> _columnas;

        public TableDesignerWindow(Conexion conexion, string tabla)
        {
            InitializeComponent();
            _conexion      = conexion;
            _tabla         = tabla;
            Title          = string.Format("Design - {0}  [{1}]", tabla, conexion.Nombre);
            txtMotor.Text  = conexion.Motor.ToString();
        }

        // ── Carga inicial ─────────────────────────────────────────────────────────

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await CargarColumnas();
        }

        private async System.Threading.Tasks.Task CargarColumnas()
        {
            txtEstado.Text         = "Cargando columnas...";
            btnScript.IsEnabled    = false;
            btnAgregar.IsEnabled   = false;
            btnRecargar.IsEnabled  = false;

            try
            {
                var lista  = await TableDesignerService.GetColumnasDesignAsync(_conexion, _tabla);
                _columnas  = new ObservableCollection<ColumnDesignInfo>(lista);
                dgColumnas.ItemsSource = _columnas;
                txtEstado.Text = string.Format("{0} columna(s) cargada(s).", _columnas.Count);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error cargando columnas:\n" + ex.Message, Title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                txtEstado.Text = "Error al cargar.";
            }
            finally
            {
                btnScript.IsEnabled   = true;
                btnAgregar.IsEnabled  = true;
                btnRecargar.IsEnabled = true;
            }
        }

        // ── Toolbar: Agregar columna ──────────────────────────────────────────────

        private void AgregarColumna_Click(object sender, RoutedEventArgs e)
        {
            var nueva = new ColumnDesignInfo
            {
                Nombre       = "NuevaColumna",
                TipoDato     = "varchar",
                Longitud     = 50,
                EsNulable    = true,
                EsNueva      = true
            };
            // No llamamos MarcarComoOriginal() porque es nueva: Modificado no se evalúa para nuevas

            _columnas.Add(nueva);
            dgColumnas.SelectedItem  = nueva;
            dgColumnas.ScrollIntoView(nueva);

            // Forzar edición inmediata del nombre
            dgColumnas.CurrentCell = new DataGridCellInfo(nueva, dgColumnas.Columns[0]);
            dgColumnas.BeginEdit();

            txtEstado.Text = "Nueva columna agregada. Completá el nombre y el tipo de dato.";
        }

        // ── Toolbar: Generar script ───────────────────────────────────────────────

        //private void GenerarScript_Click(object sender, RoutedEventArgs e)
        //{
        //    if (_columnas == null || _columnas.Count == 0) return;

        //    string script = TableDesignerService.GenerarScript(
        //        _conexion.Motor, _tabla, _columnas.ToList());

        //    var ventana = new ScriptResultWindow(script, _conexion.Motor)
        //    {
        //        Owner = this
        //    };
        //    ventana.ShowDialog();
        //}

        private void GenerarScript_Click(object sender, RoutedEventArgs e)
        {
            if (_columnas == null || _columnas.Count == 0) return;

            MainWindow.scriptDiseño = TableDesignerService.GenerarScript(
                _conexion.Motor, _tabla, _columnas.ToList());

            this.Close();
        }

        // ── Toolbar: Recargar ─────────────────────────────────────────────────────

        private async void Recargar_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show(
                "¿Recargar columnas desde la base de datos?\nSe perderán los cambios no guardados.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (r == MessageBoxResult.Yes)
                await CargarColumnas();
        }

        // ── Context menu: Marcar / desmarcar para eliminar ────────────────────────

        private void EliminarColumna_Click(object sender, RoutedEventArgs e)
        {
            if (!(dgColumnas.SelectedItem is ColumnDesignInfo col)) return;

            if (col.EsNueva)
            {
                // Columna todavía no existe en la BD → quitar directo de la lista
                _columnas.Remove(col);
                txtEstado.Text = "Columna nueva eliminada.";
            }
            else
            {
                col.MarcarParaEliminar = true;
                txtEstado.Text = string.Format("Columna '{0}' marcada para eliminar.", col.NombreOriginal);
            }
        }

        private void DesmarcarEliminar_Click(object sender, RoutedEventArgs e)
        {
            if (!(dgColumnas.SelectedItem is ColumnDesignInfo col)) return;

            col.MarcarParaEliminar = false;
            txtEstado.Text = string.Format("Columna '{0}' desmarcada.", col.NombreOriginal);
        }
    }
}
