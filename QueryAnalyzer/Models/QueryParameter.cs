
using System.Data.Odbc;

namespace Models
{
    public class QueryParameter
    {
        public string Nombre { get; set; }
        public OdbcType Tipo { get; set; }
        public string Valor { get; set; }
    }
}
