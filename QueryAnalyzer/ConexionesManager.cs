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
    }
}
