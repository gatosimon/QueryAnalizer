using System;
using System.Linq;
using System.Windows;
using QueryAnalyzer.Models;

namespace QueryAnalyzer
{
    public partial class GuardarConsultaDialog : Window
    {
        public ConsultaGuardada Resultado { get; private set; }

        private readonly string _consultaCompleta;

        // ── Modo NUEVA consulta ──────────────────────────────────────────────
        public GuardarConsultaDialog(string consultaCompleta, string conexionNombre = "")
        {
            InitializeComponent();
            AplicarTemaActual();

            _consultaCompleta = consultaCompleta;

            // Pre-completar campos
            txtUsuario.Text = Environment.UserName;

            // Vista previa: primeras 300 chars
            const int PREVIEW_LEN = 300;
            txtPreview.Text = consultaCompleta.Length <= PREVIEW_LEN
                ? consultaCompleta
                : consultaCompleta.Substring(0, PREVIEW_LEN) + "\n…";

            Loaded += (s, e) => txtTitulo.Focus();
        }

        // ── Modo EDITAR consulta existente ───────────────────────────────────
        /// <summary>
        /// Abre el diálogo pre-completado con los datos de <paramref name="consultaExistente"/>
        /// para permitir editar título, descripción y usuario sin modificar la consulta SQL.
        /// </summary>
        public GuardarConsultaDialog(ConsultaGuardada consultaExistente)
        {
            InitializeComponent();
            AplicarTemaActual();

            Title = "Editar consulta guardada";
            _consultaCompleta = consultaExistente.Consulta ?? string.Empty;
            _fechaOriginal    = consultaExistente.Fecha;
            _esEdicion        = true;

            txtTitulo.Text      = consultaExistente.Titulo      ?? string.Empty;
            txtDescripcion.Text = consultaExistente.Descripcion ?? string.Empty;
            txtUsuario.Text     = consultaExistente.Usuario     ?? Environment.UserName;

            const int PREVIEW_LEN = 300;
            txtPreview.Text = _consultaCompleta.Length <= PREVIEW_LEN
                ? _consultaCompleta
                : _consultaCompleta.Substring(0, PREVIEW_LEN) + "\n…";

            Loaded += (s, e) => txtTitulo.Focus();
        }

        private readonly bool     _esEdicion    = false;
        private readonly DateTime _fechaOriginal = DateTime.MinValue;

        private void AplicarTemaActual()
        {
            var mw = Application.Current.MainWindow;
            if (mw == null) return;
            var tema = mw.Resources.MergedDictionaries.FirstOrDefault();
            if (tema == null) return;
            var wd = Resources.MergedDictionaries;
            if (wd.Count > 0) wd[0] = tema; else wd.Add(tema);
        }

        private void btnGuardar_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtTitulo.Text))
            {
                MessageBox.Show("El título es obligatorio.", "Guardar consulta",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                txtTitulo.Focus();
                return;
            }

            Resultado = new ConsultaGuardada
            {
                Titulo         = txtTitulo.Text.Trim(),
                Descripcion    = txtDescripcion.Text.Trim(),
                Consulta       = _consultaCompleta,
                Usuario        = txtUsuario.Text.Trim(),
                // En edición se conserva la fecha original; en nueva, se pone ahora
                Fecha          = _esEdicion ? _fechaOriginal : DateTime.Now,
                ConexionNombre = MainWindow.conexionActual?.Nombre ?? ""
            };

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
