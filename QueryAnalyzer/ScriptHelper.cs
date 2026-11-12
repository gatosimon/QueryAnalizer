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

        // ── Transferencia entre motores distintos: literales según la columna destino ─────

        /// <summary>Entrecomilla un identificador con la sintaxis del motor (corchetes en SQL Server, comillas dobles en el resto).</summary>
        public static string QuoteId(string id, TipoMotor motor)
        {
            if (motor == TipoMotor.MS_SQL) return "[" + id.Replace("]", "]]") + "]";
            return "\"" + id.Replace("\"", "\"\"") + "\"";
        }

        private static bool EsNumericoClr(Type t)
        {
            return t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) ||
                   t == typeof(int)  || t == typeof(uint)  || t == typeof(long)  || t == typeof(ulong) ||
                   t == typeof(float) || t == typeof(double) || t == typeof(decimal);
        }

        private static bool EsTexto(CategoriaTipo c) { return c == CategoriaTipo.Texto || c == CategoriaTipo.TextoUnicode; }

        private static string Quote(string s) { return "'" + s.Replace("'", "''") + "'"; }

        /// <summary>Cantidad de decimales de fracción de segundo que admite la columna de B.</summary>
        private static int FraccionesDeSegundo(TipoMotor motor, ColumnaDestino col)
        {
            if (col.Categoria != CategoriaTipo.FechaHora && col.Categoria != CategoriaTipo.FechaHoraTz) return 3;
            switch (motor)
            {
                case TipoMotor.MS_SQL:
                    if (col.Tipo == "smalldatetime") return 0;
                    if (col.Tipo == "datetime") return 3;
                    return 7;                 // datetime2 / datetimeoffset
                case TipoMotor.POSTGRES:
                case TipoMotor.DB2:
                    return 6;
                default:
                    return 3;                 // SQLite guarda texto: se conserva el formato histórico
            }
        }

        private static string FormatoFechaHora(int fracciones)
        {
            return fracciones <= 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss." + new string('f', fracciones);
        }

        private static string LiteralFecha(DateTime d, TipoMotor motor, ColumnaDestino col)
        {
            if (col.Categoria == CategoriaTipo.Fecha)
                return "'" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";
            if (col.Categoria == CategoriaTipo.Hora)
                return "'" + d.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "'";
            return "'" + d.ToString(FormatoFechaHora(FraccionesDeSegundo(motor, col)), CultureInfo.InvariantCulture) + "'";
        }

        private static string LiteralDouble(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
                throw new InvalidOperationException("El valor " + d.ToString(CultureInfo.InvariantCulture) +
                    " (no finito) no se puede escribir como literal SQL en el motor destino.");
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string LiteralFloat(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f))
                throw new InvalidOperationException("El valor " + f.ToString(CultureInfo.InvariantCulture) +
                    " (no finito) no se puede escribir como literal SQL en el motor destino.");
            return f.ToString("R", CultureInfo.InvariantCulture);
        }

        private static bool? InterpretarBool(string s)
        {
            switch (s.Trim().ToLowerInvariant())
            {
                case "1": case "t": case "true": case "y": case "yes": case "s": case "si": case "sí": return true;
                case "0": case "f": case "false": case "n": case "no": return false;
                default: return null;
            }
        }

        private static bool TipoOrigenEsChar(string tipoA)
        {
            if (string.IsNullOrEmpty(tipoA)) return false;
            string t = tipoA.Trim().ToUpperInvariant();
            return t == "CHAR" || t == "CHARACTER" || t == "NCHAR" || t == "BPCHAR" || t == "GRAPHIC";
        }

        private static bool ColumnaEsChar(ColumnaDestino col)
        {
            string t = col.Tipo ?? "";
            return t == "char" || t == "character" || t == "nchar" || t == "bpchar" || t == "graphic";
        }

        /// <summary>
        /// Literal SQL del valor para escribirlo en la columna <paramref name="col"/> de B. Convierte según el tipo
        /// del destino (booleanos, fechas con su precisión, Unicode, CHAR de ancho fijo, etc.). Sin
        /// <paramref name="col"/> es idéntico a <see cref="EscaparValorSql(object, TipoMotor)"/>.
        /// </summary>
        public static string EscaparValorSql(object valor, TipoMotor motor, ColumnaDestino col, string tipoA = null)
        {
            if (col == null) return EscaparValorSql(valor, motor);
            if (valor == null || valor == DBNull.Value) return "NULL";

            Type t = valor.GetType();
            CategoriaTipo cat = col.Categoria;

            if (t == typeof(bool))
            {
                bool b = (bool)valor;
                if (cat == CategoriaTipo.Bool) return b ? "TRUE" : "FALSE";
                if (EsTexto(cat)) return b ? "'1'" : "'0'";
                return b ? "1" : "0";
            }

            if (EsNumericoClr(t))
            {
                if (cat == CategoriaTipo.Bool) return Convert.ToDecimal(valor, CultureInfo.InvariantCulture) == 0m ? "FALSE" : "TRUE";
                string num = t == typeof(double) ? LiteralDouble((double)valor)
                           : t == typeof(float)  ? LiteralFloat((float)valor)
                           : Convert.ToString(valor, CultureInfo.InvariantCulture);
                return EsTexto(cat) ? "'" + num + "'" : num;
            }

            if (t == typeof(DateTime)) return LiteralFecha((DateTime)valor, motor, col);

            if (t == typeof(DateTimeOffset))
            {
                var o = (DateTimeOffset)valor;
                if (cat == CategoriaTipo.FechaHoraTz && (motor == TipoMotor.MS_SQL || motor == TipoMotor.POSTGRES))
                    return "'" + o.ToString(FormatoFechaHora(FraccionesDeSegundo(motor, col)) + " zzz", CultureInfo.InvariantCulture) + "'";
                return LiteralFecha(o.DateTime, motor, col);
            }

            if (t == typeof(TimeSpan) && cat == CategoriaTipo.Hora)
            {
                var ts = (TimeSpan)valor;
                string hora = ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
                if (motor != TipoMotor.DB2 && ts.Ticks % TimeSpan.TicksPerSecond != 0)
                    hora += "." + (ts.Ticks % TimeSpan.TicksPerSecond).ToString("0000000", CultureInfo.InvariantCulture).Substring(0, 6);
                return "'" + hora + "'";
            }

            if (t == typeof(byte[])) return LiteralBinario((byte[])valor, motor);

            string s = valor.ToString();

            if (cat == CategoriaTipo.Bool)
            {
                bool? bb = InterpretarBool(s);
                if (bb.HasValue) return bb.Value ? "TRUE" : "FALSE";
            }

            if (EsTexto(cat))
            {
                // CHAR(n) rellena con espacios: al pasar a una columna de largo variable esos espacios sobran
                if (TipoOrigenEsChar(tipoA) && !ColumnaEsChar(col)) s = s.TrimEnd(' ');
                if (motor == TipoMotor.POSTGRES) s = s.Replace("\0", "");   // PostgreSQL no admite NUL en texto
                // Sin la N inicial SQL Server convierte a la página de códigos y se pierden los caracteres Unicode
                if (motor == TipoMotor.MS_SQL && cat == CategoriaTipo.TextoUnicode) return "N" + Quote(s);
            }

            return Quote(s);
        }

        /// <summary>Igual que el INSERT de <see cref="EscribirInsert(TextWriter, string, string, object[], bool, TipoMotor)"/> pero convirtiendo cada valor al tipo de la columna destino.</summary>
        public static void EscribirInsert(
            TextWriter w, string nombreTabla, string columnas, object[] valores,
            bool usarOverridingSystemValue, TipoMotor motor, ColumnaDestino[] destino, string[] tiposA)
        {
            if (destino == null) { EscribirInsert(w, nombreTabla, columnas, valores, usarOverridingSystemValue, motor); return; }
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
                var bytes = valores[i] as byte[];
                if (bytes != null) EscribirValor(w, bytes, motor);
                else w.Write(EscaparValorSql(valores[i], motor, destino[i], tiposA != null ? tiposA[i] : null));
            }
            w.WriteLine(");");
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
