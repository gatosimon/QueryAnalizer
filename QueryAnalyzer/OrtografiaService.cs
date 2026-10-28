using System;
using System.Collections.Generic;
using System.Data.Odbc;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CapiDL;

namespace QueryAnalyzer
{
    /// <summary>
    /// Busca posibles errores ortográficos en los datos de las columnas de texto. Solo lee: nunca modifica la base.
    /// </summary>
    public class OrtografiaService
    {
        private const int MaxHallazgosPorColumna = 1000;
        private const int LargoEjemplo = 90;

        private static readonly HashSet<string> TiposTexto = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "char", "varchar", "nchar", "nvarchar", "text", "ntext", "character", "character varying",
            "clob", "long varchar", "graphic", "vargraphic", "long vargraphic", "dbclob", "bpchar"
        };

        private static readonly HashSet<string> TiposLob = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "text", "ntext", "clob", "dbclob", "long varchar", "long vargraphic"
        };

        // Nombres de columnas que casi seguro no llevan prosa: claves, contraseñas, documentos, códigos, rutas
        private static readonly Regex NombreTecnico = new Regex(
            "pass|clave|contras|hash|token|mail|url|cuit|cuil|dni|guid|uuid|^cod|^id$|^id_|_id$|^nro|^num|numero|" +
            "telefono|^tel|^cp$|patente|dominio|sigla|usuario|^user|^(alta|baja|mod)(usuario|user|fecha|hora)|^ip$|path|^ruta",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NombreDescriptivo = new Regex(
            "desc|obs|detalle|motivo|texto|coment|titulo|concepto|asunto|nota|leyenda|resumen|fundament|mensaje|" +
            "domicilio|direccion|razon|denominacion|referencia|aclaracion|memo|glosa",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NombrePersonalOLugar = new Regex(
            "nombre|apellido|calle|barrio|localidad|ciudad|provincia|empresa|comercio",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly Conexion _conn;
        private readonly string _connStr;

        public OrtografiaService(Conexion conn, string connStr)
        {
            _conn = conn;
            _connStr = connStr;
        }

        // ── Tablas y columnas ─────────────────────────────────────────────

        public List<TablaOrtografia> GetTablas()
        {
            return new LimpiadorBDService(_conn, _connStr).GetTablas()
                .Select(t => new TablaOrtografia { Schema = t.Schema, Nombre = t.Nombre })
                .OrderBy(t => t.NombreCompleto, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Columnas de tipo texto de la tabla, con el puntaje de "probablemente descriptiva".</summary>
        public List<ColumnaOrtografia> GetColumnasTexto(TablaOrtografia tabla)
        {
            var resultado = new List<ColumnaOrtografia>();
            var db = new DataBase(_connStr);
            try
            {
                string sql;
                switch (_conn.Motor)
                {
                    case TipoMotor.MS_SQL:
                        sql = "SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM INFORMATION_SCHEMA.COLUMNS " +
                              $"WHERE TABLE_SCHEMA='{Esc(tabla.Schema)}' AND TABLE_NAME='{Esc(tabla.Nombre)}' ORDER BY ORDINAL_POSITION";
                        break;
                    case TipoMotor.POSTGRES:
                        sql = "SELECT column_name, data_type, character_maximum_length FROM information_schema.columns " +
                              $"WHERE table_schema='{Esc(tabla.Schema)}' AND table_name='{Esc(tabla.Nombre)}' ORDER BY ordinal_position";
                        break;
                    case TipoMotor.DB2:
                        sql = "SELECT COLNAME, TYPENAME, LENGTH FROM SYSCAT.COLUMNS " +
                              $"WHERE TABSCHEMA='{Esc(tabla.Schema)}' AND TABNAME='{Esc(tabla.Nombre)}' ORDER BY COLNO";
                        break;
                    default: // SQLite
                        sql = $"PRAGMA table_info(\"{tabla.Nombre.Replace("\"", "\"\"")}\")";
                        break;
                }

                db.CommandText = sql;
                while (db.Read())
                {
                    string nombre, tipo;
                    int? largo = null;
                    if (_conn.Motor == TipoMotor.SQLite)
                    {
                        nombre = db.Reader["name"].ToString().Trim();
                        tipo = db.Reader["type"].ToString().Trim();
                        var m = Regex.Match(tipo, @"^(.*?)\s*\((\d+)");
                        if (m.Success) { tipo = m.Groups[1].Value; largo = int.Parse(m.Groups[2].Value); }
                    }
                    else
                    {
                        nombre = db.Reader[0].ToString().Trim();
                        tipo = db.IsDBNull(1) ? "" : db.Reader[1].ToString().Trim();
                        if (!db.IsDBNull(2)) largo = Convert.ToInt32(db.Reader[2]);
                    }

                    if (!EsTipoTexto(tipo)) continue;
                    var col = new ColumnaOrtografia
                    {
                        Schema = tabla.Schema,
                        Tabla = tabla.Nombre,
                        Nombre = nombre,
                        Tipo = tipo,
                        Largo = largo,
                        SinDistinct = NoAdmiteDistinct(tipo)
                    };
                    col.Puntaje = CalcularPuntaje(nombre, tipo, largo);
                    resultado.Add(col);
                }
            }
            finally { db.CloseConnection(); }
            return resultado;
        }

        // SQL Server rechaza DISTINCT sobre text/ntext y DB2 sobre los LOB; PostgreSQL y SQLite lo admiten siempre
        private bool NoAdmiteDistinct(string tipo)
        {
            switch (_conn.Motor)
            {
                case TipoMotor.MS_SQL: return tipo.Equals("text", StringComparison.OrdinalIgnoreCase) || tipo.Equals("ntext", StringComparison.OrdinalIgnoreCase);
                case TipoMotor.DB2:    return TiposLob.Contains(tipo);
                default:               return false;
            }
        }

        private bool EsTipoTexto(string tipo)
        {
            if (string.IsNullOrEmpty(tipo)) return false;
            if (_conn.Motor == TipoMotor.SQLite)
            {
                string t = tipo.ToLowerInvariant();
                return t.Contains("char") || t.Contains("text") || t.Contains("clob");
            }
            return TiposTexto.Contains(tipo);
        }

        internal static int CalcularPuntaje(string nombre, string tipo, int? largo)
        {
            if (NombreTecnico.IsMatch(nombre)) return 5;
            if (NombreDescriptivo.IsMatch(nombre)) return 75;
            if (NombrePersonalOLugar.IsMatch(nombre)) return 45;
            if (TiposLob.Contains(tipo) || !largo.HasValue || largo.Value < 0 || largo.Value >= 100) return 55;
            if (largo.Value >= 40) return 45;
            if (largo.Value >= 15) return 30;
            return 10;
        }

        // ── Análisis ──────────────────────────────────────────────────────

        private class Acumulado
        {
            public int Valores;
            public string Ejemplo;
        }

        /// <summary>
        /// Lee los valores distintos de la columna (en streaming, hasta el tope) y revisa cada palabra distinta una vez.
        /// Cancelar el token corta también la consulta en curso en el servidor.
        /// </summary>
        public ResultadoColumnaOrtografia AnalizarColumna(ColumnaOrtografia col, OpcionesOrtografia op, CancellationToken ct)
        {
            var corrector = CorrectorOrtografico.Instancia;
            var palabras = new Dictionary<string, Acumulado>(StringComparer.Ordinal);
            int mojibake = 0;
            string mojibakeFragmento = null, mojibakeEjemplo = null;
            var res = new ResultadoColumnaOrtografia();

            int timeoutSeg = ConfigManager.ObtenerConfiguracion().TimeoutConsultaSegundos;
            var db = new DataBase(_connStr);
            CancellationTokenRegistration registro = default(CancellationTokenRegistration);
            try
            {
                if (_conn.Motor == TipoMotor.POSTGRES)
                {
                    db.CommandText = $"SET statement_timeout = {timeoutSeg * 1000}";
                    db.NonQuery();
                }

                db.CommandText = ConsultaValores(col, op.MaxValoresPorColumna);
                db.CommandTimeout = timeoutSeg;
                var cmd = db.Command;
                registro = ct.Register(() => { try { cmd.Cancel(); } catch { } });

                try
                {
                    var vistas = new HashSet<string>(StringComparer.Ordinal);
                    while (db.Read())
                    {
                        if ((res.ValoresLeidos & 255) == 0) ct.ThrowIfCancellationRequested();
                        if (db.IsDBNull(0)) continue;
                        res.ValoresLeidos++;

                        string valor = ComoTexto(db.GetValue(0));
                        if (string.IsNullOrWhiteSpace(valor)) continue;

                        string fragmento;
                        if (CorrectorOrtografico.TieneCodificacionRota(valor, out fragmento))
                        {
                            mojibake++;
                            if (mojibakeFragmento == null) { mojibakeFragmento = fragmento; mojibakeEjemplo = Recortar(valor, fragmento); }
                            continue;
                        }

                        vistas.Clear();
                        foreach (string token in CorrectorOrtografico.Palabras(valor))
                        {
                            if (op.IgnorarSiglas && CorrectorOrtografico.EsSigla(token)) continue;
                            if (!vistas.Add(token)) continue;
                            Acumulado a;
                            if (!palabras.TryGetValue(token, out a))
                            {
                                a = new Acumulado { Ejemplo = Recortar(valor, token) };
                                palabras[token] = a;
                            }
                            a.Valores++;
                        }
                    }
                    res.Truncado = res.ValoresLeidos >= op.MaxValoresPorColumna;
                }
                catch when (ct.IsCancellationRequested)
                {
                    // El comando cancelado hace fallar a Read(): se informa como cancelación, no como error
                    throw new OperationCanceledException(ct);
                }
            }
            finally
            {
                registro.Dispose();
                db.CloseConnection();
            }

            if (mojibake > 0)
                res.Hallazgos.Add(Nuevo(col, mojibakeFragmento, CategoriaOrtografia.Codificacion,
                    "Texto guardado con otra codificación (por ejemplo \"Ã³\" en lugar de \"ó\")", mojibake, mojibakeEjemplo));

            var halladas = new List<HallazgoOrtografico>();
            foreach (var kv in palabras)
            {
                ct.ThrowIfCancellationRequested();
                if (OrtografiaManager.EsPalabraIgnorada(kv.Key)) continue;
                var r = corrector.Revisar(kv.Key);
                if (r.Correcta) continue;
                if (!op.TildeEsError && r.Categoria == CategoriaOrtografia.FaltaTilde) continue;
                halladas.Add(Nuevo(col, kv.Key, r.Categoria, r.Sugerencias, kv.Value.Valores, kv.Value.Ejemplo));
            }
            res.Hallazgos.AddRange(halladas.OrderByDescending(h => h.Valores).ThenBy(h => h.Palabra).Take(MaxHallazgosPorColumna));
            return res;
        }

        private static HallazgoOrtografico Nuevo(ColumnaOrtografia col, string palabra, string categoria,
            string sugerencias, int valores, string ejemplo)
        {
            return new HallazgoOrtografico
            {
                Categoria = categoria, Schema = col.Schema, Tabla = col.Tabla, Columna = col.Nombre,
                Palabra = palabra, Sugerencias = sugerencias, Valores = valores, Ejemplo = ejemplo
            };
        }

        /// <summary>Fragmento del valor centrado en la palabra, para reconocer de dónde salió.</summary>
        private static string Recortar(string valor, string buscado)
        {
            valor = Regex.Replace(valor, @"\s+", " ").Trim();
            if (valor.Length <= LargoEjemplo) return valor;
            int pos = Math.Max(0, valor.IndexOf(buscado, StringComparison.Ordinal));
            int ini = Math.Max(0, Math.Min(pos, valor.Length - 1) - LargoEjemplo / 2);
            int largo = Math.Min(LargoEjemplo, valor.Length - ini);
            return (ini > 0 ? "…" : "") + valor.Substring(ini, largo) + (ini + largo < valor.Length ? "…" : "");
        }

        private static string ComoTexto(object v)
        {
            var bytes = v as byte[];
            if (bytes == null) return v.ToString();

            // DB2 guarda texto como FOR BIT DATA: ANSI (Windows-1252) con relleno de blancos o ceros
            int largo = bytes.Length;
            while (largo > 0 && (bytes[largo - 1] == 0x20 || bytes[largo - 1] == 0x00)) largo--;
            for (int i = 0; i < largo; i++)
            {
                byte b = bytes[i];
                bool imprimible = (b >= 0x20 && b != 0x7F) || b == 0x09 || b == 0x0A || b == 0x0D;
                if (!imprimible) return null;   // binario de verdad
            }
            Encoding ansi;
            try { ansi = Encoding.GetEncoding(1252); } catch { ansi = Encoding.GetEncoding(28591); }
            return ansi.GetString(bytes, 0, largo);
        }

        // ── SQL ───────────────────────────────────────────────────────────

        private string ConsultaValores(ColumnaOrtografia col, int max)
        {
            string c = QuoteId(col.Nombre);
            string t = QuoteTabla(col.Schema, col.Tabla);
            string distinct = col.SinDistinct ? "" : "DISTINCT ";
            switch (_conn.Motor)
            {
                case TipoMotor.MS_SQL:
                    return $"SELECT {distinct}TOP {max} {c} FROM {t} WHERE {c} IS NOT NULL";
                case TipoMotor.DB2:
                    return $"SELECT {distinct}{c} FROM {t} WHERE {c} IS NOT NULL FETCH FIRST {max} ROWS ONLY";
                default:
                    return $"SELECT {distinct}{c} FROM {t} WHERE {c} IS NOT NULL LIMIT {max}";
            }
        }

        /// <summary>Consulta para ver en el editor las filas que contienen el hallazgo.</summary>
        public string ConsultaVerFilas(HallazgoOrtografico h)
        {
            string c = QuoteId(h.Columna);
            string t = QuoteTabla(h.Schema, h.Tabla);
            string patron = Esc(h.Palabra);
            string like = _conn.Motor == TipoMotor.POSTGRES ? "ILIKE" : "LIKE";
            string donde = $"WHERE {c} {like} '%{patron}%'";
            switch (_conn.Motor)
            {
                case TipoMotor.MS_SQL: return $"SELECT TOP 100 *\r\nFROM {t}\r\n{donde};";
                case TipoMotor.DB2:    return $"SELECT *\r\nFROM {t}\r\n{donde}\r\nFETCH FIRST 100 ROWS ONLY;";
                default:               return $"SELECT *\r\nFROM {t}\r\n{donde}\r\nLIMIT 100;";
            }
        }

        private string QuoteId(string nombre)
        {
            if (_conn.Motor == TipoMotor.MS_SQL) return "[" + nombre.Replace("]", "]]") + "]";
            return "\"" + nombre.Replace("\"", "\"\"") + "\"";
        }

        private string QuoteTabla(string schema, string tabla)
        {
            return string.IsNullOrEmpty(schema) ? QuoteId(tabla) : QuoteId(schema) + "." + QuoteId(tabla);
        }

        private static string Esc(string s) => (s ?? "").Replace("'", "''");
    }
}
