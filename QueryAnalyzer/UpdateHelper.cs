using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Xml;

namespace QueryAnalyzer
{
    public static class UpdateHelper
    {
        private const string UpdaterFileName = "AutoUpdater.exe";
        private const string AppFolder = "QueryAnalyzer";
        private const string SkipUpdateFlagFile = "skip_update_check.flag";
        private const string MarkerFileName = "update_marker.xml";

        private static string GetUpdaterDir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppFolder);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }
        /// <summary>
        /// Devuelve la versión instalada actualmente según update_marker.xml.
        /// Si el archivo no existe (primera ejecución antes de la primera actualización),
        /// devuelve la versión del assembly como fallback.
        /// </summary>
        public static string GetInstalledVersion()
        {
            try
            {
                string appDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string markerPath = Path.Combine(appDir, MarkerFileName);

                if (!File.Exists(markerPath))
                {
                    // Todavía no se actualizó nunca: usar la versión del assembly
                    return $"No actualizada aún.\nVersión del ensamblado: {Assembly.GetExecutingAssembly().GetName().Version}";
                }

                var doc = new XmlDocument();
                doc.Load(markerPath);

                XmlNode node = doc.SelectSingleNode("/VersionMarker/InstalledVersion");
                if (node != null && !string.IsNullOrEmpty(node.InnerText))
                    return node.InnerText.Trim();

                // Nodo no encontrado: fallback al assembly
                return Assembly.GetExecutingAssembly().GetName().Version.ToString();
            }
            catch (Exception ex)
            {
                LogToFile("Error al leer version instalada: " + ex.Message);
                return Assembly.GetExecutingAssembly().GetName().Version.ToString();
            }
        }

        /// <summary>
        /// Devuelve la fecha de la última actualización instalada.
        /// Devuelve null si nunca se actualizó o si no se puede leer el archivo.
        /// </summary>
        public static DateTime? GetInstallDate()
        {
            try
            {
                string appDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string markerPath = Path.Combine(appDir, MarkerFileName);

                if (!File.Exists(markerPath)) return null;

                var doc = new XmlDocument();
                doc.Load(markerPath);

                XmlNode node = doc.SelectSingleNode("/VersionMarker/InstallDate");
                if (node == null || string.IsNullOrEmpty(node.InnerText)) return null;

                DateTime date;
                if (DateTime.TryParse(node.InnerText, out date))
                    return date;

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string FindEmbeddedResourceName(Assembly assembly)
        {
            string[] names = assembly.GetManifestResourceNames();
            LogToFile("Recursos embebidos encontrados en el assembly (" + names.Length + "):");
            foreach (string n in names)
                LogToFile("  -> " + n);

            foreach (string n in names)
            {
                if (n.EndsWith(UpdaterFileName, StringComparison.OrdinalIgnoreCase))
                {
                    LogToFile("Recurso seleccionado: " + n);
                    return n;
                }
            }
            LogToFile("WARN: No se encontro ningun recurso embebido que termine en " + UpdaterFileName);
            return null;
        }

        private static bool EnsureAutoUpdaterExists(out string updaterPath)
        {
            string updaterDir = GetUpdaterDir();
            updaterPath = Path.Combine(updaterDir, UpdaterFileName);

            Assembly assembly = Assembly.GetExecutingAssembly();
            string resourceName = FindEmbeddedResourceName(assembly);

            if (resourceName == null)
            {
                bool exists = File.Exists(updaterPath);
                LogToFile(exists
                    ? "Recurso no embebido pero AutoUpdater.exe existe en disco: " + updaterPath
                    : "Recurso no embebido y AutoUpdater.exe NO existe. No se puede actualizar.");
                return exists;
            }

            using (Stream resourceStream = assembly.GetManifestResourceStream(resourceName))
            {
                if (resourceStream == null)
                {
                    LogToFile("ERROR: GetManifestResourceStream devolvio null para: " + resourceName);
                    return File.Exists(updaterPath);
                }

                bool needsExtract = true;
                if (File.Exists(updaterPath))
                {
                    long diskSize = new FileInfo(updaterPath).Length;
                    long resourceSize = resourceStream.Length;
                    needsExtract = diskSize != resourceSize;
                    LogToFile("AutoUpdater.exe en disco: " + diskSize + " bytes | " +
                              "Recurso embebido: " + resourceSize + " bytes | " +
                              "Necesita extraccion: " + needsExtract);
                }
                else
                {
                    LogToFile("AutoUpdater.exe no existe. Se extrae del recurso embebido en: " + updaterPath);
                }

                if (needsExtract)
                {
                    resourceStream.Seek(0, SeekOrigin.Begin);
                    try
                    {
                        using (var fs = new FileStream(updaterPath, FileMode.Create, FileAccess.Write))
                        {
                            byte[] buffer = new byte[81920];
                            int read;
                            while ((read = resourceStream.Read(buffer, 0, buffer.Length)) > 0)
                                fs.Write(buffer, 0, read);
                        }
                        LogToFile("AutoUpdater.exe extraido correctamente en: " + updaterPath);
                    }
                    catch (Exception ex)
                    {
                        LogToFile("ERROR al extraer AutoUpdater.exe: " + ex.Message);
                        return false;
                    }
                }
            }

            return File.Exists(updaterPath);
        }

        /// <summary>
        /// Verifica si existe el flag que indica que la app acaba de ser actualizada
        /// y relanzada por AutoUpdater. Si existe, lo elimina y retorna true para
        /// que la app arranque directamente sin lanzar AutoUpdater de nuevo.
        /// </summary>
        private static bool CheckAndClearSkipFlag(string appDir)
        {
            try
            {
                string flagPath = Path.Combine(appDir, SkipUpdateFlagFile);
                if (!File.Exists(flagPath)) return false;

                string version = File.ReadAllText(flagPath).Trim();
                LogToFile("Flag de salto detectado. App recien actualizada a v" + version +
                          ". Se omite verificacion de actualizaciones.");
                File.Delete(flagPath);
                return true;
            }
            catch (Exception ex)
            {
                LogToFile("Error al verificar flag de salto: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Verifica actualizaciones de forma sincrónica.
        /// Devuelve true si la app debe continuar su arranque normal.
        /// Devuelve false si se aplicó una actualización y la app debe cerrarse
        /// (AutoUpdater ya relanzó la versión nueva).
        /// </summary>
        public static bool CheckForUpdates(string manifestUrl, bool silent = true)
        {
            try
            {
                string appDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string appExe = Assembly.GetExecutingAssembly().Location;
                LogToFile("=== CheckForUpdates iniciado ===");
                LogToFile("AppDir: " + appDir);
                LogToFile("AppExe: " + appExe);
                LogToFile("ManifestUrl: " + manifestUrl);

                // Si la app fue relanzada por AutoUpdater tras una actualización,
                // saltear el check para no mostrar UAC de nuevo innecesariamente
                if (CheckAndClearSkipFlag(appDir))
                    return true;

                string updaterPath;
                if (!EnsureAutoUpdaterExists(out updaterPath))
                {
                    LogToFile("No se pudo obtener AutoUpdater.exe. Se omite la verificacion.");
                    return true;
                }

                string appName = Assembly.GetExecutingAssembly().GetName().Name;
                int pid = Process.GetCurrentProcess().Id;

                string args =
                    "--app-name \"" + appName + "\" " +
                    "--manifest-url \"" + manifestUrl + "\" " +
                    "--app-dir \"" + appDir + "\" " +
                    "--app-exe \"" + appExe + "\" " +
                    "--pid " + pid +
                    (silent ? " --silent" : "");

                LogToFile("Lanzando: " + updaterPath);
                LogToFile("Args: " + args);

                var psi = new ProcessStartInfo
                {
                    FileName = updaterPath,
                    Arguments = args,
                    UseShellExecute = true,
                    Verb = "runas"
                };

                Process proc = Process.Start(psi);
                proc.WaitForExit();

                int exitCode = proc.ExitCode;
                LogToFile("AutoUpdater termino con ExitCode: " + exitCode);

                // ExitCode 1 = update aplicado, app ya fue relanzada → no continuar
                // ExitCode 0 = sin update / omitido / error → continuar normalmente
                return exitCode != 1;
            }
            catch (Exception ex)
            {
                LogToFile("ERROR en CheckForUpdates: " + ex.Message);
                return true;
            }
        }

        private static readonly object _logLock = new object();

        private static void LogToFile(string message)
        {
            try
            {
                string logPath = Path.Combine(GetUpdaterDir(), "updatehelper.log");
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message;
                lock (_logLock)
                    File.AppendAllText(logPath, line + Environment.NewLine);
            }
            catch { }
        }
    }
}