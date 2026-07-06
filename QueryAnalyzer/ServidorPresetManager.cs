using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using QueryAnalyzer.Models;

namespace QueryAnalyzer
{
    /// <summary>
    /// Carga y guarda la lista de servidores predefinidos desde
    /// %APPDATA%\QueryAnalyzer\servidores_preset.xml.
    /// La primera vez que no existe el archivo lo crea con ejemplos editables.
    /// </summary>
    public static class ServidorPresetManager
    {
        private static readonly string ARCHIVO =
            Path.Combine(App.AppDataFolder, "servidores_preset.xml");

        /// <summary>Carga los presets; si el archivo no existe los crea con ejemplos.</summary>
        public static List<ServidorPreset> Cargar()
        {
            try
            {
                if (!File.Exists(ARCHIVO))
                    return Seed();

                using (var fs = new FileStream(ARCHIVO, FileMode.Open, FileAccess.Read))
                {
                    var ser = new XmlSerializer(typeof(List<ServidorPreset>));
                    return (List<ServidorPreset>)ser.Deserialize(fs)
                           ?? Seed();
                }
            }
            catch
            {
                return Seed();
            }
        }

        /// <summary>Guarda la lista de presets en disco.</summary>
        public static void Guardar(List<ServidorPreset> lista)
        {
            try
            {
                using (var fs = new FileStream(ARCHIVO, FileMode.Create, FileAccess.Write))
                {
                    var ser = new XmlSerializer(typeof(List<ServidorPreset>));
                    ser.Serialize(fs, lista);
                }
            }
            catch { /* no interrumpir la app si falla el guardado */ }
        }

        // ── Datos de ejemplo para la primera ejecución ───────────────────────
        // Editá este método (o el XML resultante en AppData) para agregar tus
        // servidores reales con sus datos de acceso.
        private static List<ServidorPreset> Seed()
        {
            var lista = new List<ServidorPreset>
            {
                new ServidorPreset
                {
                    NombreVisible = "SQL Server DESARROLLO",
                    Servidor      = @"SQLDESARROLLO\DESARROLLO",
                    Puerto        = "50000",
                    EsWeb         = false,
                    Usuario       = "usuario",
                    Contrasena    = "ci?r0ba",
                    Motor         = TipoMotor.MS_SQL
                },
                new ServidorPreset
                {
                    NombreVisible = "SQL Server PRODUCCION",
                    Servidor      = @"SQLPRODUCCION\PRODUCCION",
                    Puerto        = "",
                    EsWeb         = false,
                    Usuario       = "usuario",
                    Contrasena    = "ci?r0ba",
                    Motor         = TipoMotor.MS_SQL
                },
                new ServidorPreset
                {
                    NombreVisible = "RafaelaGobAr DESA",
                    Servidor      = "133.123.108.29",
                    Puerto        = "",
                    EsWeb         = true,
                    Usuario       = "rafaelaweb",
                    Contrasena    = "gw2471n",
                    Motor         = TipoMotor.MS_SQL
                },
                new ServidorPreset
                {
                    NombreVisible = "Desarrollo DB2",
                    Servidor      = "133.123.120.120",
                    Puerto        = "50000",
                    EsWeb         = false,
                    Usuario       = "db2admin",
                    Contrasena    = "db2admin",
                    Motor         = TipoMotor.DB2
                },
                new ServidorPreset
                {
                    NombreVisible = "DB2 DRI",
                    Servidor      = "133.123.108.29",
                    Puerto        = "50000",
                    EsWeb         = false,
                    Usuario       = "db2admin",
                    Contrasena    = "db2admin",
                    Motor         = TipoMotor.DB2
                },
                new ServidorPreset
                {
                    NombreVisible = "Produccion DB2",
                    Servidor      = "SERVER01",
                    Puerto        = "50000",
                    EsWeb         = false,
                    Usuario       = "db2admin",
                    Contrasena    = "db2admin",
                    Motor         = TipoMotor.DB2
                },
                new ServidorPreset
                {
                    NombreVisible = "GIS",
                    Servidor      = "200.58.108.122",
                    Puerto        = "5432",
                    EsWeb         = false,
                    Usuario       = "magic",
                    Contrasena    = "DEriu7Dp45",
                    Motor         = TipoMotor.POSTGRES
                },
            };

            Guardar(lista);
            return lista;
        }
    }
}
