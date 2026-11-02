using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace QueryAnalyzer
{
    internal static class ScriptHelper
    {
        public static string GenerarScriptInsert(
            DataTable dt, string nombreTabla, bool conDelete,
            HashSet<string> columnasExcluidas = null,
            bool usarOverridingSystemValue = false,
            TipoMotor motor = TipoMotor.MS_SQL)
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
                var vals = string.Join(", ", colsFiltradas.Select(c => EscaparValorSql(row[c], motor)));
                sb.AppendLine($"INSERT INTO {nombreTabla} ({cols}){ov} VALUES ({vals});");
            }

            return sb.ToString();
        }

        public static string EscaparValorSql(object valor, TipoMotor motor = TipoMotor.MS_SQL)
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
                return LiteralBinario((byte[])valor, motor);

            return "'" + valor.ToString().Replace("'", "''") + "'";
        }

        // ── Escritura en streaming (sin armar strings gigantes en memoria) ────────────

        private static readonly char[] HexChars = "0123456789ABCDEF".ToCharArray();

        /// <summary>
        /// Escribe una sentencia INSERT directamente en el <see cref="TextWriter"/>.
        /// El formato es idéntico al de <see cref="GenerarScriptInsert"/> pero los valores
        /// binarios se emiten por bloques, de modo que su tamaño no se duplica en memoria.
        /// </summary>
        /// <param name="columnas">Lista de columnas ya unida con ", ".</param>
        public static void EscribirInsert(
            TextWriter w, string nombreTabla, string columnas, object[] valores,
            bool usarOverridingSystemValue, TipoMotor motor)
        {
            w.Write("INSERT INTO ");
            w.Write(nombreTabla);
            w.Write(" (");
            w.Write(columnas);
            w.Write(")");
            if (usarOverridingSystemValue) w.Write(" OVERRIDING SYSTEM VALUE");
            w.Write(" VALUES (");
            for (int i = 0; i < valores.Length; i++)
            {
                if (i > 0) w.Write(", ");
                EscribirValor(w, valores[i], motor);
            }
            w.WriteLine(");");
        }

        public static void EscribirValor(TextWriter w, object valor, TipoMotor motor)
        {
            var bytes = valor as byte[];
            if (bytes == null) { w.Write(EscaparValorSql(valor, motor)); return; }

            switch (motor)
            {
                case TipoMotor.POSTGRES: w.Write("decode('"); break;
                case TipoMotor.DB2:
                case TipoMotor.SQLite:   w.Write("X'");       break;
                default:                 w.Write("0x");       break;
            }

            var buf = new char[8192];
            int p = 0;
            foreach (byte b in bytes)
            {
                buf[p++] = HexChars[b >> 4];
                buf[p++] = HexChars[b & 0x0F];
                if (p == buf.Length) { w.Write(buf, 0, p); p = 0; }
            }
            if (p > 0) w.Write(buf, 0, p);

            switch (motor)
            {
                case TipoMotor.POSTGRES: w.Write("','hex')"); break;
                case TipoMotor.DB2:
                case TipoMotor.SQLite:   w.Write("'");        break;
            }
        }

        /// <summary>
        /// Literal hexadecimal de un valor binario según el motor destino.
        /// Un byte[] vacío produce el literal vacío (0x en MSSQL), no NULL.
        /// </summary>
        private static string LiteralBinario(byte[] bytes, TipoMotor motor)
        {
            var hex = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                hex.Append(b.ToString("X2"));

            switch (motor)
            {
                case TipoMotor.POSTGRES: return $"decode('{hex}','hex')";
                case TipoMotor.DB2:
                case TipoMotor.SQLite:   return $"X'{hex}'";
                default:                 return "0x" + hex;
            }
        }
    }
}
