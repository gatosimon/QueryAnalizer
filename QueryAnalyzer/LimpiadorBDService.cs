using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Odbc;
using System.Linq;
using System.Text;
using CapiDL;

namespace QueryAnalyzer
{
    public class LimpiadorBDService
    {
        private readonly Conexion _conn;
        private readonly string _connStr;

        public LimpiadorBDService(Conexion conn, string connStr)
        {
            _conn = conn;
            _connStr = connStr;
        }

        // ── Tablas ────────────────────────────────────────────────────────

        public List<TablaConfigLimpiador> GetTablas()
        {
            var resultado = new List<TablaConfigLimpiador>();
            var db = new DataBase(_connStr);
            try
            {
                var dt = db.GetSchema("TABLEs");
                foreach (DataRow row in dt.Rows)
                {
                    string tipo = ObtenerCampo(row, "TABLE_TYPE");
                    if (tipo != "TABLE" && tipo != "BASE TABLE") continue;
                    string schema = ObtenerCampo(row, "TABLE_SCHEM", "TABLE_SCHEMA");
                    string nombre = ObtenerCampo(row, "TABLE_NAME");
                    if (string.IsNullOrEmpty(nombre)) continue;
                    resultado.Add(new TablaConfigLimpiador { Schema = schema, Nombre = nombre, Incluir = true });
                }
            }
            catch { }
            finally { db.CloseConnection(); }
            return resultado;
        }

        public List<string> GetColumnas(string schema, string tabla)
        {
            var cols = new List<string>();
            var db = new DataBase(_connStr);
            try
            {
                string sql = null;
                switch (_conn.Motor)
                {
                    case TipoMotor.MS_SQL:
                        sql = $"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='{Esc(schema)}' AND TABLE_NAME='{Esc(tabla)}' ORDER BY ORDINAL_POSITION";
                        break;
                    case TipoMotor.POSTGRES:
                        sql = $"SELECT column_name FROM information_schema.columns WHERE table_schema='{Esc(schema)}' AND table_name='{Esc(tabla)}' ORDER BY ordinal_position";
                        break;
                    case TipoMotor.DB2:
                        sql = $"SELECT COLNAME FROM SYSCAT.COLUMNS WHERE TABSCHEMA='{Esc(schema)}' AND TABNAME='{Esc(tabla)}' ORDER BY COLNO";
                        break;
                    case TipoMotor.SQLite:
                        sql = $"PRAGMA table_info(\"{Esc(tabla)}\")";
                        break;
                }

                if (sql != null)
                {
                    db.CommandText = sql;
                    while (db.Read())
                        cols.Add(_conn.Motor == TipoMotor.SQLite
                            ? db.Reader["name"].ToString()
                            : db.Reader[0].ToString());
                }
                else
                {
                    var dt = db.GetSchema("Columns", new string[] { null, schema, tabla });
                    foreach (DataRow row in dt.Rows)
                        cols.Add(row["COLUMN_NAME"].ToString());
                }
            }
            catch { }
            finally { db.CloseConnection(); }
            return cols;
        }

        // ── FK Graph ──────────────────────────────────────────────────────

        public List<FKRelacionLimpiador> GetRelaciones(List<string> nombresTablas)
        {
            var resultado = new List<FKRelacionLimpiador>();
            var db = new DataBase(_connStr);
            try
            {
                try
                {
                    var fkSchema = db.GetSchema("ForeignKeys");
                    foreach (DataRow row in fkSchema.Rows)
                    {
                        string to = ObtenerCampo(row, "FK_TABLE_NAME", "FKTABLE_NAME");
                        string fc = ObtenerCampo(row, "FK_COLUMN_NAME", "FKCOLUMN_NAME");
                        string po = ObtenerCampo(row, "PK_TABLE_NAME", "PKTABLE_NAME");
                        string pc = ObtenerCampo(row, "PK_COLUMN_NAME", "PKCOLUMN_NAME");
                        if (!string.IsNullOrEmpty(to) && !string.IsNullOrEmpty(po))
                            resultado.Add(new FKRelacionLimpiador { TablaOrigen = to, ColumnaOrigen = fc, TablaDestino = po, ColumnaDestino = pc });
                    }
                    if (resultado.Count > 0) return resultado;
                }
                catch { }

                string sql = null;
                switch (_conn.Motor)
                {
                    case TipoMotor.MS_SQL:
                        sql = @"SELECT fk.TABLE_NAME, cu.COLUMN_NAME, pk.TABLE_NAME, pt.COLUMN_NAME
                                FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
                                JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS fk ON rc.CONSTRAINT_NAME = fk.CONSTRAINT_NAME
                                JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS pk ON rc.UNIQUE_CONSTRAINT_NAME = pk.CONSTRAINT_NAME
                                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE cu ON rc.CONSTRAINT_NAME = cu.CONSTRAINT_NAME
                                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE pt ON rc.UNIQUE_CONSTRAINT_NAME = pt.CONSTRAINT_NAME
                                    AND cu.ORDINAL_POSITION = pt.ORDINAL_POSITION";
                        break;
                    case TipoMotor.POSTGRES:
                        sql = @"SELECT kcu.table_name, kcu.column_name, ccu.table_name, ccu.column_name
                                FROM information_schema.table_constraints tc
                                JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name
                                JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name
                                WHERE tc.constraint_type = 'FOREIGN KEY'";
                        break;
                    case TipoMotor.DB2:
                        sql = @"SELECT R.TABNAME, K.COLNAME, R.REFTABNAME, F.COLNAME
                                FROM SYSCAT.REFERENCES R
                                JOIN SYSCAT.KEYCOLUSE K ON R.CONSTNAME=K.CONSTNAME AND R.TABSCHEMA=K.TABSCHEMA AND R.TABNAME=K.TABNAME
                                JOIN SYSCAT.KEYCOLUSE F ON R.REFKEYNAME=F.CONSTNAME AND R.REFTABSCHEMA=F.TABSCHEMA AND R.REFTABNAME=F.TABNAME
                                    AND K.COLSEQ=F.COLSEQ";
                        break;
                    case TipoMotor.SQLite:
                        foreach (string tabla in nombresTablas)
                        {
                            try
                            {
                                db.CommandText = $"PRAGMA foreign_key_list(\"{tabla}\")";
                                while (db.Read())
                                    resultado.Add(new FKRelacionLimpiador
                                    {
                                        TablaOrigen = tabla,
                                        ColumnaOrigen = db.Reader["from"].ToString(),
                                        TablaDestino = db.Reader["table"].ToString(),
                                        ColumnaDestino = db.Reader["to"].ToString()
                                    });
                            }
                            catch { }
                        }
                        return resultado;
                }

                if (sql != null)
                {
                    db.CommandText = sql;
                    while (db.Read())
                        resultado.Add(new FKRelacionLimpiador
                        {
                            TablaOrigen = db.Reader[0].ToString(),
                            ColumnaOrigen = db.Reader[1].ToString(),
                            TablaDestino = db.Reader[2].ToString(),
                            ColumnaDestino = db.Reader[3].ToString()
                        });
                }
            }
            catch { }
            finally { db.CloseConnection(); }
            return resultado;
        }

        // ── Análisis ──────────────────────────────────────────────────────

        public AnalisisResultLimpiador Analizar(List<TablaConfigLimpiador> configs, ModoConflicto modo)
        {
            var result = new AnalisisResultLimpiador();
            var configuradas = configs.Where(c => c.Incluir && c.TieneCondiciones).ToList();
            var nombresTablas = configuradas.Select(c => c.Nombre).ToList();
            var relaciones = GetRelaciones(nombresTablas);
            var dictConfig = configuradas.ToDictionary(c => c.Nombre, c => c, StringComparer.OrdinalIgnoreCase);

            var db = new DataBase(_connStr);
            try
            {
                foreach (var cfg in configuradas)
                {
                    var analisis = new TablaAnalisisLimpiador { NombreCompleto = cfg.NombreCompleto };
                    string q = Quote(cfg.NombreCompleto);
                    string condBaja = CondicionBajaHelper.ToCondicionSql(cfg.CondicionesBaja, QuoteCampo);
                    string condActivo = CondicionBajaHelper.ToNegacionSql(cfg.CondicionesBaja, QuoteCampo);

                    try
                    {
                        db.CommandText = $"SELECT COUNT(*) FROM {q} WHERE {condBaja}";
                        analisis.RegistrosBaja = Convert.ToInt32(db.Scalar());

                        db.CommandText = $"SELECT COUNT(*) FROM {q} WHERE {condActivo}";
                        analisis.RegistrosActivos = Convert.ToInt32(db.Scalar());
                    }
                    catch (Exception ex)
                    {
                        analisis.Estado = "Sin campo";
                        analisis.Conflictos.Add($"Error al evaluar condición: {ex.Message}");
                        result.Tablas.Add(analisis);
                        continue;
                    }

                    // Detectar conflictos FK
                    var fksHaciaEsta = relaciones
                        .Where(r => string.Equals(r.TablaDestino, cfg.Nombre, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var fk in fksHaciaEsta)
                    {
                        string qHija = Quote(fk.TablaOrigen);
                        try
                        {
                            string pk = cfg.CampoPK ?? "Id";
                            string subconsulta = $"SELECT [{pk}] FROM {q} WHERE {condBaja}";
                            string sqlConflicto;
                            if (dictConfig.TryGetValue(fk.TablaOrigen, out var cfgHija) && cfgHija.TieneCondiciones)
                            {
                                string condHijaActiva = CondicionBajaHelper.ToNegacionSql(cfgHija.CondicionesBaja, QuoteCampo);
                                sqlConflicto = $"SELECT COUNT(*) FROM {qHija} h WHERE h.[{fk.ColumnaOrigen}] IN ({subconsulta}) AND {condHijaActiva}";
                            }
                            else
                            {
                                sqlConflicto = $"SELECT COUNT(*) FROM {qHija} h WHERE h.[{fk.ColumnaOrigen}] IN ({subconsulta})";
                            }

                            db.CommandText = sqlConflicto;
                            int cant = Convert.ToInt32(db.Scalar());
                            if (cant > 0)
                            {
                                analisis.TieneConflictos = true;
                                analisis.Conflictos.Add($"{cant} fila(s) activa(s) en '{fk.TablaOrigen}' referencian a registros en baja (FK: {fk.ColumnaOrigen} → {fk.ColumnaDestino})");
                            }
                        }
                        catch { }
                    }

                    if (analisis.TieneConflictos)
                    {
                        switch (modo)
                        {
                            case ModoConflicto.Bloquear:
                                analisis.Estado = "Conflicto";
                                result.HayConflictosBloquantes = true;
                                break;
                            case ModoConflicto.BajaEnCascada:
                                analisis.Estado = "Cascada";
                                break;
                            case ModoConflicto.Ignorar:
                                analisis.Estado = "Se omite";
                                break;
                        }
                    }
                    else
                    {
                        analisis.Estado = "OK";
                    }

                    result.Tablas.Add(analisis);
                }
            }
            finally { db.CloseConnection(); }

            if (result.HayConflictosBloquantes)
                result.Advertencias.Add("Hay tablas con conflictos FK. Resolvé los conflictos o cambiá el modo a 'Baja en cascada' o 'Ignorar'.");

            return result;
        }

        // ── Generación de script ──────────────────────────────────────────

        public string GenerarScript(List<TablaConfigLimpiador> configs, AnalisisResultLimpiador analysis, ModoConflicto modo)
        {
            var sb = new StringBuilder();
            var configuradas = configs.Where(c => c.Incluir && c.TieneCondiciones).ToList();
            var nombresTablas = configuradas.Select(c => c.Nombre).ToList();
            var relaciones = GetRelaciones(nombresTablas);
            var motor = _conn.Motor.ToString().Replace("_", " ");

            sb.AppendLine("-- ════════════════════════════════════════════════════════════");
            sb.AppendLine($"-- LIMPIADOR DE BD — Generado por QueryAnalyzer");
            sb.AppendLine($"-- Conexión : {_conn.Nombre} | Motor: {motor}");
            sb.AppendLine($"-- Modo FK  : {modo}");
            sb.AppendLine("-- REVISAR CUIDADOSAMENTE ANTES DE EJECUTAR");
            sb.AppendLine("-- ════════════════════════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine(InicioTransaccion());
            sb.AppendLine();

            var ordenDEL = OrdenTopologico(configuradas, relaciones, hijosAntes: true);
            var dictCfg = configuradas.ToDictionary(c => c.Nombre, c => c, StringComparer.OrdinalIgnoreCase);
            var dictAnalisis = analysis.Tablas.ToDictionary(t => t.NombreCompleto, t => t, StringComparer.OrdinalIgnoreCase);

            // ── PASO 1: Cascada ──────────────────────────────────────────
            if (modo == ModoConflicto.BajaEnCascada)
            {
                sb.AppendLine("-- ── PASO 1: Dar de baja en cascada a hijos activos ─────────────────");
                foreach (var cfg in configuradas)
                {
                    string condPadre = CondicionBajaHelper.ToCondicionSql(cfg.CondicionesBaja, QuoteCampo);
                    string pk = cfg.CampoPK ?? "Id";
                    var fksHaciaEsta = relaciones.Where(r =>
                        string.Equals(r.TablaDestino, cfg.Nombre, StringComparison.OrdinalIgnoreCase));

                    foreach (var fk in fksHaciaEsta)
                    {
                        if (!dictCfg.TryGetValue(fk.TablaOrigen, out var cfgHija) || !cfgHija.TieneCondiciones) continue;
                        string qHija = Quote(cfgHija.NombreCompleto);
                        string qPadre = Quote(cfg.NombreCompleto);

                        // Construir SET para la hija
                        var setClauses = cfgHija.CondicionesBaja
                            .Where(c => !string.IsNullOrEmpty(c.ValorSet))
                            .Select(c => $"{QuoteCampo(c.Campo)} = {c.ValorSet}")
                            .ToList();

                        if (setClauses.Any())
                        {
                            sb.AppendLine($"UPDATE {qHija} SET {string.Join(", ", setClauses)}");
                            sb.AppendLine($"    WHERE [{fk.ColumnaOrigen}] IN (SELECT [{pk}] FROM {qPadre} WHERE {condPadre});");
                        }
                        else
                        {
                            sb.AppendLine($"-- AJUSTAR: cascada a {cfgHija.NombreCompleto} sin ValorSet configurado");
                            sb.AppendLine($"-- UPDATE {qHija} SET <campo_baja> = <valor> WHERE [{fk.ColumnaOrigen}] IN (SELECT [{pk}] FROM {qPadre} WHERE {condPadre});");
                        }
                        sb.AppendLine();
                    }
                }
            }

            // ── PASO 2: DELETE ──────────────────────────────────────────
            sb.AppendLine("-- ── PASO 2: Eliminar registros dados de baja (hijos antes que padres) ─");
            foreach (var cfg in ordenDEL)
            {
                if (dictAnalisis.TryGetValue(cfg.NombreCompleto, out var ta) && ta.Estado == "Se omite")
                {
                    sb.AppendLine($"-- OMITIDO (tiene hijos activos): {Quote(cfg.NombreCompleto)}");
                    continue;
                }
                string cond = CondicionBajaHelper.ToCondicionSql(cfg.CondicionesBaja, QuoteCampo);
                sb.AppendLine($"DELETE FROM {Quote(cfg.NombreCompleto)} WHERE {cond};");
            }
            sb.AppendLine();

            // ── PASO 3: Reordenar IDs ───────────────────────────────────
            var conReorden = configuradas.Where(c => c.ReordenarIds && !string.IsNullOrEmpty(c.CampoPK)).ToList();
            if (conReorden.Any())
            {
                sb.AppendLine("-- ── PASO 3: Reordenamiento de IDs ──────────────────────────────────");
                sb.AppendLine(DeshabilitarConstraints(configuradas));
                sb.AppendLine();

                var ordenID = OrdenTopologico(conReorden, relaciones, hijosAntes: false);
                foreach (var cfg in ordenID)
                    sb.Append(GenerarBloqueReordenamiento(cfg, relaciones, dictCfg));

                sb.AppendLine(RehabilitarConstraints(configuradas));
                sb.AppendLine(ResetSecuencias(conReorden));
            }

            sb.AppendLine();
            sb.AppendLine("-- ── Confirmar o revertir ────────────────────────────────────────────");
            sb.AppendLine("-- COMMIT;   -- ← Descomentar solo después de revisar el resultado");
            sb.AppendLine("ROLLBACK;    -- ← Retirar esta línea cuando estés listo para confirmar");

            return sb.ToString();
        }

        // ── Helpers de script ─────────────────────────────────────────────

        private string InicioTransaccion()
            => _conn.Motor == TipoMotor.POSTGRES ? "BEGIN;" : "BEGIN TRANSACTION;";

        private string DeshabilitarConstraints(List<TablaConfigLimpiador> tablas)
        {
            var sb = new StringBuilder();
            switch (_conn.Motor)
            {
                case TipoMotor.MS_SQL:
                    foreach (var t in tablas) sb.AppendLine($"ALTER TABLE {Quote(t.NombreCompleto)} NOCHECK CONSTRAINT ALL;");
                    break;
                case TipoMotor.POSTGRES:
                    sb.AppendLine("SET CONSTRAINTS ALL DEFERRED;");
                    break;
                case TipoMotor.SQLite:
                    sb.AppendLine("PRAGMA foreign_keys = OFF;");
                    break;
                case TipoMotor.DB2:
                    sb.AppendLine("-- DB2: deshabilitar FK constraints antes del reordenamiento");
                    foreach (var t in tablas)
                        sb.AppendLine($"-- ALTER TABLE {Quote(t.NombreCompleto)} ALTER FOREIGN KEY <nombre_fk> NOT ENFORCED;");
                    break;
            }
            return sb.ToString();
        }

        private string RehabilitarConstraints(List<TablaConfigLimpiador> tablas)
        {
            var sb = new StringBuilder();
            switch (_conn.Motor)
            {
                case TipoMotor.MS_SQL:
                    foreach (var t in tablas) sb.AppendLine($"ALTER TABLE {Quote(t.NombreCompleto)} WITH CHECK CHECK CONSTRAINT ALL;");
                    break;
                case TipoMotor.POSTGRES:
                    sb.AppendLine("SET CONSTRAINTS ALL IMMEDIATE;");
                    break;
                case TipoMotor.SQLite:
                    sb.AppendLine("PRAGMA foreign_keys = ON;");
                    break;
            }
            return sb.ToString();
        }

        private string ResetSecuencias(List<TablaConfigLimpiador> tablas)
        {
            var sb = new StringBuilder();
            switch (_conn.Motor)
            {
                case TipoMotor.MS_SQL:
                    sb.AppendLine("-- Resetear identity (ajustar max_id según resultado real):");
                    foreach (var t in tablas) sb.AppendLine($"-- DBCC CHECKIDENT('{t.NombreCompleto}', RESEED, <max_nuevo_id>);");
                    break;
                case TipoMotor.POSTGRES:
                    sb.AppendLine("-- Resetear secuencias:");
                    foreach (var t in tablas) sb.AppendLine($"-- SELECT setval(pg_get_serial_sequence('{t.NombreCompleto}', '{t.CampoPK}'), MAX({t.CampoPK})) FROM {Quote(t.NombreCompleto)};");
                    break;
            }
            return sb.ToString();
        }

        private string GenerarBloqueReordenamiento(TablaConfigLimpiador cfg, List<FKRelacionLimpiador> relaciones, Dictionary<string, TablaConfigLimpiador> dictCfg)
        {
            var sb = new StringBuilder();
            string q = Quote(cfg.NombreCompleto);
            string pk = cfg.CampoPK;
            string tmpMap = $"#map_{cfg.Nombre.Replace(" ", "_")}";
            var fksHaciaEsta = relaciones.Where(r =>
                string.Equals(r.TablaDestino, cfg.Nombre, StringComparison.OrdinalIgnoreCase)).ToList();

            sb.AppendLine($"-- Reordenar IDs en {cfg.NombreCompleto}");
            switch (_conn.Motor)
            {
                case TipoMotor.MS_SQL:
                    sb.AppendLine($"SELECT [{pk}] AS old_id, ROW_NUMBER() OVER (ORDER BY [{pk}]) AS new_id INTO {tmpMap} FROM {q};");
                    foreach (var fk in fksHaciaEsta)
                    {
                        string qHija = dictCfg.TryGetValue(fk.TablaOrigen, out var cfgH) ? Quote(cfgH.NombreCompleto) : $"[{fk.TablaOrigen}]";
                        sb.AppendLine($"UPDATE h SET h.[{fk.ColumnaOrigen}] = m.new_id FROM {qHija} h INNER JOIN {tmpMap} m ON h.[{fk.ColumnaOrigen}] = m.old_id;");
                    }
                    sb.AppendLine($"-- Nota: si {cfg.Nombre} tiene IDENTITY usar SET IDENTITY_INSERT ON + INSERT+DELETE");
                    sb.AppendLine($"UPDATE t SET t.[{pk}] = m.new_id FROM {q} t INNER JOIN {tmpMap} m ON t.[{pk}] = m.old_id;");
                    sb.AppendLine($"DROP TABLE {tmpMap};");
                    break;
                case TipoMotor.POSTGRES:
                    sb.AppendLine($"WITH mapping AS (SELECT \"{pk}\" AS old_id, ROW_NUMBER() OVER (ORDER BY \"{pk}\") AS new_id FROM {q})");
                    foreach (var fk in fksHaciaEsta)
                    {
                        string qHija = dictCfg.TryGetValue(fk.TablaOrigen, out var cfgH) ? Quote(cfgH.NombreCompleto) : $"\"{fk.TablaOrigen}\"";
                        sb.AppendLine($"UPDATE {qHija} h SET \"{fk.ColumnaOrigen}\" = m.new_id FROM mapping m WHERE h.\"{fk.ColumnaOrigen}\" = m.old_id;");
                    }
                    sb.AppendLine($"UPDATE {q} t SET \"{pk}\" = m.new_id FROM mapping m WHERE t.\"{pk}\" = m.old_id;");
                    break;
                default:
                    sb.AppendLine($"-- Reordenamiento para {cfg.NombreCompleto}: actualizar FKs y luego PK");
                    foreach (var fk in fksHaciaEsta)
                        sb.AppendLine($"-- UPDATE [{fk.TablaOrigen}] SET [{fk.ColumnaOrigen}] = <nuevo_id> WHERE [{fk.ColumnaOrigen}] = <viejo_id>;");
                    sb.AppendLine($"-- UPDATE {q} SET [{pk}] = <nuevo_id> WHERE [{pk}] = <viejo_id>;");
                    break;
            }
            sb.AppendLine();
            return sb.ToString();
        }

        // ── Orden topológico ──────────────────────────────────────────────

        private List<TablaConfigLimpiador> OrdenTopologico(List<TablaConfigLimpiador> tablas, List<FKRelacionLimpiador> relaciones, bool hijosAntes)
        {
            var nombres = new HashSet<string>(tablas.Select(t => t.Nombre), StringComparer.OrdinalIgnoreCase);
            var dictCfg = tablas.ToDictionary(t => t.Nombre, t => t, StringComparer.OrdinalIgnoreCase);
            var visitados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resultado = new List<TablaConfigLimpiador>();

            Func<string, IEnumerable<string>> dependencias = tabla => hijosAntes
                ? relaciones.Where(r => string.Equals(r.TablaDestino, tabla, StringComparison.OrdinalIgnoreCase) && nombres.Contains(r.TablaOrigen)).Select(r => r.TablaOrigen)
                : relaciones.Where(r => string.Equals(r.TablaOrigen, tabla, StringComparison.OrdinalIgnoreCase) && nombres.Contains(r.TablaDestino)).Select(r => r.TablaDestino);

            void Visitar(string nombre)
            {
                if (visitados.Contains(nombre)) return;
                visitados.Add(nombre);
                foreach (var dep in dependencias(nombre)) Visitar(dep);
                if (dictCfg.TryGetValue(nombre, out var cfg)) resultado.Add(cfg);
            }

            foreach (var t in tablas) Visitar(t.Nombre);
            return resultado;
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private string Quote(string nombreCompleto)
        {
            var partes = nombreCompleto.Split('.');
            if (_conn.Motor == TipoMotor.POSTGRES || _conn.Motor == TipoMotor.SQLite)
                return string.Join(".", partes.Select(p => $"\"{p}\""));
            return string.Join(".", partes.Select(p => $"[{p}]"));
        }

        private string QuoteCampo(string campo)
        {
            if (_conn.Motor == TipoMotor.POSTGRES || _conn.Motor == TipoMotor.SQLite)
                return $"\"{campo}\"";
            return $"[{campo}]";
        }

        private string Esc(string s) => (s ?? "").Replace("'", "''");

        private string ObtenerCampo(DataRow row, params string[] candidatos)
        {
            foreach (string c in candidatos)
                if (row.Table.Columns.Contains(c) && row[c] != DBNull.Value)
                    return row[c].ToString();
            return string.Empty;
        }

        // ── Ejecución directa ─────────────────────────────────────────────

        public class EjecucionProgreso
        {
            public int    Total           { get; set; }
            public int    Completadas     { get; set; }
            public string UltimaSentencia { get; set; }
            public bool   Exitoso         { get; set; }
            public string Error           { get; set; }
        }

        private OdbcTransaction _txnPendiente;
        private DataBase _dbPendiente;

        public EjecucionProgreso EjecutarScript(string script, Action<EjecucionProgreso> onProgreso)
        {
            var progreso = new EjecucionProgreso();
            var db = new DataBase(_connStr);
            OdbcTransaction txn = null;
            try
            {
                var sentencias = ParsearSentencias(script);
                progreso.Total = sentencias.Count;

                txn = db.Connection.BeginTransaction();
                var cmd = new OdbcCommand("", db.Connection, txn);

                for (int i = 0; i < sentencias.Count; i++)
                {
                    var s = sentencias[i].Trim();
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    progreso.Completadas = i + 1;
                    progreso.UltimaSentencia = s.Length > 80 ? s.Substring(0, 80) + "…" : s;
                    onProgreso?.Invoke(progreso);
                    cmd.CommandText = s;
                    cmd.ExecuteNonQuery();
                }

                progreso.Exitoso = true;
                onProgreso?.Invoke(progreso);
                _txnPendiente = txn;
                _dbPendiente = db;
                return progreso;
            }
            catch (Exception ex)
            {
                progreso.Exitoso = false;
                progreso.Error = ex.Message;
                try { txn?.Rollback(); } catch { }
                db.CloseConnection();
                return progreso;
            }
        }

        public void ConfirmarCommit()
        {
            try { _txnPendiente?.Commit(); }
            finally { _txnPendiente = null; _dbPendiente?.CloseConnection(); _dbPendiente = null; }
        }

        public void CancelarRollback()
        {
            try { _txnPendiente?.Rollback(); }
            finally { _txnPendiente = null; _dbPendiente?.CloseConnection(); _dbPendiente = null; }
        }

        private List<string> ParsearSentencias(string script)
        {
            var result = new List<string>();
            var lineas = script.Split('\n');
            var bloque = new StringBuilder();
            foreach (var linea in lineas)
            {
                string l = linea.TrimEnd();
                if (l.TrimStart().StartsWith("--")) continue;
                if (l.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
                {
                    string b = bloque.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(b)) result.Add(b);
                    bloque.Clear();
                    continue;
                }
                bloque.AppendLine(l);
                if (l.TrimEnd().EndsWith(";"))
                {
                    string b = bloque.ToString().Trim().TrimEnd(';');
                    if (!string.IsNullOrWhiteSpace(b)) result.Add(b);
                    bloque.Clear();
                }
            }
            string final = bloque.ToString().Trim().TrimEnd(';');
            if (!string.IsNullOrWhiteSpace(final)) result.Add(final);
            return result;
        }
    }
}
