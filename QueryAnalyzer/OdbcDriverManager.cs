using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace QueryAnalyzer
{
    // ── Estado de instalación ────────────────────────────────────────────────────
    public enum EstadoDriver
    {
        Instalado,
        NoInstalado
    }

    // ── Fuente del instalador ────────────────────────────────────────────────────
    public enum FuenteInstalar
    {
        /// <summary>Embebido en el exe; se extrae a %TEMP% al instalar.</summary>
        Bundle,
        /// <summary>Forma parte del sistema operativo; no requiere instalar.</summary>
        SistemaOperativo,
        /// <summary>Solo se puede descargar (URL provista).</summary>
        DescargaExterna
    }

    // ── Información de un driver ODBC ────────────────────────────────────────────
    public class DriverInfo
    {
        /// <summary>Nombre amigable que se muestra en la UI.</summary>
        public string Nombre { get; set; }

        /// <summary>Descripción corta (motor de base de datos).</summary>
        public string Descripcion { get; set; }

        /// <summary>Uno o más nombres de ODBC drivers a buscar en el registro.</summary>
        public string[] NombresOdbc { get; set; }

        public FuenteInstalar Fuente { get; set; }

        /// <summary>Nombre del archivo instalador (embebido como recurso en el exe).</summary>
        public string InstaladorArchivo { get; set; }

        /// <summary>Argumentos de línea de comandos para instalación silenciosa.</summary>
        public string InstaladorArgs { get; set; }

        /// <summary>URL de descarga cuando Fuente == DescargaExterna.</summary>
        public string UrlDescarga { get; set; }

        /// <summary>Nota adicional que se muestra al usuario.</summary>
        public string Nota { get; set; }

        // ── Propiedades calculadas en tiempo de ejecución ──────────────────────
        public EstadoDriver Estado { get; set; } = EstadoDriver.NoInstalado;

        public bool EstaInstalado => Estado == EstadoDriver.Instalado;

        /// <summary>
        /// True si el instalador está disponible: es de tipo Bundle y tiene
        /// un nombre de archivo definido (el exe real está embebido en el ensamblado).
        /// </summary>
        public bool InstaladorDisponible =>
            Fuente == FuenteInstalar.Bundle &&
            !string.IsNullOrEmpty(InstaladorArchivo);

        public bool PuedaDescargar =>
            Fuente == FuenteInstalar.DescargaExterna &&
            !string.IsNullOrEmpty(UrlDescarga);
    }

    // ── Manager principal ────────────────────────────────────────────────────────
    public static class OdbcDriverManager
    {
        // Prefijo de los recursos embebidos (definido por LogicalName en el .csproj)
        private const string ResourcePrefix = "QueryAnalyzer.Drivers.";

        // Carpeta temporal donde se extraen los instaladores
        private static readonly string TempDriversFolder =
            Path.Combine(Path.GetTempPath(), "QueryAnalyzer_Drivers");

        // ── Catálogo de drivers soportados ────────────────────────────────────
        public static List<DriverInfo> ObtenerCatalogo()
        {
            return new List<DriverInfo>
            {
                new DriverInfo
                {
                    Nombre            = "SQLite ODBC Driver",
                    Descripcion       = "Conectividad ODBC para bases de datos SQLite (.db, .sqlite)",
                    NombresOdbc       = new[] { "SQLite3 ODBC Driver", "SQLite ODBC Driver", "SQLite ODBC (UTF-8) Driver" },
                    Fuente            = FuenteInstalar.Bundle,
                    InstaladorArchivo = "sqliteodbc.exe",
                    InstaladorArgs    = "/S",        // NSIS silent
                    Nota              = "Instalador embebido en la aplicación."
                },
                new DriverInfo
                {
                    Nombre            = "PostgreSQL ODBC Driver",
                    Descripcion       = "Conectividad ODBC para PostgreSQL (psqlodbc 16.x)",
                    NombresOdbc       = new[]
                    {
                        "PostgreSQL Unicode", "PostgreSQL ANSI",
                        "PostgreSQL Unicode(x86)", "PostgreSQL ANSI(x86)",
                        "PostgreSQL ODBC Driver(UNICODE)", "PostgreSQL ODBC Driver(ANSI)"
                    },
                    Fuente            = FuenteInstalar.Bundle,
                    InstaladorArchivo = "psqlodbc-setup.exe",
                    InstaladorArgs    = "/quiet",    // WiX bootstrapper silent
                    Nota              = "Instalador embebido en la aplicación."
                },
                new DriverInfo
                {
                    Nombre      = "SQL Server ODBC Driver",
                    Descripcion = "Driver nativo de Windows para Microsoft SQL Server",
                    NombresOdbc = new[]
                    {
                        "SQL Server",
                        "ODBC Driver 17 for SQL Server",
                        "ODBC Driver 18 for SQL Server"
                    },
                    Fuente      = FuenteInstalar.DescargaExterna,
                    UrlDescarga = "https://learn.microsoft.com/sql/connect/odbc/download-odbc-driver-for-sql-server",
                    Nota        = "El driver \"SQL Server\" viene preinstalado en Windows. " +
                                  "Si necesita el moderno \"ODBC Driver 17/18 for SQL Server\" " +
                                  "puede descargarlo gratis desde Microsoft."
                },
                new DriverInfo
                {
                    Nombre      = "IBM DB2 ODBC Driver",
                    Descripcion = "Driver ODBC del cliente IBM DB2 (DB2CLI.DLL)",
                    NombresOdbc = new[] { "IBM DB2 ODBC DRIVER", "IBM DB2 ODBC DRIVER - DB2COPY1" },
                    Fuente      = FuenteInstalar.DescargaExterna,
                    UrlDescarga = "https://www.ibm.com/support/pages/db2-odbc-cli-driver-download-and-installation-information",
                    Nota        = "El driver IBM DB2 viene incluido con el IBM Data Server Client. " +
                                  "Descargue el \"IBM Data Server Driver for ODBC and CLI\" desde la página de IBM."
                }
            };
        }

        // ── Detección de drivers instalados ──────────────────────────────────
        /// <summary>
        /// Detecta qué drivers están instalados leyendo el registro ODBC de 32 bits.
        /// La app es x86: necesita los drivers 32-bit (WOW6432Node).
        /// </summary>
        public static void ActualizarEstados(List<DriverInfo> catalogo)
        {
            var instalados = ObtenerDriversInstalados32();
            foreach (var d in catalogo)
            {
                d.Estado = d.NombresOdbc.Any(n =>
                    instalados.Contains(n, StringComparer.OrdinalIgnoreCase))
                    ? EstadoDriver.Instalado
                    : EstadoDriver.NoInstalado;
            }
        }

        /// <summary>
        /// Devuelve la lista de nombres de drivers ODBC instalados en el hive de 32 bits.
        /// </summary>
        public static List<string> ObtenerDriversInstalados32()
        {
            var resultado = new List<string>();

            // Leer explícitamente desde WOW6432Node (cubre procesos de 32 y 64 bits)
            try
            {
                using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (var key  = hive.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers", false))
                {
                    if (key != null)
                        resultado.AddRange(key.GetValueNames());
                }
            }
            catch { /* acceso denegado u otro error — continuar */ }

            // Fallback: hive nativo del proceso (ya es 32-bit en esta app)
            if (resultado.Count == 0)
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers", false))
                    {
                        if (key != null)
                            resultado.AddRange(key.GetValueNames());
                    }
                }
                catch { }
            }

            return resultado;
        }

        // ── Extracción de recursos embebidos ──────────────────────────────────
        /// <summary>
        /// Extrae el instalador desde el recurso embebido en el exe hacia una
        /// carpeta temporal. Devuelve la ruta del archivo extraído, o null si falla.
        /// Si el archivo temporal ya existe y no está bloqueado, lo reutiliza.
        /// </summary>
        private static string ExtraerInstaladorATemporal(DriverInfo driver)
        {
            var resourceName = ResourcePrefix + driver.InstaladorArchivo;
            var assembly = Assembly.GetExecutingAssembly();

            // Verificar que el recurso existe en el ensamblado
            var recursos = assembly.GetManifestResourceNames();
            if (!recursos.Contains(resourceName, StringComparer.OrdinalIgnoreCase))
                return null;

            try
            {
                Directory.CreateDirectory(TempDriversFolder);
                var destino = Path.Combine(TempDriversFolder, driver.InstaladorArchivo);

                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return null;

                    // Solo reescribir si el tamaño difiere (evita reescribir en cada clic)
                    bool necesitaEscribir = true;
                    if (File.Exists(destino))
                    {
                        var info = new FileInfo(destino);
                        necesitaEscribir = info.Length != stream.Length;
                        stream.Position = 0;
                    }

                    if (necesitaEscribir)
                    {
                        using (var file = File.Create(destino))
                            stream.CopyTo(file);
                    }
                }

                return destino;
            }
            catch
            {
                return null;
            }
        }

        // ── Instalación ───────────────────────────────────────────────────────
        /// <summary>
        /// Extrae el instalador embebido a %TEMP%, lo ejecuta y espera a que termine.
        /// Devuelve (exitCode, mensajeError). exitCode == 0 → éxito.
        /// </summary>
        public static async Task<(int ExitCode, string Error)> InstalarAsync(DriverInfo driver)
        {
            if (!driver.InstaladorDisponible)
                return (-1, "Este driver no tiene instalador bundleado.");

            // Extraer desde el recurso embebido en el exe
            var rutaInstalador = await Task.Run(() => ExtraerInstaladorATemporal(driver));

            if (string.IsNullOrEmpty(rutaInstalador))
                return (-1, $"No se pudo extraer el instalador embebido \"{driver.InstaladorArchivo}\" " +
                             "desde el ejecutable. Verifique que la aplicación no esté dañada.");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName        = rutaInstalador,
                    Arguments       = driver.InstaladorArgs,
                    UseShellExecute = true,   // necesario para triggear UAC
                    Verb            = "runas" // solicitar elevación
                };

                var proc = Process.Start(psi);
                if (proc == null)
                    return (-1, "No se pudo iniciar el proceso instalador.");

                // Esperar sin bloquear el hilo UI
                await Task.Run(() => proc.WaitForExit());
                return (proc.ExitCode, null);
            }
            catch (System.ComponentModel.Win32Exception ex)
                when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED (usuario rechazó UAC)
            {
                return (-2, "Instalación cancelada (se rechazó la elevación UAC).");
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        /// <summary>
        /// Devuelve true si hay al menos un driver bundleado que no está instalado.
        /// </summary>
        public static bool HayDriversFaltantesInstalables(List<DriverInfo> catalogo)
            => catalogo.Any(d => !d.EstaInstalado && d.InstaladorDisponible);

        /// <summary>Abre la URL de descarga en el navegador predeterminado.</summary>
        public static void AbrirUrlDescarga(string url)
        {
            try { Process.Start(url); } catch { }
        }
    }
}
