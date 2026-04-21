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

        private string _descripcionTablaOriginal = null;

        // ── Resultado expuesto a MainWindow ──────────────────────────────────────
        /// <summary>
        /// Nombre al que se quiere renombrar la tabla.
        /// Null o vacío = sin renombre.
        /// </summary>
        public string NuevoNombreTabla { get; private set; }

        /// <summary>
        /// True = el usuario eligió revisar el script antes de ejecutar (default).
        /// False = aplicar directamente.
        /// </summary>
        public bool SoloGenerarScript { get; private set; } = true;

        public TableDesignerWindow(Conexion conexion, string tabla)
        {
            InitializeComponent();
            _conexion     = conexion;
            _tabla        = tabla;
            Title         = $"Design - {tabla}  [{conexion.Nombre}]";
            txtMotor.Text = conexion.Motor.ToString();

            // Exponer los tipos de dato del motor al DataGrid para que el ComboBox los use.
            // Se guarda en Tag del DataGrid para que el binding dentro del DataTemplate
            // lo resuelva sin necesidad de DataContext en la ventana.
            dgColumnas.Tag = TableDesignerService.GetTiposDato(conexion.Motor);
        }

        // ── Carga inicial ─────────────────────────────────────────────────────────

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Aplicar el tema activo de MainWindow antes de mostrar contenido
            AplicarTemaActual();
            await CargarColumnas();
        }

        /// <summary>
        /// Copia el ResourceDictionary de tema activo desde MainWindow a esta ventana,
        /// garantizando que los DynamicResource resuelvan con los colores correctos
        /// independientemente de si el usuario está en modo claro u oscuro.
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

        private async System.Threading.Tasks.Task CargarColumnas()
        {
            txtEstado.Text         = "Cargando columnas...";
            btnScript.IsEnabled    = false;
            btnAgregar.IsEnabled   = false;
            btnRecargar.IsEnabled  = false;
            btnRenombrar.IsEnabled = false;

            try
            {
                var lista  = await TableDesignerService.GetColumnasDesignAsync(_conexion, _tabla);
                _columnas  = new ObservableCollection<ColumnDesignInfo>(lista);
                dgColumnas.ItemsSource = _columnas;
                txtEstado.Text = $"{_columnas.Count} columna(s) cargada(s).";

                string desc = await TableDesignerService.GetDescripcionTablaAsync(_conexion, _tabla);
                _descripcionTablaOriginal = desc;
                txtDescripcionTabla.Text  = desc ?? string.Empty;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error cargando columnas:\n" + ex.Message, Title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                txtEstado.Text = "Error al cargar.";
            }
            finally
            {
                btnScript.IsEnabled    = true;
                btnAgregar.IsEnabled   = true;
                btnRecargar.IsEnabled  = true;
                btnRenombrar.IsEnabled = true;
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

            _columnas.Add(nueva);
            dgColumnas.SelectedItem  = nueva;
            dgColumnas.ScrollIntoView(nueva);

            dgColumnas.CurrentCell = new DataGridCellInfo(nueva, dgColumnas.Columns[0]);
            dgColumnas.BeginEdit();

            txtEstado.Text = "Nueva columna agregada. Completá el nombre y el tipo de dato.";
        }

        // ── Toolbar: Renombrar tabla ──────────────────────────────────────────────

        private void RenombrarTabla_Click(object sender, RoutedEventArgs e)
        {
            string nuevoNombre = txtNuevoNombre.Text.Trim();

            if (string.IsNullOrEmpty(nuevoNombre))
            {
                MessageBox.Show("Escribí el nuevo nombre de la tabla antes de continuar.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                txtNuevoNombre.Focus();
                return;
            }

            if (nuevoNombre.Equals(_tabla, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("El nuevo nombre es igual al nombre actual.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Confirmar para evitar clicks accidentales
            var r = MessageBox.Show(
                $"¿Renombrar la tabla '{_tabla}' a '{nuevoNombre}'?\n\nSe agregará la sentencia RENAME al script.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (r != MessageBoxResult.Yes) return;

            NuevoNombreTabla = nuevoNombre;
            txtEstado.Text   = $"Tabla marcada para renombrar → '{nuevoNombre}'.";
            btnRenombrar.IsEnabled = false;   // una sola vez por sesión
            txtNuevoNombre.IsEnabled = false;
        }

        // ── Toolbar: Generar script ───────────────────────────────────────────────

        private void GenerarScript_Click(object sender, RoutedEventArgs e)
        {
            if (_columnas == null || _columnas.Count == 0) return;

            // Confirmar la edición de cualquier celda que esté activa en el DataGrid
            // (LostFocus puede no haber propagado el binding si el foco va directo al botón)
            dgColumnas.CommitEdit(DataGridEditingUnit.Row, true);

            if (txtNuevoNombre.Text.Trim().Length > 0)
            {
                string nn = txtNuevoNombre.Text.Trim();
                if (!string.IsNullOrEmpty(nn) && !nn.Equals(_tabla, StringComparison.OrdinalIgnoreCase))
                    NuevoNombreTabla = nn;
            }

            string descActual = txtDescripcionTabla.Text.Trim();

            MainWindow.scriptDiseño = TableDesignerService.GenerarScript(
                _conexion.Motor,
                _tabla,
                _columnas.ToList(),
                NuevoNombreTabla,
                string.IsNullOrEmpty(descActual) ? null : descActual,
                _descripcionTablaOriginal);

            SoloGenerarScript = rbRevisarScript.IsChecked == true;
            this.Close();
        }

        // ── Toolbar: Recargar ─────────────────────────────────────────────────────

        private async void Recargar_Click(object sender, RoutedEventArgs e)
        {
            var r = MessageBox.Show(
                "¿Recargar columnas desde la base de datos?\nSe perderán los cambios no guardados.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (r == MessageBoxResult.Yes)
            {
                NuevoNombreTabla = null;
                txtNuevoNombre.Text      = string.Empty;
                txtNuevoNombre.IsEnabled = true;
                btnRenombrar.IsEnabled   = true;
                await CargarColumnas();
            }
        }

        // ── Context menu: Marcar / desmarcar para eliminar ────────────────────────

        private void EliminarColumna_Click(object sender, RoutedEventArgs e)
        {
            if (!(dgColumnas.SelectedItem is ColumnDesignInfo col)) return;

            if (col.EsNueva)
            {
                _columnas.Remove(col);
                txtEstado.Text = "Columna nueva eliminada.";
            }
            else
            {
                col.MarcarParaEliminar = true;
                txtEstado.Text = $"Columna '{col.NombreOriginal}' marcada para eliminar.";
            }
        }

        private void DesmarcarEliminar_Click(object sender, RoutedEventArgs e)
        {
            if (!(dgColumnas.SelectedItem is ColumnDesignInfo col)) return;

            col.MarcarParaEliminar = false;
            txtEstado.Text = $"Columna '{col.NombreOriginal}' desmarcada.";
        }
    }
}
