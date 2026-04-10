using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace QueryAnalyzer
{
    public static class ConexionesManager
    {
        public static string[] BasesDB2 = new string[] { "CONTABIL", "CONTAICD", "CONTAIMV", "CONTCBEL", "CONTIDS", "DOCUMENT", "GENERAL", "GIS", "HISTABM", "HISTORIC", "INFORMAT", "LICENCIA", "RRHH", "SISUS", "TRIBUTOS" };

        // La persistencia fue migrada a config.xml (ConfigManager).
        // Estos métodos delegan en ConfigManager para mantener compatibilidad
        // con el resto del código sin necesidad de modificarlo.

        public static Dictionary<string, Conexion> Cargar()
        {
            return ConfigManager.CargarConexiones();
        }

        public static void Guardar(Dictionary<string, Conexion> conexiones)
        {
            ConfigManager.GuardarConexiones(conexiones);
        }

        public static string GetConnectionString(Conexion conexion)
        {
            return GetConnectionString(conexion.Motor, conexion.Servidor, conexion.Puerto, conexion.BaseDatos, conexion.Usuario, conexion.Contrasena, conexion.EsWeb);
        }

        public static string GetConnectionString(TipoMotor motor, string servidor, string puerto, string baseDatos, string usuario, string contraseña, bool esWeb)
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
                    stringConnection = $"Driver={{{driver}}};Database={servidor};"; //"Data Source={conexionActual.Servidor};Version=3;";
                    break;
                default:
                    break;
            }
            return stringConnection;
        }
        
        public static string ObtenerNombreDriver(TipoMotor motor)
        {
            string palabraClave = string.Empty;
            switch (motor)
            {
                case TipoMotor.MS_SQL:
                    palabraClave = "SQL Server";
                    break;
                case TipoMotor.DB2:
                    palabraClave = "DB2";
                    break;
                case TipoMotor.POSTGRES:
                    palabraClave = "PostgreSQL";
                    break;
                case TipoMotor.SQLite:
                    palabraClave = "SQLite";
                    break;
                default:
                    palabraClave = string.Empty;
                    break;
            }
            List<string> drivers = new List<string>();

            // Los drivers de ODBC se encuentran en esta ruta del registro
            string registroPath = @"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers";

            // Forzamos la apertura de la vista de 32 bits (RegistryView.Registry32)
            using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            {
                using (var rk = baseKey.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers"))
                {
                    if (rk != null)
                    {
                        foreach (string name in rk.GetValueNames())
                        {
                            drivers.Add(name);
                        }
                    }
                }
            }
            // Buscamos el que coincida con tu base de datos (ej: "PostgreSQL" o "IBM DB2")
            string driver = drivers.FirstOrDefault(d => d.IndexOf(palabraClave, StringComparison.OrdinalIgnoreCase) >= 0).Trim();
            if (driver.Length == 0)
            {
                System.Windows.MessageBox.Show($"No se encontró un driver ODBC x86 para {driver}");
            }
            return driver;
        }
    }
}
