using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Odbc;
using System.IO;
using System.Linq;
using System.Text;
using CapiDL;

namespace QueryAnalyzer
{
    public static class PasadorDatosService
    {
        // ─────────────────────────────────────────────────────────────────────
        // FK graph
        // ─────────────────────────────────────────────────────────────────────

        public static List<FkEdge> ObtenerFKsEntreTablas(
            string connStr, TipoMotor motor, List<TablaTransfer> tablas)
        {
            var nombres = new HashSet<string>(
                tablas.Select(t => t.Nombre),
                StringComparer.OrdinalIgnoreCase);

            return ObtenerTodasLasFKs(connStr, motor, tablas)
                .Where(e => nombres.Contains(NombreSimple(e.TablaOrigen))
                         && nombres.Contains(NombreSimple(e.TablaDestino)))
                .ToList();
        }

        public static List<string> DetectarFKsExternas(
            string connStr, TipoMotor motor, List<TablaTransfer> tablas)
        {
            var nombres = new HashSet<string>(
                tablas.Select(t => t.Nombre),
                StringComparer.OrdinalIgnoreCase);

            var avisos = new List<string>();
            foreach (var e in ObtenerTodasLasFKs(connStr, motor, tablas))
            {
                bool origenDentro  = nombres.Contains(NombreSimple(e.TablaOrigen));
                bool destinoDentro = nombres.Contains(NombreSimple(e.TablaDestino));

                if (origenDentro && !destinoDentro)
                    avisos.Add($"'{e.TablaOrigen}' tiene FK hacia '{e.TablaDestino}' (fuera del conjunto). " +
                               "Datos insertados podrían quedar con referencias inválidas.");

                if (!origenDentro && destinoDentro)
                    avisos.Add($"'{e.TablaDestino}' es referenciada por '{e.TablaOrigen}' (fuera del conjunto). " +
                               "El DELETE puede fallar si hay filas apuntando a ella.");
            }
            return avisos.Distinct().ToList();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Topological sort (Kahn)
        // ─────────────────────────────────────────────────────────────────────

        public static (List<TablaTransfer> OrdenInsert, List<string> Ciclicas) OrdenarPorDependencias(
            List<TablaTransfer> tablas, List<FkEdge> edges)
        {
            var inDegree  = tablas.ToDictionary(t => t.Nombre, _ => 0, StringComparer.OrdinalIgnoreCase);
            var dependeDe = tablas.ToDictionary(t => t.Nombre, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            var requiereMe= tablas.ToDictionary(t => t.Nombre, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var e in edges)
            {
                string origen  = NombreSimple(e.TablaOrigen);
                string destino = NombreSimple(e.TablaDestino);
                if (!inDegree.ContainsKey(origen) || !inDegree.ContainsKey(destino)) continue;
                if (dependeDe[origen].Add(destino))
                {
                    inDegree[origen]++;
                    requiereMe[destino].Add(origen);
                }
            }

            var cola = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
            var ordenNombres = new List<string>();
            while (cola.Count > 0)
            {
                string actual = cola.Dequeue();
                ordenNombres.Add(actual);
                foreach (string dep in requiereMe[actual])
                    if (--inDegree[dep] == 0) cola.Enqueue(dep);
            }

            var ciclicas = inDegree.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
            ordenNombres.AddRange(ciclicas);

            var mapa = tablas.ToDictionary(t => t.Nombre, t => t, StringComparer.OrdinalIgnoreCase);
            return (ordenNombres.Where(n => mapa.ContainsKey(n)).Select(n => mapa[n]).ToList(), ciclicas);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Metadata de columnas (por motor)
        // ─────────────────────────────────────────────────────────────────────

        public static Dictionary<string, TablaMetadata> ObtenerMetadataTablas(
            string connStrB, TipoMotor motorB, List<TablaTransfer> tablas,
            RegistroIncidencias incidencias = null)
        {
            var resultado = new Dictionary<string, TablaMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tablas)
                resultado[t.NombreCompleto] = new TablaMetadata();

            if (string.IsNullOrEmpty(connStrB)) return resultado;

            DataBase db = null;
            try { db = new DataBase(connStrB, false); }
            catch (Exception exCon)
            {
                incidencias?.Agregar("Metadata", null, SeveridadIncidencia.Aviso, "Sin metadata de columnas",
                    "No se pudo conectar a B para leer columnas identity/calculadas: " + exCon.Message,
                    "La carga sigue sin esas optimizaciones; si B tiene columnas identity GENERATED ALWAYS o calculadas, el INSERT fallará y se hará rollback.");
                return resultado;
            }

            try
            {
                foreach (var tabla in tablas)
                {
                    var meta = resultado[tabla.NombreCompleto];
                    try
                    {
                        switch (motorB)
                        {
                            case TipoMotor.MS_SQL:
                                CargarMetadataMsSql(db, tabla, meta);
                                break;
                            case TipoMotor.POSTGRES:
                                CargarMetadataPostgres(db, tabla, meta);
                                break;
                            case TipoMotor.DB2:
                                CargarMetadataDb2(db, tabla, meta);
                                break;
                            case TipoMotor.SQLite:
                                CargarMetadataSqlite(db, tabla, meta);
                                break;
                        }
                    }
                    catch (Exception exMeta)
                    {
                        // metadata vacía — sin optimizaciones para esta tabla, pero se deja constancia
                        incidencias?.Agregar("Metadata", tabla.NombreCompleto, SeveridadIncidencia.Aviso,
                            "Sin metadata de columnas", exMeta.Message,
                            "Si la tabla tiene columnas identity GENERATED ALWAYS o calculadas, el INSERT fallará y se hará rollback.");
                    }
                }
            }
            finally { try { db.CloseConnection(); } catch { } }

            return resultado;
        }

        private static void CargarMetadataMsSql(DataBase db, TablaTransfer tabla, TablaMetadata meta)
        {
            db.CommandText =
                $"SELECT c.name, c.is_identity, c.is_computed, " +
                $"CASE WHEN tp.name IN ('timestamp','rowversion') THEN 1 ELSE 0 END AS es_ts " +
                $"FROM sys.columns c JOIN sys.types tp ON c.user_type_id = tp.user_type_id " +
                $"WHERE c.object_id = OBJECT_ID('{tabla.NombreCompleto}')";
            var dt = new DataTable();
            db.DataAdapter.Fill(dt);
            foreach (DataRow r in dt.Rows)
            {
                string col = r["name"].ToString();
                if ("1".Equals(r["is_identity"].ToString())) meta.ColumnasIdentity.Add(col);
                if ("1".Equals(r["is_computed"].ToString()) || "1".Equals(r["es_ts"].ToString()))
                    meta.ColumnasExcluidas.Add(col);
            }
        }

        private static void CargarMetadataPostgres(DataBase db, TablaTransfer tabla, TablaMetadata meta)
        {
            string schema = string.IsNullOrEmpty(tabla.Schema) ? "public" : tabla.Schema;
            db.CommandText =
                $"SELECT column_name, is_identity, identity_generation, is_generated " +
                $"FROM information_schema.columns " +
                $"WHERE table_schema = '{schema}' AND table_name = '{tabla.Nombre}'";
            var dt = new DataTable();
            db.DataAdapter.Fill(dt);
            foreach (DataRow r in dt.Rows)
            {
                string col = r["column_name"].ToString();
                if ("YES".Equals(r["is_identity"].ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    meta.ColumnasIdentity.Add(col);
                    if ("ALWAYS".Equals(r["identity_generation"].ToString(), StringComparison.OrdinalIgnoreCase))
                        meta.ColumnasIdentityAlways.Add(col);
                }
                if ("ALWAYS".Equals(r["is_generated"].ToString(), StringComparison.OrdinalIgnoreCase))
                    meta.ColumnasExcluidas.Add(col);
            }
        }

        private static void CargarMetadataDb2(DataBase db, TablaTransfer tabla, TablaMetadata meta)
        {
            string schema = (tabla.Schema ?? "").ToUpper();
            string nombre = tabla.Nombre.ToUpper();
            db.CommandText =
                $"SELECT COLNAME, IDENTITY, GENERATED FROM SYSCAT.COLUMNS " +
                $"WHERE TABSCHEMA = '{schema}' AND TABNAME = '{nombre}'";
            var dt = new DataTable();
            db.DataAdapter.Fill(dt);
            foreach (DataRow r in dt.Rows)
            {
                string col  = r["COLNAME"].ToString();
                string iden = r["IDENTITY"].ToString();
                string gen  = r["GENERATED"].ToString();
                if ("Y".Equals(iden, StringComparison.OrdinalIgnoreCase))
                {
                    meta.ColumnasIdentity.Add(col);
                    if ("A".Equals(gen, StringComparison.OrdinalIgnoreCase))
                        meta.ColumnasIdentityAlways.Add(col);
                }
                else if ("A".Equals(gen, StringComparison.OrdinalIgnoreCase))
                    meta.ColumnasExcluidas.Add(col);
            }
        }

        private static void CargarMetadataSqlite(DataBase db, TablaTransfer tabla, TablaMetadata meta)
        {
            db.CommandText = $"PRAGMA table_xinfo('{tabla.Nombre}')";
            var dt = new DataTable();
            db.DataAdapter.Fill(dt);
            foreach (DataRow r in dt.Rows)
            {
                string hidden = r["hidden"].ToString();
                if (hidden == "2" || hidden == "3")
                    meta.ColumnasExcluidas.Add(r["name"].ToString());
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Prevalidación del origen (A)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Cuenta las filas de cada tabla de A. Sirve para verificar que se puedan leer antes de
        /// tocar B y para dimensionar la barra de progreso. Las tablas que fallan quedan fuera del
        /// diccionario devuelto y registradas como incidencia.
        /// </summary>
        public static Dictionary<string, long> PrevalidarOrigen(
            string connStrA, List<TablaTransfer> tablas, RegistroIncidencias incidencias,
            Action<string> onProgress)
        {
            var conteos = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var t in tablas)
            {
                n++;
                onProgress?.Invoke($"[A] ({n}/{tablas.Count}) Verificando {t.NombreCompleto}...");
                try { conteos[t.NombreCompleto] = ContarFilas(connStrA, t.NombreCompleto); }
                catch (Exception ex)
                {
                    incidencias.Agregar("Origen", t.NombreCompleto, SeveridadIncidencia.Error,
                        "Tabla ilegible en A", ex.Message,
                        "Omitir la tabla (no se vaciará ni se cargará en B) o abortar y revisar que exista y que haya permisos en A.");
                }
            }
            return conteos;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Backup de B — en streaming: una sola fila en memoria, directo a disco
        // ─────────────────────────────────────────────────────────────────────

        public class ResultadoBackup
        {
            public bool Completo { get; set; } = true;
            public int  Tablas   { get; set; }
            public long Filas    { get; set; }
            public int  TablasConIncidencias { get; set; }
        }

        private static StreamWriter NuevoWriter(string ruta, bool conBom = true)
        {
            return new StreamWriter(ruta, false, new UTF8Encoding(conBom), 65536);
        }

        internal static string NombreArchivoTabla(string nombreCompleto)
        {
            return nombreCompleto.Replace(".", "_").Replace(" ", "_");
        }

        /// <summary>
        /// Genera el backup del estado actual de B. Si <paramref name="rutaArchivo"/> no es null escribe
        /// un único archivo; si <paramref name="carpetaPorTabla"/> no es null, un archivo por tabla.
        /// Nunca traga una falla: todo lo que no se pudo respaldar queda como incidencia y el
        /// resultado queda marcado como incompleto para que se decida antes de vaciar B.
        /// </summary>
        public static ResultadoBackup GenerarBackup(
            string connStrB, List<TablaTransfer> ordenDelete, TipoMotor motorB,
            string rutaArchivo, string carpetaPorTabla,
            OpcionesPasado opciones, RegistroIncidencias incidencias, Action<string> onProgress)
        {
            var res = new ResultadoBackup();
            int inicio = incidencias.Count;
            StreamWriter unico = null;
            try
            {
                if (!string.IsNullOrEmpty(rutaArchivo))
                {
                    unico = NuevoWriter(rutaArchivo);
                    unico.WriteLine("-- ╔═══════════════════════════════════════════════════╗");
                    unico.WriteLine("-- ║  BACKUP DE BASE DE DATOS B (estado previo)        ║");
                    unico.WriteLine($"-- ║  Generado: {DateTime.Now:yyyy-MM-dd HH:mm:ss}               ║");
                    unico.WriteLine("-- ╚═══════════════════════════════════════════════════╝");
                    unico.WriteLine();
                }

                int n = 0;
                foreach (var tabla in ordenDelete)
                {
                    n++;
                    onProgress?.Invoke($"[Backup] ({n}/{ordenDelete.Count}) Leyendo {tabla.NombreCompleto}...");
                    StreamWriter propio = null;
                    int antes = incidencias.Count;
                    try
                    {
                        TextWriter w = unico;
                        if (!string.IsNullOrEmpty(carpetaPorTabla))
                        {
                            propio = NuevoWriter(Path.Combine(carpetaPorTabla,
                                NombreArchivoTabla(tabla.NombreCompleto) + "_backup.sql"));
                            propio.WriteLine($"-- BACKUP {tabla.NombreCompleto} — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                            w = propio;
                        }
                        res.Filas += BackupTabla(w, connStrB, tabla, motorB, opciones, incidencias, onProgress);
                        res.Tablas++;
                    }
                    catch (IOException ioEx)
                    {
                        // Disco lleno, archivo bloqueado, ruta inválida: no tiene sentido seguir
                        incidencias.Agregar("Backup", tabla.NombreCompleto, SeveridadIncidencia.Critica,
                            "Error de escritura del backup", ioEx.Message,
                            "Liberar espacio o elegir otra ubicación y repetir. No se vació nada de B.");
                        res.Completo = false;
                        break;
                    }
                    catch (Exception ex)
                    {
                        res.Completo = false;
                        incidencias.Agregar("Backup", tabla.NombreCompleto, SeveridadIncidencia.Error,
                            ex is OutOfMemoryException ? "Memoria insuficiente al respaldar" : "No se pudo respaldar la tabla",
                            ex.Message,
                            TransferenciaStreaming.Sugerir(ex) + " La sección de esta tabla quedó incompleta en el archivo: no la restaures.");
                        try { (propio ?? unico)?.WriteLine("-- TABLA INCOMPLETA (no restaurar esta sección): " + ex.Message); } catch { }
                    }
                    finally { try { propio?.Dispose(); } catch { } }

                    if (incidencias.Count > antes) res.TablasConIncidencias++;
                }

                if (incidencias.Hay(SeveridadIncidencia.Error, inicio)) res.Completo = false;

                if (unico != null)
                {
                    try
                    {
                        unico.WriteLine(res.Completo
                            ? $"-- FIN BACKUP: {res.Tablas} tablas, {res.Filas} filas"
                            : $"-- BACKUP INCOMPLETO: {res.Tablas} de {ordenDelete.Count} tablas, {res.Filas} filas, " +
                              $"{res.TablasConIncidencias} tabla(s) con incidencias (ver incidencias_*.csv)");
                    }
                    catch { }
                }
            }
            finally { try { unico?.Dispose(); } catch { } }

            return res;
        }

        private static long BackupTabla(
            TextWriter w, string connStr, TablaTransfer tabla, TipoMotor motor,
            OpcionesPasado op, RegistroIncidencias inc, Action<string> onProgress)
        {
            long esperadas = -1;
            try { esperadas = ContarFilas(connStr, tabla.NombreCompleto); }
            catch (Exception exC)
            {
                inc.Agregar("Backup", tabla.NombreCompleto, SeveridadIncidencia.Aviso, "Sin conteo previo",
                    "No se pudo contar las filas antes del backup: " + exC.Message,
                    "No se podrá verificar que el backup tenga todas las filas.");
            }

            w.WriteLine($"-- ═══ {tabla.NombreCompleto}  ({(esperadas >= 0 ? esperadas.ToString() : "?")} filas) ═══");

            long filas, leidas;
            using (var lector = LectorTabla.Abrir(connStr, tabla, null, op.UmbralLobBytes))
            {
                filas = EscribirFilasScript(w, lector, tabla.NombreCompleto, motor, false, op, inc, "Backup",
                    n => onProgress?.Invoke($"[Backup]   {tabla.NombreCompleto}: {n} filas..."));
                leidas = lector.NumFila;
            }

            w.WriteLine($"-- FIN {tabla.NombreCompleto}: {filas} filas");
            w.WriteLine();

            if (esperadas >= 0 && leidas != esperadas)
                inc.Agregar("Backup", tabla.NombreCompleto, SeveridadIncidencia.Error,
                    "Conteo del backup distinto al de B",
                    $"B tenía {esperadas} filas al contar y se leyeron {leidas}.",
                    "Si otro usuario modifica B durante el backup, repetilo. Si no, no continúes con el vaciado.");
            return filas;
        }

        /// <summary>
        /// Escribe los INSERT de todas las filas del lector. Las filas con un binario sobre el umbral
        /// no se incluyen y quedan registradas (el script queda incompleto y se decide aparte).
        /// </summary>
        private static long EscribirFilasScript(
            TextWriter w, LectorTabla lector, string nombreTabla, TipoMotor motor, bool overriding,
            OpcionesPasado op, RegistroIncidencias inc, string fase, Action<long> onFilas)
        {
            string cols = string.Join(", ", lector.Columnas);
            var vals = new object[lector.Columnas.Length];
            long filas = 0;
            var grandes = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase); // col → [cantidad, máximo]

            while (true)
            {
                bool hay;
                try { hay = lector.LeerFila(vals); }
                catch (LobExcedeUmbralException lx)
                {
                    inc.Agregar(fase, nombreTabla, SeveridadIncidencia.Error, "Fila con binario sobre el umbral",
                        $"El valor supera {TransferenciaStreaming.Mb(op.UmbralLobBytes)} (se leyeron al menos {TransferenciaStreaming.Mb(lx.Bytes)}) y la fila no se incluyó en el script.",
                        "Esa fila NO está en el script/backup. Respaldala o transferila por otro medio antes de continuar.",
                        lx.Columna, lx.Clave);
                    continue;
                }
                if (!hay) break;

                ScriptHelper.EscribirInsert(w, nombreTabla, cols, vals, overriding, motor);
                filas++;

                if (motor == TipoMotor.DB2)
                    for (int k = 0; k < vals.Length; k++)
                    {
                        var b = vals[k] as byte[];
                        if (b == null || b.Length <= TransferenciaStreaming.LimiteLiteralDb2) continue;
                        long[] acc;
                        if (!grandes.TryGetValue(lector.Columnas[k], out acc)) grandes[lector.Columnas[k]] = acc = new long[2];
                        acc[0]++;
                        if (b.Length > acc[1]) acc[1] = b.Length;
                    }

                if (onFilas != null && filas % 50000 == 0) onFilas(filas);
            }

            foreach (var kv in grandes)
                inc.Agregar(fase, nombreTabla, SeveridadIncidencia.Aviso, "Literales binarios muy grandes para DB2",
                    $"{kv.Value[0]} valor(es) de la columna {kv.Key} superan ~16 KB (máximo {TransferenciaStreaming.Mb(kv.Value[1])}).",
                    "DB2 limita el tamaño de los literales hexadecimales (verificar en tu versión): ese script podría no ejecutarse. La transferencia directa A→B usa parámetros y no tiene este límite.",
                    kv.Key);

            return filas;
        }

        // ─────────────────────────────────────────────────────────────────────
        // "Backup de esquema" (script restaurable de las tablas elegidas), en streaming
        // ─────────────────────────────────────────────────────────────────────

        public class ResultadoBackupEsquema
        {
            public int  Tablas   { get; set; }
            public int  Errores  { get; set; }
            public long Filas    { get; set; }
            public bool Completo { get; set; } = true;
        }

        /// <summary>
        /// Escribe en <paramref name="rutaDestino"/> un script que deja la base como estaba: FASE 1 (FK),
        /// transacción con DELETE + INSERT, FASE 3 (FK). Mismo formato que reconoce el restaurador,
        /// pero escrito fila a fila. Una tabla que no se pudo leer queda fuera del script <b>y de sus
        /// DELETE</b>, para que restaurar nunca vacíe una tabla que el backup no puede devolver.
        /// </summary>
        public static ResultadoBackupEsquema GenerarBackupEsquema(
            string connStr, TipoMotor motor, string nombreConexion, string rutaDestino,
            List<TablaTransfer> ordenInsert, List<TablaTransfer> ordenDelete, List<string> ciclicas,
            OpcionesPasado op, RegistroIncidencias inc, Action<string> onProgress)
        {
            var res = new ResultadoBackupEsquema();
            int inicio = inc.Count;

            onProgress?.Invoke("Leyendo metadata de las tablas...");
            var metadata = ObtenerMetadataTablas(connStr, motor, ordenInsert, inc);

            // Conteo previo: arma el índice del encabezado y detecta tablas ilegibles antes de escribir
            var conteos  = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var fallidas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var estados  = new List<Tuple<string, string>>();
            int k = 0;
            foreach (var t in ordenInsert)
            {
                k++;
                onProgress?.Invoke($"[Backup] ({k}/{ordenInsert.Count}) Contando {t.NombreCompleto}...");
                try
                {
                    long c = ContarFilas(connStr, t.NombreCompleto);
                    conteos[t.NombreCompleto] = c;
                    estados.Add(Tuple.Create(t.NombreCompleto, $"{c} filas"));
                    res.Filas += c;
                }
                catch (Exception ex)
                {
                    fallidas.Add(t.NombreCompleto);
                    res.Errores++;
                    estados.Add(Tuple.Create(t.NombreCompleto, "ERROR → " + ex.Message));
                    inc.Agregar("Backup", t.NombreCompleto, SeveridadIncidencia.Error, "Tabla ilegible",
                        ex.Message,
                        "La tabla quedó fuera del script y de sus DELETE: restaurar no la toca. Revisá que exista y los permisos.");
                }
            }

            var ordenInsertOk = ordenInsert.Where(t => !fallidas.Contains(t.NombreCompleto)).ToList();
            var ordenDeleteOk = ordenDelete.Where(t => !fallidas.Contains(t.NombreCompleto)).ToList();
            res.Filas = 0;   // se recalcula con lo realmente escrito

            using (var w = NuevoWriter(rutaDestino))
            {
                w.WriteLine("-- ══════════════════════════════════════════════════════════");
                w.WriteLine("--  BACKUP DE ESQUEMA — script restaurable");
                w.WriteLine("-- ══════════════════════════════════════════════════════════");
                w.WriteLine($"--  Conexión : {nombreConexion}");
                w.WriteLine($"--  Generado : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                w.WriteLine($"--  Tablas   : {ordenInsert.Count} ({res.Errores} con error) — {conteos.Values.Sum()} filas");
                w.WriteLine("--");
                w.WriteLine("--  Ejecutar este script contra la MISMA base la deja tal como");
                w.WriteLine("--  estaba: vacía las tablas y reinserta todo el contenido.");
                w.WriteLine("-- ──────────────────────────────────────────────────────────");
                w.WriteLine("--  Orden de restauración (padres primero):");
                for (int i = 0; i < estados.Count; i++)
                    w.WriteLine($"--    {i + 1,3}. {estados[i].Item1}  ({estados[i].Item2})");
                if (ciclicas != null && ciclicas.Count > 0)
                {
                    w.WriteLine("--");
                    w.WriteLine("--  Estas tablas forman un ciclo de FK entre sí, así que no existe un");
                    w.WriteLine("--  orden válido entre ellas y quedaron al final. NO afecta a este");
                    w.WriteLine("--  backup: el script deshabilita las FK antes de los DELETE/INSERT.");
                    w.WriteLine("--  Solo importa si ejecutás el script por partes o sin la FASE 1.");
                    foreach (string c in ciclicas)
                        w.WriteLine($"--    • {c}");
                }
                w.WriteLine("-- ══════════════════════════════════════════════════════════");
                w.WriteLine();

                var fase1 = new StringBuilder();
                AgregarScriptFaseDeshabilitarFKs(fase1, motor, ordenDeleteOk);
                w.Write(fase1.ToString());

                w.WriteLine();
                w.WriteLine("-- ═══ FASE 2: DML ═══");
                w.WriteLine("BEGIN TRAN");
                w.WriteLine();
                w.WriteLine("  -- DELETE en orden reverso FK (hijas primero)");
                foreach (var t in ordenDeleteOk)
                    w.WriteLine($"  DELETE FROM {t.NombreCompleto};");

                w.WriteLine();
                w.WriteLine("  -- INSERT en orden FK (padres primero)");
                k = 0;
                foreach (var t in ordenInsertOk)
                {
                    k++;
                    string nombre = t.NombreCompleto;
                    long esperadas = conteos[nombre];
                    if (esperadas == 0) { w.WriteLine($"  -- {nombre}: sin filas."); res.Tablas++; continue; }

                    onProgress?.Invoke($"[Backup] ({k}/{ordenInsertOk.Count}) {nombre} ({esperadas} filas)...");
                    metadata.TryGetValue(nombre, out TablaMetadata meta);
                    bool overriding = (motor == TipoMotor.POSTGRES || motor == TipoMotor.DB2) && meta?.TieneIdentityAlways == true;
                    bool identityInsert = motor == TipoMotor.MS_SQL && meta?.TieneIdentity == true;

                    w.WriteLine();
                    w.WriteLine($"  -- {nombre} ({esperadas} filas)");
                    if (identityInsert) w.WriteLine($"  SET IDENTITY_INSERT {nombre} ON;");
                    try
                    {
                        long leidas;
                        using (var lector = LectorTabla.Abrir(connStr, t, meta?.ColumnasExcluidas, op.UmbralLobBytes))
                        {
                            res.Filas += EscribirFilasScript(w, lector, nombre, motor, overriding, op, inc, "Backup",
                                n => onProgress?.Invoke($"[Backup]   {nombre}: {n} filas..."));
                            leidas = lector.NumFila;
                        }
                        res.Tablas++;
                        if (leidas != esperadas)
                            inc.Agregar("Backup", nombre, SeveridadIncidencia.Error, "Conteo del backup distinto al de la base",
                                $"Al contar había {esperadas} filas y se leyeron {leidas}.",
                                "Si otro usuario modificó la tabla durante el backup, repetilo.");
                    }
                    catch (IOException) { throw; }
                    catch (Exception ex)
                    {
                        res.Errores++;
                        w.WriteLine($"-- TABLA INCOMPLETA (no restaurar este script): {ex.Message}");
                        inc.Agregar("Backup", nombre, SeveridadIncidencia.Error,
                            ex is OutOfMemoryException ? "Memoria insuficiente al respaldar" : "No se pudo respaldar la tabla",
                            ex.Message, TransferenciaStreaming.Sugerir(ex) + " La tabla quedó incompleta en el script: no lo restaures tal cual.");
                    }
                    if (identityInsert) w.WriteLine($"  SET IDENTITY_INSERT {nombre} OFF;");
                }

                w.WriteLine();
                w.WriteLine("COMMIT");
                w.WriteLine();
                var fase3 = new StringBuilder();
                AgregarScriptFaseRehabilitarFKs(fase3, motor, ordenInsertOk);
                w.Write(fase3.ToString());
            }

            res.Completo = !inc.Hay(SeveridadIncidencia.Error, inicio);
            return res;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Transferencia principal (streaming: una fila en memoria por vez)
        // ─────────────────────────────────────────────────────────────────────

        public static ResultadoPasado TransferirDatos(
            string connStrA,
            string connStrB,
            TipoMotor motorA,
            TipoMotor motorB,
            List<TablaTransfer> ordenInsert,
            List<TablaTransfer> ordenDelete,
            OpcionesPasado opciones,
            RegistroIncidencias incidencias,
            Action<string> onProgress,
            Action<string> onSql = null,
            Action<int, int> onStep = null)
        {
            var resultado = new ResultadoPasado { Incidencias = incidencias };

            // 1. Metadata de B (identity / columnas no insertables)
            onProgress?.Invoke("Obteniendo metadata de columnas de B...");
            var metadata = ObtenerMetadataTablas(connStrB, motorB, ordenInsert, incidencias);

            // 2. Modo "solo script": se escribe a disco, tabla por tabla
            if (opciones.GenerarSoloScript)
            {
                GenerarScriptEnArchivos(connStrA, connStrB, motorB, ordenInsert, ordenDelete, metadata,
                    opciones, incidencias, resultado, onProgress, onStep);
                resultado.Exito = !incidencias.Hay(SeveridadIncidencia.Error);
                if (!resultado.Exito) resultado.Error = "El script quedó incompleto: revisá las incidencias.";
                return resultado;
            }

            // 3. Total para la barra de progreso (filas de A + un paso por cada DELETE)
            long totalFilas = ordenDelete.Count;
            if (opciones.ConteosA != null)
                foreach (var t in ordenInsert)
                {
                    long c;
                    if (opciones.ConteosA.TryGetValue(t.NombreCompleto, out c)) totalFilas += c;
                }
            var avance = new TransferenciaStreaming.Avance(totalFilas, onStep, onSql);

            // 4. Ejecutar en B
            onProgress?.Invoke("Conectando a la base B...");
            DataBase dbB = null;
            OdbcTransaction tx = null;
            bool commitOk = false;
            bool modificoDatos = false;
            string tablaActual = null;
            string faseActual = "Transferencia";

            try
            {
                dbB = new DataBase(connStrB);

                // FASE 1: Deshabilitar FKs (fuera de tx)
                faseActual = "FK";
                onProgress?.Invoke("Deshabilitando FK constraints en B...");
                EjecutarFaseDeshabilitarFKs(dbB.Connection, motorB, ordenDelete, onProgress, avance.Sql);

                // FASE 2: Transacción única (si está activada)
                faseActual = "Transferencia";
                if (opciones.Transaccional)
                {
                    onProgress?.Invoke("Iniciando transacción...");
                    tx = dbB.Connection.BeginTransaction();
                }
                else
                {
                    incidencias.Agregar("Transferencia", null, SeveridadIncidencia.Aviso, "Sin transacción",
                        "La opción Transaccional está desactivada: cada sentencia se confirma sola.",
                        "Si algo falla, B queda con una parte de los datos. Usá el backup para volver atrás.");
                }

                // DELETEs
                onProgress?.Invoke("Ejecutando DELETEs en B...");
                foreach (var tabla in ordenDelete)
                {
                    tablaActual = tabla.NombreCompleto;
                    string sql = $"DELETE FROM {tabla.NombreCompleto}";
                    avance.Sql(sql);
                    onProgress?.Invoke($"[DELETE] {tabla.NombreCompleto}");
                    modificoDatos = true;
                    EjecutarDml(dbB.Connection, tx, sql);
                    avance.Sumar(1);
                }

                // INSERTs, en streaming desde A
                onProgress?.Invoke("Ejecutando INSERTs en B...");
                foreach (var tabla in ordenInsert)
                {
                    tablaActual = tabla.NombreCompleto;

                    long conteo = -1;
                    if (opciones.ConteosA != null && !opciones.ConteosA.TryGetValue(tabla.NombreCompleto, out conteo))
                        conteo = -1;
                    if (conteo == 0)
                    {
                        onProgress?.Invoke($"[INSERT] {tabla.NombreCompleto} — sin datos, omitida.");
                        continue;
                    }

                    metadata.TryGetValue(tabla.NombreCompleto, out TablaMetadata meta);
                    onProgress?.Invoke(conteo >= 0
                        ? $"[INSERT] {tabla.NombreCompleto} ({conteo} filas)..."
                        : $"[INSERT] {tabla.NombreCompleto}...");

                    resultado.FilasTransferidas += InsertarTabla(
                        dbB.Connection, tx, connStrA, tabla, meta, motorB, opciones, incidencias, avance);
                }

                tx?.Commit();
                commitOk = true;
                avance.Forzar();
                onProgress?.Invoke(opciones.Transaccional
                    ? "✓ Transacción completada (COMMIT)."
                    : "✓ Carga completada.");
            }
            catch (Exception ex)
            {
                RegistrarFalla(incidencias, ex, tablaActual, faseActual);
                resultado.Error = (ex is ErrorTransferencia && ex.InnerException != null) ? ex.InnerException.Message : ex.Message;

                if (tx != null)
                {
                    try
                    {
                        tx.Rollback();
                        onProgress?.Invoke("✗ Se realizó ROLLBACK. B queda intacta.");
                    }
                    catch (Exception exRb)
                    {
                        incidencias.Agregar("Transferencia", tablaActual, SeveridadIncidencia.Critica, "Falló el ROLLBACK",
                            exRb.Message, "B puede haber quedado con datos parciales. Restaurá el backup de B antes de seguir.");
                    }
                }
                else if (modificoDatos)
                {
                    incidencias.Agregar("Transferencia", tablaActual, SeveridadIncidencia.Critica,
                        "B quedó parcialmente modificada",
                        "La carga se interrumpió sin transacción: hay tablas vaciadas o cargadas a medias.",
                        "Restaurá el backup de B o repetí la transferencia completa.");
                }
            }
            finally
            {
                // FASE 3: Rehabilitar FKs — siempre, haya commit o rollback
                if (dbB != null)
                {
                    try
                    {
                        onProgress?.Invoke("Rehabilitando FK constraints en B...");
                        EjecutarFaseRehabilitarFKs(dbB.Connection, motorB, ordenInsert, onProgress, null);
                    }
                    catch (Exception exFk)
                    {
                        incidencias.Agregar("FK", null, SeveridadIncidencia.Error, "No se pudieron rehabilitar las FK",
                            exFk.Message,
                            "Las FK de B pueden haber quedado deshabilitadas: ejecutá la FASE 3 manualmente (SET INTEGRITY ... IMMEDIATE CHECKED / WITH CHECK CHECK CONSTRAINT).");
                    }
                    try { dbB.CloseConnection(); } catch { }
                }
            }

            resultado.Exito = commitOk;

            // 5. Verificación de consistencia A vs B
            if (commitOk)
            {
                onProgress?.Invoke("Verificando consistencia entre A y B...");
                VerificarConsistencia(connStrA, connStrB, ordenInsert, incidencias, onProgress);
            }

            return resultado;
        }

        /// <summary>Copia una tabla de A a B leyendo fila por fila y ejecutando un INSERT por fila.</summary>
        private static long InsertarTabla(
            OdbcConnection connB, OdbcTransaction tx, string connStrA, TablaTransfer tabla,
            TablaMetadata meta, TipoMotor motorB, OpcionesPasado op, RegistroIncidencias inc,
            TransferenciaStreaming.Avance avance)
        {
            bool overriding = (motorB == TipoMotor.POSTGRES || motorB == TipoMotor.DB2)
                              && meta?.TieneIdentityAlways == true;
            bool identityInsert = motorB == TipoMotor.MS_SQL && meta?.TieneIdentity == true;

            if (identityInsert)
                EjecutarDml(connB, tx, $"SET IDENTITY_INSERT {tabla.NombreCompleto} ON");

            long insertadas = 0;
            using (var lector = LectorTabla.Abrir(connStrA, tabla, meta?.ColumnasExcluidas, op.UmbralLobBytes))
            {
                string cols = string.Join(", ", lector.Columnas);
                var vals = new object[lector.Columnas.Length];
                var sb = new StringBuilder(1024);

                while (true)
                {
                    bool hay;
                    try { hay = lector.LeerFila(vals); }
                    catch (LobExcedeUmbralException lx)
                    {
                        if (!op.OmitirFilasLobGrandes) throw;

                        inc.Agregar("Transferencia", tabla.NombreCompleto, SeveridadIncidencia.Error,
                            "Fila omitida: binario sobre el umbral",
                            $"El valor supera {TransferenciaStreaming.Mb(op.UmbralLobBytes)} y la fila no se copió a B.",
                            "La tabla quedó incompleta en B. Transferí esa fila por otro medio.",
                            lx.Columna, lx.Clave);
                        avance.Sumar(1);
                        continue;
                    }
                    if (!hay) break;

                    var pars = new List<OdbcParameter>();
                    string sql = ConstruirInsert(sb, tabla.NombreCompleto, cols, vals, overriding, motorB, pars);
                    avance.Sql(sql);

                    try { EjecutarDml(connB, tx, sql, null, pars); }
                    catch (Exception ex)
                    {
                        throw new ErrorTransferencia(ex.Message, ex)
                        {
                            Tabla = tabla.NombreCompleto,
                            Clave = TransferenciaStreaming.ClaveFila(lector.Columnas, vals, vals.Length, lector.NumFila),
                            Sql   = TransferenciaStreaming.Truncar(sql, 300)
                        };
                    }

                    insertadas++;
                    avance.Sumar(1);
                }
            }

            if (identityInsert)
                EjecutarDml(connB, tx, $"SET IDENTITY_INSERT {tabla.NombreCompleto} OFF");

            return insertadas;
        }

        private static string ConstruirInsert(
            StringBuilder sb, string nombreTabla, string cols, object[] vals,
            bool overriding, TipoMotor motor, List<OdbcParameter> pars)
        {
            sb.Clear();
            sb.Append("INSERT INTO ").Append(nombreTabla).Append(" (").Append(cols).Append(')');
            if (overriding) sb.Append(" OVERRIDING SYSTEM VALUE");
            sb.Append(" VALUES (");
            for (int i = 0; i < vals.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                var b = vals[i] as byte[];
                if (b != null && b.Length > TransferenciaStreaming.LimiteLiteralBinario)
                {
                    // Binarios grandes: por parámetro. Un literal hexadecimal de ese tamaño
                    // se duplica en memoria varias veces y los motores lo rechazan.
                    sb.Append('?');
                    pars.Add(new OdbcParameter
                    {
                        OdbcType = b.Length > TransferenciaStreaming.LimiteVarBinario ? OdbcType.Image : OdbcType.VarBinary,
                        Value = b,
                        Size = b.Length
                    });
                }
                else sb.Append(ScriptHelper.EscaparValorSql(vals[i], motor));
            }
            sb.Append(')');
            return sb.ToString();
        }

        /// <summary>Convierte una excepción en una incidencia con el contexto disponible.</summary>
        private static void RegistrarFalla(RegistroIncidencias inc, Exception ex, string tablaActual, string fase)
        {
            var et = ex as ErrorTransferencia;
            var lx = ex as LobExcedeUmbralException;
            Exception raiz = et?.InnerException ?? ex;

            string tipo = lx != null ? "Binario sobre el umbral"
                        : raiz is OutOfMemoryException ? "Memoria insuficiente"
                        : et != null ? "Fila rechazada por B"
                        : "Falla en " + fase;

            string mensaje = raiz.Message;
            if (et != null && !string.IsNullOrEmpty(et.Sql)) mensaje += " | SQL: " + et.Sql;

            string accion = lx != null
                ? "La transferencia se abortó y B quedó intacta. Activá \"Omitir filas con binarios grandes\" (la tabla quedará incompleta) o transferí esa fila por otro medio."
                : TransferenciaStreaming.Sugerir(raiz);

            inc.Agregar(fase, et?.Tabla ?? tablaActual, SeveridadIncidencia.Error, tipo, mensaje, accion,
                lx?.Columna, et?.Clave ?? lx?.Clave);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Script de transferencia en archivos (modo "solo script")
        // ─────────────────────────────────────────────────────────────────────

        private static void GenerarScriptEnArchivos(
            string connStrA, string connStrB, TipoMotor motorB,
            List<TablaTransfer> ordenInsert, List<TablaTransfer> ordenDelete,
            Dictionary<string, TablaMetadata> metadata, OpcionesPasado op, RegistroIncidencias inc,
            ResultadoPasado resultado, Action<string> onProgress, Action<int, int> onStep)
        {
            string carpeta = op.CarpetaScript;
            Directory.CreateDirectory(carpeta);
            resultado.CarpetaScript = carpeta;

            var pre = new StringBuilder();
            pre.AppendLine($"-- ═══ SCRIPT DE TRANSFERENCIA — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══");
            pre.AppendLine($"-- Origen: {TransferenciaStreaming.EnmascararConexion(connStrA)}");
            pre.AppendLine($"-- Destino: {TransferenciaStreaming.EnmascararConexion(connStrB)}");
            pre.AppendLine();
            AgregarScriptFaseDeshabilitarFKs(pre, motorB, ordenDelete);
            pre.AppendLine();
            pre.AppendLine("-- ═══ FASE 2: DML ═══");
            pre.AppendLine("BEGIN TRAN");
            pre.AppendLine();
            pre.AppendLine("  -- DELETE en orden reverso FK");
            foreach (var tabla in ordenDelete)
                pre.AppendLine($"  DELETE FROM {tabla.NombreCompleto};");
            pre.AppendLine();
            pre.AppendLine("  -- INSERT en orden FK");
            resultado.PrefijoScript = pre.ToString();

            var suf = new StringBuilder();
            suf.AppendLine("COMMIT");
            suf.AppendLine();
            AgregarScriptFaseRehabilitarFKs(suf, motorB, ordenInsert);
            resultado.SufijoScript = suf.ToString();

            int n = 0;
            foreach (var tabla in ordenInsert)
            {
                n++;
                onProgress?.Invoke($"[Script] ({n}/{ordenInsert.Count}) {tabla.NombreCompleto}...");
                onStep?.Invoke(n, ordenInsert.Count);

                metadata.TryGetValue(tabla.NombreCompleto, out TablaMetadata meta);
                bool overriding = (motorB == TipoMotor.POSTGRES || motorB == TipoMotor.DB2)
                                  && meta?.TieneIdentityAlways == true;
                bool identity = motorB == TipoMotor.MS_SQL && meta?.TieneIdentity == true;

                string archivo = Path.Combine(carpeta, n.ToString("000") + "_" + NombreArchivoTabla(tabla.NombreCompleto) + ".sql");
                long filas = 0;
                bool ok = true;
                try
                {
                    using (var w = NuevoWriter(archivo, false))
                    using (var lector = LectorTabla.Abrir(connStrA, tabla, meta?.ColumnasExcluidas, op.UmbralLobBytes))
                        filas = EscribirFilasScript(w, lector, tabla.NombreCompleto, motorB, overriding, op, inc, "Script",
                            c => onProgress?.Invoke($"[Script]   {tabla.NombreCompleto}: {c} filas..."));
                }
                catch (Exception ex)
                {
                    ok = false;
                    inc.Agregar("Script", tabla.NombreCompleto, SeveridadIncidencia.Error,
                        ex is OutOfMemoryException ? "Memoria insuficiente al generar el script" : "No se pudo generar el script de la tabla",
                        ex.Message, TransferenciaStreaming.Sugerir(ex) + " La tabla no está en el script.");
                }

                if (!ok || filas == 0)
                {
                    try { File.Delete(archivo); } catch { }
                    continue;
                }

                resultado.PartesScript.Add(new ParteScript
                {
                    Tabla   = tabla.NombreCompleto,
                    Archivo = archivo,
                    Filas   = filas,
                    Antes   = identity ? $"  SET IDENTITY_INSERT {tabla.NombreCompleto} ON;" : null,
                    Despues = identity ? $"  SET IDENTITY_INSERT {tabla.NombreCompleto} OFF;" : null
                });
            }
        }

        /// <summary>Arma el script completo (un solo archivo) a partir de las partes ya escritas.</summary>
        public static void EnsamblarScript(ResultadoPasado r, string destino)
        {
            using (var w = NuevoWriter(destino, true))
            {
                w.Write(r.PrefijoScript);
                foreach (var p in r.PartesScript)
                {
                    if (!string.IsNullOrEmpty(p.Antes)) w.WriteLine(p.Antes);
                    w.Flush();
                    using (var fs = File.OpenRead(p.Archivo)) fs.CopyTo(w.BaseStream);
                    if (!string.IsNullOrEmpty(p.Despues)) w.WriteLine(p.Despues);
                }
                w.Write(r.SufijoScript);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Verificación post-transferencia
        // ─────────────────────────────────────────────────────────────────────

        public static void VerificarConsistencia(
            string connStrA, string connStrB,
            List<TablaTransfer> tablas,
            RegistroIncidencias incidencias,
            Action<string> onProgress)
        {
            bool todoOk = true;
            foreach (var tabla in tablas)
            {
                try
                {
                    long cntA = ContarFilas(connStrA, tabla.NombreCompleto);
                    long cntB = ContarFilas(connStrB, tabla.NombreCompleto);
                    if (cntA == cntB)
                        onProgress?.Invoke($"  ✓ {tabla.NombreCompleto}: {cntA} filas (A = B)");
                    else
                    {
                        onProgress?.Invoke($"  ✗ {tabla.NombreCompleto}: A={cntA} filas / B={cntB} filas — DIFERENCIA DETECTADA");
                        incidencias.Agregar("Verificación", tabla.NombreCompleto, SeveridadIncidencia.Error,
                            "Conteo distinto entre A y B", $"A tiene {cntA} filas, B tiene {cntB}.",
                            "Si no se omitieron filas a propósito, A pudo cambiar durante la transferencia o B tiene triggers/restricciones: revisar y repetir.");
                        todoOk = false;
                    }
                }
                catch (Exception ex)
                {
                    onProgress?.Invoke($"  {tabla.NombreCompleto}: no se pudo verificar ({ex.Message})");
                    incidencias.Agregar("Verificación", tabla.NombreCompleto, SeveridadIncidencia.Aviso,
                        "No se pudo verificar", ex.Message, "Comparar el conteo de filas de A y B manualmente.");
                }
            }
            onProgress?.Invoke(todoOk
                ? "✓ Verificación completada: A y B están consistentes."
                : "Verificación completada con diferencias. Revisar incidencias.");
        }

        private static long ContarFilas(string connStr, string nombreCompleto)
        {
            var DB = new DataBase(connStr, false);
            try
            {
                DB.CommandText = $"SELECT COUNT(*) FROM {nombreCompleto}";
                var dt = new DataTable();
                DB.DataAdapter.Fill(dt);
                return Convert.ToInt64(dt.Rows[0][0]);
            }
            finally { try { DB.CloseConnection(); } catch { } }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Fase FK disable / enable — por motor
        // ─────────────────────────────────────────────────────────────────────

        public static void AgregarScriptFaseDeshabilitarFKs(
            StringBuilder sb, TipoMotor motor, List<TablaTransfer> tablas)
        {
            sb.AppendLine("-- ═══ FASE 1: Deshabilitar FK constraints ═══");
            switch (motor)
            {
                case TipoMotor.MS_SQL:
                    foreach (var t in tablas) sb.AppendLine($"ALTER TABLE {t.NombreCompleto} NOCHECK CONSTRAINT ALL;");
                    break;
                case TipoMotor.POSTGRES:
                    sb.AppendLine("SET session_replication_role = replica;");
                    break;
                case TipoMotor.DB2:
                    foreach (var t in tablas) sb.AppendLine($"SET INTEGRITY FOR {t.NombreCompleto} OFF CASCADE DEFERRED;");
                    break;
                case TipoMotor.SQLite:
                    sb.AppendLine("PRAGMA foreign_keys = OFF;");
                    break;
            }
        }

        public static void AgregarScriptFaseRehabilitarFKs(
            StringBuilder sb, TipoMotor motor, List<TablaTransfer> tablas)
        {
            sb.AppendLine("-- ═══ FASE 3: Rehabilitar FK constraints ═══");
            switch (motor)
            {
                case TipoMotor.MS_SQL:
                    foreach (var t in tablas) sb.AppendLine($"ALTER TABLE {t.NombreCompleto} WITH CHECK CHECK CONSTRAINT ALL;");
                    break;
                case TipoMotor.POSTGRES:
                    sb.AppendLine("SET session_replication_role = DEFAULT;");
                    break;
                case TipoMotor.DB2:
                    foreach (var t in tablas) sb.AppendLine($"SET INTEGRITY FOR {t.NombreCompleto} IMMEDIATE CHECKED;");
                    break;
                case TipoMotor.SQLite:
                    sb.AppendLine("PRAGMA foreign_keys = ON;");
                    break;
            }
        }

        private static void EjecutarFaseDeshabilitarFKs(
            OdbcConnection conn, TipoMotor motor, List<TablaTransfer> tablas,
            Action<string> onProgress, Action<string> onSql)
        {
            switch (motor)
            {
                case TipoMotor.MS_SQL:
                    foreach (var t in tablas)
                    {
                        string sql = $"ALTER TABLE {t.NombreCompleto} NOCHECK CONSTRAINT ALL";
                        onSql?.Invoke(sql); EjecutarDml(conn, null, sql);
                    }
                    break;
                case TipoMotor.POSTGRES:
                {
                    string sql = "SET session_replication_role = replica";
                    onSql?.Invoke(sql); EjecutarDml(conn, null, sql);
                    break;
                }
                case TipoMotor.DB2:
                    foreach (var t in tablas)
                    {
                        string sql = $"SET INTEGRITY FOR {t.NombreCompleto} OFF CASCADE DEFERRED";
                        onSql?.Invoke(sql); EjecutarDml(conn, null, sql);
                    }
                    break;
                case TipoMotor.SQLite:
                {
                    string sql = "PRAGMA foreign_keys = OFF";
                    onSql?.Invoke(sql); EjecutarDml(conn, null, sql);
                    break;
                }
            }
        }

        private static void EjecutarFaseRehabilitarFKs(
            OdbcConnection conn, TipoMotor motor, List<TablaTransfer> tablas,
            Action<string> onProgress, Action<string> onSql)
        {
            switch (motor)
            {
                case TipoMotor.MS_SQL:
                    foreach (var t in tablas)
                    {
                        string sql = $"ALTER TABLE {t.NombreCompleto} WITH CHECK CHECK CONSTRAINT ALL";
                        onSql?.Invoke(sql); EjecutarDml(conn, null, sql);
                    }
                    break;
                case TipoMotor.POSTGRES:
                {
                    string sql = "SET session_replication_role = DEFAULT";
                    onSql?.Invoke(sql); EjecutarDml(conn, null, sql);
                    break;
                }
                case TipoMotor.DB2:
                    foreach (var t in tablas)
                    {
                        string sql = $"SET INTEGRITY FOR {t.NombreCompleto} IMMEDIATE CHECKED";
                        onSql?.Invoke(sql); EjecutarDml(conn, null, sql);
                    }
                    break;
                case TipoMotor.SQLite:
                {
                    string sql = "PRAGMA foreign_keys = ON";
                    onSql?.Invoke(sql); EjecutarDml(conn, null, sql);
                    break;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers internos
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Ejecuta una sentencia DML. <paramref name="onComando"/> permite al llamador
        /// quedarse con el OdbcCommand en curso para poder cancelarlo desde otro hilo.
        /// <paramref name="parametros"/> son los valores de los '?' de la sentencia (binarios grandes).
        /// </summary>
        internal static void EjecutarDml(OdbcConnection conn, OdbcTransaction tx, string sql,
                                         Action<OdbcCommand> onComando = null,
                                         IList<OdbcParameter> parametros = null)
        {
            using (var cmd = new OdbcCommand(sql, conn))
            {
                if (tx != null) cmd.Transaction = tx;
                cmd.CommandTimeout = TransferenciaStreaming.TimeoutSegundos();
                if (parametros != null)
                    foreach (var p in parametros) cmd.Parameters.Add(p);
                onComando?.Invoke(cmd);
                try { cmd.ExecuteNonQuery(); }
                finally { onComando?.Invoke(null); }
            }
        }

        private static List<FkEdge> ObtenerTodasLasFKs(
            string connStr, TipoMotor motor, List<TablaTransfer> tablas)
        {
            var resultado = new List<FkEdge>();
            try
            {
                var conn = new Conexion { Motor = motor };
                var svc  = new EsquematizadorService(conn);
                var db   = new DataBase(connStr);
                var rels = svc.ObtenerRelacionesFK(db, tablas.Select(t => t.Nombre).ToList());
                db.CloseConnection();
                foreach (var r in rels)
                    resultado.Add(new FkEdge
                    {
                        TablaOrigen    = r.TablaOrigen,
                        TablaDestino   = r.TablaDestino,
                        ColumnaOrigen  = r.ColumnaOrigen,
                        ColumnaDestino = r.ColumnaDestino,
                    });
            }
            catch { }
            return resultado;
        }

        private static string NombreSimple(string nombreCompleto)
        {
            int dot = nombreCompleto.LastIndexOf('.');
            return dot >= 0 ? nombreCompleto.Substring(dot + 1) : nombreCompleto;
        }
    }
}
