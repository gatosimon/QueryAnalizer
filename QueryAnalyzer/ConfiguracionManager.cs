using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace QueryAnalyzer
{
    /// <summary>
    /// Configuración general de la aplicación. Se serializa a config.xml en AppData.
    /// Centraliza preferencias del usuario y las conexiones guardadas.
    /// Reemplaza el antiguo conexiones.xml (se migra automáticamente si existe).
    /// </summary>
    [Serializable]
    [XmlRoot("Configuracion")]
    public class Configuracion
    {
        /// <summary>Habilita/deshabilita el IntelliSense en el editor de consultas. Default: true.</summary>
        [XmlElement("IntelliSenseHabilitado")]
        public bool IntelliSenseHabilitado { get; set; }

        /// <summary>Carga automáticamente la última consulta del historial al cambiar de conexión. Default: true.</summary>
        [XmlElement("CargarUltimaConsultaAlConectar")]
        public bool CargarUltimaConsultaAlConectar { get; set; }

        /// <summary>Indica si se usa el tema oscuro. Default: false (tema claro).</summary>
        [XmlElement("ModoOscuro")]
        public bool ModoOscuro { get; set; }

        /// <summary>Lista de conexiones guardadas (migradas desde conexiones.xml).</summary>
        [XmlArray("Conexiones")]
        [XmlArrayItem("Conexion")]
        public List<Conexion> Conexiones { get; set; }

        // ── Constructor: valores por defecto ─────────────────────────────────
        public Configuracion()
        {
            IntelliSenseHabilitado       = true;
            CargarUltimaConsultaAlConectar = true;
            ModoOscuro                   = false;
            Conexiones                   = new List<Conexion>();
        }
    }

    /// <summary>
    /// Manager único para config.xml.
    /// Gestiona preferencias + conexiones en un solo archivo.
    /// Si existe conexiones.xml (legacy), lo migra automáticamente y lo elimina.
    /// </summary>
    public static class ConfiguracionManager
    {
        private static readonly string ArchivoConfig =
            Path.Combine(App.AppDataFolder, "config.xml");

        // Ruta del archivo legacy que se reemplaza por config.xml
        private static readonly string ArchivoConexionesLegacy =
            Path.Combine(App.AppDataFolder, "conexiones.xml");

        // ── API principal ─────────────────────────────────────────────────────

        /// <summary>
        /// Carga la configuración desde config.xml.
        /// Si existe conexiones.xml (legacy) y aún no existe config.xml, migra automáticamente
        /// el contenido y elimina el archivo antiguo para evitar redundancia.
        /// </summary>
        public static Configuracion Cargar()
        {
            // Migración transparente: conexiones.xml → config.xml
            if (File.Exists(ArchivoConexionesLegacy) && !File.Exists(ArchivoConfig))
            {
                try { MigrarConexionesLegacy(); }
                catch { /* No bloquear el arranque si la migración falla */ }
            }

            if (!File.Exists(ArchivoConfig))
                return new Configuracion();

            try
            {
                using (var fs = new FileStream(ArchivoConfig, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var serializer = new XmlSerializer(typeof(Configuracion));
                    var cfg = (Configuracion)serializer.Deserialize(fs);
                    return cfg ?? new Configuracion();
                }
            }
            catch
            {
                return new Configuracion();
            }
        }

        /// <summary>
        /// Guarda la configuración completa en config.xml.
        /// </summary>
        public static void Guardar(Configuracion config)
        {
            if (config == null) throw new ArgumentNullException("config");

            string dir = Path.GetDirectoryName(ArchivoConfig);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            try
            {
                using (var fs = new FileStream(ArchivoConfig, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var serializer = new XmlSerializer(typeof(Configuracion));
                    serializer.Serialize(fs, config);
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error guardando configuración: " + ex.Message);
            }
        }

        // ── Helpers para conexiones (compatibilidad con ConexionesManager) ────

        /// <summary>
        /// Obtiene el diccionario de conexiones de la configuración activa.
        /// </summary>
        public static Dictionary<string, Conexion> CargarConexiones()
        {
            var cfg = Cargar();
            var dict = new Dictionary<string, Conexion>(StringComparer.OrdinalIgnoreCase);
            if (cfg.Conexiones != null)
            {
                foreach (var c in cfg.Conexiones)
                {
                    if (!string.IsNullOrEmpty(c.Nombre))
                        dict[c.Nombre] = c;
                }
            }
            return dict;
        }

        /// <summary>
        /// Persiste el diccionario de conexiones en config.xml, preservando el resto
        /// de la configuración (IntelliSense, tema, etc.).
        /// </summary>
        public static void GuardarConexiones(Dictionary<string, Conexion> conexiones)
        {
            var cfg = Cargar();
            cfg.Conexiones = (conexiones != null)
                ? conexiones.Values.ToList()
                : new List<Conexion>();
            Guardar(cfg);
        }

        // ── Migración legacy ──────────────────────────────────────────────────

        /// <summary>
        /// Lee el archivo conexiones.xml (formato antiguo con XmlSerializer de List&lt;Conexion&gt;),
        /// vuelca las conexiones en config.xml y elimina el archivo antiguo.
        /// </summary>
        private static void MigrarConexionesLegacy()
        {
            List<Conexion> conexiones = null;

            using (var fs = new FileStream(ArchivoConexionesLegacy, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var serializer = new XmlSerializer(typeof(List<Conexion>));
                conexiones = (List<Conexion>)serializer.Deserialize(fs);
            }

            var cfgNueva = new Configuracion
            {
                Conexiones = conexiones ?? new List<Conexion>()
            };

            Guardar(cfgNueva);

            // Eliminar el archivo legado para evitar redundancia
            try { File.Delete(ArchivoConexionesLegacy); } catch { }
        }
    }
}
