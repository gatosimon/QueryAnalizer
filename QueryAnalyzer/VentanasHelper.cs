using System;
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

            var destino = owner;

            // Dos puntos, porque cubren casos distintos:
            //  - Closing: la hija todavía tiene el foreground, así que Win32 concede la
            //    activación del owner. Es lo que resuelve las ventanas no modales.
            //  - Closed (diferido): durante Closing de un modal el owner está
            //    deshabilitado y Activate() no hace nada; recién sirve después.
            ventana.Closing += (s, e) =>
            {
                if (e.Cancel) return;
                ActivarDirecto(destino);
            };
            ventana.Closed += (s, e) => Reactivar(destino);

            return ventana;
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
            }
            catch { }   // la ventana pudo cerrarse entre el agendado y la ejecución
        }

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
