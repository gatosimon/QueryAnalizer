using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace QueryAnalyzer
{
    /// <summary>
    /// Centraliza la lectura y escritura del archivo config.xml en AppData.
    /// Reemplaza el rol de ConexionesManager para la persistencia de conexiones,
    /// y agrega la gestión de la configuración de la aplicación.
    ///
    /// Ruta: %AppData%\QueryAnalyzer\config.xml
    ///
    /// Migración: si existe un conexiones.xml heredado en la misma carpeta,
    /// lo importa al nuevo config.xml y lo elimina.
    /// </summary>
    public static class ConfigManager
    {
        private static readonly string ConfigPath =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QueryAnalyzer", "config.xml");

        // Ruta antigua (para migración automática)
        private static readonly string LegacyConexionesPath =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QueryAnalyzer", "conexiones.xml");

        // Caché en memoria para evitar múltiples lecturas de disco
        private static QueryAnalyzerConfig _cache = null;

        // ── Carga ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Carga el config.xml. Si no existe lo crea con valores por defecto.
        /// Ejecuta la migración de conexiones.xml si lo encuentra.
        /// </summary>
        public static QueryAnalyzerConfig Cargar()
        {
            if (_cache != null) return _cache;

            // Migración: si existe el archivo legado, importar y borrar
            if (File.Exists(LegacyConexionesPath) && !File.Exists(ConfigPath))
            {
                _cache = MigrarDesdeConexionesXml();
                Guardar(_cache);
                try { File.Delete(LegacyConexionesPath); } catch { /* silencioso */ }
                return _cache;
            }

            if (!File.Exists(ConfigPath))
            {
                _cache = new QueryAnalyzerConfig();
                Guardar(_cache);
                return _cache;
            }

            try
            {
                using (var fs = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read))
                {
                    var ser = new XmlSerializer(typeof(QueryAnalyzerConfig));
                    _cache = (QueryAnalyzerConfig)ser.Deserialize(fs);
                    if (_cache == null) _cache = new QueryAnalyzerConfig();
                    if (_cache.Configuracion == null) _cache.Configuracion = new AppConfig();
                    if (_cache.Conexiones == null) _cache.Conexiones = new List<Conexion>();
                    return _cache;
                }
            }
            catch
            {
                _cache = new QueryAnalyzerConfig();
                return _cache;
            }
        }

        // ── Guardado ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Persiste el config.xml en disco e invalida el caché.
        /// </summary>
        public static void Guardar(QueryAnalyzerConfig config)
        {
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                using (var fs = new FileStream(ConfigPath, FileMode.Create, FileAccess.Write))
                {
                    var ser = new XmlSerializer(typeof(QueryAnalyzerConfig));
                    ser.Serialize(fs, config);
                }
                _cache = config;
            }
            catch (Exception ex)
            {
                throw new Exception("Error guardando config.xml: " + ex.Message, ex);
            }
        }

        /// <summary>Invalida el caché de memoria (útil al modificar el archivo externamente).</summary>
        public static void InvalidarCache() => _cache = null;

        // ── Helpers de acceso rápido ───────────────────────────────────────────────

        public static AppConfig ObtenerConfiguracion() => Cargar().Configuracion;

        public static Dictionary<string, Conexion> CargarConexiones()
        {
            var lista = Cargar().Conexiones ?? new List<Conexion>();
            return lista.ToDictionary(c => c.Nombre, StringComparer.OrdinalIgnoreCase);
        }

        public static void GuardarConexiones(Dictionary<string, Conexion> conexiones)
        {
            var cfg = Cargar();
            cfg.Conexiones = conexiones.Values.ToList();
            Guardar(cfg);
        }

        public static void GuardarConfiguracion(AppConfig config)
        {
            var cfg = Cargar();
            cfg.Configuracion = config;
            Guardar(cfg);
        }

        // ── Migración desde conexiones.xml legado ──────────────────────────────────

        private static QueryAnalyzerConfig MigrarDesdeConexionesXml()
        {
            var result = new QueryAnalyzerConfig();
            try
            {
                using (var fs = new FileStream(LegacyConexionesPath, FileMode.Open, FileAccess.Read))
                {
                    var ser = new XmlSerializer(typeof(List<Conexion>));
                    var lista = (List<Conexion>)ser.Deserialize(fs);
                    if (lista != null) result.Conexiones = lista;
                }
            }
            catch { /* Si falla la migración arranca con lista vacía */ }
            return result;
        }

        // ── Detección de driver ODBC (movida aquí desde ConexionesManager) ─────────

        public static string ObtenerNombreDriver(TipoMotor motor)
        {
            string palabraClave = string.Empty;
            switch (motor)
            {
                case TipoMotor.MS_SQL:    palabraClave = "SQL Server";  break;
                case TipoMotor.DB2:       palabraClave = "DB2";         break;
                case TipoMotor.POSTGRES:  palabraClave = "PostgreSQL";  break;
                case TipoMotor.SQLite:    palabraClave = "SQLite";      break;
            }

            var drivers = new List<string>();
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var rk = baseKey.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers"))
            {
                if (rk != null)
                    foreach (string name in rk.GetValueNames())
                        drivers.Add(name);
            }

            string driver = drivers
                .FirstOrDefault(d => d.IndexOf(palabraClave, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? string.Empty;

            if (driver.Length == 0)
                System.Windows.MessageBox.Show($"No se encontró un driver ODBC x86 para {palabraClave}");

            return driver.Trim();
        }
    }
}
