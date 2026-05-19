using System;

namespace QueryAnalyzer.Models
{
    [Serializable]
    public class ConsultaGuardada
    {
        public string Titulo { get; set; }
        public string Descripcion { get; set; }
        public string Consulta { get; set; }
        public string Usuario { get; set; }
        public DateTime Fecha { get; set; }
        /// <summary>Nombre de la conexión asociada (informativo, no vinculante).</summary>
        public string ConexionNombre { get; set; }
    }
}
