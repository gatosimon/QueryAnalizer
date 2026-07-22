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
            if (conexion != null && !string.IsNullOrWhiteSpace(conexion.ConnectionStringCustom))
                return NormalizarConnectionString(conexion.ConnectionStringCustom);

            return GetConnectionString(conexion.Motor, conexion.Servidor, conexion.Puerto, conexion.BaseDatos, conexion.Usuario, conexion.Contrasena, conexion.EsWeb);
        }

        /// <summary>
        /// Si el string es una URI postgresql://user:pass@host:port/db la convierte a ODBC.
        /// Cualquier otro formato lo devuelve sin modificar.
        /// </summary>
        public static string NormalizarConnectionString(string connStr)
        {
            if (string.IsNullOrWhiteSpace(connStr)) return connStr;

            if (connStr.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
                connStr.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
                return ConvertirUriPostgresAOdbc(connStr);

            return connStr;
        }

        private static string ConvertirUriPostgresAOdbc(string uri)
        {
            try
            {
                var u        = new Uri(uri);
                string host  = u.Host;
                int    port  = u.Port > 0 ? u.Port : 5432;
                string db    = u.AbsolutePath.TrimStart('/');
                string user  = string.Empty;
                string pass  = string.Empty;

                if (!string.IsNullOrEmpty(u.UserInfo))
                {
                    int idx = u.UserInfo.IndexOf(':');
                    if (idx >= 0)
                    {
                        user = Uri.UnescapeDataString(u.UserInfo.Substring(0, idx));
                        pass = Uri.UnescapeDataString(u.UserInfo.Substring(idx + 1));
                    }
                    else
                    {
                        user = Uri.UnescapeDataString(u.UserInfo);
                    }
                }

                // Leer sslmode del query string si viene explícito
                string sslMode = "prefer";
                foreach (var param in u.Query.TrimStart('?').Split('&'))
                {
                    var kv = param.Split('=');
                    if (kv.Length == 2 && kv[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase))
                    { sslMode = kv[1]; break; }
                }

                // Hosts remotos siempre requieren SSL (Railway, Supabase, etc.)
                bool esLocal = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                            || host == "127.0.0.1" || host == "::1";
                if (!esLocal && sslMode == "prefer")
                    sslMode = "require";

                string driver = ObtenerNombreDriver(TipoMotor.POSTGRES);
                return $"Driver={{{driver}}};Server={host};Port={port};Database={db};Uid={user};Pwd={pass};SSLmode={sslMode};";
            }
            catch
            {
                return uri;
            }
        }

        /// <summary>
        /// Reemplaza la base de datos en un connection string ODBC ya armado.
        /// </summary>
        public static string CambiarBaseDatos(string connStr, string nuevaBase, TipoMotor motor)
        {
            if (string.IsNullOrWhiteSpace(connStr) || string.IsNullOrWhiteSpace(nuevaBase))
                return connStr;

            if (motor == TipoMotor.SQLite)
                return connStr; // SQLite: el server path es la base, no se cambia desde aquí

            // Reemplaza Database=xxx; o DBName=xxx;
            var patrones = new[] { "Database=", "DBName=" };
            foreach (string patron in patrones)
            {
                int idx = connStr.IndexOf(patron, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    int fin = connStr.IndexOf(';', idx + patron.Length);
                    string antes  = connStr.Substring(0, idx);
                    string despues = fin >= 0 ? connStr.Substring(fin) : ";";
                    return antes + patron + nuevaBase + despues;
                }
            }

            // Si no encontró ninguno, agrega al final
            return connStr.TrimEnd(';') + ";Database=" + nuevaBase + ";";
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
                        //stringConnection = $@"Driver={{{driver}}};Server=SQL{servidor}\{servidor};Database={baseDatos};Uid={usuario};Pwd={contraseña};TrustServerCertificate=yes;";
                        stringConnection = $@"Driver={{{driver}}};Server={servidor};Database={baseDatos};Uid={usuario};Pwd={contraseña};TrustServerCertificate=yes;";
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
