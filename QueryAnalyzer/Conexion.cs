using System;
using System.Data.Odbc;

namespace QueryAnalyzer
{
    [Serializable]
    public class Conexion
    {
        public string Nombre { get; set; }
        public TipoMotor Motor { get; set; }
        public string Servidor { get; set; }
        public string BaseDatos { get; set; }
        public string Usuario { get; set; }
        public string Contrasena { get; set; }

        public override string ToString()
        {
            return $"Nombre={Nombre}; Motor={Motor}; Servidor={Servidor}; BaseDatos={BaseDatos}; Usuario={Usuario}; Contraseña={Contrasena}";
        }
    }

    public enum TipoMotor
    {
        MS_SQL,
        DB2,
        POSTGRES,
        SQLite
    }
}
