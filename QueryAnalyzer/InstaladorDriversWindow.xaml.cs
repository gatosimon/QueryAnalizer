using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace QueryAnalyzer
{
    // ── ViewModel por driver ─────────────────────────────────────────────────────
    public class DriverVM : INotifyPropertyChanged
    {
        private readonly DriverInfo _info;

        public DriverVM(DriverInfo info) { _info = info; Actualizar(); }

        public string Nombre       => _info.Nombre;
        public string Descripcion  => _info.Descripcion;
        public string Nota         => _info.Nota;
        public DriverInfo Info     => _info;

        // ── Propiedades derivadas del estado ──────────────────────────────────
        private string  _iconoEstado;
        private string  _colorEstadoStr;
        private string  _estadoTexto;

        public string IconoEstado  => _iconoEstado;
        public string EstadoTexto  => _estadoTexto;

        public Brush ColorEstado =>
            _colorEstadoStr == "green"
                ? new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50))
                : new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));

        // ── Visibilidades de los botones ──────────────────────────────────────
        // Mostrar el botón siempre que el driver sea de tipo Bundle y no esté instalado,
        // sin importar si el archivo instalador está presente en este momento.
        // El chequeo de existencia del archivo se hace en el click handler.
        public Visibility VisibilidadInstalar
            => (!_info.EstaInstalado && _info.Fuente == FuenteInstalar.Bundle)
               ? Visibility.Visible : Visibility.Collapsed;

        public Visibility VisibilidadDescargar
            => (!_info.EstaInstalado && _info.PuedaDescargar)
               ? Visibility.Visible : Visibility.Collapsed;

        public Visibility VisibilidadListo
            => (_info.EstaInstalado && _info.Fuente != FuenteInstalar.SistemaOperativo)
               ? Visibility.Visible : Visibility.Collapsed;

        public Visibility VisibilidadSO
            => (_info.EstaInstalado && _info.Fuente == FuenteInstalar.SistemaOperativo)
               ? Visibility.Visible : Visibility.Collapsed;

        // ── Actualizar tras cambio de estado ──────────────────────────────────
        public void Actualizar()
        {
            if (_info.EstaInstalado)
            {
                _iconoEstado    = "✔";
                _colorEstadoStr = "green";
                _estadoTexto    = "Instalado";
            }
            else
            {
                _iconoEstado    = "✖";
                _colorEstadoStr = "red";
                _estadoTexto    = "No instalado";
            }
            OnPropertyChanged(null); // notificar todo
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ── Ventana ──────────────────────────────────────────────────────────────────
    public partial class InstaladorDriversWindow : Window
    {
        private List<DriverVM> _vms;
        private bool _instalandoEnCurso = false;

        public InstaladorDriversWindow()
        {
            InitializeComponent();
            AplicarTemaActual();
        }

        // ── Tema: mismo patrón que PreferenciasWindow ─────────────────────────
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

        // ── Carga inicial ─────────────────────────────────────────────────────
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            CargarDrivers();
        }

        private void CargarDrivers()
        {
            var catalogo = OdbcDriverManager.ObtenerCatalogo();
            OdbcDriverManager.ActualizarEstados(catalogo);

            _vms = new System.Collections.Generic.List<DriverVM>();
            foreach (var d in catalogo)
                _vms.Add(new DriverVM(d));

            listDrivers.ItemsSource = _vms;
            ActualizarBannerEstado();
        }

        private void ActualizarBannerEstado()
        {
            int faltantes = 0;
            int instalados = 0;
            foreach (var vm in _vms)
            {
                if (vm.Info.EstaInstalado) instalados++;
                else faltantes++;
            }

            if (faltantes == 0)
                txtEstado.Text = $"✔ Todos los drivers están instalados ({instalados} de {_vms.Count}).";
            else
                txtEstado.Text = $"⚠ {faltantes} driver(s) no instalado(s). " +
                                 $"Haga clic en \"Instalar\" o \"Descargar\" según corresponda.";
        }

        // ── Botón Instalar ────────────────────────────────────────────────────
        private async void BtnInstalar_Click(object sender, RoutedEventArgs e)
        {
            if (_instalandoEnCurso) return;

            // Tag contiene el DriverVM (DataContext del DataTemplate), no el DriverInfo directamente
            var vm = (sender as Button)?.Tag as DriverVM;
            if (vm == null) return;
            var driver = vm.Info;

            // Verificar que el instalador esté presente en disco en este momento
            if (!driver.InstaladorDisponible)
            {
                txtEstado.Text = $"⚠ No se encontró el instalador en: {driver.InstaladorRuta ?? "(ruta no definida)"}. " +
                                 "Asegúrese de que la carpeta Drivers\\ esté junto al ejecutable.";
                return;
            }

            _instalandoEnCurso = true;
            txtEstado.Text = $"⏳ Instalando {driver.Nombre}... aguarde.";
            SetBotonesHabilitados(false);

            var (exitCode, error) = await OdbcDriverManager.InstalarAsync(driver);

            // Re-detectar estado después de instalar
            var catalogo = OdbcDriverManager.ObtenerCatalogo();
            OdbcDriverManager.ActualizarEstados(catalogo);

            // Actualizar los VMs con los nuevos estados
            foreach (var vmItem in _vms)
            {
                var actualizado = catalogo.Find(d => d.Nombre == vmItem.Nombre);
                if (actualizado != null)
                {
                    vmItem.Info.Estado = actualizado.Estado;
                    vmItem.Actualizar();
                }
            }

            if (exitCode == 0)
            {
                txtEstado.Text = $"✔ {driver.Nombre} instalado correctamente. " +
                                 "Puede ser necesario reiniciar la aplicación.";
            }
            else if (exitCode == -2)
            {
                txtEstado.Text = "⚠ Instalación cancelada (UAC rechazado).";
            }
            else
            {
                txtEstado.Text = string.IsNullOrEmpty(error)
                    ? $"⚠ El instalador finalizó con código {exitCode}. Verifique manualmente."
                    : $"⚠ Error al instalar: {error}";
            }

            ActualizarBannerEstado();
            SetBotonesHabilitados(true);
            _instalandoEnCurso = false;
        }

        // ── Botón Descargar ───────────────────────────────────────────────────
        private void BtnDescargar_Click(object sender, RoutedEventArgs e)
        {
            // Tag contiene el DriverVM (DataContext del DataTemplate)
            var vm = (sender as Button)?.Tag as DriverVM;
            if (vm == null) return;
            var driver = vm.Info;
            if (!string.IsNullOrEmpty(driver.UrlDescarga))
                OdbcDriverManager.AbrirUrlDescarga(driver.UrlDescarga);
        }

        // ── Botón Verificar ───────────────────────────────────────────────────
        private void BtnVerificar_Click(object sender, RoutedEventArgs e)
        {
            var catalogo = OdbcDriverManager.ObtenerCatalogo();
            OdbcDriverManager.ActualizarEstados(catalogo);

            foreach (var vm in _vms)
            {
                var actualizado = catalogo.Find(d => d.Nombre == vm.Nombre);
                if (actualizado != null)
                {
                    vm.Info.Estado = actualizado.Estado;
                    vm.Actualizar();
                }
            }

            ActualizarBannerEstado();
        }

        // ── Botón Cerrar ──────────────────────────────────────────────────────
        private void BtnCerrar_Click(object sender, RoutedEventArgs e) => Close();

        // ── Helper ────────────────────────────────────────────────────────────
        private void SetBotonesHabilitados(bool habilitado)
        {
            // Deshabilitar todos los botones de instalar/verificar mientras se instala
            // (los botones están dentro del ItemsControl; recorremos visualmente)
            // La forma más simple: deshabilitar la ventana entera y re-habilitarla
            IsEnabled = habilitado;
        }
    }
}
