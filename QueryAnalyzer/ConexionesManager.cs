using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace QueryAnalyzer
{
    public static class ConexionesManager
    {
        public static string[] BasesDB2 = new string[] { "CONTABIL", "CONTAICD", "CONTAIMV", "CONTCBEL", "CONTIDS", "DOCUMENT", "GENERAL", "GIS", "HISTABM", "HISTORIC", "INFORMAT", "LICENCIA", "RRHH", "SISUS", "TRIBUTOS" };

        private static readonly string ArchivoXml = "conexiones.xml";

        public static Dictionary<string, Conexion> Cargar()
        {
            if (!File.Exists(ArchivoXml))
                return new Dictionary<string, Conexion>();

            try
            {
                using (var fs = new FileStream(ArchivoXml, FileMode.Open))
                {
                    var serializer = new XmlSerializer(typeof(List<Conexion>));
                    var lista = (List<Conexion>)serializer.Deserialize(fs);
                    var dict = new Dictionary<string, Conexion>();
                    foreach (var c in lista)
                        dict[c.Nombre] = c;
                    return dict;
                }
            }
            catch
            {
                return new Dictionary<string, Conexion>();
            }
        }

        public static void Guardar(Dictionary<string, Conexion> conexiones)
        {
            try
            {
                var lista = new List<Conexion>(conexiones.Values);
                using (var fs = new FileStream(ArchivoXml, FileMode.Create))
                {
                    var serializer = new XmlSerializer(typeof(List<Conexion>));
                    serializer.Serialize(fs, lista);
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Error guardando conexiones: " + ex.Message);
            }
        }

        public static string GetConnectionString(Conexion conexion)
        {
            return GetConnectionString(conexion.Motor, conexion.Servidor, conexion.BaseDatos, conexion.Usuario, conexion.Contrasena);
        }

        public static string GetConnectionString(TipoMotor motor, string servidor, string baseDatos, string usuario, string contraseña)
        {
            string stringConnection = string.Empty;
            switch (motor)
            {
                case TipoMotor.MS_SQL:
                    stringConnection = $@"Driver={{ODBC Driver 17 for SQL Server}};Server=SQL{servidor}\{servidor};Database={baseDatos};Uid={usuario};Pwd={contraseña};TrustServerCertificate=yes;";
                    break;
                case TipoMotor.DB2:
                    stringConnection = $"Driver={{IBM DB2 ODBC DRIVER}};Database={baseDatos};Hostname={servidor};Port=50000; Protocol=TCPIP;Uid={usuario};Pwd={contraseña};";
                    break;
                case TipoMotor.POSTGRES:
                    stringConnection = $"Driver={{PostgreSQL Unicode}};Server={servidor};Port=5432;Database={baseDatos};Uid={usuario};Pwd={contraseña};";
                    break;
                case TipoMotor.SQLite:
                    stringConnection = $"Driver={{SQLite3 ODBC Driver}};Database={servidor};"; //"Data Source={conexionActual.Servidor};Version=3;";
                    break;
                default:
                    break;
            }
            return stringConnection;
        }
    }
}
