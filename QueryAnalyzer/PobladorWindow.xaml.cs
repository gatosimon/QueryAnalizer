using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace QueryAnalyzer
{
    /// <summary>
    /// Diálogo de configuración y ejecución del poblado de tablas con datos de prueba.
    /// Recibe la lista de tablas/vistas a poblar (nombre, schema, tipo) y, al confirmar,
    /// llama a PobladorService para insertar las filas respetando las FKs.
    /// </summary>
    public partial class PobladorWindow : Window
    {
        // Información de cada tabla a poblar: (schema, nombre, tipo)
        private readonly List<Tuple<string, string, string>> _tablas;
        private readonly Conexion _conexion;
        private CancellationTokenSource _cts;

        /// <param name="conexion">Conexión activa.</param>
        /// <param name="tablas">Lista de tuplas (schema, nombre, tipo) de las tablas seleccionadas.</param>
        public PobladorWindow(Conexion conexion, List<Tuple<string, string, string>> tablas)
        {
            InitializeComponent();
            AplicarTemaActual();

            _conexion = conexion;
            _tablas   = tablas ?? new List<Tuple<string, string, string>>();

            PoblarListaTablas();
        }

        // ── Tema ─────────────────────────────────────────────────────────────

        private void AplicarTemaActual()
        {
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow == null) return;
            var mainMerged = mainWindow.Resources.MergedDictionaries;
            if (mainMerged.Count == 0) return;

            var temaActual = mainMerged[0];
            var misMerged  = this.Resources.MergedDictionaries;
            if (misMerged.Count > 0) misMerged[0] = temaActual;
            else misMerged.Add(temaActual);
        }

        // ── Inicialización de la lista ────────────────────────────────────────

        private void PoblarListaTablas()
        {
            lstTablas.Items.Clear();
            foreach (var t in _tablas)
            {
                string etiqueta = string.IsNullOrEmpty(t.Item1)
                    ? t.Item2
                    : $"{t.Item1}.{t.Item2}";

                // Las vistas no se pueblan directamente, se marcan como advertencia
                if (t.Item3 == "VIEW" || t.Item3 == "V" || t.Item3 == "view")
                    etiqueta = $"[VISTA] {etiqueta}";

                lstTablas.Items.Add(etiqueta);
            }

            if (lstTablas.Items.Count == 0)
            {
                lstTablas.Items.Add("(No hay tablas seleccionadas)");
                btnPoblar.IsEnabled = false;
            }
        }

        // ── Poblado ───────────────────────────────────────────────────────────

        private async void BtnPoblar_Click(object sender, RoutedEventArgs e)
        {
            // Validar cantidad
            if (!int.TryParse(txtCantidad.Text.Trim(), out int cantidad) || cantidad < 1)
            {
                MessageBox.Show("Ingrese una cantidad válida (número entero mayor a 0).",
                    "Cantidad inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
                txtCantidad.Focus();
                return;
            }

            // Confirmar con el usuario
            int tablasPoblables = 0;
            foreach (var t in _tablas)
            {
                if (t.Item3 != "VIEW" && t.Item3 != "V" && t.Item3 != "view")
                    tablasPoblables++;
            }

            if (tablasPoblables == 0)
            {
                MessageBox.Show("Las vistas no se pueden poblar directamente. Seleccione al menos una tabla.",
                    "Sin tablas", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var resp = MessageBox.Show(
                $"Se insertarán hasta {cantidad} filas en {tablasPoblables} tabla(s).\n\n" +
                "Los datos generados son de prueba.\n¿Continuar?",
                "Confirmar poblado",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (resp != MessageBoxResult.Yes) return;

            // Iniciar proceso
            _cts = new CancellationTokenSource();
            btnPoblar.IsEnabled  = false;
            btnCancelar.Content  = "Cancelar";
            txtLog.Clear();
            progressBar.Value    = 0;
            progressBar.Maximum  = tablasPoblables;

            int procesadas = 0;

            foreach (var tabla in _tablas)
            {
                if (_cts.Token.IsCancellationRequested) break;

                string schema    = tabla.Item1;
                string nombre    = tabla.Item2;
                string tipo      = tabla.Item3;

                // Las vistas se omiten
                if (tipo == "VIEW" || tipo == "V" || tipo == "view")
                {
                    AgregarLog($"[OMITIDO] '{nombre}' es una vista. Las vistas no se pueblan directamente.");
                    continue;
                }

                AgregarLog($"Poblando '{(string.IsNullOrEmpty(schema) ? nombre : schema + "." + nombre)}'...");

                try
                {
                    int insertados = await PobladorService.PoblarTablaAsync(
                        _conexion,
                        schema,
                        nombre,
                        cantidad,
                        msg => Dispatcher.Invoke(() => AgregarLog("  " + msg)),
                        _cts.Token);

                    AgregarLog($"  -> {insertados} registros insertados.");
                }
                catch (OperationCanceledException)
                {
                    AgregarLog("[CANCELADO] El proceso fue interrumpido por el usuario.");
                    break;
                }
                catch (Exception ex)
                {
                    AgregarLog($"  [ERROR] {ex.Message}");
                }

                procesadas++;
                progressBar.Value = procesadas;
            }

            AgregarLog(string.Empty);
            AgregarLog(_cts.Token.IsCancellationRequested
                ? "Proceso cancelado."
                : $"Proceso completado. {procesadas} tabla(s) procesada(s).");

            btnPoblar.IsEnabled = true;
            btnCancelar.Content = "Cerrar";
            _cts = null;
        }

        private void BtnCerrar_Click(object sender, RoutedEventArgs e)
        {
            // Si hay un proceso en curso, cancelarlo
            _cts?.Cancel();
            Close();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private void AgregarLog(string mensaje)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AgregarLog(mensaje));
                return;
            }
            txtLog.AppendText((string.IsNullOrEmpty(mensaje) ? string.Empty : $"[{DateTime.Now:HH:mm:ss}] {mensaje}") + "\n");
            txtLog.ScrollToEnd();
        }
    }
}
