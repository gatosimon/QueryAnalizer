using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace QueryAnalyzer
{
    public partial class App : Application
    {
        // Carpeta con permisos de escritura garantizados para cualquier usuario
        public static readonly string AppDataFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QueryAnalyzer");

        // ModernWpf y System.ValueTuple viajan dentro del .exe (recursos "Embedded.*.dll"),
        // asi el update por .exe solo sigue funcionando sin DLLs nuevas en disco.
        private static readonly Dictionary<string, Assembly> _ensambladosEmbebidos =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        public App()
        {
            // Debe registrarse antes de InitializeComponent: el XAML de App ya referencia ModernWpf
            AppDomain.CurrentDomain.AssemblyResolve += ResolverEnsambladoEmbebido;
        }

        private static Assembly ResolverEnsambladoEmbebido(object sender, ResolveEventArgs args)
        {
            string nombre = new AssemblyName(args.Name).Name;
            lock (_ensambladosEmbebidos)
            {
                Assembly ya;
                if (_ensambladosEmbebidos.TryGetValue(nombre, out ya)) return ya;

                using (Stream s = typeof(App).Assembly.GetManifestResourceStream("Embedded." + nombre + ".dll"))
                {
                    if (s == null) return null;
                    var datos = new byte[s.Length];
                    int leido = 0;
                    while (leido < datos.Length)
                    {
                        int n = s.Read(datos, leido, datos.Length - leido);
                        if (n <= 0) break;
                        leido += n;
                    }
                    Assembly asm = Assembly.Load(datos);
                    _ensambladosEmbebidos[nombre] = asm;
                    return asm;
                }
            }
        }

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

            // Si CheckForUpdates devuelve false, se aplicó una actualización
            // y la versión nueva ya fue relanzada. No continuar con esta instancia.
            if (!UpdateHelper.CheckForUpdates("https://github.com/gatosimon/QueryAnalyzerUpdates/releases/latest/download/version.xml"))
                return;
        }
    }
}