using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        /// <summary>Incluido en la carpeta Drivers\ junto al exe.</summary>
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

        /// <summary>Nombre del archivo instalador dentro de la carpeta Drivers\.</summary>
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

        /// <summary>Ruta absoluta al instalador (calculada al leer la carpeta Drivers).</summary>
        public string InstaladorRuta { get; set; }

        public bool InstaladorDisponible =>
            Fuente == FuenteInstalar.Bundle &&
            !string.IsNullOrEmpty(InstaladorRuta) &&
            File.Exists(InstaladorRuta);

        public bool PuedaDescargar =>
            Fuente == FuenteInstalar.DescargaExterna &&
            !string.IsNullOrEmpty(UrlDescarga);
    }

    // ── Manager principal ────────────────────────────────────────────────────────
    public static class OdbcDriverManager
    {
        // Carpeta de instaladores junto al ejecutable
        private static readonly string DriversFolder =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Drivers");

        // ── Catálogo de drivers soportados ────────────────────────────────────
        public static List<DriverInfo> ObtenerCatalogo()
        {
            var lista = new List<DriverInfo>
            {
                new DriverInfo
                {
                    Nombre        = "SQLite ODBC Driver",
                    Descripcion   = "Conectividad ODBC para bases de datos SQLite (.db, .sqlite)",
                    NombresOdbc   = new[] { "SQLite3 ODBC Driver", "SQLite ODBC Driver", "SQLite ODBC (UTF-8) Driver" },
                    Fuente        = FuenteInstalar.Bundle,
                    InstaladorArchivo = "sqliteodbc.exe",
                    InstaladorArgs    = "/S",           // NSIS silent
                    Nota          = "Instalador incluido con la aplicación."
                },
                new DriverInfo
                {
                    Nombre        = "PostgreSQL ODBC Driver",
                    Descripcion   = "Conectividad ODBC para PostgreSQL (psqlodbc 16.x)",
                    NombresOdbc   = new[]
                    {
                        "PostgreSQL Unicode", "PostgreSQL ANSI",
                        "PostgreSQL Unicode(x86)", "PostgreSQL ANSI(x86)",
                        "PostgreSQL ODBC Driver(UNICODE)", "PostgreSQL ODBC Driver(ANSI)"
                    },
                    Fuente        = FuenteInstalar.Bundle,
                    InstaladorArchivo = "psqlodbc-setup.exe",
                    InstaladorArgs    = "/quiet",       // WiX bootstrapper silent
                    Nota          = "Instalador incluido con la aplicación."
                },
                new DriverInfo
                {
                    Nombre        = "SQL Server ODBC Driver",
                    Descripcion   = "Driver nativo de Windows para Microsoft SQL Server",
                    NombresOdbc   = new[]
                    {
                        "SQL Server",
                        "ODBC Driver 17 for SQL Server",
                        "ODBC Driver 18 for SQL Server"
                    },
                    Fuente        = FuenteInstalar.DescargaExterna,
                    UrlDescarga   = "https://learn.microsoft.com/sql/connect/odbc/download-odbc-driver-for-sql-server",
                    Nota          = "El driver \"SQL Server\" viene preinstalado en Windows. " +
                                   "Si necesita el moderno \"ODBC Driver 17/18 for SQL Server\" " +
                                   "puede descargarlo gratis desde Microsoft."
                },
                new DriverInfo
                {
                    Nombre        = "IBM DB2 ODBC Driver",
                    Descripcion   = "Driver ODBC del cliente IBM DB2 (DB2CLI.DLL)",
                    NombresOdbc   = new[] { "IBM DB2 ODBC DRIVER", "IBM DB2 ODBC DRIVER - DB2COPY1" },
                    Fuente        = FuenteInstalar.DescargaExterna,
                    UrlDescarga   = "https://www.ibm.com/support/pages/ibm-data-server-driver-odbc-and-cli",
                    Nota          = "El driver IBM DB2 viene incluido con el IBM Data Server Client. " +
                                   "Requiere cuenta IBM para descargarlo desde IBM Fix Central."
                }
            };

            // Resolver rutas de instaladores bundleados
            foreach (var d in lista.Where(x => x.Fuente == FuenteInstalar.Bundle))
                d.InstaladorRuta = Path.Combine(DriversFolder, d.InstaladorArchivo);

            return lista;
        }

        // ── Detección de drivers instalados ──────────────────────────────────
        /// <summary>
        /// Detecta qué drivers están instalados leyendo el registro ODBC de 32 bits.
        /// La app es x86, por lo que necesita los drivers de 32 bits
        /// (HKLM\SOFTWARE\WOW6432Node\ODBC\ODBCINST.INI en un proceso 64-bit,
        ///  o HKLM\SOFTWARE\ODBC\ODBCINST.INI desde un proceso 32-bit — .NET redirige automáticamente).
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

            // Intentar leer explícitamente desde WOW6432Node (cubre procesos de 32 y 64 bits)
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

            // Fallback: leer hive nativo del proceso (ya es 32-bit para esta app)
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

        // ── Instalación ───────────────────────────────────────────────────────
        /// <summary>
        /// Lanza el instalador del driver de forma asíncrona y espera a que termine.
        /// Devuelve (exitCode, mensajeError). exitCode == 0 → éxito.
        /// </summary>
        public static async Task<(int ExitCode, string Error)> InstalarAsync(DriverInfo driver)
        {
            if (!driver.InstaladorDisponible)
                return (-1, "El archivo instalador no está disponible.");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName        = driver.InstaladorRuta,
                    Arguments       = driver.InstaladorArgs,
                    UseShellExecute = true,   // necesario para trigger UAC
                    Verb            = "runas" // solicitar elevación
                };

                var proc = Process.Start(psi);
                if (proc == null)
                    return (-1, "No se pudo iniciar el instalador.");

                // Esperar sin bloquear el hilo UI
                await Task.Run(() => proc.WaitForExit());
                return (proc.ExitCode, null);
            }
            catch (System.ComponentModel.Win32Exception ex)
                when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED (usuario rechazó UAC)
            {
                return (-2, "El usuario canceló la elevación de privilegios (UAC).");
            }
            catch (Exception ex)
            {
                return (-1, ex.Message);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        /// <summary>
        /// Devuelve true si hay al menos un driver de los soportados que no está instalado
        /// Y que tiene un instalador disponible (bundleado).
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
