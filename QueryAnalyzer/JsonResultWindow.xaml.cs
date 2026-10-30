using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace QueryAnalyzer
{
    /// <summary>
    /// Visor del texto generado por la exportación de resultados (JSON por defecto; también
    /// se usa para Markdown). Muestra el contenido en un editor y deja al usuario decidir
    /// qué hacer con él: copiarlo al portapapeles o guardarlo en disco (uno o todos los
    /// resultados).
    /// </summary>
    public partial class JsonResultWindow : Window
    {
        // nombre de la pestaña -> texto generado (se actualiza con lo que el usuario edite)
        private readonly Dictionary<string, string> _documentos;
        private readonly Action<string> _log;
        private readonly ExcelService _svc = new ExcelService();
        private readonly string _formato;     // "JSON", "Markdown"
        private readonly string _extension;   // ".json", ".md"

        // Sin BOM: el JSON estándar no lo lleva.
        private static readonly System.Text.Encoding Utf8SinBom =
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private string _actual;      // pestaña mostrada en el editor
        private bool _cargando;      // evita que el cambio de selección pise el texto

        /// <param name="documentos">Texto generado por cada pestaña de resultados.</param>
        /// <param name="log">Callback opcional para escribir en el panel de mensajes.</param>
        /// <param name="formato">Nombre del formato, para títulos y mensajes.</param>
        /// <param name="extension">Extensión de los archivos que se guardan, con punto.</param>
        public JsonResultWindow(IDictionary<string, string> documentos, Action<string> log = null,
                                string formato = "JSON", string extension = ".json")
        {
            InitializeComponent();
            AplicarTemaActual();

            _formato   = formato;
            _extension = extension;

            _documentos = documentos != null
                ? documentos.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, string>();
            _log = log;

            bool varios = _documentos.Count > 1;
            pnlSelector.Visibility     = varios ? Visibility.Visible : Visibility.Collapsed;
            btnGuardarTodos.Visibility = varios ? Visibility.Visible : Visibility.Collapsed;

            _cargando = true;
            cmbResultados.ItemsSource = _documentos.Keys.ToList();
            _cargando = false;

            if (_documentos.Count > 0)
            {
                if (varios) cmbResultados.SelectedIndex = 0;   // dispara MostrarDocumento
                else        MostrarDocumento(_documentos.Keys.First());
            }
        }

        /// <summary>
        /// Copia el ResourceDictionary de tema activo desde MainWindow a esta ventana.
        /// </summary>
        private void AplicarTemaActual()
        {
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow == null) return;
            var tema = mainWindow.Resources.MergedDictionaries.FirstOrDefault();
            if (tema == null) return;
            var wd = this.Resources.MergedDictionaries;
            if (wd.Count > 0) wd[0] = tema;
            else wd.Add(tema);
        }

        // ────────────────────────────────────────────────────────────────────
        // Navegación entre resultados
        // ────────────────────────────────────────────────────────────────────

        private void cmbResultados_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_cargando) return;
            var nombre = cmbResultados.SelectedItem as string;
            if (nombre == null) return;
            MostrarDocumento(nombre);
        }

        private void MostrarDocumento(string nombre)
        {
            GuardarEdicionActual();

            _actual = nombre;
            _cargando = true;
            txtJson.Text = _documentos[nombre];
            _cargando = false;
            txtJson.CaretIndex = 0;

            Title = _documentos.Count > 1
                ? string.Format("{0} generado  [{1}]", _formato, nombre)
                : _formato + " generado";

            ActualizarEstado();
        }

        /// <summary>Persiste en memoria lo que el usuario haya editado antes de cambiar de resultado.</summary>
        private void GuardarEdicionActual()
        {
            if (_actual != null && _documentos.ContainsKey(_actual))
                _documentos[_actual] = txtJson.Text;
        }

        private void ActualizarEstado()
        {
            string caracteres = txtJson.Text.Length.ToString("N0");
            txtEstado.Text = _documentos.Count > 1
                ? string.Format("{0} resultados · {1} caracteres", _documentos.Count, caracteres)
                : string.Format("{0} caracteres", caracteres);
        }

        // ────────────────────────────────────────────────────────────────────
        // Acciones
        // ────────────────────────────────────────────────────────────────────

        private void Copiar_Click(object sender, RoutedEventArgs e)
        {
            GuardarEdicionActual();

            if (string.IsNullOrEmpty(txtJson.Text))
            {
                MessageBox.Show("No hay contenido para copiar.", Title,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Clipboard.SetText(txtJson.Text);
            MessageBox.Show(_formato + " copiado al portapapeles.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Guardar_Click(object sender, RoutedEventArgs e)
        {
            GuardarEdicionActual();

            var sfd = new System.Windows.Forms.SaveFileDialog
            {
                Title            = "Guardar " + _formato,
                Filter           = string.Format("{0} (*{1})|*{1}|Todos los archivos (*.*)|*.*", _formato, _extension),
                FileName         = _svc.SanitizarNombreArchivo(_actual ?? "resultado") + _extension,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };
            if (sfd.ShowDialog(this.OwnerWin32()) != System.Windows.Forms.DialogResult.OK) return;

            try
            {
                System.IO.File.WriteAllText(sfd.FileName, txtJson.Text, Utf8SinBom);
                Notificar(_formato + " generado en: " + sfd.FileName);
                txtEstado.Text = "Guardado en: " + sfd.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo guardar el archivo: " + ex.Message, Title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void GuardarTodos_Click(object sender, RoutedEventArgs e)
        {
            GuardarEdicionActual();

            var fbd = new System.Windows.Forms.FolderBrowserDialog
            {
                Description         = "Seleccionar carpeta donde guardar los archivos " + _formato,
                ShowNewFolderButton = true
            };
            if (fbd.ShowDialog(this.OwnerWin32()) != System.Windows.Forms.DialogResult.OK) return;

            try
            {
                foreach (var doc in _documentos)
                {
                    string nombre = _svc.SanitizarNombreArchivo(doc.Key) + _extension;
                    string ruta   = System.IO.Path.Combine(fbd.SelectedPath, nombre);
                    System.IO.File.WriteAllText(ruta, doc.Value, Utf8SinBom);
                }

                Notificar(string.Format("{0} archivos {1} generados en: {2}",
                    _documentos.Count, _formato, fbd.SelectedPath));
                txtEstado.Text = string.Format("{0} archivos guardados en: {1}",
                    _documentos.Count, fbd.SelectedPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudieron guardar los archivos: " + ex.Message, Title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cerrar_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Notificar(string mensaje)
        {
            if (_log != null) _log(mensaje);
        }
    }
}
