using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Odbc;
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
            string connStrB, TipoMotor motorB, List<TablaTransfer> tablas)
        {
            var resultado = new Dictionary<string, TablaMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tablas)
                resultado[t.NombreCompleto] = new TablaMetadata();

            if (string.IsNullOrEmpty(connStrB)) return resultado;

            DataBase db = null;
            try { db = new DataBase(connStrB, false); }
            catch { return resultado; }

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
                    catch { /* metadata vacía — sin optimizaciones para esta tabla */ }
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
        // Backup de B
        // ─────────────────────────────────────────────────────────────────────

        public static string GenerarBackupScript(
            string connStrB, List<TablaTransfer> ordenDelete,
            Action<string> onProgress = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- ╔═══════════════════════════════════════════════════╗");
            sb.AppendLine("-- ║  BACKUP DE BASE DE DATOS B (estado previo)        ║");
            sb.AppendLine($"-- ║  Generado: {DateTime.Now:yyyy-MM-dd HH:mm:ss}               ║");
            sb.AppendLine("-- ╚═══════════════════════════════════════════════════╝");
            sb.AppendLine();

            foreach (var tabla in ordenDelete)
            {
                onProgress?.Invoke($"[Backup] Leyendo {tabla.NombreCompleto}...");
                try
                {
                    var DB = new DataBase(connStrB, false);
                    DB.CommandText = $"SELECT * FROM {tabla.NombreCompleto}";
                    var dt = new DataTable();
                    DB.DataAdapter.Fill(dt);
                    DB.CloseConnection();

                    sb.AppendLine($"-- ═══ {tabla.NombreCompleto}  ({dt.Rows.Count} filas) ═══");
                    sb.AppendLine(ScriptHelper.GenerarScriptInsert(dt, tabla.NombreCompleto, conDelete: false));
                    sb.AppendLine();
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"-- ERROR leyendo {tabla.NombreCompleto}: {ex.Message}");
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        public static Dictionary<string, string> GenerarBackupPorTabla(
            string connStrB, List<TablaTransfer> ordenDelete,
            Action<string> onProgress = null)
        {
            var scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tabla in ordenDelete)
            {
                onProgress?.Invoke($"[Backup] Leyendo {tabla.NombreCompleto}...");
                try
                {
                    var DB = new DataBase(connStrB, false);
                    DB.CommandText = $"SELECT * FROM {tabla.NombreCompleto}";
                    var dt = new DataTable();
                    DB.DataAdapter.Fill(dt);
                    DB.CloseConnection();
                    scripts[tabla.NombreCompleto] =
                        $"-- BACKUP {tabla.NombreCompleto} ({dt.Rows.Count} filas) — {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                        ScriptHelper.GenerarScriptInsert(dt, tabla.NombreCompleto, conDelete: false);
                }
                catch (Exception ex)
                {
                    scripts[tabla.NombreCompleto] = $"-- ERROR leyendo {tabla.NombreCompleto}: {ex.Message}";
                }
            }
            return scripts;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Transferencia principal
        // ─────────────────────────────────────────────────────────────────────

        public static ResultadoPasado TransferirDatos(
            string connStrA,
            string connStrB,
            TipoMotor motorA,
            TipoMotor motorB,
            List<TablaTransfer> ordenInsert,
            List<TablaTransfer> ordenDelete,
            OpcionesPasado opciones,
            Action<string> onProgress,
            Action<string> onSql = null,
            Action<int, int> onStep = null)
        {
            var resultado = new ResultadoPasado();
            var sbScript  = new StringBuilder();

            // 1. Cargar datos de A
            onProgress?.Invoke("Leyendo datos de la base A...");
            var datosA = new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase);
            foreach (var tabla in ordenInsert)
            {
                onProgress?.Invoke($"[A] Leyendo {tabla.NombreCompleto}...");
                try
                {
                    var DB = new DataBase(connStrA, false);
                    DB.CommandText = $"SELECT * FROM {tabla.NombreCompleto}";
                    var dt = new DataTable();
                    DB.DataAdapter.Fill(dt);
                    DB.CloseConnection();
                    datosA[tabla.Nombre] = dt;
                }
                catch (Exception ex)
                {
                    resultado.Advertencias.Add($"No se pudo leer '{tabla.NombreCompleto}' de A: {ex.Message}");
                    datosA[tabla.Nombre] = new DataTable();
                }
            }

            // 2. Obtener metadata de B
            onProgress?.Invoke("Obteniendo metadata de columnas de B...");
            var metadata = ObtenerMetadataTablas(connStrB, motorB, ordenInsert);

            // Calcular total de pasos para la barra de progreso
            int totalFilas = ordenDelete.Count + ordenInsert.Sum(t =>
                datosA.TryGetValue(t.Nombre, out var dt2) ? dt2.Rows.Count : 0);
            int pasoActual = 0;

            // 3. Construir script completo (script path)
            sbScript.AppendLine($"-- ═══ SCRIPT DE TRANSFERENCIA — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ═══");
            sbScript.AppendLine($"-- Origen: {connStrA}");
            sbScript.AppendLine($"-- Destino: {connStrB}");
            sbScript.AppendLine();

            // Sección FASE 1: deshabilitar FKs (para el script)
            AgregarScriptFaseDeshabilitarFKs(sbScript, motorB, ordenDelete);

            sbScript.AppendLine();
            sbScript.AppendLine("-- ═══ FASE 2: DML ═══");
            sbScript.AppendLine("BEGIN TRAN");
            sbScript.AppendLine();
            sbScript.AppendLine("  -- DELETE en orden reverso FK");
            foreach (var tabla in ordenDelete)
                sbScript.AppendLine($"  DELETE FROM {tabla.NombreCompleto};");

            sbScript.AppendLine();
            sbScript.AppendLine("  -- INSERT en orden FK");
            foreach (var tabla in ordenInsert)
            {
                if (!datosA.TryGetValue(tabla.Nombre, out DataTable dtA) || dtA.Rows.Count == 0) continue;
                var meta      = metadata.ContainsKey(tabla.NombreCompleto) ? metadata[tabla.NombreCompleto] : null;
                bool overriding = (motorB == TipoMotor.POSTGRES || motorB == TipoMotor.DB2)
                                  && meta?.TieneIdentityAlways == true;

                if (motorB == TipoMotor.MS_SQL && meta?.TieneIdentity == true)
                    sbScript.AppendLine($"  SET IDENTITY_INSERT {tabla.NombreCompleto} ON;");

                string scriptTabla = ScriptHelper.GenerarScriptInsert(
                    dtA, tabla.NombreCompleto, conDelete: false,
                    meta?.ColumnasExcluidas, overriding);
                resultado.ScriptsPorTabla[tabla.NombreCompleto] = scriptTabla;
                sbScript.AppendLine(scriptTabla);

                if (motorB == TipoMotor.MS_SQL && meta?.TieneIdentity == true)
                    sbScript.AppendLine($"  SET IDENTITY_INSERT {tabla.NombreCompleto} OFF;");
            }

            sbScript.AppendLine("COMMIT");
            sbScript.AppendLine();
            AgregarScriptFaseRehabilitarFKs(sbScript, motorB, ordenInsert);

            resultado.ScriptTransferencia = sbScript.ToString();

            if (opciones.GenerarSoloScript)
            {
                resultado.Exito = true;
                return resultado;
            }

            // 4. Ejecutar en B
            onProgress?.Invoke("Conectando a la base B...");
            var dbB = new DataBase(connStrB);
            OdbcTransaction tx = null;
            bool commitOk = false;

            try
            {
                // FASE 1: Deshabilitar FKs (fuera de tx)
                onProgress?.Invoke("Deshabilitando FK constraints en B...");
                EjecutarFaseDeshabilitarFKs(dbB.Connection, motorB, ordenDelete, onProgress, onSql);

                // FASE 2: Transacción única
                onProgress?.Invoke("Iniciando transacción...");
                tx = dbB.Connection.BeginTransaction();

                // DELETEs
                onProgress?.Invoke("Ejecutando DELETEs en B...");
                foreach (var tabla in ordenDelete)
                {
                    string sql = $"DELETE FROM {tabla.NombreCompleto}";
                    onSql?.Invoke(sql);
                    onProgress?.Invoke($"[DELETE] {tabla.NombreCompleto}");
                    EjecutarDml(dbB.Connection, tx, sql);
                    onStep?.Invoke(++pasoActual, totalFilas);
                }

                // INSERTs
                onProgress?.Invoke("Ejecutando INSERTs en B...");
                foreach (var tabla in ordenInsert)
                {
                    if (!datosA.TryGetValue(tabla.Nombre, out DataTable dtA) || dtA.Rows.Count == 0)
                    {
                        onProgress?.Invoke($"[INSERT] {tabla.NombreCompleto} — sin datos, omitida.");
                        continue;
                    }

                    var meta      = metadata.ContainsKey(tabla.NombreCompleto) ? metadata[tabla.NombreCompleto] : null;
                    bool overriding = (motorB == TipoMotor.POSTGRES || motorB == TipoMotor.DB2)
                                      && meta?.TieneIdentityAlways == true;

                    onProgress?.Invoke($"[INSERT] {tabla.NombreCompleto} ({dtA.Rows.Count} filas)...");

                    if (motorB == TipoMotor.MS_SQL && meta?.TieneIdentity == true)
                    {
                        string sqlId = $"SET IDENTITY_INSERT {tabla.NombreCompleto} ON";
                        onSql?.Invoke(sqlId);
                        EjecutarDml(dbB.Connection, tx, sqlId);
                    }

                    foreach (DataRow row in dtA.Rows)
                    {
                        string sql = BuildInsertSql(tabla.NombreCompleto, dtA, row, meta?.ColumnasExcluidas, overriding);
                        onSql?.Invoke(sql);
                        EjecutarDml(dbB.Connection, tx, sql);
                        onStep?.Invoke(++pasoActual, totalFilas);
                    }

                    if (motorB == TipoMotor.MS_SQL && meta?.TieneIdentity == true)
                    {
                        string sqlId = $"SET IDENTITY_INSERT {tabla.NombreCompleto} OFF";
                        onSql?.Invoke(sqlId);
                        EjecutarDml(dbB.Connection, tx, sqlId);
                    }
                }

                tx.Commit();
                commitOk = true;
                onProgress?.Invoke("✓ Transacción completada (COMMIT).");
            }
            catch (Exception ex)
            {
                resultado.Error = ex.Message;
                try { tx?.Rollback(); onProgress?.Invoke("✗ Se realizó ROLLBACK. B queda intacta."); } catch { }
            }
            finally
            {
                // FASE 3: Rehabilitar FKs — siempre, haya commit o rollback
                try
                {
                    onProgress?.Invoke("Rehabilitando FK constraints en B...");
                    EjecutarFaseRehabilitarFKs(dbB.Connection, motorB, ordenInsert, onProgress, onSql);
                }
                catch (Exception exFk) { resultado.Advertencias.Add($"Error al rehabilitar FK constraints: {exFk.Message}"); }

                try { dbB.CloseConnection(); } catch { }
            }

            resultado.Exito = commitOk;

            // 5. Verificación de consistencia A vs B
            if (commitOk)
            {
                onProgress?.Invoke("Verificando consistencia entre A y B...");
                VerificarConsistencia(connStrA, connStrB, ordenInsert, resultado, onProgress);
            }

            return resultado;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Verificación post-transferencia
        // ─────────────────────────────────────────────────────────────────────

        public static void VerificarConsistencia(
            string connStrA, string connStrB,
            List<TablaTransfer> tablas,
            ResultadoPasado resultado,
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
                        resultado.Advertencias.Add($"Inconsistencia en '{tabla.NombreCompleto}': A tiene {cntA} filas, B tiene {cntB}.");
                        todoOk = false;
                    }
                }
                catch (Exception ex)
                {
                    onProgress?.Invoke($"  ⚠ {tabla.NombreCompleto}: no se pudo verificar ({ex.Message})");
                }
            }
            onProgress?.Invoke(todoOk
                ? "✓ Verificación completada: A y B están consistentes."
                : "⚠ Verificación completada con diferencias. Revisar advertencias.");
        }

        private static long ContarFilas(string connStr, string nombreCompleto)
        {
            var DB = new DataBase(connStr, false);
            DB.CommandText = $"SELECT COUNT(*) FROM {nombreCompleto}";
            var dt = new DataTable();
            DB.DataAdapter.Fill(dt);
            DB.CloseConnection();
            return Convert.ToInt64(dt.Rows[0][0]);
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

        private static string BuildInsertSql(
            string nombreTabla, DataTable dt, DataRow row,
            HashSet<string> excluidas, bool overriding)
        {
            var cols = dt.Columns.Cast<DataColumn>()
                .Where(c => excluidas == null || !excluidas.Contains(c.ColumnName))
                .ToList();
            string colStr = string.Join(", ", cols.Select(c => c.ColumnName));
            string valStr = string.Join(", ", cols.Select(c => ScriptHelper.EscaparValorSql(row[c])));
            string ov = overriding ? " OVERRIDING SYSTEM VALUE" : "";
            return $"INSERT INTO {nombreTabla} ({colStr}){ov} VALUES ({valStr})";
        }

        /// <summary>
        /// Ejecuta una sentencia DML. <paramref name="onComando"/> permite al llamador
        /// quedarse con el OdbcCommand en curso para poder cancelarlo desde otro hilo.
        /// </summary>
        internal static void EjecutarDml(OdbcConnection conn, OdbcTransaction tx, string sql,
                                         Action<OdbcCommand> onComando = null)
        {
            using (var cmd = new OdbcCommand(sql, conn))
            {
                if (tx != null) cmd.Transaction = tx;
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
