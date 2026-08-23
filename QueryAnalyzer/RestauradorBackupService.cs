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
    /// <summary>Fase del script de backup a la que pertenece una sentencia.</summary>
    public enum FaseBackup
    {
        /// <summary>FASE 1: deshabilitar FK. Corre FUERA de la transacción.</summary>
        PreDml,
        /// <summary>FASE 2: DELETE / INSERT. Corre DENTRO de la transacción.</summary>
        Dml,
        /// <summary>FASE 3: rehabilitar FK. Corre FUERA de la transacción y SIEMPRE.</summary>
        PostDml
    }

    public class SentenciaBackup
    {
        public string     Sql   { get; set; }
        public FaseBackup Fase  { get; set; }
        /// <summary>Línea del archivo donde termina la sentencia (para reportar errores).</summary>
        public int        Linea { get; set; }
    }

    /// <summary>Resultado de parsear un script .sql de backup.</summary>
    public class PlanRestauracion
    {
        public List<SentenciaBackup> Sentencias     { get; set; } = new List<SentenciaBackup>();
        /// <summary>Nombre de la conexión con la que se generó el backup.</summary>
        public string                ConexionOrigen { get; set; }
        /// <summary>Fecha de generación declarada en la cabecera.</summary>
        public string                Generado       { get; set; }
        public List<string>          Tablas         { get; set; } = new List<string>();
        /// <summary>El archivo trae la firma de un backup de QueryAnalyzer.</summary>
        public bool                  FirmaValida    { get; set; }
        public int                   TotalInserts   { get; set; }
        public int                   TotalDeletes   { get; set; }
    }

    public class ProgresoRestauracion
    {
        public int          Total           { get; set; }
        public int          Completadas     { get; set; }
        public string       UltimaSentencia { get; set; }
        public bool         Exitoso         { get; set; }
        public bool         Cancelado       { get; set; }
        public string       Error           { get; set; }
        public List<string> Advertencias    { get; } = new List<string>();
    }

    /// <summary>
    /// Lee y ejecuta los scripts .sql que genera "💾 Backup Esquema"
    /// (<see cref="FirmaScriptBackup"/>).
    ///
    /// El script tiene tres fases y el ejecutor las respeta:
    ///   FASE 1  deshabilitar FK  → fuera de transacción (son settings de sesión)
    ///   FASE 2  DELETE / INSERT  → dentro de una única transacción
    ///   FASE 3  rehabilitar FK   → fuera de transacción y siempre, aun tras rollback
    ///
    /// El BEGIN TRAN / COMMIT que trae el texto se descarta: la transacción la
    /// maneja este servicio para poder hacer rollback ante cualquier error.
    /// </summary>
    public static class RestauradorBackupService
    {
        /// <summary>Firma del encabezado que emite btnGenerarInserts_Click en MainWindow.</summary>
        public const string FirmaScriptBackup = "BACKUP DE ESQUEMA — script restaurable";

        /// <summary>Cada cuántas sentencias se reporta progreso (evita saturar el Dispatcher).</summary>
        private const int PasoReporte = 50;

        // ─────────────────────────────────────────────────────────────────────
        // Parseo
        // ─────────────────────────────────────────────────────────────────────

        private static readonly Regex RxConexion = new Regex(@"^--\s*Conexión\s*:\s*(.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex RxGenerado = new Regex(@"^--\s*Generado\s*:\s*(.+?)\s*$", RegexOptions.Compiled);
        private static readonly Regex RxTabla    = new Regex(@"^--\s*\d+\.\s+(\S+)\s+\(", RegexOptions.Compiled);
        private static readonly Regex RxControlTx = new Regex(
            @"^(BEGIN\s+TRAN(SACTION)?|COMMIT(\s+TRAN(SACTION)?)?|ROLLBACK(\s+TRAN(SACTION)?)?)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Convierte el texto del .sql en una lista de sentencias etiquetadas por fase.
        ///
        /// El parser es quote-aware porque ScriptHelper.EscaparValorSql no escapa
        /// saltos de línea: un VARCHAR con CRLF produce un INSERT multilínea, y un
        /// valor puede contener ';' o '--' que NO son separador ni comentario.
        /// </summary>
        public static PlanRestauracion ParsearBackup(string contenido)
        {
            var plan = new PlanRestauracion
            {
                FirmaValida = contenido != null && contenido.Contains(FirmaScriptBackup)
            };
            if (string.IsNullOrEmpty(contenido)) return plan;

            var fase             = FaseBackup.Dml;   // sin marcadores, todo es DML
            bool dentroDeLiteral = false;
            var bloque           = new StringBuilder();

            string[] lineas = contenido.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int nl = 0; nl < lineas.Length; nl++)
            {
                string linea = lineas[nl];

                if (!dentroDeLiteral)
                {
                    string t = linea.Trim();

                    // Línea de comentario completa: puede traer marcador de fase o cabecera.
                    if (t.StartsWith("--"))
                    {
                        InspeccionarComentario(t, plan, ref fase);
                        continue;
                    }
                    if (t.Length == 0) continue;

                    // Separador de lote (los scripts del Limpiador lo usan).
                    if (t.Equals("GO", StringComparison.OrdinalIgnoreCase))
                    {
                        VolcarSentencia(bloque, plan, fase, nl + 1);
                        continue;
                    }
                }

                int i = 0;
                while (i < linea.Length)
                {
                    char c = linea[i];

                    if (dentroDeLiteral)
                    {
                        bloque.Append(c);
                        if (c == '\'')
                        {
                            // '' es una comilla escapada, no cierra el literal
                            if (i + 1 < linea.Length && linea[i + 1] == '\'')
                            {
                                bloque.Append('\'');
                                i += 2;
                                continue;
                            }
                            dentroDeLiteral = false;
                        }
                        i++;
                        continue;
                    }

                    // Comentario de fin de línea
                    if (c == '-' && i + 1 < linea.Length && linea[i + 1] == '-') break;

                    if (c == '\'')
                    {
                        dentroDeLiteral = true;
                        bloque.Append(c);
                        i++;
                        continue;
                    }

                    if (c == ';')
                    {
                        VolcarSentencia(bloque, plan, fase, nl + 1);
                        i++;
                        continue;
                    }

                    bloque.Append(c);
                    i++;
                }

                if (dentroDeLiteral)
                {
                    // El literal sigue en la línea siguiente: preservar el salto textual.
                    bloque.Append('\n');
                }
                else
                {
                    string pendiente = bloque.ToString().Trim();
                    if (pendiente.Length == 0)
                    {
                        bloque.Clear();
                    }
                    else if (RxControlTx.IsMatch(pendiente))
                    {
                        // BEGIN TRAN / COMMIT sueltos (sin ';'): la transacción la
                        // maneja Restaurar(), no el script.
                        bloque.Clear();
                    }
                    else
                    {
                        bloque.Append('\n');   // sentencia multilínea, sigue abajo
                    }
                }
            }

            VolcarSentencia(bloque, plan, fase, lineas.Length);

            plan.TotalInserts = plan.Sentencias.Count(s => EmpiezaCon(s.Sql, "INSERT"));
            plan.TotalDeletes = plan.Sentencias.Count(s => EmpiezaCon(s.Sql, "DELETE"));
            return plan;
        }

        private static void InspeccionarComentario(string comentario, PlanRestauracion plan, ref FaseBackup fase)
        {
            if (comentario.IndexOf("FASE 1", StringComparison.OrdinalIgnoreCase) >= 0) { fase = FaseBackup.PreDml;  return; }
            if (comentario.IndexOf("FASE 2", StringComparison.OrdinalIgnoreCase) >= 0) { fase = FaseBackup.Dml;     return; }
            if (comentario.IndexOf("FASE 3", StringComparison.OrdinalIgnoreCase) >= 0) { fase = FaseBackup.PostDml; return; }

            if (plan.ConexionOrigen == null)
            {
                var m = RxConexion.Match(comentario);
                if (m.Success) { plan.ConexionOrigen = m.Groups[1].Value; return; }
            }
            if (plan.Generado == null)
            {
                var m = RxGenerado.Match(comentario);
                if (m.Success) { plan.Generado = m.Groups[1].Value; return; }
            }

            // Listado "--    1. dbo.Tabla  (123 filas)" de la cabecera
            var mt = RxTabla.Match(comentario);
            if (mt.Success) plan.Tablas.Add(mt.Groups[1].Value);
        }

        private static void VolcarSentencia(StringBuilder bloque, PlanRestauracion plan, FaseBackup fase, int linea)
        {
            string sql = bloque.ToString().Trim();
            bloque.Clear();
            if (sql.Length == 0) return;
            if (RxControlTx.IsMatch(sql)) return;
            plan.Sentencias.Add(new SentenciaBackup { Sql = sql, Fase = fase, Linea = linea });
        }

        private static bool EmpiezaCon(string sql, string palabra)
        {
            return sql != null &&
                   sql.TrimStart().StartsWith(palabra, StringComparison.OrdinalIgnoreCase);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Ejecución
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ejecuta el plan contra <paramref name="connStr"/> usando UNA sola conexión
        /// (SET IDENTITY_INSERT, session_replication_role y PRAGMA son de sesión).
        /// Commit automático si todo salió bien; rollback ante cualquier error.
        /// </summary>
        /// <param name="onComandoActivo">
        /// Recibe el OdbcCommand en curso (y null al terminarlo) para poder cancelar
        /// desde el hilo de UI.
        /// </param>
        public static ProgresoRestauracion Restaurar(
            string connStr,
            PlanRestauracion plan,
            Action<ProgresoRestauracion> onProgreso,
            Action<OdbcCommand> onComandoActivo,
            CancellationToken ct)
        {
            var progreso = new ProgresoRestauracion { Total = plan.Sentencias.Count };

            var preDml  = plan.Sentencias.Where(s => s.Fase == FaseBackup.PreDml).ToList();
            var dml     = plan.Sentencias.Where(s => s.Fase == FaseBackup.Dml).ToList();
            var postDml = plan.Sentencias.Where(s => s.Fase == FaseBackup.PostDml).ToList();

            var db = new DataBase(connStr);
            OdbcTransaction tx = null;

            try
            {
                // ── FASE 1 ── fuera de transacción.
                // Un fallo acá no es fatal: si las FK quedan activas los DELETE de la
                // FASE 2 fallarán y la transacción hará rollback igual.
                foreach (var s in preDml)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        PasadorDatosService.EjecutarDml(db.Connection, null, s.Sql, onComandoActivo);
                    }
                    catch (Exception ex)
                    {
                        progreso.Advertencias.Add($"FASE 1 (línea {s.Linea}): {ex.Message}");
                    }
                    Avanzar(progreso, s, onProgreso);
                }

                // ── FASE 2 ── transacción única.
                tx = db.Connection.BeginTransaction();
                foreach (var s in dml)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        PasadorDatosService.EjecutarDml(db.Connection, tx, s.Sql, onComandoActivo);
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(
                            $"Línea {s.Linea}: {ex.Message}\n\nSentencia: {Recortar(s.Sql, 300)}", ex);
                    }
                    Avanzar(progreso, s, onProgreso);
                }

                tx.Commit();
                tx = null;
                progreso.Exitoso = true;
            }
            catch (OperationCanceledException)
            {
                progreso.Cancelado = true;
                progreso.Error     = "Cancelado por el usuario.";
                RevertirSilencioso(ref tx);
            }
            catch (Exception ex)
            {
                progreso.Error = ex.Message;
                RevertirSilencioso(ref tx);
            }
            finally
            {
                // ── FASE 3 ── siempre, aun tras rollback: las FK no pueden quedar
                // deshabilitadas por un error nuestro.
                foreach (var s in postDml)
                {
                    try
                    {
                        PasadorDatosService.EjecutarDml(db.Connection, null, s.Sql, null);
                    }
                    catch (Exception ex)
                    {
                        progreso.Advertencias.Add($"FASE 3 (línea {s.Linea}): {ex.Message}");
                    }
                    progreso.Completadas++;
                }

                progreso.UltimaSentencia = null;
                onProgreso?.Invoke(progreso);
                try { onComandoActivo?.Invoke(null); } catch { }
                try { db.CloseConnection(); } catch { }
            }

            return progreso;
        }

        private static void Avanzar(ProgresoRestauracion progreso, SentenciaBackup s,
                                    Action<ProgresoRestauracion> onProgreso)
        {
            progreso.Completadas++;
            if (onProgreso == null) return;
            if (progreso.Completadas % PasoReporte != 0 && progreso.Completadas != progreso.Total) return;
            progreso.UltimaSentencia = Recortar(s.Sql, 120);
            onProgreso(progreso);
        }

        private static void RevertirSilencioso(ref OdbcTransaction tx)
        {
            if (tx == null) return;
            try { tx.Rollback(); } catch { }
            tx = null;
        }

        private static string Recortar(string texto, int max)
        {
            if (string.IsNullOrEmpty(texto)) return texto;
            texto = texto.Replace('\n', ' ').Replace('\r', ' ');
            return texto.Length > max ? texto.Substring(0, max) + "…" : texto;
        }
    }
}
