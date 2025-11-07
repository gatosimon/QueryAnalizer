using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace QueryAnalyzer
{
    [Serializable]
    public class Historial
    {
        private static readonly string ArchivoXml = "historial.xml";

        public Conexion conexion { get; set; }
        public string Consulta { get; set; }
        // Cada elemento es un string[]: [0]=Nombre, [1]=Tipo (string), [2]=Valor
        public List<string[]> Parametros { get; set; }

        // NUEVO: Fecha para ordenar (más reciente -> más viejo)
        public DateTime Fecha { get; set; }
    }
}