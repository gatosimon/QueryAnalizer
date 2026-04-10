using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace QueryAnalyzer
{
    /// <summary>
    /// Gestiona las conexiones ODBC: lectura/escritura y construcción de cadenas de conexión.
    /// La persistencia de conexiones fue migrada a ConfiguracionManager (config.xml).
    /// Esta clase actúa como fachada que delega en ConfiguracionManager para mantener
    /// la compatibilidad con el código existente.
    /// </summary>
    public static class ConexionesManager
    {
        /// <summary>Lista fija de bases de datos DB2 disponibles.</summary>
        public static string[] BasesDB2 = new string[]
        {
            "CONTABIL", "CONTAICD", "CONTAIMV", "CONTCBEL", "CONTIDS",
            "DOCUMENT", "GENERAL", "GIS", "HISTABM", "HISTORIC",
            "INFORMAT", "LICENCIA", "RRHH", "SISUS", "TRIBUTOS"
        };

        // ── Persistencia (delegada a ConfiguracionManager) ────────────────────

        /// <summary>
        /// Carga el diccionario de conexiones desde config.xml (vía ConfiguracionManager).
        /// Si existe el archivo legacy conexiones.xml, la migración ocurre automáticamente.
        /// </summary>
        public static Dictionary<string, Conexion> Cargar()
        {
            return ConfiguracionManager.CargarConexiones();
        }

        /// <summary>
        /// Guarda el diccionario de conexiones en config.xml (vía ConfiguracionManager).
        /// </summary>
        public static void Guardar(Dictionary<string, Conexion> conexiones)
        {
            ConfiguracionManager.GuardarConexiones(conexiones);
        }

        // ── Construcción de cadenas de conexión ───────────────────────────────

        public static string GetConnectionString(Conexion conexion)
        {
            return GetConnectionString(
                conexion.Motor,
                conexion.Servidor,
                conexion.Puerto,
                conexion.BaseDatos,
                conexion.Usuario,
                conexion.Contrasena,
                conexion.EsWeb);
        }

        public static string GetConnectionString(
            TipoMotor motor,
            string servidor,
            string puerto,
            string baseDatos,
            string usuario,
            string contraseña,
            bool esWeb)
        {
            string stringConnection = string.Empty;
            string driver = ObtenerNombreDriver(motor);

            switch (motor)
            {
                case TipoMotor.MS_SQL:
                    if (esWeb)
                    {
                        stringConnection = $@"Driver={{{driver}}};Server={servidor};Database={baseDatos};Uid={usuario};Pwd={contraseña};TrustServerCertificate=yes;";
                    }
                    else if (servidor.EndsWith("WEB"))
                    {
                        stringConnection = $@"Driver={{{driver}}};Server=SQL{servidor.Replace("WEB", string.Empty)}\{servidor};Database=;Uid={usuario};Pwd={contraseña};TrustServerCertificate=yes;";
                    }
                    else
                    {
                        stringConnection = $@"Driver={{{driver}}};Server=SQL{servidor}\{servidor};Database={baseDatos};Uid={usuario};Pwd={contraseña};TrustServerCertificate=yes;";
                    }
                    break;

                case TipoMotor.DB2:
                    stringConnection = $"Driver={{{driver}}};Database={baseDatos};Hostname={servidor};{(puerto.Trim().Length > 0 ? $"Port={puerto};" : string.Empty)}Protocol=TCPIP;Uid={usuario};Pwd={contraseña};";
                    break;

                case TipoMotor.POSTGRES:
                    stringConnection = $"Driver={{{driver}}};Server={servidor};{(puerto.Trim().Length > 0 ? $"Port={puerto};" : string.Empty)}Database={baseDatos};Uid={usuario};Pwd={contraseña};";
                    break;

                case TipoMotor.SQLite:
                    stringConnection = $"Driver={{{driver}}};Database={servidor};";
                    break;
            }

            return stringConnection;
        }

        /// <summary>
        /// Busca en el registro de Windows (vista 32 bits) el driver ODBC que
        /// corresponde al motor indicado y devuelve su nombre exacto.
        /// </summary>
        public static string ObtenerNombreDriver(TipoMotor motor)
        {
            string palabraClave = string.Empty;
            switch (motor)
            {
                case TipoMotor.MS_SQL:   palabraClave = "SQL Server";  break;
                case TipoMotor.DB2:      palabraClave = "DB2";         break;
                case TipoMotor.POSTGRES: palabraClave = "PostgreSQL";  break;
                case TipoMotor.SQLite:   palabraClave = "SQLite";      break;
            }

            List<string> drivers = new List<string>();

            // Los drivers ODBC de 32 bits se encuentran en esta ruta del registro
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var rk = baseKey.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers"))
            {
                if (rk != null)
                {
                    foreach (string name in rk.GetValueNames())
                        drivers.Add(name);
                }
            }

            // Buscamos el driver que coincida con la palabra clave del motor
            string driver = drivers
                .FirstOrDefault(d => d.IndexOf(palabraClave, StringComparison.OrdinalIgnoreCase) >= 0);

            if (string.IsNullOrEmpty(driver))
            {
                System.Windows.MessageBox.Show($"No se encontró un driver ODBC x86 para {palabraClave}");
                return string.Empty;
            }

            return driver.Trim();
        }
    }
}
