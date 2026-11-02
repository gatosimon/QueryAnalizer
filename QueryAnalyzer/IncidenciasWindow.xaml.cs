using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace QueryAnalyzer
{
    /// <summary>
    /// Muestra las incidencias (fallas y anomalías) de una transferencia o backup.
    /// En modo decisión bloquea hasta que el usuario elige abortar (por defecto) o continuar.
    /// </summary>
    public partial class IncidenciasWindow : Window
    {
        private readonly List<IncidenciaPasado> _todas;
        private readonly bool _modoDecision;
        private readonly string _carpetaLogs;

        /// <summary>True solo si el usuario eligió continuar en el modo decisión.</summary>
        public bool Continuar { get; private set; }

        public IncidenciasWindow(IEnumerable<IncidenciaPasado> incidencias, string titulo, string resumen,
                                 bool modoDecision, string textoContinuar = null, string carpetaLogs = null)
        {
            InitializeComponent();
            AplicarTemaActual();

            _todas = (incidencias ?? Enumerable.Empty<IncidenciaPasado>()).ToList();
            _modoDecision = modoDecision;
            _carpetaLogs = carpetaLogs;

            Title = titulo ?? "Incidencias";
            txtResumen.Text = resumen ?? "";

            cmbFiltro.Items.Add("Todas");
            cmbFiltro.Items.Add("Avisos o peores");
            cmbFiltro.Items.Add("Errores o peores");
            cmbFiltro.SelectedIndex = _todas.Any(i => i.Severidad >= SeveridadIncidencia.Error) ? 2 : 0;

            if (modoDecision)
            {
                // En decisión el cierre por la X o Esc equivale a abortar
                btnCerrar.Content = "Abortar";
                btnCerrar.ToolTip = "No continuar. B no se modifica.";
                btnContinuar.Visibility = Visibility.Visible;
                if (!string.IsNullOrEmpty(textoContinuar)) btnContinuar.Content = textoContinuar;
            }
            btnCarpetaLogs.IsEnabled = !string.IsNullOrEmpty(carpetaLogs);
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

        /// <summary>
        /// Muestra las incidencias y espera la decisión. Devuelve true solo si el usuario
        /// eligió continuar. Debe llamarse desde el hilo de la UI.
        /// </summary>
        public static bool PedirDecision(Window owner, IEnumerable<IncidenciaPasado> incidencias,
                                         string titulo, string resumen, string textoContinuar, string carpetaLogs)
        {
            var w = new IncidenciasWindow(incidencias, titulo, resumen, true, textoContinuar, carpetaLogs);
            w.MostrarModal(owner);
            return w.Continuar;
        }

        // ── Filtro y detalle ──────────────────────────────────────────────────

        private IEnumerable<IncidenciaPasado> Visibles()
        {
            switch (cmbFiltro.SelectedIndex)
            {
                case 1:  return _todas.Where(i => i.Severidad >= SeveridadIncidencia.Aviso);
                case 2:  return _todas.Where(i => i.Severidad >= SeveridadIncidencia.Error);
                default: return _todas;
            }
        }

        private void cmbFiltro_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (dgIncidencias == null) return;
            var lista = Visibles().ToList();
            dgIncidencias.ItemsSource = lista;
            if (lista.Count > 0) dgIncidencias.SelectedIndex = 0;
            else txtDetalle.Text = "";
        }

        private void dgIncidencias_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var i = dgIncidencias.SelectedItem as IncidenciaPasado;
            if (i == null) { txtDetalle.Text = ""; return; }

            var sb = new StringBuilder();
            sb.AppendLine($"{i.SeveridadTexto} · {i.Fase} · {i.Tipo}");
            if (!string.IsNullOrEmpty(i.Tabla))   sb.AppendLine("Tabla: " + i.Tabla);
            if (!string.IsNullOrEmpty(i.Columna)) sb.AppendLine("Columna: " + i.Columna);
            if (!string.IsNullOrEmpty(i.Clave))   sb.AppendLine("Fila: " + i.Clave);
            sb.AppendLine();
            sb.AppendLine(i.Mensaje);
            if (!string.IsNullOrEmpty(i.AccionSugerida))
            {
                sb.AppendLine();
                sb.AppendLine("Qué hacer: " + i.AccionSugerida);
            }
            txtDetalle.Text = sb.ToString();
        }

        // ── Botones ───────────────────────────────────────────────────────────

        private void btnExportar_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title            = "Exportar incidencias",
                Filter           = "CSV (*.csv)|*.csv",
                DefaultExt       = "csv",
                FileName         = $"incidencias_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                RegistroIncidencias.EscribirCsv(_todas, dlg.FileName);
                System.Diagnostics.Process.Start(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo guardar el archivo: " + ex.Message, Title,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnCopiar_Click(object sender, RoutedEventArgs e)
        {
            var texto = string.Join(Environment.NewLine, Visibles().Select(i => i.ToString()));
            if (texto.Length == 0) return;
            try { Clipboard.SetText(texto); } catch { }
        }

        private void btnCarpetaLogs_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(_carpetaLogs); } catch { }
        }

        private void btnContinuar_Click(object sender, RoutedEventArgs e)
        {
            var conf = MessageBox.Show(
                "Hay incidencias sin resolver. Si continuás, lo que no se pudo respaldar, leer o cargar " +
                "queda sin cubrir.\n\n¿Confirmás que querés continuar?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (conf != MessageBoxResult.Yes) return;

            Continuar = true;
            Close();
        }

        private void btnCerrar_Click(object sender, RoutedEventArgs e)
        {
            Continuar = false;
            Close();
        }
    }
}
