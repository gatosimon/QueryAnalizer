using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace QueryAnalyzer
{
    public enum ModoConflicto { Bloquear, BajaEnCascada, Ignorar }

    // ── Condición de baja compuesta ───────────────────────────────────────

    public class CondicionBaja
    {
        public string Campo      { get; set; }
        // Operadores: "IS NOT NULL", "IS NULL", "IS NOT EMPTY", "IS EMPTY",
        //             "=", "<>", ">", ">=", "<", "<="
        public string Operador   { get; set; }
        public string Valor      { get; set; }   // para operadores de comparación; null si no aplica
        public string ValorSet   { get; set; }   // valor a SET en cascade baja (ej: "'SISTEMA'", "GETDATE()")
        public string Combinador { get; set; } = "AND";   // "AND" | "OR" — conecta con la siguiente condición

        public static readonly string[] OperadoresSinValor =
            { "IS NOT NULL", "IS NULL", "IS NOT EMPTY", "IS EMPTY" };

        // SQL de la expresión de esta condición (sin combinador)
        public string ToExprSql(string campoQuoted)
        {
            switch (Operador)
            {
                case "IS NOT NULL":  return $"{campoQuoted} IS NOT NULL";
                case "IS NULL":      return $"{campoQuoted} IS NULL";
                case "IS NOT EMPTY": return $"({campoQuoted} IS NOT NULL AND {campoQuoted} <> '')";
                case "IS EMPTY":     return $"({campoQuoted} IS NULL OR {campoQuoted} = '')";
                default:             return $"{campoQuoted} {Operador} {EscVal(Valor)}";
            }
        }

        private static string EscVal(string v)
        {
            if (string.IsNullOrEmpty(v)) return "NULL";
            if (int.TryParse(v, out _)) return v;
            if (v.Equals("true", System.StringComparison.OrdinalIgnoreCase) ||
                v.Equals("false", System.StringComparison.OrdinalIgnoreCase)) return v;
            return $"'{v.Replace("'", "''")}'";
        }
    }

    // ── Tabla ─────────────────────────────────────────────────────────────

    public class TablaConfigLimpiador
    {
        public string Schema  { get; set; }
        public string Nombre  { get; set; }
        public bool   Incluir { get; set; } = true;
        public List<CondicionBaja> CondicionesBaja { get; set; } = new List<CondicionBaja>();
        public string CampoPK    { get; set; }
        public bool   ReordenarIds { get; set; }

        public string NombreCompleto =>
            string.IsNullOrEmpty(Schema) ? Nombre : $"{Schema}.{Nombre}";

        public bool TieneCondiciones =>
            CondicionesBaja != null && CondicionesBaja.Count > 0;

        public string ResumenCondiciones =>
            !TieneCondiciones
                ? "(sin condición)"
                : string.Join(" ", CondicionesBaja.Select((c, i) =>
                    (i > 0 ? c.Combinador + " " : "") +
                    c.Campo + " " + c.Operador +
                    (!string.IsNullOrEmpty(c.Valor) ? " " + c.Valor : "")));
    }

    // ── FK / Análisis ─────────────────────────────────────────────────────

    public class FKRelacionLimpiador
    {
        public string TablaOrigen    { get; set; }
        public string ColumnaOrigen  { get; set; }
        public string TablaDestino   { get; set; }
        public string ColumnaDestino { get; set; }
    }

    public class TablaAnalisisLimpiador
    {
        public string NombreCompleto  { get; set; }
        public int    RegistrosBaja   { get; set; }
        public int    RegistrosActivos { get; set; }
        public bool   TieneConflictos { get; set; }
        public List<string> Conflictos { get; set; } = new List<string>();
        public string Estado { get; set; }  // "OK", "Conflicto", "Cascada", "Se omite", "Sin campo"
    }

    public class AnalisisResultLimpiador
    {
        public List<TablaAnalisisLimpiador> Tablas      { get; set; } = new List<TablaAnalisisLimpiador>();
        public List<string>                 Advertencias { get; set; } = new List<string>();
        public bool HayConflictosBloquantes { get; set; }
    }

    // ── Helpers SQL para condiciones ──────────────────────────────────────

    public static class CondicionBajaHelper
    {
        public static string ToCondicionSql(List<CondicionBaja> conds, System.Func<string, string> quoteCampo)
        {
            if (conds == null || conds.Count == 0) return "1=1";
            var sb = new StringBuilder();
            for (int i = 0; i < conds.Count; i++)
            {
                sb.Append(conds[i].ToExprSql(quoteCampo(conds[i].Campo)));
                if (i < conds.Count - 1)
                    sb.Append($" {conds[i].Combinador} ");
            }
            return sb.ToString();
        }

        public static string ToNegacionSql(List<CondicionBaja> conds, System.Func<string, string> quoteCampo)
            => $"NOT ({ToCondicionSql(conds, quoteCampo)})";
    }
}
