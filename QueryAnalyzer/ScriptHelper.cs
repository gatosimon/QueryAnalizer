using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;

namespace QueryAnalyzer
{
    internal static class ScriptHelper
    {
        public static string GenerarScriptInsert(
            DataTable dt, string nombreTabla, bool conDelete,
            HashSet<string> columnasExcluidas = null,
            bool usarOverridingSystemValue = false)
        {
            var sb = new StringBuilder();
            var colsFiltradas = dt.Columns.Cast<DataColumn>()
                .Where(c => columnasExcluidas == null || !columnasExcluidas.Contains(c.ColumnName))
                .ToList();

            var cols = string.Join(", ", colsFiltradas.Select(c => c.ColumnName));
            string ov = usarOverridingSystemValue ? " OVERRIDING SYSTEM VALUE" : "";

            if (conDelete)
            {
                sb.AppendLine($"DELETE FROM {nombreTabla};");
                sb.AppendLine();
            }

            foreach (DataRow row in dt.Rows)
            {
                var vals = string.Join(", ", colsFiltradas.Select(c => EscaparValorSql(row[c])));
                sb.AppendLine($"INSERT INTO {nombreTabla} ({cols}){ov} VALUES ({vals});");
            }

            return sb.ToString();
        }

        public static string EscaparValorSql(object valor)
        {
            if (valor == null || valor == DBNull.Value)
                return "NULL";

            Type t = valor.GetType();

            if (t == typeof(bool))
                return (bool)valor ? "1" : "0";

            if (t == typeof(byte)  || t == typeof(short)   || t == typeof(int) ||
                t == typeof(long)  || t == typeof(float)   || t == typeof(double) ||
                t == typeof(decimal))
                return Convert.ToString(valor, CultureInfo.InvariantCulture);

            if (t == typeof(DateTime))
                return $"'{((DateTime)valor).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}'";

            if (t == typeof(DateTimeOffset))
                return $"'{((DateTimeOffset)valor).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}'";

            if (t == typeof(byte[]))
                return "NULL";

            return "'" + valor.ToString().Replace("'", "''") + "'";
        }
    }
}
