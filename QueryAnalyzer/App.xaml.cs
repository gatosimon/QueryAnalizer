using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace QueryAnalyzer
{
    public partial class App : Application
    {
        // Carpeta con permisos de escritura garantizados para cualquier usuario
        public static readonly string AppDataFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QueryAnalyzer");

        protected override void OnStartup(StartupEventArgs e)
        {
            // Crear carpeta de datos si no existe
            if (!Directory.Exists(AppDataFolder))
                Directory.CreateDirectory(AppDataFolder);

            string logPath = Path.Combine(AppDataFolder, "error.log");

            // Excepciones no controladas en cualquier hilo
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
                File.WriteAllText(logPath, ex.ExceptionObject.ToString());

            // Excepciones en el hilo de UI (el mas comun en WPF)
            DispatcherUnhandledException += (s, ex) =>
            {
                File.WriteAllText(logPath, ex.Exception.ToString());
                ex.Handled = true;
            };

            // Excepciones en tareas async no observadas
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                File.WriteAllText(logPath, ex.Exception.ToString());
                ex.SetObserved();
            };

            base.OnStartup(e);
        }
    }
}
//using System;
//using System.Windows;

//namespace QueryAnalyzer
//{
//    public partial class App : Application
//    {
//        protected override void OnStartup(StartupEventArgs e)
//        {
//            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
//                System.IO.File.WriteAllText("error.log", ex.ExceptionObject.ToString());
//            base.OnStartup(e);
//        }
//    }
//}
