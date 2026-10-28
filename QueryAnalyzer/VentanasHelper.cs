using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace QueryAnalyzer
{
    /// <summary>
    /// Centraliza la apertura de ventanas secundarias.
    ///
    /// Sin esto, al cerrar una pantalla el foco se escapa a otra aplicación: Windows
    /// activa la siguiente ventana del Z-order global cuando la que se destruye no
    /// tiene una relación de propiedad válida (o cuando su owner quedó deshabilitado
    /// mientras había un modal arriba). El helper asigna el Owner y además reactiva
    /// explícitamente esa ventana al cerrarse la hija.
    /// </summary>
    internal static class VentanasHelper
    {
        /// <summary>
        /// Asigna el Owner y engancha la devolución de foco. Devuelve la misma ventana
        /// para poder encadenar.
        /// </summary>
        public static T Preparar<T>(this T ventana, Window owner) where T : Window
        {
            if (ventana == null) return null;

            if (owner != null && !ReferenceEquals(ventana, owner))
                ventana.Owner = owner;

            EngancharFoco(ventana);
            return ventana;
        }

        private static readonly ConditionalWeakTable<Window, object> _conFoco =
            new ConditionalWeakTable<Window, object>();

        /// <summary>
        /// Hace que, al cerrarse la ventana, el foco vuelva a QueryAnalyzer (su Owner, o la
        /// principal) aunque otra aplicación estuviera detrás. Es idempotente: App lo engancha
        /// a toda ventana al cargarse y Preparar lo hace antes de mostrarla.
        /// </summary>
        public static void EngancharFoco(Window ventana)
        {
            if (ventana == null) return;
            object marca;
            if (_conFoco.TryGetValue(ventana, out marca)) return;
            _conFoco.Add(ventana, new object());

            Window destino = null;
            bool teniaForeground = false;
            bool cerrada = false;

            // Dos puntos, porque cubren casos distintos:
            //  - Closing: la hija todavía tiene el foreground, así que Win32 concede la
            //    activación del destino. Si la hija era modal, el destino está deshabilitado
            //    y no se puede activar: se lo habilita acá (WPF lo haría recién después de
            //    ocultar la hija, cuando Windows ya le pasó el foreground a otra aplicación).
            //  - Closed (diferido): red de seguridad por si Closing no alcanzó.
            // Solo actúa si la hija tenía el foreground: así no roba el foco cuando el cierre
            // lo dispara un proceso de fondo ni con ventanas que nunca se activan (por
            // ejemplo la lista de IntelliSense de AvalonEdit).
            ventana.Closing += (s, e) =>
            {
                if (e.Cancel) return;
                destino = DestinoDe(ventana);
                if (destino == null) return;

                IntPtr hVentana = new WindowInteropHelper(ventana).Handle;
                teniaForeground = hVentana != IntPtr.Zero && GetForegroundWindow() == hVentana;
                if (!teniaForeground) return;

                IntPtr hDestino = new WindowInteropHelper(destino).Handle;
                bool habilitadoPorNosotros = false;
                if (hDestino != IntPtr.Zero && !IsWindowEnabled(hDestino))
                {
                    EnableWindow(hDestino, true);
                    habilitadoPorNosotros = true;
                }

                ActivarDirecto(destino);

                // Otro handler de Closing puede cancelar el cierre después del nuestro:
                // si la ventana sigue abierta, el destino vuelve a quedar bloqueado por el modal.
                if (habilitadoPorNosotros)
                {
                    ventana.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (!cerrada && ventana.IsVisible)
                            EnableWindow(hDestino, false);
                    }));
                }
            };
            ventana.Closed += (s, e) =>
            {
                cerrada = true;
                if (teniaForeground) Reactivar(destino);
            };
        }

        // El Owner se lee en Closing, porque después de cerrarse WPF ya lo limpió
        private static Window DestinoDe(Window ventana)
        {
            if (ventana.Owner != null) return ventana.Owner;
            var principal = Application.Current != null ? Application.Current.MainWindow : null;
            return ReferenceEquals(principal, ventana) ? null : principal;
        }

        /// <summary>Abre la ventana como modal, con Owner y devolución de foco.</summary>
        public static bool? MostrarModal(this Window ventana, Window owner)
        {
            return ventana.Preparar(owner).ShowDialog();
        }

        /// <summary>Abre la ventana sin bloquear, con Owner y devolución de foco.</summary>
        public static void Mostrar(this Window ventana, Window owner)
        {
            ventana.Preparar(owner).Show();
        }

        /// <summary>
        /// Trae al frente la ventana indicada (o la principal si es null) una vez que la
        /// secundaria terminó de cerrarse.
        /// </summary>
        public static void Reactivar(Window objetivo)
        {
            Window w = objetivo ?? (Application.Current != null ? Application.Current.MainWindow : null);
            if (w == null) return;

            // Diferido: en Closed el HWND de la hija todavía no se destruyó del todo.
            // Si activamos ahora, Windows nos vuelve a mover el foreground encima.
            w.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => ActivarDirecto(w)));
        }

        private static void ActivarDirecto(Window w)
        {
            if (w == null) return;
            try
            {
                if (!w.IsVisible) return;
                if (w.WindowState == WindowState.Minimized)
                    w.WindowState = WindowState.Normal;
                w.Activate();
                w.Focus();

                // Activate() de WPF a veces solo parpadea en la barra de tareas; si el foreground
                // sigue siendo de la app (o de nadie), SetForegroundWindow sí lo concede.
                IntPtr h = new WindowInteropHelper(w).Handle;
                if (h != IntPtr.Zero && ForegroundEsDeLaApp())
                    SetForegroundWindow(h);
            }
            catch { }   // la ventana pudo cerrarse entre el agendado y la ejecución
        }

        private static bool ForegroundEsDeLaApp()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return true;
            uint pid;
            GetWindowThreadProcessId(fg, out pid);
            return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        /// <summary>
        /// Owner Win32 para los diálogos de System.Windows.Forms (FolderBrowserDialog,
        /// ColorDialog, etc.), que no aceptan un Window de WPF.
        /// </summary>
        public static System.Windows.Forms.IWin32Window OwnerWin32(this Window ventana)
        {
            return new WindowWrapper(new WindowInteropHelper(ventana).Handle);
        }

        private sealed class WindowWrapper : System.Windows.Forms.IWin32Window
        {
            public WindowWrapper(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; private set; }
        }
    }
}
