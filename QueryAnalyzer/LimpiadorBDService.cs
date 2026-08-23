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
                    // Destildadas por omisión: esta herramienta genera DELETEs, conviene
                    // que haya que optar por incluir y no acordarse de excluir.
                    resultado.Add(new TablaConfigLimpiador { Schema = schema, Nombre = nombre, Incluir = false });
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

        /// <summary>
        /// Metadatos de todas las columnas del esquema, en orden de la tabla: tipo, si es
        /// IDENTITY y si es insertable. Una sola consulta al catálogo para toda la base, igual
        /// que <see cref="GetPrimaryKeys"/> — el barrido necesita el tipo de cada columna de FK
        /// para saber si el centinela es 0 o '', y el reordenamiento necesita saber si la PK es
        /// IDENTITY antes de elegir el camino.
        /// La clave externa es el nombre calificado (schema.tabla); la interna, el nombre de columna.
        /// El orden de inserción de la lista interna es el orden de columnas de la tabla, que es
        /// el que necesita el INSERT explícito del camino IDENTITY_INSERT.
        /// </summary>
        public Dictionary<string, List<ColumnaInfoLimpiador>> GetInfoColumnas(List<string> nombresTablas)
        {
            var resultado = new Dictionary<string, List<ColumnaInfoLimpiador>>(StringComparer.OrdinalIgnoreCase);
            var db = new DataBase(_connStr);
            try
            {
                string sql = null;
                switch (_conn.Motor)
                {
                    case TipoMotor.MS_SQL:
                        // sys.columns en vez de INFORMATION_SCHEMA: is_identity y is_computed no
                        // están en la vista estándar, y necesito los tres datos en una sola pasada.
                        sql = @"SELECT s.name, t.name, c.name, ty.name, c.is_identity,
                                       CASE WHEN c.is_computed = 1 OR ty.name IN ('timestamp','rowversion')
                                            THEN 1 ELSE 0 END
                                FROM sys.columns c
                                JOIN sys.tables   t  ON c.object_id = t.object_id
                                JOIN sys.schemas  s  ON t.schema_id = s.schema_id
                                JOIN sys.types    ty ON c.user_type_id = ty.user_type_id
                                ORDER BY s.name, t.name, c.column_id";
                        break;
                    case TipoMotor.POSTGRES:
                        sql = @"SELECT table_schema, table_name, column_name, data_type,
                                       CASE WHEN is_identity = 'YES' OR column_default LIKE 'nextval%'
                                            THEN 1 ELSE 0 END,
                                       CASE WHEN is_generated = 'ALWAYS' THEN 1 ELSE 0 END
                                FROM information_schema.columns
                                WHERE table_schema NOT IN ('pg_catalog','information_schema')
                                ORDER BY table_schema, table_name, ordinal_position";
                        break;
                    case TipoMotor.DB2:
                        sql = @"SELECT TABSCHEMA, TABNAME, COLNAME, TYPENAME,
                                       CASE WHEN IDENTITY = 'Y' THEN 1 ELSE 0 END,
                                       CASE WHEN GENERATED <> ' ' THEN 1 ELSE 0 END
                                FROM SYSCAT.COLUMNS
                                ORDER BY TABSCHEMA, TABNAME, COLNO";
                        break;
                    case TipoMotor.SQLite:
                        // Una conexión por tabla: mismo motivo que GetRelaciones — con el reader
                        // del PRAGMA anterior abierto el driver rechaza el siguiente.
                        foreach (string tabla in nombresTablas ?? new List<string>())
                        {
                            var dbT = new DataBase(_connStr);
                            try
                            {
                                dbT.CommandText = $"PRAGMA table_info(\"{Esc(tabla)}\")";
                                var lista = new List<ColumnaInfoLimpiador>();
                                while (dbT.Read())
                                    lista.Add(new ColumnaInfoLimpiador
                                    {
                                        Nombre = dbT.Reader["name"].ToString(),
                                        Tipo   = dbT.Reader["type"].ToString()
                                    });
                                if (lista.Count > 0) resultado[tabla] = lista;
                            }
                            catch { }
                            finally { dbT.CloseConnection(); }
                        }
                        return resultado;
                }

                if (sql != null)
                {
                    db.CommandText = sql;
                    while (db.Read())
                    {
                        string schema = db.IsDBNull(0) ? "" : db.Reader[0].ToString().Trim();
                        string tabla  = db.Reader[1].ToString().Trim();
                        string clave  = string.IsNullOrEmpty(schema) ? tabla : $"{schema}.{tabla}";
                        if (!resultado.TryGetValue(clave, out var lista))
                            resultado[clave] = lista = new List<ColumnaInfoLimpiador>();
                        lista.Add(new ColumnaInfoLimpiador
                        {
                            Nombre       = db.Reader[2].ToString().Trim(),
                            Tipo         = db.IsDBNull(3) ? "" : db.Reader[3].ToString().Trim(),
                            EsIdentity   = !db.IsDBNull(4) && Convert.ToInt32(db.Reader[4]) == 1,
                            NoInsertable = !db.IsDBNull(5) && Convert.ToInt32(db.Reader[5]) == 1
                        });
                    }
                }
            }
            catch { }
            finally { db.CloseConnection(); }
            return resultado;
        }

        /// <summary>Metadatos de una columna puntual, o null si el catálogo no la trajo.</summary>
        private static ColumnaInfoLimpiador InfoDe(
            Dictionary<string, List<ColumnaInfoLimpiador>> info, string tablaCompleta, string columna)
        {
            if (info == null || !info.TryGetValue(tablaCompleta, out var cols)) return null;
            return cols.FirstOrDefault(c => string.Equals(c.Nombre, columna, StringComparison.OrdinalIgnoreCase));
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
                        string ts = ObtenerCampo(row, "FK_TABLE_SCHEM", "FKTABLE_SCHEM", "FK_TABLE_SCHEMA", "FKTABLE_SCHEMA");
                        string to = ObtenerCampo(row, "FK_TABLE_NAME", "FKTABLE_NAME");
                        string fc = ObtenerCampo(row, "FK_COLUMN_NAME", "FKCOLUMN_NAME");
                        string ps = ObtenerCampo(row, "PK_TABLE_SCHEM", "PKTABLE_SCHEM", "PK_TABLE_SCHEMA", "PKTABLE_SCHEMA");
                        string po = ObtenerCampo(row, "PK_TABLE_NAME", "PKTABLE_NAME");
                        string pc = ObtenerCampo(row, "PK_COLUMN_NAME", "PKCOLUMN_NAME");
                        string fn = ObtenerCampo(row, "FK_NAME", "FKNAME", "CONSTRAINT_NAME");
                        if (!string.IsNullOrEmpty(to) && !string.IsNullOrEmpty(po))
                            resultado.Add(new FKRelacionLimpiador
                            {
                                SchemaOrigen = ts, TablaOrigen = to, ColumnaOrigen = fc,
                                SchemaDestino = ps, TablaDestino = po, ColumnaDestino = pc,
                                NombreFK = string.IsNullOrEmpty(fn) ? $"{to}→{po}" : fn
                            });
                    }
                    if (resultado.Count > 0) return resultado;
                }
                catch { }

                string sql = null;
                switch (_conn.Motor)
                {
                    case TipoMotor.MS_SQL:
                        // El JOIN va por CONSTRAINT_SCHEMA además del nombre: los nombres de
                        // constraint son únicos por esquema, no por base. Sin eso, dos esquemas
                        // con constraints homónimos producen un join cruzado — no duplicados,
                        // filas mal armadas que mezclan la hija de un esquema con el padre del otro.
                        sql = @"SELECT fk.TABLE_SCHEMA, fk.TABLE_NAME, cu.COLUMN_NAME,
                                       pk.TABLE_SCHEMA, pk.TABLE_NAME, pt.COLUMN_NAME, rc.CONSTRAINT_NAME
                                FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
                                JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS fk
                                    ON rc.CONSTRAINT_NAME = fk.CONSTRAINT_NAME
                                   AND rc.CONSTRAINT_SCHEMA = fk.CONSTRAINT_SCHEMA
                                JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS pk
                                    ON rc.UNIQUE_CONSTRAINT_NAME = pk.CONSTRAINT_NAME
                                   AND rc.UNIQUE_CONSTRAINT_SCHEMA = pk.CONSTRAINT_SCHEMA
                                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE cu
                                    ON rc.CONSTRAINT_NAME = cu.CONSTRAINT_NAME
                                   AND rc.CONSTRAINT_SCHEMA = cu.CONSTRAINT_SCHEMA
                                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE pt
                                    ON rc.UNIQUE_CONSTRAINT_NAME = pt.CONSTRAINT_NAME
                                   AND rc.UNIQUE_CONSTRAINT_SCHEMA = pt.CONSTRAINT_SCHEMA
                                   AND cu.ORDINAL_POSITION = pt.ORDINAL_POSITION
                                ORDER BY rc.CONSTRAINT_SCHEMA, rc.CONSTRAINT_NAME, cu.ORDINAL_POSITION";
                        break;
                    case TipoMotor.POSTGRES:
                        sql = @"SELECT kcu.table_schema, kcu.table_name, kcu.column_name,
                                       ccu.table_schema, ccu.table_name, ccu.column_name, tc.constraint_name
                                FROM information_schema.table_constraints tc
                                JOIN information_schema.key_column_usage kcu
                                    ON tc.constraint_name = kcu.constraint_name
                                   AND tc.constraint_schema = kcu.constraint_schema
                                JOIN information_schema.constraint_column_usage ccu
                                    ON ccu.constraint_name = tc.constraint_name
                                   AND ccu.constraint_schema = tc.constraint_schema
                                WHERE tc.constraint_type = 'FOREIGN KEY'
                                ORDER BY tc.constraint_schema, tc.constraint_name, kcu.ordinal_position";
                        break;
                    case TipoMotor.DB2:
                        sql = @"SELECT R.TABSCHEMA, R.TABNAME, K.COLNAME,
                                       R.REFTABSCHEMA, R.REFTABNAME, F.COLNAME, R.CONSTNAME
                                FROM SYSCAT.REFERENCES R
                                JOIN SYSCAT.KEYCOLUSE K ON R.CONSTNAME=K.CONSTNAME AND R.TABSCHEMA=K.TABSCHEMA AND R.TABNAME=K.TABNAME
                                JOIN SYSCAT.KEYCOLUSE F ON R.REFKEYNAME=F.CONSTNAME AND R.REFTABSCHEMA=F.TABSCHEMA AND R.REFTABNAME=F.TABNAME
                                    AND K.COLSEQ=F.COLSEQ
                                ORDER BY R.TABSCHEMA, R.CONSTNAME, K.COLSEQ";
                        break;
                    case TipoMotor.SQLite:
                        // Una conexión por tabla: con el reader del PRAGMA anterior todavía
                        // abierto el driver ODBC rechaza el siguiente y se pierde una tabla
                        // de cada dos. Mismo patrón que GetColumnas.
                        foreach (string tabla in nombresTablas)
                        {
                            var dbT = new DataBase(_connStr);
                            try
                            {
                                dbT.CommandText = $"PRAGMA foreign_key_list(\"{tabla}\")";
                                while (dbT.Read())
                                    resultado.Add(new FKRelacionLimpiador
                                    {
                                        TablaOrigen = tabla,
                                        ColumnaOrigen = dbT.Reader["from"].ToString(),
                                        TablaDestino = dbT.Reader["table"].ToString(),
                                        ColumnaDestino = dbT.Reader["to"].ToString(),
                                        // "id" agrupa las columnas de una misma FK compuesta
                                        NombreFK = $"{tabla}#{dbT.Reader["id"]}"
                                    });
                            }
                            catch { }
                            finally { dbT.CloseConnection(); }
                        }
                        return resultado;
                }

                if (sql != null)
                {
                    db.CommandText = sql;
                    while (db.Read())
                    {
                        string ts = db.IsDBNull(0) ? "" : db.Reader[0].ToString().Trim();
                        string to = db.Reader[1].ToString().Trim();
                        string ps = db.IsDBNull(3) ? "" : db.Reader[3].ToString().Trim();
                        string po = db.Reader[4].ToString().Trim();
                        string fn = db.IsDBNull(6) ? "" : db.Reader[6].ToString();
                        resultado.Add(new FKRelacionLimpiador
                        {
                            SchemaOrigen = ts,
                            TablaOrigen = to,
                            ColumnaOrigen = db.Reader[2].ToString().Trim(),
                            SchemaDestino = ps,
                            TablaDestino = po,
                            ColumnaDestino = db.Reader[5].ToString().Trim(),
                            NombreFK = string.IsNullOrEmpty(fn) ? $"{to}→{po}" : fn
                        });
                    }
                }
            }
            catch { }
            finally { db.CloseConnection(); }
            // Dedup por nombre calificado: duplicados acá se traducen en sentencias repetidas
            // río abajo, y un remapeo de IDs repetido corrompe los datos.
            return resultado
                .GroupBy(r => $"{r.NombreFK}|{r.OrigenCompleto}|{r.ColumnaOrigen}|{r.DestinoCompleto}|{r.ColumnaDestino}",
                         StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        // ── Claves primarias ──────────────────────────────────────────────

        /// <summary>
        /// Devuelve las columnas de la PK de cada tabla, en orden de la clave.
        /// Una sola consulta al catálogo para todo el esquema (no una por tabla).
        /// La clave del diccionario es el nombre calificado (schema.tabla), igual que
        /// el matcheo de FKs de este módulo.
        /// </summary>
        public Dictionary<string, List<string>> GetPrimaryKeys(List<string> nombresTablas)
        {
            var resultado = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var db = new DataBase(_connStr);
            try
            {
                string sql = null;
                switch (_conn.Motor)
                {
                    case TipoMotor.MS_SQL:
                        sql = @"SELECT ku.TABLE_SCHEMA, ku.TABLE_NAME, ku.COLUMN_NAME
                                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku
                                    ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
                                   AND tc.TABLE_SCHEMA   = ku.TABLE_SCHEMA
                                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                                ORDER BY ku.TABLE_SCHEMA, ku.TABLE_NAME, ku.ORDINAL_POSITION";
                        break;
                    case TipoMotor.POSTGRES:
                        sql = @"SELECT ku.table_schema, ku.table_name, ku.column_name
                                FROM information_schema.table_constraints tc
                                JOIN information_schema.key_column_usage ku
                                    ON tc.constraint_name = ku.constraint_name
                                   AND tc.table_schema    = ku.table_schema
                                WHERE tc.constraint_type = 'PRIMARY KEY'
                                ORDER BY ku.table_schema, ku.table_name, ku.ordinal_position";
                        break;
                    case TipoMotor.DB2:
                        sql = @"SELECT K.TABSCHEMA, K.TABNAME, K.COLNAME
                                FROM SYSCAT.KEYCOLUSE K
                                JOIN SYSCAT.TABCONST C ON K.CONSTNAME = C.CONSTNAME
                                    AND K.TABSCHEMA = C.TABSCHEMA AND K.TABNAME = C.TABNAME
                                WHERE C.TYPE = 'P'
                                ORDER BY K.TABSCHEMA, K.TABNAME, K.COLSEQ";
                        break;
                    case TipoMotor.SQLite:
                        // SQLite no tiene catálogo consultable: PRAGMA por tabla, y una
                        // conexión por tabla — con el reader anterior abierto el driver ODBC
                        // rechaza el PRAGMA siguiente y se pierde una tabla de cada dos.
                        foreach (string tabla in nombresTablas)
                        {
                            var dbT = new DataBase(_connStr);
                            try
                            {
                                var pks = new List<KeyValuePair<int, string>>();
                                dbT.CommandText = $"PRAGMA table_info(\"{tabla}\")";
                                while (dbT.Read())
                                {
                                    int orden = Convert.ToInt32(dbT.Reader["pk"]);
                                    if (orden > 0) pks.Add(new KeyValuePair<int, string>(orden, dbT.Reader["name"].ToString()));
                                }
                                if (pks.Count > 0)
                                    resultado[tabla] = pks.OrderBy(p => p.Key).Select(p => p.Value).ToList();
                            }
                            catch { }
                            finally { dbT.CloseConnection(); }
                        }
                        return resultado;
                }

                if (sql != null)
                {
                    db.CommandText = sql;
                    while (db.Read())
                    {
                        string schema = db.IsDBNull(0) ? "" : db.Reader[0].ToString().Trim();
                        string tabla  = db.Reader[1].ToString().Trim();
                        string col    = db.Reader[2].ToString().Trim();
                        if (string.IsNullOrEmpty(tabla) || string.IsNullOrEmpty(col)) continue;

                        AgregarColumnaPK(resultado, string.IsNullOrEmpty(schema) ? tabla : $"{schema}.{tabla}", col);
                    }
                }
            }
            catch { }
            finally { db.CloseConnection(); }
            return resultado;
        }

        private static void AgregarColumnaPK(Dictionary<string, List<string>> destino, string clave, string columna)
        {
            if (!destino.TryGetValue(clave, out var cols))
            {
                cols = new List<string>();
                destino[clave] = cols;
            }
            cols.Add(columna);
        }

        /// <summary>
        /// Completa las PKs que el catálogo no resolvió usando el grafo de FKs:
        /// ColumnaDestino de una FK es, por definición, columna de la PK del padre.
        /// Sólo cubre tablas que son padre de alguna FK — justamente las que
        /// necesita la baja en cascada.
        /// </summary>
        public void CompletarPKsDesdeFKs(Dictionary<string, List<string>> pks, List<FKRelacionLimpiador> relaciones)
        {
            foreach (var grupo in relaciones.GroupBy(r => r.ClaveFK, StringComparer.OrdinalIgnoreCase))
            {
                var primera = grupo.First();
                if (string.IsNullOrEmpty(primera.TablaDestino)) continue;
                if (pks.ContainsKey(primera.DestinoCompleto)) continue;

                var cols = grupo.Select(r => r.ColumnaDestino)
                                .Where(c => !string.IsNullOrEmpty(c))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
                if (cols.Count > 0) pks[primera.DestinoCompleto] = cols;
            }
        }

        // ── Análisis ──────────────────────────────────────────────────────

        /// <param name="universoTablas">
        /// Universo de tablas de la base entera, no sólo las configuradas. Cumple dos funciones:
        /// (a) alimenta el grafo de FKs — si una tabla fuera de alcance referencia a una que sí lo
        /// está, ese conflicto tiene que detectarse igual (sólo lo usa la rama SQLite de
        /// GetRelaciones; los demás motores traen todas las FKs del catálogo); y (b) aporta el
        /// Schema de las tablas que la cascada de BorradoEnCascada alcanza sin que el usuario las
        /// haya tildado. Null → las configuradas.
        /// </param>
        /// <param name="opciones">
        /// Barrido de huérfanos. Null o destildado → el análisis es el de siempre y la solapa de
        /// relaciones truncadas queda vacía.
        /// </param>
        public AnalisisResultLimpiador Analizar(List<TablaConfigLimpiador> configs, ModoConflicto modo,
                                                List<TablaConfigLimpiador> universoTablas = null,
                                                OpcionesBarrido opciones = null)
        {
            var configuradas = configs.Where(c => c.Incluir && c.TieneCondiciones).ToList();
            // El alcance del barrido de huérfanos son TODAS las tildadas, con condición de baja o
            // sin ella: una tabla puede no tener nada que dar de baja y aun así quedar colgando.
            var enAlcance = configs.Where(c => c.Incluir).ToList();
            var nombresTablas = enAlcance.Select(c => c.Nombre).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var relaciones = GetRelaciones(universoTablas?.Select(t => t.Nombre).ToList() ?? nombresTablas);

            // El borrado en cascada no tiene conflictos que resolver: los hijos activos que
            // referencian a un padre en baja se eliminan también. El análisis pasa de detectar
            // bloqueos a estimar el alcance del arrastre.
            // El iterativo se depura solo, con un criterio distinto: llenarle la solapa de truncadas
            // mostraría un conteo que su barrido no usa (basta UNA FK rota) y haría esperar un
            // borrado mucho mayor que el real.
            bool depurar = opciones != null && opciones.DepurarHuerfanos
                           && modo != ModoConflicto.BorradoIterativo;
            var result = modo == ModoConflicto.BorradoEnCascada
                ? AnalizarBorradoEnCascada(configuradas, relaciones, universoTablas, depurar)
                : AnalizarPorConflictos(configuradas, relaciones, modo);

            if (depurar)
                AnalizarRelacionesTruncadas(result, enAlcance, relaciones, opciones);

            // Sólo tiene sentido donde algo puede quedar retenido: es el único modo que no borra
            // todo lo que está en baja. El iterativo tampoco retiene nada —borra todo lo que
            // encuentra— así que su informe es el opuesto: cuánto se lleva de más.
            if (modo == ModoConflicto.BorradoSeguro)
                AnalizarRetenciones(result, configuradas, relaciones, universoTablas);
            else if (modo == ModoConflicto.BorradoIterativo)
                AnalizarArrastreIterativo(result, enAlcance, relaciones, universoTablas, opciones);

            return result;
        }

        /// <summary>
        /// Estima cuántas filas ACTIVAS se lleva el modo iterativo. Es el informe que reemplaza al de
        /// retenciones: donde el borrado seguro avisa qué NO va a poder borrar, éste avisa qué va a
        /// borrar de más.
        ///
        /// El conteo simula el estado posterior al borrado de bajas —ver el parámetro bajaPadre de
        /// <see cref="ExprDesconectadaTotal"/>—, porque sobre la base sin tocar el padre en baja
        /// todavía existe y no habría una sola fila desconectada que mostrar.
        ///
        /// Es un PISO, no el total: cuenta una vuelta del barrido, y el loop puede dar varias. Una
        /// fila que recién queda desconectada cuando se borra la de arriba no aparece acá.
        /// </summary>
        private void AnalizarArrastreIterativo(
            AnalisisResultLimpiador result,
            List<TablaConfigLimpiador> enAlcance,
            List<FKRelacionLimpiador> relaciones,
            List<TablaConfigLimpiador> universoTablas,
            OpcionesBarrido opciones)
        {
            var limite   = EsquemasEnAlcance(enAlcance);
            var cierre   = CierreCascada(enAlcance.Select(c => c.NombreCompleto), relaciones, limite);
            var resolver = ResolverCierre(cierre, enAlcance, universoTablas, out var noResueltos);
            var info     = GetInfoColumnas(cierre.Select(NombreCortoDe).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            bool centinelas = opciones == null || opciones.CentinelasComoSinReferencia;

            // Sólo desaparecen las filas en baja de tablas TILDADAS: el paso 2 no toca a las demás.
            var bajaPadre = enAlcance
                .Where(c => c.TieneCondiciones)
                .ToDictionary(
                    c => c.NombreCompleto,
                    c => CondicionBajaHelper.ToCondicionSql(c.CondicionesBaja, QuoteCampoAlias("p")),
                    StringComparer.OrdinalIgnoreCase);

            var porTabla = result.Tablas.ToDictionary(t => t.NombreCompleto, t => t, StringComparer.OrdinalIgnoreCase);
            int totalActivas = 0;

            var db = new DataBase(_connStr);
            try
            {
                foreach (var nombre in cierre.Where(n => !noResueltos.Contains(n))
                                             .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    string expr = ExprDesconectadaTotal(nombre, relaciones, info, centinelas, "h", limite, bajaPadre);
                    if (expr == null) continue;

                    // "Activa" = no cumple la condición de baja propia. Sin condición configurada,
                    // toda la tabla cuenta como activa: nada de ella se borra en el paso 2.
                    string soloActivas = resolver.TryGetValue(nombre, out var cfg) && cfg.TieneCondiciones
                        ? $"NOT ({CondicionBajaHelper.ToCondicionSql(cfg.CondicionesBaja, QuoteCampoAlias("h"))}) AND "
                        : "";

                    try
                    {
                        db.CommandText = $"SELECT COUNT(*) FROM {Quote(nombre)} h WHERE {soloActivas}{expr}";
                        int cant = Convert.ToInt32(db.Scalar());
                        if (cant == 0) continue;

                        totalActivas += cant;
                        if (!porTabla.TryGetValue(nombre, out var ta))
                        {
                            ta = new TablaAnalisisLimpiador { NombreCompleto = nombre, Estado = "Arrastre" };
                            porTabla[nombre] = ta;
                            result.Tablas.Add(ta);
                        }
                        ta.CascadaEstimada = cant;
                    }
                    catch { }
                }
            }
            finally { db.CloseConnection(); }

            if (totalActivas > 0)
                result.Advertencias.Add(
                    $"⚠ Este modo se lleva al menos {totalActivas} fila(s) ACTIVAS que quedarían " +
                    "apuntando a registros borrados. Es un piso: el barrido repite hasta que no queda " +
                    "nada, y cada vuelta puede desconectar más. Revisá la columna \"Cascada estimada\".");
            else
                result.Advertencias.Add(
                    "Ninguna fila activa queda desconectada por el borrado de bajas. El barrido sólo " +
                    "va a limpiar relaciones que ya estaban rotas, si las hay.");
        }

        /// <summary>
        /// Condiciones de baja deducidas de los nombres de columna. Vive acá y no en la ventana
        /// porque el análisis de retenciones necesita evaluar la condición de tablas que el usuario
        /// NO configuró: para saber si lo que retiene a un padre está vivo o también está de baja,
        /// hay que poder preguntárselo a una tabla que nadie tildó.
        /// </summary>
        public List<CondicionBaja> DetectarCondiciones(List<string> cols, string combinador)
        {
            var conds = new List<CondicionBaja>();

            // BajaUsuario → IS NOT EMPTY
            var colBajaUsr = cols.FirstOrDefault(c => string.Equals(c, "BajaUsuario", StringComparison.OrdinalIgnoreCase));
            if (colBajaUsr != null)
                conds.Add(new CondicionBaja { Campo = colBajaUsr, Operador = "IS NOT EMPTY", ValorSet = "'SISTEMA'", Combinador = combinador });

            // BajaFecha → <> fecha cero
            var colBajaFecha = cols.FirstOrDefault(c => string.Equals(c, "BajaFecha", StringComparison.OrdinalIgnoreCase)
                                                      || string.Equals(c, "FechaBaja", StringComparison.OrdinalIgnoreCase));
            if (colBajaFecha != null)
                // Sin comillas y con guiones: EscVal las agrega, y sin guiones int.TryParse
                // lo tomaría como entero y emitiría 19000101 pelado.
                conds.Add(new CondicionBaja { Campo = colBajaFecha, Operador = "<>", Valor = "1900-01-01", ValorSet = "GETDATE()", Combinador = combinador });

            // Si no hay usuario/fecha, buscar campos genéricos
            if (conds.Count == 0)
            {
                var genericoBaja = cols.FirstOrDefault(c =>
                    new[] { "Baja", "IsDeleted", "Eliminado", "Deleted" }
                    .Any(x => string.Equals(c, x, StringComparison.OrdinalIgnoreCase)));
                if (genericoBaja != null)
                    conds.Add(new CondicionBaja { Campo = genericoBaja, Operador = "=", Valor = "1", ValorSet = "1", Combinador = combinador });
                else
                {
                    var campoActivo = cols.FirstOrDefault(c =>
                        new[] { "Activo", "Vigente", "Active" }
                        .Any(x => string.Equals(c, x, StringComparison.OrdinalIgnoreCase)));
                    if (campoActivo != null)
                        conds.Add(new CondicionBaja { Campo = campoActivo, Operador = "=", Valor = "0", ValorSet = "0", Combinador = combinador });
                }
            }
            return conds;
        }

        /// <summary>
        /// Informe del modo BorradoSeguro: qué filas dadas de baja NO se van a poder borrar, y por
        /// qué. Sin esto el modo hace lo correcto pero en silencio, y averiguar por qué una baja
        /// sobrevivió obliga a salir a buscar a mano tabla por tabla.
        ///
        /// Por cada relación se cuentan por separado los dos motivos, porque no se resuelven igual:
        /// las filas retenidas por hijos VIVOS son una decisión de negocio, y las retenidas sólo por
        /// hijos que también están de baja son una cadena incompleta que se arregla incluyendo esa
        /// tabla en el alcance.
        ///
        /// Cuando la tabla que retiene YA está en el alcance con condición propia, la segunda
        /// categoría no existe: el script la va a borrar en esta misma corrida y el padre queda
        /// libre solo, así que sólo se informa lo retenido por filas vivas.
        /// </summary>
        private void AnalizarRetenciones(
            AnalisisResultLimpiador result,
            List<TablaConfigLimpiador> configuradas,
            List<FKRelacionLimpiador> relaciones,
            List<TablaConfigLimpiador> universoTablas)
        {
            var enAlcance = IndexarPorNombreCompleto(configuradas);
            // Cache: la condición de baja de una tabla no configurada se deduce de sus columnas,
            // y una misma tabla suele retener a varios padres.
            var condCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Retenciones cuya hija está en el alcance: sólo son reales si la hija tampoco se puede
            // borrar, y eso se resuelve al final, cuando ya están todas las relaciones evaluadas.
            var transitivas = new List<RetencionLimpiador>();

            // Una tabla sin filas en baja no tiene nada que pueda quedar retenido, así que sus
            // relaciones no aportan más que ruido y dos consultas cada una. El conteo ya lo hizo el
            // análisis; acá sólo se lo aprovecha.
            var bajasPorTabla = result.Tablas.ToDictionary(
                t => t.NombreCompleto, t => t.RegistrosBaja, StringComparer.OrdinalIgnoreCase);

            var db = new DataBase(_connStr);
            try
            {
                foreach (var cfg in configuradas.Where(c => c.TieneCondiciones && c.TienePK))
                {
                    string padre     = cfg.NombreCompleto;
                    if (bajasPorTabla.TryGetValue(padre, out int enBaja) && enBaja == 0) continue;
                    string qPadre    = Quote(padre);
                    string condPadre = CondicionBajaHelper.ToCondicionSql(cfg.CondicionesBaja, QuoteCampoAlias("h"));

                    // Mismo criterio de agrupado y de dedupe que GuardasHijasVivas: si acá no
                    // coincidieran, el informe hablaría de relaciones que el script no mira.
                    var emitidas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var grupos = relaciones
                        .Where(r => string.Equals(r.DestinoCompleto, padre, StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(r.OrigenCompleto, padre, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(r => r.ClaveFK, StringComparer.OrdinalIgnoreCase);

                    foreach (var grupoFK in grupos)
                    {
                        var cols  = grupoFK.OrderBy(f => f.ColumnaOrigen, StringComparer.OrdinalIgnoreCase).ToList();
                        string hija = cols[0].OrigenCompleto;
                        string firma = hija + "|" + string.Join(",", cols.Select(f => $"{f.ColumnaOrigen}→{f.ColumnaDestino}"));
                        if (!emitidas.Add(firma)) continue;

                        string qHija = Quote(hija);
                        string join  = JoinFK(cols, "h", "e");
                        string condHija = CondicionBajaDe(hija, enAlcance, universoTablas, condCache);
                        bool hijaEnAlcance = enAlcance.TryGetValue(hija, out var cfgHija) && cfgHija.TieneCondiciones;

                        // "Vivo" = no cumple la condición de baja. Sin condición reconocible no hay
                        // forma de saberlo, así que cuenta como vivo: el informe prefiere decir
                        // "revisalo" antes que prometer que se resuelve solo.
                        string hijoVivo = condHija == null
                            ? $"EXISTS (SELECT 1 FROM {qHija} e WHERE {join})"
                            : $"EXISTS (SELECT 1 FROM {qHija} e WHERE {join} AND NOT ({condHija}))";
                        string hijoCualquiera = $"EXISTS (SELECT 1 FROM {qHija} e WHERE {join})";

                        // Dos COUNT y no un SELECT con dos SUM(CASE WHEN EXISTS…): T-SQL prohíbe
                        // subconsultas dentro de una función de agregación y rechaza la consulta
                        // entera con "Cannot perform an aggregate function on an expression
                        // containing an aggregate or a subquery". Falla siempre, no según los datos.
                        // Scalar() además evita dejar un DataReader abierto entre vueltas del loop.
                        int porVivos = 0, porCadena = 0;
                        string error = null;
                        try
                        {
                            db.CommandText = $"SELECT COUNT(*) FROM {qPadre} h WHERE {condPadre} AND {hijoVivo}";
                            porVivos = Convert.ToInt32(db.Scalar());

                            db.CommandText = $"SELECT COUNT(*) FROM {qPadre} h WHERE {condPadre} AND NOT {hijoVivo} AND {hijoCualquiera}";
                            porCadena = Convert.ToInt32(db.Scalar());
                        }
                        catch (Exception ex) { error = ex.Message; }

                        string columnas = string.Join(", ", cols.Select(f => $"{f.ColumnaOrigen} → {f.ColumnaDestino}"));

                        if (error != null)
                        {
                            result.Retenciones.Add(new RetencionLimpiador
                            {
                                TablaRetenida = padre, TablaQueRetiene = hija,
                                Columnas = columnas, Error = error,
                                // Con conteo fallido la consulta igual sirve —de hecho es lo único
                                // que sirve— para ir a mirar qué hay del otro lado.
                                SelectSql = SelectQueRetiene(qPadre, condPadre, qHija, join, condHija, false)
                            });
                            continue;
                        }

                        if (porVivos > 0)
                            result.Retenciones.Add(new RetencionLimpiador
                            {
                                TablaRetenida   = padre,
                                TablaQueRetiene = hija,
                                Columnas        = columnas,
                                FilasRetenidas  = porVivos,
                                CadenaIncompleta = false,
                                SelectSql       = SelectQueRetiene(qPadre, condPadre, qHija, join, condHija, false)
                            });

                        if (porCadena > 0)
                        {
                            var ret = new RetencionLimpiador
                            {
                                TablaRetenida    = padre,
                                TablaQueRetiene  = hija,
                                Columnas         = columnas,
                                FilasRetenidas   = porCadena,
                                CadenaIncompleta = !hijaEnAlcance,
                                RetenidaEnCadena = hijaEnAlcance,
                                PuedeIncluirse   = condHija != null,
                                SelectSql        = SelectQueRetiene(qPadre, condPadre, qHija, join, condHija, true)
                            };
                            // Con la hija fuera del alcance la traba es segura: nadie la borra.
                            // Con la hija dentro, depende de si ella misma queda retenida, y eso
                            // recién se sabe cuando están todas las relaciones evaluadas.
                            if (hijaEnAlcance) transitivas.Add(ret);
                            else               result.Retenciones.Add(ret);
                        }
                    }
                }
            }
            finally { db.CloseConnection(); }

            // Cierre transitivo de la traba. Una hija que está en el alcance normalmente se borra
            // sola y libera al padre, salvo que ella misma haya quedado retenida — y ahí el padre
            // queda trabado también. Es exactamente el caso de MPreguntas 1: la retiene
            // MSeccionesPreguntasRepetibles, que está tildada y en baja, pero que a su vez no se
            // puede borrar porque 42 filas vivas de PreguntasPorCuestionario la referencian.
            //
            // Se repite hasta que deja de crecer porque la cadena puede tener más de dos eslabones:
            // liberar a un padre puede depender de una hija que depende de otra.
            bool crecio = true;
            while (crecio)
            {
                crecio = false;
                var trabadas = new HashSet<string>(
                    result.Retenciones.Select(r => r.TablaRetenida), StringComparer.OrdinalIgnoreCase);
                foreach (var r in transitivas.ToList())
                {
                    if (!trabadas.Contains(r.TablaQueRetiene)) continue;
                    result.Retenciones.Add(r);
                    transitivas.Remove(r);
                    crecio = true;
                }
            }

            result.Retenciones = result.Retenciones
                .OrderByDescending(r => r.EsAccionable)
                .ThenByDescending(r => r.FilasRetenidas)
                .ThenBy(r => r.TablaRetenida, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Condición de baja de una tabla calificada con el alias "e", venga de la configuración del
        /// usuario o deducida de sus columnas. Devuelve null si la tabla no tiene campos de baja
        /// reconocibles, que es lo que distingue "no está de baja" de "no se puede saber".
        /// </summary>
        private string CondicionBajaDe(
            string tabla,
            Dictionary<string, TablaConfigLimpiador> enAlcance,
            List<TablaConfigLimpiador> universoTablas,
            Dictionary<string, string> cache)
        {
            if (cache.TryGetValue(tabla, out var yaEsta)) return yaEsta;

            string cond = null;
            if (enAlcance.TryGetValue(tabla, out var cfg) && cfg.TieneCondiciones)
                cond = CondicionBajaHelper.ToCondicionSql(cfg.CondicionesBaja, QuoteCampoAlias("e"));
            else
            {
                var ref_ = universoTablas?.FirstOrDefault(t =>
                    string.Equals(t.NombreCompleto, tabla, StringComparison.OrdinalIgnoreCase));
                try
                {
                    var cols = GetColumnas(ref_?.Schema ?? SchemaDe(tabla), ref_?.Nombre ?? NombreCortoDe(tabla));
                    var conds = DetectarCondiciones(cols, "AND");
                    if (conds.Any())
                        cond = CondicionBajaHelper.ToCondicionSql(conds, QuoteCampoAlias("e"));
                }
                catch { }
            }

            cache[tabla] = cond;
            return cond;
        }

        private static string SchemaDe(string nombreCompleto)
        {
            int i = nombreCompleto.IndexOf('.');
            return i < 0 ? "" : nombreCompleto.Substring(0, i);
        }

        private static string NombreCortoDe(string nombreCompleto)
        {
            int i = nombreCompleto.IndexOf('.');
            return i < 0 ? nombreCompleto : nombreCompleto.Substring(i + 1);
        }

        /// <summary>Consulta que devuelve las filas que están reteniendo, para poder mirarlas.</summary>
        private string SelectQueRetiene(string qPadre, string condPadre, string qHija, string join,
                                        string condHija, bool soloEnBaja)
        {
            string filtro = condHija == null ? ""
                          : soloEnBaja      ? $" AND ({condHija})"
                                            : $" AND NOT ({condHija})";
            return $"SELECT e.* FROM {qHija} e\n" +
                   $"WHERE EXISTS (SELECT 1 FROM {qPadre} h WHERE {join} AND {condPadre}){filtro};";
        }

        private AnalisisResultLimpiador AnalizarPorConflictos(
            List<TablaConfigLimpiador> configuradas, List<FKRelacionLimpiador> relaciones, ModoConflicto modo)
        {
            var result = new AnalisisResultLimpiador();
            var dictConfig = IndexarPorNombreCompleto(configuradas);

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

                    // Detectar conflictos FK — agrupadas por constraint para soportar FK compuesta
                    var fksHaciaEsta = relaciones
                        .Where(r => string.Equals(r.DestinoCompleto, cfg.NombreCompleto, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(r => r.ClaveFK, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    string condPadreAlias = CondicionBajaHelper.ToCondicionSql(cfg.CondicionesBaja, QuoteCampoAlias("p"));

                    foreach (var grupoFK in fksHaciaEsta)
                    {
                        var colsFK = grupoFK.ToList();
                        string tablaHija = colsFK[0].OrigenCompleto;
                        // El catálogo ya trae la hija calificada; la config sólo aporta sus condiciones
                        dictConfig.TryGetValue(tablaHija, out var cfgHija);
                        string qHija = Quote(tablaHija);
                        try
                        {
                            string join = JoinFK(colsFK, "p", "h");
                            var sb = new StringBuilder();
                            sb.Append($"SELECT COUNT(*) FROM {qHija} h ");
                            sb.Append($"WHERE EXISTS (SELECT 1 FROM {q} p WHERE {join} AND {condPadreAlias})");
                            if (cfgHija != null && cfgHija.TieneCondiciones)
                                sb.Append($" AND {CondicionBajaHelper.ToNegacionSql(cfgHija.CondicionesBaja, QuoteCampoAlias("h"))}");

                            db.CommandText = sb.ToString();
                            int cant = Convert.ToInt32(db.Scalar());
                            if (cant > 0)
                            {
                                analisis.TieneConflictos = true;
                                string detalleFK = string.Join(", ", colsFK.Select(f => $"{f.ColumnaOrigen} → {f.ColumnaDestino}"));
                                analisis.Conflictos.Add($"{cant} fila(s) activa(s) en '{tablaHija}' referencian a registros en baja (FK: {detalleFK})");
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
                            case ModoConflicto.BorradoSeguro:
                                // Los hijos activos no bloquean nada acá: son justamente el
                                // mecanismo de retención. La tabla se procesa igual, pero las
                                // filas en baja que todavía tengan hijos vivos sobreviven.
                                analisis.Estado = "Baja (retiene)";
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

            var sinPK = configuradas.Where(c => !c.TienePK).Select(c => c.NombreCompleto).ToList();
            if (sinPK.Any())
                result.Advertencias.Add($"Sin PK detectada ({sinPK.Count}): {string.Join(", ", sinPK)}. Usá '🔍 Detectar campos automáticamente' o cargala a mano (doble clic en la tabla).");

            var compuestasReorden = configuradas.Where(c => c.ReordenarIds && !c.PKSimple).Select(c => c.NombreCompleto).ToList();
            if (compuestasReorden.Any())
                result.Advertencias.Add($"Quedan fuera del reordenamiento de IDs por PK compuesta o ausente ({compuestasReorden.Count}): {string.Join(", ", compuestasReorden)}.");

            return result;
        }

        // ── Relaciones truncadas / huérfanos ──────────────────────────────

        /// <summary>
        /// FKs cuya HIJA está en alcance, agrupadas por constraint y deduplicadas por juego de
        /// columnas. Se descartan las auto-FK degeneradas (columna a sí misma): esa fila satisface
        /// el constraint consigo misma, nunca puede quedar colgando, y barrerla vaciaría la tabla.
        /// </summary>
        private List<IGrouping<string, FKRelacionLimpiador>> FKsDeHijasEnAlcance(
            HashSet<string> hijas, List<FKRelacionLimpiador> relaciones)
            => relaciones
                .Where(r => hijas.Contains(r.OrigenCompleto))
                .GroupBy(r => r.ClaveFK, StringComparer.OrdinalIgnoreCase)
                .GroupBy(g => string.Join("|", g.OrderBy(r => r.ColumnaOrigen, StringComparer.OrdinalIgnoreCase)
                                                .Select(r => $"{r.OrigenCompleto}.{r.ColumnaOrigen}→{r.DestinoCompleto}.{r.ColumnaDestino}")),
                         StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Where(g => !EsAutoFKDegenerada(g.ToList()))
                .ToList();

        /// <summary>
        /// Expresión que distingue "esta fila REFERENCIA a alguien" de "esta fila no referencia a
        /// nadie". Es la mitad más importante del barrido: una fila sin referencia no es huérfana,
        /// es una fila suelta legítima, y borrarla sería un falso positivo.
        ///
        /// En una FK compuesta alcanza con que UNA columna sea nula para que el constraint no se
        /// verifique (semántica MATCH SIMPLE), así que se exigen todas no nulas.
        /// </summary>
        private string ExprConReferencia(
            List<FKRelacionLimpiador> colsFK,
            Dictionary<string, List<ColumnaInfoLimpiador>> info,
            bool centinelas,
            string alias)
        {
            var partes = new List<string>();
            foreach (var fk in colsFK)
            {
                string col = $"{alias}.{QuoteCampo(fk.ColumnaOrigen)}";
                partes.Add($"{col} IS NOT NULL");
                if (!centinelas) continue;

                // El tipo no es opcional: aplicar <> 0 sobre un varchar revienta en MS SQL.
                var ci = InfoDe(info, fk.OrigenCompleto, fk.ColumnaOrigen);
                if (ci == null) continue;
                if (ci.EsNumerico)   partes.Add($"{col} <> 0");
                else if (ci.EsTexto) partes.Add($"{col} <> ''");
            }
            return string.Join(" AND ", partes);
        }

        /// <summary>
        /// Expresión de "esta fila está huérfana por esta FK": referencia a alguien, y ese alguien
        /// no existe. Condición positiva — si un supuesto está mal la fila NO entra y sobrevive.
        /// </summary>
        private string ExprHuerfanaPorFK(
            List<FKRelacionLimpiador> colsFK,
            Dictionary<string, List<ColumnaInfoLimpiador>> info,
            bool centinelas,
            string alias)
        {
            string conRef = ExprConReferencia(colsFK, info, centinelas, alias);
            string qPadre = Quote(colsFK[0].DestinoCompleto);
            string join   = JoinFK(colsFK, "p", alias);
            return $"({conRef} AND NOT EXISTS (SELECT 1 FROM {qPadre} p WHERE {join}))";
        }

        /// <summary>
        /// Expresión de "esta fila no conecta con NADA", para el modo BorradoIterativo. Es la
        /// contracara de <see cref="ExprHuerfanaPorFK"/>, que alcanza con una sola FK rota: acá se
        /// exigen todas, y por eso es mucho más conservadora.
        ///
        /// Las FKs se agrupan por EL DATO —el juego de columnas de origen—, no por constraint. Si
        /// IdPregunta apunta a Preguntas y también a PreguntasHistoricas, ese es un solo dato con
        /// dos destinos posibles, y encontrarlo en cualquiera de los dos lo deja conectado.
        ///
        /// Un dato vacío (NULL, o centinela si la opción está tildada) se IGNORA: no apuntar a nada
        /// no es prueba de estar roto. De ahí el "NOT (conRef) OR …" de cada término, que lo deja
        /// pasar sin opinar.
        ///
        /// Y hace falta que al menos un dato apunte a algo — la guarda que abre la expresión. Sin
        /// ella, una fila con todas sus FKs en NULL satisface todos los términos por vacuidad y se
        /// borraría, cuando es justamente el caso donde no hay ninguna evidencia de rotura.
        ///
        /// Devuelve null si la tabla no tiene FKs salientes dentro del límite: un catálogo suelto
        /// no participa del barrido y su DELETE no se emite.
        /// </summary>
        /// <param name="bajaPadre">
        /// Padre calificado → su condición de baja con alias "p". Sólo lo usa el análisis previo,
        /// para estimar sobre la base SIN tocar: en ese momento el padre en baja todavía existe, así
        /// que sin esto la expresión no encontraría una sola fila desconectada y el informe diría
        /// cero. Descontarlo simula el estado en que va a quedar la base después del paso 2.
        /// Null al generar el script, donde el borrado ya pasó de verdad.
        /// </param>
        private string ExprDesconectadaTotal(
            string tabla,
            List<FKRelacionLimpiador> relaciones,
            Dictionary<string, List<ColumnaInfoLimpiador>> info,
            bool centinelas,
            string alias,
            HashSet<string> limiteEsquemas,
            Dictionary<string, string> bajaPadre = null)
        {
            // Una FK compuesta llega como una fila por columna: reagrupar por constraint primero.
            var porConstraint = relaciones
                .Where(r => string.Equals(r.OrigenCompleto, tabla, StringComparison.OrdinalIgnoreCase)
                            && DentroDelLimite(r.SchemaDestino, limiteEsquemas))
                .GroupBy(r => r.ClaveFK, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(f => f.ColumnaOrigen, StringComparer.OrdinalIgnoreCase).ToList())
                .Where(cols => !EsAutoFKDegenerada(cols))
                .ToList();

            if (!porConstraint.Any()) return null;

            var porDato = porConstraint
                .GroupBy(cols => string.Join("|", cols.Select(f => f.ColumnaOrigen)),
                         StringComparer.OrdinalIgnoreCase)
                .ToList();

            var apuntaAAlgo = new List<string>();
            var terminos    = new List<string>();

            foreach (var dato in porDato)
            {
                // Todas las FKs del dato comparten las columnas de origen, así que cualquiera
                // sirve para armar el "apunta a algo".
                string conRef = ExprConReferencia(dato.First(), info, centinelas, alias);
                apuntaAAlgo.Add($"({conRef})");

                var enNinguno = dato.Select(cols =>
                {
                    string cb = null;
                    bool simula = bajaPadre != null && bajaPadre.TryGetValue(cols[0].DestinoCompleto, out cb);
                    string vivo = simula ? $" AND NOT ({cb})" : "";
                    return $"NOT EXISTS (SELECT 1 FROM {Quote(cols[0].DestinoCompleto)} p WHERE {JoinFK(cols, "p", alias)}{vivo})";
                });

                terminos.Add($"(NOT ({conRef}) OR ({string.Join(" AND ", enNinguno)}))");
            }

            return $"(({string.Join(" OR ", apuntaAAlgo)})" +
                   $"\n       AND {string.Join("\n       AND ", terminos)})";
        }

        /// <summary>
        /// Semilla del barrido: por cada tabla en alcance, el OR de todas sus FKs rotas.
        /// Las FKs cuyo padre cae fuera del límite de esquema se informan pero no entran —
        /// el límite es duro y no se borra por una relación que el usuario no puso en alcance.
        /// </summary>
        private Dictionary<string, string> SemillasHuerfanos(
            List<TablaConfigLimpiador> enAlcance,
            List<FKRelacionLimpiador> relaciones,
            Dictionary<string, List<ColumnaInfoLimpiador>> info,
            OpcionesBarrido opciones,
            out List<IGrouping<string, FKRelacionLimpiador>> fksDentro,
            out List<IGrouping<string, FKRelacionLimpiador>> fksFuera)
        {
            var hijas  = new HashSet<string>(enAlcance.Select(c => c.NombreCompleto), StringComparer.OrdinalIgnoreCase);
            var limite = EsquemasEnAlcance(enAlcance);
            var todas  = FKsDeHijasEnAlcance(hijas, relaciones);

            fksDentro = todas.Where(g => DentroDelLimite(g.First().SchemaDestino, limite)).ToList();
            fksFuera  = todas.Where(g => !DentroDelLimite(g.First().SchemaDestino, limite)).ToList();

            bool centinelas = opciones == null || opciones.CentinelasComoSinReferencia;
            return fksDentro
                .GroupBy(g => g.First().OrigenCompleto, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => string.Join(" OR ", g.Select(f => ExprHuerfanaPorFK(f.ToList(), info, centinelas, "h"))),
                    StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Cuenta las relaciones truncadas y las filas colgando, FK por FK. No borra nada:
        /// es el informe que hay que mirar antes de generar el script.
        /// </summary>
        private void AnalizarRelacionesTruncadas(
            AnalisisResultLimpiador result,
            List<TablaConfigLimpiador> enAlcance,
            List<FKRelacionLimpiador> relaciones,
            OpcionesBarrido opciones)
        {
            var info = GetInfoColumnas(enAlcance.Select(c => c.Nombre).ToList());
            var semillas = SemillasHuerfanos(enAlcance, relaciones, info, opciones, out var fksDentro, out var fksFuera);
            bool centinelas = opciones.CentinelasComoSinReferencia;
            var porTabla = result.Tablas.ToDictionary(t => t.NombreCompleto, t => t, StringComparer.OrdinalIgnoreCase);

            var db = new DataBase(_connStr);
            try
            {
                foreach (var grupoFK in fksDentro.Concat(fksFuera))
                {
                    var cols = grupoFK.ToList();
                    bool fuera = fksFuera.Contains(grupoFK);
                    var rt = new RelacionTruncada
                    {
                        TablaHija      = cols[0].OrigenCompleto,
                        TablaPadre     = cols[0].DestinoCompleto,
                        NombreFK       = cols[0].NombreFK,
                        Columnas       = string.Join(", ", cols.Select(f => $"{f.ColumnaOrigen} → {f.ColumnaDestino}")),
                        FueraDeAlcance = fuera
                    };

                    string qHija = Quote(rt.TablaHija);
                    string expr  = ExprHuerfanaPorFK(cols, info, centinelas, "h");
                    try
                    {
                        db.CommandText = $"SELECT COUNT(*) FROM {qHija} h WHERE {expr}";
                        rt.FilasRotas = Convert.ToInt32(db.Scalar());
                    }
                    catch (Exception ex) { rt.Error = ex.Message; }

                    if (rt.FilasRotas > 0 && cols.Count == 1)
                        rt.Ejemplos = MuestraValores(db, qHija, cols[0].ColumnaOrigen, expr);

                    if (rt.FilasRotas > 0 || rt.Error != null)
                        result.Truncadas.Add(rt);
                }

                // Filas de cada tabla con al menos una FK rota. No es la suma por FK: una misma
                // fila colgada de dos FKs se contaría dos veces.
                foreach (var kv in semillas)
                {
                    try
                    {
                        db.CommandText = $"SELECT COUNT(*) FROM {Quote(kv.Key)} h WHERE {kv.Value}";
                        int cant = Convert.ToInt32(db.Scalar());
                        if (cant == 0) continue;
                        if (!porTabla.TryGetValue(kv.Key, out var ta))
                        {
                            ta = new TablaAnalisisLimpiador { NombreCompleto = kv.Key, Estado = "Huérfanos" };
                            porTabla[kv.Key] = ta;
                            result.Tablas.Add(ta);
                        }
                        ta.Huerfanos = cant;
                    }
                    catch { }
                }
            }
            finally { db.CloseConnection(); }

            result.Truncadas = result.Truncadas
                .OrderByDescending(t => t.FilasRotas)
                .ThenBy(t => t.TablaHija, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var conRotura = result.Truncadas.Where(t => !t.FueraDeAlcance && t.Error == null).ToList();
            if (conRotura.Any())
                result.Advertencias.Add(
                    $"Relaciones truncadas: {conRotura.Count} FK con filas colgando " +
                    $"({conRotura.Sum(t => t.FilasRotas)} fila(s) en total). El barrido las elimina y arrastra " +
                    "a los hijos que queden huérfanos por ese borrado, hasta cerrar la cadena. " +
                    "Mirá la solapa 'Relaciones truncadas' antes de ejecutar.");

            var fueraConRotura = result.Truncadas.Where(t => t.FueraDeAlcance && t.FilasRotas > 0).ToList();
            if (fueraConRotura.Any())
                result.Advertencias.Add(
                    $"{fueraConRotura.Count} relación(es) truncada(s) apuntan a un padre FUERA del esquema en " +
                    $"alcance ({string.Join(", ", fueraConRotura.Select(t => $"{t.TablaHija} → {t.TablaPadre}").Distinct())}). " +
                    "Se informan pero NO se borran: el límite de esquema es duro. " +
                    "Para depurarlas hay que incluir el otro esquema en el alcance.");

            var errores = result.Truncadas.Where(t => t.Error != null).ToList();
            if (errores.Any())
                result.Advertencias.Add(
                    $"⚠ {errores.Count} relación(es) no se pudieron evaluar (ver columna 'Alcance'): " +
                    $"{string.Join(", ", errores.Select(t => t.TablaHija).Distinct(StringComparer.OrdinalIgnoreCase))}. " +
                    "Esas FKs quedan fuera del barrido.");

            var sinPK = enAlcance.Where(c => semillas.ContainsKey(c.NombreCompleto) && !c.TienePK)
                                 .Select(c => c.NombreCompleto).ToList();
            if (sinPK.Any())
            {
                result.HayConflictosBloquantes = true;
                result.Advertencias.Add(
                    $"⛔ {sinPK.Count} tabla(s) con huérfanos no tienen PK detectada y sin eso no se puede armar " +
                    $"su conjunto de borrado: {string.Join(", ", sinPK)}. " +
                    "Usá '🔍 Detectar campos automáticamente' o cargá la PK a mano (doble clic en la tabla).");
            }

            if (_conn.Motor != TipoMotor.MS_SQL)
                result.Advertencias.Add(
                    "El barrido de huérfanos está implementado completo sólo en MS SQL. En los demás motores " +
                    "el freno de seguridad y la verificación final quedan comentados: revisá el script a mano.");
        }

        /// <summary>Unos pocos valores huérfanos de ejemplo, para poder mirarlos antes de borrar.</summary>
        private string MuestraValores(DataBase db, string qTabla, string columna, string exprHuerfana)
        {
            try
            {
                string col = QuoteCampo(columna);
                string sql;
                switch (_conn.Motor)
                {
                    case TipoMotor.MS_SQL:
                        sql = $"SELECT DISTINCT TOP 5 h.{col} FROM {qTabla} h WHERE {exprHuerfana}";
                        break;
                    case TipoMotor.DB2:
                        sql = $"SELECT DISTINCT h.{col} FROM {qTabla} h WHERE {exprHuerfana} FETCH FIRST 5 ROWS ONLY";
                        break;
                    default:
                        sql = $"SELECT DISTINCT h.{col} FROM {qTabla} h WHERE {exprHuerfana} LIMIT 5";
                        break;
                }
                db.CommandText = sql;
                var vals = new List<string>();
                while (db.Read()) vals.Add(db.IsDBNull(0) ? "NULL" : db.Reader[0].ToString().Trim());
                return string.Join(", ", vals);
            }
            catch { return ""; }
        }

        // ── Análisis del borrado en cascada ───────────────────────────────

        /// <summary>
        /// Análisis del modo BorradoEnCascada. Nunca bloquea: informa cuántas filas se eliminan
        /// por baja lógica, hasta dónde llega el arrastre y qué tablas toca que el usuario no
        /// tildó — que es el dato importante antes de ejecutar.
        /// </summary>
        /// <param name="hayBarridoHuerfanos">
        /// El barrido de huérfanos está activo, así que las huérfanas preexistentes ya salen en su
        /// propio informe FK por FK: repetirlas acá como advertencia suelta sólo confunde.
        /// </param>
        private AnalisisResultLimpiador AnalizarBorradoEnCascada(
            List<TablaConfigLimpiador> configuradas,
            List<FKRelacionLimpiador> relaciones,
            List<TablaConfigLimpiador> universoTablas,
            bool hayBarridoHuerfanos)
        {
            var result = new AnalisisResultLimpiador();
            var dictCfg = IndexarPorNombreCompleto(configuradas);
            var limite = EsquemasEnAlcance(configuradas);
            var cierre = CierreCascada(configuradas.Select(c => c.NombreCompleto), relaciones, limite);
            var resolver = ResolverCierre(cierre, configuradas, universoTablas, out var noResueltos);
            var externas = ReferenciasExternas(cierre, relaciones, limite);
            var porTabla = new Dictionary<string, TablaAnalisisLimpiador>(StringComparer.OrdinalIgnoreCase);

            var db = new DataBase(_connStr);
            try
            {
                // Tablas tildadas: son la semilla del borrado
                foreach (var cfg in configuradas)
                {
                    var a = new TablaAnalisisLimpiador { NombreCompleto = cfg.NombreCompleto, Estado = "Baja" };
                    string q = Quote(cfg.NombreCompleto);
                    try
                    {
                        db.CommandText = $"SELECT COUNT(*) FROM {q} WHERE {CondicionBajaHelper.ToCondicionSql(cfg.CondicionesBaja, QuoteCampo)}";
                        a.RegistrosBaja = Convert.ToInt32(db.Scalar());
                        db.CommandText = $"SELECT COUNT(*) FROM {q} WHERE {CondicionBajaHelper.ToNegacionSql(cfg.CondicionesBaja, QuoteCampo)}";
                        a.RegistrosActivos = Convert.ToInt32(db.Scalar());
                    }
                    catch (Exception ex)
                    {
                        a.Estado = "Sin campo";
                        a.Conflictos.Add($"Error al evaluar condición: {ex.Message}");
                    }
                    porTabla[cfg.NombreCompleto] = a;
                    result.Tablas.Add(a);
                }

                // Tablas que sólo alcanza el arrastre: el usuario no las eligió pero se van a tocar
                var fueraDeSeleccion = cierre.Where(n => !dictCfg.ContainsKey(n))
                                             .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                             .ToList();
                foreach (var nombre in fueraDeSeleccion)
                {
                    var a = new TablaAnalisisLimpiador
                    {
                        NombreCompleto = resolver[nombre].NombreCompleto,
                        Estado = "Cascada (fuera de selección)"
                    };
                    porTabla[nombre] = a;
                    result.Tablas.Add(a);
                }

                // Arrastre de un nivel + huérfanas que ya cuelgan hoy
                var huerfanasPrevias = new List<string>();
                foreach (var grupoFK in FKsDelBarrido(cierre, relaciones))
                {
                    var cols = grupoFK.ToList();
                    string tablaHija = cols[0].OrigenCompleto, tablaPadre = cols[0].DestinoCompleto;
                    if (EsAutoFKDegenerada(cols)) continue;

                    string qHija = Quote(resolver[tablaHija].NombreCompleto);
                    string qPadre = Quote(resolver[tablaPadre].NombreCompleto);
                    string join = JoinFK(cols, "p", "h");
                    string detalleFK = string.Join(", ", cols.Select(f => $"{f.ColumnaOrigen} → {f.ColumnaDestino}"));

                    // Filas de la hija que apuntan a un padre en baja: se las lleva el arrastre.
                    // Sólo se puede contar cuando el padre está tildado y tiene condición.
                    if (dictCfg.TryGetValue(tablaPadre, out var cfgPadre) && porTabla.TryGetValue(tablaHija, out var aHija))
                    {
                        try
                        {
                            db.CommandText =
                                $"SELECT COUNT(*) FROM {qHija} h WHERE EXISTS (SELECT 1 FROM {qPadre} p WHERE {join} " +
                                $"AND {CondicionBajaHelper.ToCondicionSql(cfgPadre.CondicionesBaja, QuoteCampoAlias("p"))})";
                            int cant = Convert.ToInt32(db.Scalar());
                            if (cant > 0)
                            {
                                aHija.CascadaEstimada += cant;
                                aHija.Conflictos.Add($"≥{cant} fila(s) se eliminan por cascada desde '{tablaPadre}' (FK: {detalleFK})");
                            }
                        }
                        catch { }
                    }

                    // Huérfanas preexistentes: el barrido no distingue las que ya colgaban.
                    if (hayBarridoHuerfanos) continue;
                    try
                    {
                        string noNulos = string.Join(" AND ", cols.Select(f => $"h.{QuoteCampo(f.ColumnaOrigen)} IS NOT NULL"));
                        db.CommandText =
                            $"SELECT COUNT(*) FROM {qHija} h WHERE {noNulos} " +
                            $"AND NOT EXISTS (SELECT 1 FROM {qPadre} p WHERE {join})";
                        int cant = Convert.ToInt32(db.Scalar());
                        if (cant > 0)
                            huerfanasPrevias.Add($"{tablaHija} ({cant} por {detalleFK})");
                    }
                    catch { }
                }

                // Filas retenidas por el límite de esquema: las referencia algo de afuera, así
                // que no se borran — y la tabla de afuera no se toca.
                foreach (var kv in externas)
                {
                    if (!porTabla.TryGetValue(kv.Key, out var aPadre)) continue;
                    string qPadre = Quote(resolver[kv.Key].NombreCompleto);
                    foreach (var grupoFK in kv.Value)
                    {
                        var cols = grupoFK.ToList();
                        string qHija = Quote(cols[0].OrigenCompleto);
                        string join = JoinFK(cols, "p", "h");
                        string condBaja = dictCfg.TryGetValue(kv.Key, out var cfgP) && cfgP.TieneCondiciones
                            ? $" AND {CondicionBajaHelper.ToCondicionSql(cfgP.CondicionesBaja, QuoteCampoAlias("p"))}"
                            : "";
                        try
                        {
                            db.CommandText =
                                $"SELECT COUNT(*) FROM {qPadre} p WHERE EXISTS " +
                                $"(SELECT 1 FROM {qHija} h WHERE {join}){condBaja}";
                            int cant = Convert.ToInt32(db.Scalar());
                            if (cant > 0)
                            {
                                aPadre.RetenidasPorExterno += cant;
                                aPadre.Conflictos.Add(
                                    $"{cant} fila(s) NO se eliminan: las referencia '{cols[0].OrigenCompleto}', " +
                                    "fuera del esquema en alcance");
                            }
                        }
                        catch { }
                    }
                }

                if (fueraDeSeleccion.Any())
                    result.Advertencias.Add(
                        $"La cascada alcanza {fueraDeSeleccion.Count} tabla(s) que NO tildaste, dentro del mismo esquema: " +
                        $"{string.Join(", ", fueraDeSeleccion)}. Es lo que evita dejar referencias colgando.");

                if (externas.Any())
                    result.Advertencias.Add(
                        $"Límite de esquema: {externas.Count} tabla(s) del alcance están referenciadas desde fuera " +
                        $"({string.Join(", ", externas.Select(k => $"{k.Key} ← {string.Join("/", k.Value.Select(g => g.First().OrigenCompleto).Distinct(StringComparer.OrdinalIgnoreCase))}"))}). " +
                        "Esas filas se retienen y nada de afuera se toca — mirá la columna 'Retenidas'. " +
                        "Para limpiarlas hay que incluir el otro esquema en el alcance.");

                if (huerfanasPrevias.Any())
                    result.Advertencias.Add(
                        $"Ya hay filas huérfanas en la base antes de limpiar ({huerfanasPrevias.Count} FK afectada(s)): " +
                        $"{string.Join(", ", huerfanasPrevias)}. El barrido también se las va a llevar.");

                if (noResueltos.Any())
                    result.Advertencias.Add(
                        $"⚠ {noResueltos.Count} tabla(s) del arrastre no están en el catálogo cargado: " +
                        $"{string.Join(", ", noResueltos.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}. " +
                        "Quedan FUERA del script y hay que revisarlas a mano.");

                // La captura del conjunto de una tabla necesita su PK. Sin ella no se puede
                // identificar qué filas borrar, y adivinar es exactamente lo que no hay que hacer:
                // se bloquea la generación como en el modo Bloquear.
                var sinPKNecesaria = cierre
                    .Where(n => !noResueltos.Contains(n) && !(resolver[n].CamposPK?.Any() ?? false))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (sinPKNecesaria.Any())
                {
                    result.HayConflictosBloquantes = true;
                    result.Advertencias.Add(
                        $"⛔ {sinPKNecesaria.Count} tabla(s) del alcance no tienen PK detectada y sin eso no se " +
                        $"puede armar su conjunto de borrado: {string.Join(", ", sinPKNecesaria)}. " +
                        "Usá '🔍 Detectar campos automáticamente' o cargá la PK a mano (doble clic en la tabla). " +
                        "No se genera el script hasta resolverlo — antes que adivinar, no borrar.");
                }

                var enCiclo = TablasEnCiclo(cierre, relaciones);
                if (enCiclo.Any())
                    result.Advertencias.Add(
                        $"{enCiclo.Count} tabla(s) forman ciclo de FK: {string.Join(", ", enCiclo.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}. " +
                        "Sólo a ellas se les suspende la validación durante el borrado; el resto corre con las FK activas.");
            }
            finally { db.CloseConnection(); }

            if (_conn.Motor != TipoMotor.MS_SQL)
                result.Advertencias.Add(
                    "Este modo suspende la validación de FK mientras borra. Fuera de MS SQL eso queda a medias: " +
                    "en Postgres 'SET CONSTRAINTS ALL DEFERRED' sólo afecta constraints DEFERRABLE (no es el default) " +
                    "y en DB2 el script sólo deja los ALTER comentados. Revisá y ajustá esos pasos a mano.");

            var sinPK = configuradas.Where(c => !c.TienePK).Select(c => c.NombreCompleto).ToList();
            if (sinPK.Any())
                result.Advertencias.Add($"Sin PK detectada ({sinPK.Count}): {string.Join(", ", sinPK)}. Usá '🔍 Detectar campos automáticamente' o cargala a mano (doble clic en la tabla).");

            var compuestasReorden = configuradas.Where(c => c.ReordenarIds && !c.PKSimple).Select(c => c.NombreCompleto).ToList();
            if (compuestasReorden.Any())
                result.Advertencias.Add($"Quedan fuera del reordenamiento de IDs por PK compuesta o ausente ({compuestasReorden.Count}): {string.Join(", ", compuestasReorden)}.");

            return result;
        }

        // ── Generación de script ──────────────────────────────────────────

        /// <param name="universoTablas">Ver <see cref="Analizar"/>: universo de tablas de la base entera.</param>
        public string GenerarScript(List<TablaConfigLimpiador> configs, AnalisisResultLimpiador analysis, ModoConflicto modo,
                                    List<TablaConfigLimpiador> universoTablas = null, OpcionesBarrido opciones = null)
        {
            var sb = new StringBuilder();
            // Sólo el modo iterativo lo llena. Limpiarlo acá evita que un informe quedado de una
            // generación anterior se muestre al final de una corrida de otro modo.
            _chequeoFKs = null;
            var configuradas = configs.Where(c => c.Incluir && c.TieneCondiciones).ToList();
            // Igual que en Analizar: el barrido de huérfanos y el reordenamiento alcanzan a TODAS
            // las tildadas, tengan condición de baja o no.
            var enAlcance = configs.Where(c => c.Incluir).ToList();
            var nombresTablas = enAlcance.Select(c => c.Nombre).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var relaciones = GetRelaciones(universoTablas?.Select(t => t.Nombre).ToList() ?? nombresTablas);
            // El modo iterativo NO usa el barrido de huérfanos: su paso 3 hace ese trabajo con un
            // criterio más conservador —exige que TODOS los datos estén rotos, no uno— y correr los
            // dos se llevaría justamente las filas que él decidió conservar.
            bool depurar = opciones != null && opciones.DepurarHuerfanos
                           && modo != ModoConflicto.BorradoIterativo;
            // Un solo viaje al catálogo: lo necesitan el barrido (tipos de las columnas de FK) y
            // el reordenamiento (IDENTITY y columnas insertables).
            var infoCols = GetInfoColumnas(nombresTablas);
            var motor = _conn.Motor.ToString().Replace("_", " ");

            sb.AppendLine("-- ════════════════════════════════════════════════════════════");
            sb.AppendLine($"-- LIMPIADOR DE BD — Generado por QueryAnalyzer");
            sb.AppendLine($"-- Conexión : {_conn.Nombre} | Motor: {motor}");
            var esquemas = enAlcance.Select(c => c.Schema)
                                    .Where(s => !string.IsNullOrEmpty(s))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(s => s)
                                    .ToList();
            sb.AppendLine($"-- Esquema  : {(esquemas.Any() ? string.Join(", ", esquemas) : "(sin esquema)")}");
            sb.AppendLine($"-- Modo FK  : {modo}{DescripcionModo(modo)}");
            sb.AppendLine($"-- Huérfanos: {(modo == ModoConflicto.BorradoIterativo ? "los barre el paso 3, en loop hasta que no quede nada" : depurar ? "se depuran las relaciones truncadas" : "no se depuran")}");
            if (modo == ModoConflicto.BorradoIterativo)
            {
                sb.AppendLine("--");
                sb.AppendLine("-- ⚠ ESTE MODO BORRA FILAS ACTIVAS. Una fila viva que sólo apuntaba a registros");
                sb.AppendLine("--   dados de baja queda apuntando a la nada y se elimina en el paso 3.");
            }
            sb.AppendLine("-- REVISAR CUIDADOSAMENTE ANTES DE EJECUTAR");
            sb.AppendLine("-- ════════════════════════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine(InicioTransaccion());
            sb.AppendLine();

            var ordenDEL = OrdenTopologico(configuradas, relaciones, hijosAntes: true);
            // Indexado sobre el alcance completo, no sólo las que tienen condición de baja: el
            // remapeo de IDs tiene que poder resolver a cualquier hija tildada. Los usos que sí
            // dependen de la condición la chequean aparte con TieneCondiciones.
            var dictCfg = IndexarPorNombreCompleto(enAlcance);
            var dictAnalisis = analysis.Tablas.ToDictionary(t => t.NombreCompleto, t => t, StringComparer.OrdinalIgnoreCase);

            // ── PASO 1: Cascada ──────────────────────────────────────────
            if (modo == ModoConflicto.BajaEnCascada)
            {
                sb.AppendLine("-- ── PASO 1: Dar de baja en cascada a hijos activos ─────────────────");
                // Padres antes que hijos: la baja se propaga hacia abajo, así que cuando se
                // marca una hija su padre ya tiene que estar marcado. Con el orden de carga
                // (alfabético) una hija se evaluaba contra el estado viejo del padre y quedaba
                // sin marcar, y después el DELETE del padre fallaba por FK.
                var ordenCascada = OrdenTopologico(configuradas, relaciones, hijosAntes: false);
                foreach (var cfg in ordenCascada)
                {
                    // La condición del padre va calificada con el alias 'p': dentro del EXISTS
                    // padre e hija están las dos en scope y suelen compartir el nombre de columna.
                    string condPadre = CondicionBajaHelper.ToCondicionSql(cfg.CondicionesBaja, QuoteCampoAlias("p"));
                    string qPadre = Quote(cfg.NombreCompleto);

                    // Agrupar por constraint: una FK compuesta llega como una fila por columna.
                    // Después, un UPDATE por juego de columnas: dos constraints distintos sobre
                    // las mismas columnas producirían la misma sentencia dos veces.
                    var fksHaciaEsta = relaciones
                        .Where(r => string.Equals(r.DestinoCompleto, cfg.NombreCompleto, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(r => r.ClaveFK, StringComparer.OrdinalIgnoreCase)
                        .GroupBy(g => string.Join("|", g.OrderBy(r => r.ColumnaOrigen, StringComparer.OrdinalIgnoreCase)
                                                        .Select(r => $"{r.OrigenCompleto}.{r.ColumnaOrigen}→{r.ColumnaDestino}")),
                                 StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First());

                    foreach (var grupoFK in fksHaciaEsta)
                    {
                        var colsFK = grupoFK.ToList();
                        if (!dictCfg.TryGetValue(colsFK[0].OrigenCompleto, out var cfgHija) || !cfgHija.TieneCondiciones) continue;
                        string qHija = Quote(cfgHija.NombreCompleto);

                        // La hija se califica con su nombre completo (no alias): así el UPDATE
                        // es SQL estándar y vale en los cuatro motores.
                        string join = JoinFK(colsFK, "p", qHija);
                        string existe = $"EXISTS (SELECT 1 FROM {qPadre} p WHERE {join} AND {condPadre})";

                        // Construir SET para la hija
                        var setClauses = cfgHija.CondicionesBaja
                            .Where(c => !string.IsNullOrEmpty(c.ValorSet))
                            .Select(c => $"{QuoteCampo(c.Campo)} = {c.ValorSet}")
                            .ToList();

                        if (colsFK.Count > 1)
                            sb.AppendLine($"-- FK compuesta ({colsFK.Count} columnas): {grupoFK.Key}");

                        if (setClauses.Any())
                        {
                            sb.AppendLine($"UPDATE {qHija} SET {string.Join(", ", setClauses)}");
                            sb.AppendLine($"    WHERE {existe};");
                        }
                        else
                        {
                            sb.AppendLine($"-- AJUSTAR: cascada a {cfgHija.NombreCompleto} sin ValorSet configurado");
                            sb.AppendLine($"-- UPDATE {qHija} SET <campo_baja> = <valor> WHERE {existe};");
                        }
                        sb.AppendLine();
                    }
                }
            }

            // Numeración de los pasos que siguen: el borrado en cascada gasta cuatro pasos antes
            // de llegar acá, los demás modos sólo dos.
            int pasoSiguiente = 3;

            if (modo == ModoConflicto.BorradoEnCascada)
            {
                GenerarBorradoEnCascada(sb, configuradas, ordenDEL, relaciones, universoTablas, opciones);
                pasoSiguiente = 5;
            }
            else if (modo == ModoConflicto.BorradoSeguro)
            {
                GenerarBorradoSeguro(sb, configuradas, relaciones, opciones);
                pasoSiguiente = 5;
            }
            else if (modo == ModoConflicto.BorradoIterativo)
            {
                // Gasta cuatro pasos y se depura solo: el barrido del paso 3 ya cubre lo que haría
                // GenerarBarridoHuerfanos, y con un criterio más conservador. Llamarlo además
                // volvería a barrer con el criterio viejo —basta UNA FK rota— y se llevaría
                // justamente las filas que este modo decidió conservar.
                GenerarBorradoIterativo(sb, configuradas, enAlcance, relaciones, universoTablas, opciones, infoCols);
                pasoSiguiente = 5;
            }
            else
            {
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
            }

            // ── Depurar relaciones truncadas ────────────────────────────
            // Va después de los DELETEs y antes del reordenamiento: primero se termina de sacar
            // todo lo que sobra, y recién ahí se compactan los IDs de lo que quedó.
            if (depurar)
            {
                GenerarBarridoHuerfanos(sb, enAlcance, relaciones, universoTablas, opciones, infoCols, pasoSiguiente);
                pasoSiguiente++;
            }

            // ── Reordenar IDs ───────────────────────────────────────────
            // Sólo PK de una columna: ROW_NUMBER() da un entero y se asigna a una columna.
            int pasoReorden = pasoSiguiente;
            var conReorden = enAlcance.Where(c => c.ReordenarIds && c.PKSimple).ToList();
            var omitidasReorden = enAlcance.Where(c => c.ReordenarIds && !c.PKSimple).ToList();

            // El remapeo sólo alcanza a las hijas directas. Si la columna que se le reescribe
            // a una hija forma parte de la PK de esa hija, el cambio tendría que propagarse
            // también a las FKs que apuntan a la hija — y eso no se hace. Detectarlo y no
            // renumerar esa tabla, en vez de dejar FKs colgando.
            var motivoCadena = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cfg in conReorden)
            {
                string pkPadre = cfg.CamposPK[0];
                foreach (var fk in relaciones.Where(r =>
                    string.Equals(r.DestinoCompleto, cfg.NombreCompleto, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.ColumnaDestino, pkPadre, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!dictCfg.TryGetValue(fk.OrigenCompleto, out var cfgHija)) continue;
                    bool esParteDePKHija = cfgHija.CamposPK
                        .Any(c => string.Equals(c, fk.ColumnaOrigen, StringComparison.OrdinalIgnoreCase));
                    if (!esParteDePKHija) continue;

                    var nietas = relaciones.Where(r =>
                        string.Equals(r.DestinoCompleto, fk.OrigenCompleto, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.ColumnaDestino, fk.ColumnaOrigen, StringComparison.OrdinalIgnoreCase))
                        .Select(r => r.OrigenCompleto)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (!nietas.Any()) continue;

                    motivoCadena[cfg.NombreCompleto] =
                        $"renumerarla cambia {fk.OrigenCompleto}.{fk.ColumnaOrigen}, que es parte de la PK de {fk.TablaOrigen}, " +
                        $"y hay FKs apuntando ahí desde: {string.Join(", ", nietas)}";
                    break;
                }
            }

            var omitidasCadena = conReorden.Where(c => motivoCadena.ContainsKey(c.NombreCompleto)).ToList();
            conReorden = conReorden.Where(c => !motivoCadena.ContainsKey(c.NombreCompleto)).ToList();

            // Renumerar una PK IDENTITY exige rehacer la tabla, y eso no se puede si tiene columnas
            // que no admiten INSERT. La tabla queda FUERA del paso entera: emitir sólo su bloque
            // comentado dejaría corriendo los UPDATE de las hijas, que apuntarían a IDs nuevos que
            // el padre nunca llegó a adoptar.
            var motivoIdentity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cfg in conReorden)
            {
                string motivo = MotivoBloqueoIdentity(cfg, infoCols);
                if (motivo != null) motivoIdentity[cfg.NombreCompleto] = motivo;
            }
            var omitidasIdentity = conReorden.Where(c => motivoIdentity.ContainsKey(c.NombreCompleto)).ToList();
            conReorden = conReorden.Where(c => !motivoIdentity.ContainsKey(c.NombreCompleto)).ToList();

            if (conReorden.Any() || omitidasReorden.Any() || omitidasCadena.Any() || omitidasIdentity.Any())
            {
                sb.AppendLine($"-- ── PASO {pasoReorden}: Reordenamiento de IDs ──────────────────────────────────");
                foreach (var cfg in omitidasReorden)
                    sb.AppendLine($"-- OMITIDO del reordenamiento ({(cfg.TienePK ? $"PK compuesta: {cfg.ResumenPK}" : "sin PK detectada")}): {Quote(cfg.NombreCompleto)}");
                foreach (var cfg in omitidasCadena)
                    sb.AppendLine($"-- OMITIDO del reordenamiento: {Quote(cfg.NombreCompleto)} — {motivoCadena[cfg.NombreCompleto]}");
                foreach (var cfg in omitidasIdentity)
                    sb.AppendLine($"-- OMITIDO del reordenamiento: {Quote(cfg.NombreCompleto)} — {motivoIdentity[cfg.NombreCompleto]}");
                if (omitidasReorden.Any() || omitidasCadena.Any() || omitidasIdentity.Any()) sb.AppendLine();
            }

            if (conReorden.Any())
            {
                // La suspensión tiene que cubrir TODA tabla que el remapeo escribe, no sólo las
                // tildadas: el UPDATE alcanza a cualquier hija del grafo de FKs, y una hija fuera
                // de la selección conservaba su FK activa y hacía fallar el UPDATE.
                var tocadas = TablasTocadasPorReorden(conReorden, relaciones, dictCfg);
                sb.AppendLine(DeshabilitarConstraints(tocadas));
                sb.AppendLine();

                var ordenID = OrdenTopologico(conReorden, relaciones, hijosAntes: false);
                foreach (var cfg in ordenID)
                    sb.Append(GenerarBloqueReordenamiento(cfg, relaciones, dictCfg, infoCols));

                sb.AppendLine(RehabilitarConstraints(tocadas));
                sb.AppendLine(ResetSecuencias(conReorden, infoCols));
            }

            sb.AppendLine();
            // Los comentarios van en línea propia a propósito: pegados a la sentencia, ParsearSentencias
            // —que corta por el ';' de fin de línea— no la corta y la entrega con el comentario dentro.
            sb.AppendLine("-- ── Confirmar o revertir ────────────────────────────────────────────");
            sb.AppendLine("-- Ejecutado desde QueryAnalyzer estas dos líneas se ignoran: la aplicación maneja");
            sb.AppendLine("-- la transacción y te pregunta al terminar. Valen al pegar el script en SSMS.");
            sb.AppendLine("-- Para confirmar en SSMS: comentá el ROLLBACK y descomentá el COMMIT.");
            sb.AppendLine("-- COMMIT;");
            sb.AppendLine("ROLLBACK;");

            return sb.ToString();
        }

        // ── Borrado en cascada ────────────────────────────────────────────

        /// <summary>
        /// Emite el modo BorradoEnCascada en dos fases: primero CAPTURA en tablas temporales qué
        /// filas se van a eliminar, propagando de padres a hijos; después BORRA por JOIN contra
        /// esos conjuntos, de hijos a padres y con las FK activas.
        ///
        /// La clave es que la condición es POSITIVA: una fila se borra sólo si fue identificada
        /// como parte del conjunto. La versión anterior hacía lo contrario —borraba lo que "no
        /// encontraba padre"— y eso falla abierto: ante cualquier desajuste entre los supuestos y
        /// los datos (un centinela 0 en vez de NULL, una FK mal detectada) no borraba de menos,
        /// vaciaba la tabla. Pasó con PreguntasPorCuestionario.
        ///
        /// Como se borra en el orden correcto, tampoco hace falta suspender las FK salvo en las
        /// tablas que forman ciclo: el motor queda de árbitro toda la corrida y un cierre
        /// incompleto se manifiesta como error de FK, no como borrado silencioso de más.
        /// </summary>
        private void GenerarBorradoEnCascada(
            StringBuilder sb,
            List<TablaConfigLimpiador> configuradas,
            List<TablaConfigLimpiador> ordenDEL,
            List<FKRelacionLimpiador> relaciones,
            List<TablaConfigLimpiador> universoTablas,
            OpcionesBarrido opciones)
        {
            var semillas = configuradas
                .Where(c => c.TieneCondiciones)
                .ToDictionary(
                    c => c.NombreCompleto,
                    c => CondicionBajaHelper.ToCondicionSql(c.CondicionesBaja, QuoteCampoAlias("h")),
                    StringComparer.OrdinalIgnoreCase);

            GenerarBarridoConjuntos(sb, semillas, configuradas, relaciones, universoTablas, "del",
                new EtiquetasBarrido
                {
                    Captura  = "PASO 1: Capturar qué se va a eliminar (padres → hijos)",
                    Intro    = "-- Nada se borra todavía. Cada #del_… junta las claves de las filas condenadas.\n" +
                               "-- Una fila entra sólo si se la identifica positivamente: si un supuesto está mal,\n" +
                               "-- la fila NO entra y sobrevive. Nunca al revés.",
                    Freno    = "PASO 2",
                    Borrado  = "PASO 3: Eliminar (hijos → padres, CON las FK activas)",
                    Limpieza = "PASO 4: Descartar las tablas temporales"
                },
                opciones != null && opciones.FrenoSeguridad);
        }

        /// <summary>
        /// Borrado físico que respeta lo que declaran las FK. Es la contracara de
        /// <see cref="GenerarBorradoEnCascada"/>: acá no se arrastra a nadie — una fila se borra
        /// sólo si está dada de baja Y ninguna fila viva la referencia.
        ///
        /// El motivo es que las FK de esta base son NO ACTION sin excepción (135 de 135 en SIEP),
        /// o sea que en cada relación el motor pide justamente lo contrario: no borrar un padre que
        /// todavía tiene hijos. La cascada emulaba un ON DELETE CASCADE que ninguna relación
        /// autoriza, y por eso una sola fila en baja ("Sin Seccion", IdSeccion 0) se llevó puestas
        /// las 41 filas activas de PreguntasPorCuestionario.
        ///
        /// La captura va de hijos a padres, al revés que en la cascada: la guarda de un padre
        /// necesita saber cuáles de sus hijos también se van, y para eso el conjunto del hijo tiene
        /// que estar armado antes. Con ese orden una sola pasada cierra la cadena — si los hijos que
        /// retenían al padre son todos condenados, el padre queda libre en la misma corrida.
        ///
        /// Tampoco hace falta suspender constraints ni tratar los ciclos aparte, como sí hace el
        /// barrido en cascada: nunca se borra un padre con hijos vivos, así que no hay orden que
        /// pueda violar una FK y el motor no tiene de qué quejarse.
        ///
        /// Se espera que retenga de más, no de menos: una fila en baja que sobrevive porque algo
        /// vivo la referencia es el comportamiento correcto de este modo, no una falla.
        /// </summary>
        private void GenerarBorradoSeguro(
            StringBuilder sb,
            List<TablaConfigLimpiador> configuradas,
            List<FKRelacionLimpiador> relaciones,
            OpcionesBarrido opciones)
        {
            const string prefijo = "del";

            var conCondicion = configuradas.Where(c => c.TieneCondiciones).ToList();
            var sinPK        = conCondicion.Where(c => !c.TienePK).ToList();
            var trabajables  = conCondicion.Where(c => c.TienePK).ToList();

            var resolver = IndexarPorNombreCompleto(trabajables);
            var orden    = OrdenTopologico(trabajables, relaciones, hijosAntes: true);

            sb.AppendLine("-- ── PASO 1: Capturar qué se va a eliminar (hijos → padres) ────────────");
            sb.AppendLine("-- Nada se borra todavía. Cada #del_… junta las claves de las filas condenadas.");
            sb.AppendLine("-- Una fila entra sólo si está dada de baja Y ninguna fila viva la referencia:");
            sb.AppendLine("-- ante la duda queda afuera y sobrevive. Nunca al revés.");
            sb.AppendLine("-- Sólo se tocan las tablas tildadas: sin arrastre, el alcance no se expande solo.");
            sb.AppendLine();

            if (sinPK.Any())
            {
                sb.AppendLine("-- ⚠ SIN PK: no hay con qué identificar las filas, así que estas tablas");
                sb.AppendLine("--   quedaron FUERA del script. Revisalas a mano:");
                foreach (var c in sinPK.OrderBy(x => x.NombreCompleto, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine($"--   • {c.NombreCompleto}");
                sb.AppendLine();
            }

            var capturadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cfg in orden)
            {
                string nombre = cfg.NombreCompleto;
                string tmp    = NombreTemporal(nombre, prefijo);
                string cols   = string.Join(", ", ClaveConjunto(cfg).Select(c => $"h.{QuoteCampo(c)}"));
                string cond   = CondicionBajaHelper.ToCondicionSql(cfg.CondicionesBaja, QuoteCampoAlias("h"));
                string guardas = GuardasHijasVivas(nombre, relaciones, resolver, capturadas, prefijo);

                sb.AppendLine($"-- Conjunto de {nombre}");
                sb.AppendLine(DropTemporal(tmp));
                sb.AppendLine(SelectInto(cols, tmp, $"{Quote(nombre)} h"));
                // Las guardas son continuación del mismo WHERE: el ';' va sólo al final, porque el
                // ejecutor corta en cada línea terminada en ';' (ver ParsearSentencias).
                sb.AppendLine($"    WHERE {cond}{guardas};");
                sb.AppendLine();
                capturadas.Add(nombre);
            }

            sb.Append(GenerarFrenoSeguridad(orden.Select(c => c.NombreCompleto), resolver, prefijo,
                                            "PASO 2", opciones != null && opciones.FrenoSeguridad));

            sb.AppendLine("-- ── PASO 3: Eliminar (hijos → padres, CON las FK activas) ─────────────");
            sb.AppendLine("-- Mismo orden que la captura: acá los hijos ya vienen primero.");
            foreach (var cfg in orden)
                sb.Append(GenerarDeleteDesdeConjunto(cfg.NombreCompleto, resolver, prefijo));
            sb.AppendLine();

            sb.AppendLine("-- ── PASO 4: Descartar las tablas temporales ───────────────────────────");
            foreach (var cfg in orden)
                sb.AppendLine(DropTemporal(NombreTemporal(cfg.NombreCompleto, prefijo)));
            sb.AppendLine();
        }

        /// <summary>
        /// Guardas que impiden borrar una fila que todavía tiene hijos vivos: un NOT EXISTS por cada
        /// FK que apunte a la tabla, venga de donde venga (también de otro esquema — una referencia
        /// de afuera retiene igual).
        ///
        /// Si la hija ya tiene su conjunto armado, los hijos condenados se descuentan del NOT EXISTS:
        /// sin eso una fila quedaría retenida por hijos que se van en la misma corrida y no se
        /// borraría nunca. Si la hija no tiene conjunto —no está tildada, o no tiene condición de
        /// baja— cualquier hijo alcanza para retener al padre.
        ///
        /// Es la misma idea de <see cref="GuardasExternasAlias"/>, que ya retiene lo referenciado
        /// desde fuera del esquema, extendida a todas las relaciones.
        /// </summary>
        private string GuardasHijasVivas(
            string tablaPadre,
            List<FKRelacionLimpiador> relaciones,
            Dictionary<string, TablaConfigLimpiador> resolver,
            HashSet<string> capturadas,
            string prefijo)
        {
            var sb = new StringBuilder();
            var emitidas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var grupos = relaciones
                .Where(r => string.Equals(r.DestinoCompleto, tablaPadre, StringComparison.OrdinalIgnoreCase)
                            // Auto-referencia: el conjunto propio se está construyendo en esta misma
                            // sentencia, así que no hay contra qué descontar los hijos condenados.
                            // Queda sin guarda; una jerarquía dentro de una tabla no se cubre acá.
                            && !string.Equals(r.OrigenCompleto, tablaPadre, StringComparison.OrdinalIgnoreCase))
                .GroupBy(r => r.ClaveFK, StringComparer.OrdinalIgnoreCase);

            foreach (var grupoFK in grupos)
            {
                var cols = grupoFK.OrderBy(f => f.ColumnaOrigen, StringComparer.OrdinalIgnoreCase).ToList();
                string hija = cols[0].OrigenCompleto;

                // El esquema declara la misma relación dos veces en doce pares de tablas: dos
                // constraints sobre las mismas columnas (RespuestasPreguntas → MPreguntas tiene
                // FK__Respuesta__IdPre__25FEC4E9 y FK_RespuestasPreguntas_Pregunta). Sin esto la
                // guarda sale duplicada. Las que difieren en columnas —Gestor y Titular hacia
                // PersonaFisica— son relaciones distintas y tienen que quedar las dos.
                string firma = hija + "|" + string.Join(",", cols.Select(f => $"{f.ColumnaOrigen}→{f.ColumnaDestino}"));
                if (!emitidas.Add(firma)) continue;

                string excluir = "";
                if (capturadas.Contains(hija) && resolver.TryGetValue(hija, out var cfgHija))
                {
                    var pkHija = ClaveConjunto(cfgHija);
                    if (pkHija.Any())
                    {
                        string anti = string.Join(" AND ", pkHija.Select(c => $"y.{QuoteCampo(c)} = e.{QuoteCampo(c)}"));
                        excluir = $"\n                        AND NOT EXISTS (SELECT 1 FROM {NombreTemporal(hija, prefijo)} y WHERE {anti})";
                    }
                }

                sb.Append($"\n      AND NOT EXISTS (SELECT 1 FROM {Quote(hija)} e WHERE {JoinFK(cols, "h", "e")}{excluir})");
            }

            return sb.ToString();
        }

        // ── Borrado iterativo ─────────────────────────────────────────────

        /// <summary>
        /// Marcadores del bloque que <see cref="EjecutarScript"/> repite. Son comentarios para que
        /// el script siga siendo pegable en SSMS —donde corren una sola vuelta— pero
        /// <see cref="ParsearSentencias"/> los reconoce y los deja pasar como sentencia propia.
        /// </summary>
        public const string MarcaLoopInicio = "-- QA:LOOP-INICIO";
        public const string MarcaLoopFin    = "-- QA:LOOP-FIN";

        /// <summary>
        /// El modo que rompe la integridad a propósito y la repara después. Cuatro pasos:
        /// suspender las constraints, borrar TODAS las bajas sin mirar a nadie, barrer en loop lo
        /// que quedó totalmente desconectado, y rehabilitar sin revalidar.
        ///
        /// Es lo contrario de los otros dos modos, que nunca dejan la base inconsistente ni por un
        /// instante y para eso necesitan temporales, orden topológico, cierre transitivo y trato
        /// aparte de los ciclos. Acá nada de eso hace falta: con las constraints suspendidas el
        /// orden no importa, y la convergencia la da el loop en vez del razonamiento topológico.
        ///
        /// Lo que sí borra de más respecto del borrado seguro son las filas ACTIVAS que sólo
        /// apuntaban a registros dados de baja. Después del paso 2 quedan apuntando a la nada, y el
        /// criterio del modo es que eso es basura. Es una decisión del usuario, no un descuido:
        /// ver <see cref="ExprDesconectadaTotal"/> para lo conservador que es igual el barrido.
        /// </summary>
        private void GenerarBorradoIterativo(
            StringBuilder sb,
            List<TablaConfigLimpiador> configuradas,
            List<TablaConfigLimpiador> enAlcance,
            List<FKRelacionLimpiador> relaciones,
            List<TablaConfigLimpiador> universoTablas,
            OpcionesBarrido opciones,
            Dictionary<string, List<ColumnaInfoLimpiador>> infoCols)
        {
            var limite   = EsquemasEnAlcance(enAlcance);
            var cierre   = CierreCascada(enAlcance.Select(c => c.NombreCompleto), relaciones, limite);
            var resolver = ResolverCierre(cierre, enAlcance, universoTablas, out var noResueltos);
            var externas = ReferenciasExternas(cierre, relaciones, limite);
            bool centinelas = opciones == null || opciones.CentinelasComoSinReferencia;

            var tablasCierre = cierre.Where(n => !noResueltos.Contains(n))
                                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                     .ToList();

            // El barrido alcanza a todo el cierre, no sólo a lo tildado, así que el catálogo de
            // columnas tiene que cubrirlo entero: sin el tipo no se emiten los centinelas y una FK
            // en 0 pasaría por "apunta a algo", saldría a buscar el padre 0 y contaría como rota.
            // En MS SQL GetInfoColumnas ya trae la base completa; esto es para los otros motores.
            var faltan = tablasCierre.Where(n => !infoCols.ContainsKey(n)).ToList();
            if (faltan.Any())
                foreach (var kv in GetInfoColumnas(faltan.Select(NombreCortoDe).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
                    if (!infoCols.ContainsKey(kv.Key)) infoCols[kv.Key] = kv.Value;

            var conCondicion = configuradas.Where(c => c.TieneCondiciones).ToList();

            // ── PASO 1: suspender la validación ─────────────────────────
            sb.AppendLine("-- ── PASO 1: Suspender la validación de FK ─────────────────────────────");
            sb.AppendLine("-- A partir de acá la base queda inconsistente A PROPÓSITO: los pasos 3 y 4 la reparan.");
            sb.AppendLine("-- Si algo corta en el medio, la transacción revierte y no queda nada a medio hacer.");
            sb.AppendLine();

            if (noResueltos.Any())
            {
                sb.AppendLine("-- ⚠ SIN RESOLVER: estas tablas del cierre no están en el catálogo cargado,");
                sb.AppendLine("--   así que quedaron FUERA del script. Revisalas a mano:");
                foreach (var n in noResueltos.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine($"--   • {n}");
                sb.AppendLine();
            }
            if (externas.Any())
            {
                sb.AppendLine($"-- ⚠ LÍMITE DE ESQUEMA: {externas.Count} tabla(s) están referenciadas desde fuera.");
                sb.AppendLine("--   Esas FK no se suspenden —son de otro esquema— así que siguen activas y las");
                sb.AppendLine("--   filas que protegen NO se borran. Es la única guarda que sobrevive en este modo.");
                foreach (var kv in externas.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine($"--   • {kv.Key} ← {string.Join(", ", kv.Value.Select(g => g.First().OrigenCompleto).Distinct(StringComparer.OrdinalIgnoreCase))}");
                sb.AppendLine();
            }

            sb.Append(DeshabilitarConstraints(tablasCierre.Select(n => resolver[n]).ToList()));
            sb.AppendLine();

            // ── PASO 2: borrar las bajas, sin mirar a nadie ─────────────
            var condPorTabla = conCondicion.ToDictionary(
                c => c.NombreCompleto,
                c => CondicionBajaHelper.ToCondicionSql(c.CondicionesBaja, QuoteCampoAlias(Quote(c.NombreCompleto))),
                StringComparer.OrdinalIgnoreCase);

            sb.AppendLine("-- ── PASO 2: Eliminar TODAS las bajas lógicas ──────────────────────────");
            sb.AppendLine("-- Sin orden, sin guardas y sin mirar quién las referencia: para eso se suspendió");
            sb.AppendLine("-- la validación. Sólo las tablas tildadas con condición de baja.");
            sb.AppendLine("-- Este modo NO usa el freno del 90%: se lleva todo lo que está de baja, y en una");
            sb.AppendLine("-- tabla donde eso es la mayoría el freno cortaría una limpieza correcta.");
            sb.AppendLine();

            if (!conCondicion.Any())
            {
                sb.AppendLine("-- (ninguna tabla tildada tiene condición de baja configurada)");
                sb.AppendLine();
            }

            foreach (var cfg in conCondicion.OrderBy(c => c.NombreCompleto, StringComparer.OrdinalIgnoreCase))
            {
                string q = Quote(cfg.NombreCompleto);
                sb.AppendLine($"DELETE FROM {q} WHERE {condPorTabla[cfg.NombreCompleto]}" +
                              $"{GuardasExternasAlias(cfg.NombreCompleto, externas, q)};");
            }
            sb.AppendLine();

            // ── PASO 3: barrer lo desconectado, en loop ─────────────────
            sb.AppendLine("-- ── PASO 3: Eliminar lo que quedó totalmente desconectado ─────────────");
            sb.AppendLine("-- Una fila se borra sólo si NINGUNO de sus datos encuentra con quién conectarse.");
            sb.AppendLine("-- Si sigue enganchada por un dato, sobrevive aunque otro esté roto.");
            sb.AppendLine("-- Un dato vacío (NULL o centinela) no cuenta ni a favor ni en contra.");
            sb.AppendLine($"-- Centinelas 0 y '' tratados como 'sin referencia': {(centinelas ? "SÍ" : "NO")}.");
            sb.AppendLine("--");
            sb.AppendLine("-- Ejecutado desde QueryAnalyzer, este bloque se REPITE hasta que una vuelta entera");
            sb.AppendLine("-- no borre nada. Pegado en SSMS corre una sola vez: repetilo a mano hasta que dé 0.");
            sb.AppendLine();
            sb.AppendLine(MarcaLoopInicio);

            int emitidos = 0;
            foreach (var nombre in tablasCierre)
            {
                string expr = ExprDesconectadaTotal(nombre, relaciones, infoCols, centinelas, Quote(nombre), limite);
                if (expr == null) continue;   // sin FKs salientes: un catálogo suelto no participa
                string q = Quote(nombre);
                sb.AppendLine($"DELETE FROM {q} WHERE {expr}{GuardasExternasAlias(nombre, externas, q)};");
                emitidos++;
            }
            if (emitidos == 0)
                sb.AppendLine("-- (ninguna tabla del alcance tiene FKs salientes: nada que barrer)");

            sb.AppendLine(MarcaLoopFin);
            sb.AppendLine();

            // ── PASO 4: devolver la validación ─────────────────────────
            sb.AppendLine("-- ── PASO 4: Rehabilitar las constraints ───────────────────────────────");
            sb.AppendLine("-- SIN revalidar lo que ya está: el criterio del paso 3 deja a propósito las filas");
            sb.AppendLine("-- rotas por un dato pero sanas por otro, así que revalidar revertiría la corrida.");
            sb.AppendLine("-- Las FK que queden violadas se informan al terminar, antes de que confirmes.");
            sb.AppendLine();
            sb.Append(RehabilitarConstraints(tablasCierre.Select(n => resolver[n]).ToList(), revalidar: false));
            sb.AppendLine();

            // Qué mirar al terminar. Se resuelve acá, con el grafo y el catálogo a mano; ejecutarlo
            // es después, sobre la transacción abierta. Ver InformeFKsVioladas.
            _chequeoFKs = FKsDelBarrido(cierre, relaciones)
                .Where(g => !EsAutoFKDegenerada(g.ToList()))
                .Select(g =>
                {
                    var cols = g.ToList();
                    var fk = new FKVioladaLimpiador
                    {
                        NombreFK   = cols[0].NombreFK,
                        TablaHija  = cols[0].OrigenCompleto,
                        TablaPadre = cols[0].DestinoCompleto,
                        Columnas   = string.Join(", ", cols.Select(f => $"{f.ColumnaOrigen} → {f.ColumnaDestino}"))
                    };
                    string sql = $"SELECT COUNT(*) FROM {Quote(fk.TablaHija)} h " +
                                 $"WHERE {ExprHuerfanaPorFK(cols, infoCols, centinelas, "h")}";
                    return Tuple.Create(fk, sql);
                })
                .ToList();
        }

        /// <summary>Encabezados de los cuatro sub-pasos de un barrido, para numerarlos según dónde caiga.</summary>
        private class EtiquetasBarrido
        {
            public string Captura;
            public string Intro;
            public string Freno;
            public string Borrado;
            public string Limpieza;
        }

        /// <summary>
        /// Motor de barrido por conjuntos, compartido por el borrado en cascada y la depuración de
        /// huérfanos. Lo único que los distingue es la semilla: qué filas arrancan condenadas.
        /// De ahí para abajo la mecánica es la misma — capturar en temporales propagando de padres
        /// a hijos, frenar si el alcance es desmedido, borrar de hijos a padres y limpiar.
        ///
        /// Que la propagación sea padre → hijo es justamente lo que cierra la cadena de huérfanos
        /// en una sola pasada: cuando se llega a una hija, el conjunto condenado de su padre ya
        /// está armado, así que las filas que quedarían colgando entran solas.
        /// </summary>
        /// <param name="alcance">Tablas que definen el límite de esquema y aportan PK/Schema al resolver.</param>
        /// <param name="frenoSeguridad">Emitir el corte del 90%. Opcional: ver <see cref="GenerarFrenoSeguridad"/>.</param>
        /// <returns>Las tablas que quedaron con conjunto, en orden de captura.</returns>
        private List<string> GenerarBarridoConjuntos(
            StringBuilder sb,
            Dictionary<string, string> semillas,
            List<TablaConfigLimpiador> alcance,
            List<FKRelacionLimpiador> relaciones,
            List<TablaConfigLimpiador> universoTablas,
            string prefijo,
            EtiquetasBarrido et,
            bool frenoSeguridad)
        {
            var limite = EsquemasEnAlcance(alcance);
            var cierre = CierreCascada(semillas.Keys, relaciones, limite);
            var resolver = ResolverCierre(cierre, alcance, universoTablas, out var noResueltos);
            var externas = ReferenciasExternas(cierre, relaciones, limite);
            var enCiclo = TablasEnCiclo(cierre, relaciones);

            // Padres antes que hijos para capturar; el borrado va al revés.
            var ordenCaptura = OrdenTopologicoNombres(cierre.ToList(), relaciones, hijosAntes: false)
                               .Where(n => !noResueltos.Contains(n)).ToList();

            var fksPorHija = FKsDelBarrido(cierre, relaciones)
                             .Where(g => !EsAutoFKDegenerada(g.ToList()))
                             .GroupBy(g => g.First().OrigenCompleto, StringComparer.OrdinalIgnoreCase)
                             .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // Qué tablas terminan con conjunto: las semilla más las que alcanza el arrastre desde
            // un conjunto ya armado. Se resuelve en el mismo orden de emisión.
            var conConjunto = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in ordenCaptura)
            {
                if (semillas.ContainsKey(n)) { conConjunto.Add(n); continue; }
                if (fksPorHija.TryGetValue(n, out var gs) &&
                    gs.Any(g => conConjunto.Contains(g.First().DestinoCompleto)))
                    conConjunto.Add(n);
            }

            // Una FK puede apuntar a un UNIQUE que NO es la PK del padre. Como las hijas joinean
            // contra la temporal del padre por las columnas referenciadas, esas columnas tienen que
            // estar en ella: si sólo se guarda la PK, el JOIN nombra columnas que no existen y el
            // script ni siquiera compila.
            var enlacesPorPadre = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var hija in ordenCaptura.Where(conConjunto.Contains))
            {
                if (!fksPorHija.TryGetValue(hija, out var grupos)) continue;
                foreach (var g in grupos)
                {
                    string padre = g.First().DestinoCompleto;
                    if (!conConjunto.Contains(padre)) continue;   // sin conjunto no hay temporal contra la que joinear
                    if (!enlacesPorPadre.TryGetValue(padre, out var colsEnlace))
                        enlacesPorPadre[padre] = colsEnlace = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in g) colsEnlace.Add(f.ColumnaDestino);
                }
            }

            if (noResueltos.Any())
            {
                sb.AppendLine("-- ⚠ SIN RESOLVER: estas tablas del arrastre no están en el catálogo cargado,");
                sb.AppendLine("--   así que quedaron FUERA del script. Revisalas a mano:");
                foreach (var n in noResueltos.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine($"--   • {n}");
                sb.AppendLine();
            }
            if (externas.Any())
            {
                sb.AppendLine($"-- ⚠ LÍMITE DE ESQUEMA: {externas.Count} tabla(s) del alcance están referenciadas desde");
                sb.AppendLine("--   fuera. Esas filas NO entran al conjunto y las tablas de afuera no se tocan.");
                foreach (var kv in externas.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine($"--   • {kv.Key} ← {string.Join(", ", kv.Value.Select(g => g.First().OrigenCompleto).Distinct(StringComparer.OrdinalIgnoreCase))}");
                sb.AppendLine();
            }
            if (enCiclo.Any())
            {
                sb.AppendLine($"-- ⚠ CICLO DE FK: {enCiclo.Count} tabla(s) forman ciclo, así que no hay orden válido.");
                sb.AppendLine("--   Sólo a ellas se les suspende la validación; el resto borra con las FK activas.");
                foreach (var n in enCiclo.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine($"--   • {n}");
                sb.AppendLine();
            }

            // ── Capturar qué se elimina ─────────────────────────────────
            sb.AppendLine($"-- ── {et.Captura} ──────────");
            if (!string.IsNullOrEmpty(et.Intro)) sb.AppendLine(et.Intro);
            sb.AppendLine();
            foreach (var nombre in ordenCaptura)
            {
                if (!conConjunto.Contains(nombre)) continue;
                semillas.TryGetValue(nombre, out var sqlSemilla);
                fksPorHija.TryGetValue(nombre, out var gruposPadre);
                sb.Append(GenerarCapturaConjunto(nombre, resolver, sqlSemilla, gruposPadre, conConjunto, externas, enlacesPorPadre, prefijo));
            }

            // ── Freno de seguridad ──────────────────────────────────────
            sb.Append(GenerarFrenoSeguridad(ordenCaptura.Where(conConjunto.Contains), resolver, prefijo, et.Freno, frenoSeguridad));

            // ── Borrar ──────────────────────────────────────────────────
            sb.AppendLine($"-- ── {et.Borrado} ───────────");
            var tablasCiclo = enCiclo.Where(conConjunto.Contains).Select(n => resolver[n]).ToList();
            if (tablasCiclo.Any())
            {
                sb.AppendLine("-- Sólo las tablas en ciclo pierden la validación, y se les devuelve al final.");
                sb.Append(DeshabilitarConstraints(tablasCiclo));
                sb.AppendLine();
            }

            foreach (var nombre in Enumerable.Reverse(ordenCaptura))
            {
                if (!conConjunto.Contains(nombre)) continue;
                if (!semillas.ContainsKey(nombre))
                    sb.AppendLine($"-- ARRASTRE (fuera de la selección, dentro del esquema): {Quote(nombre)}");
                sb.Append(GenerarDeleteDesdeConjunto(nombre, resolver, prefijo));
            }

            if (tablasCiclo.Any())
            {
                sb.AppendLine("-- Revalidar las tablas en ciclo: si algo quedó colgando, el ALTER falla y revierte.");
                sb.Append(RehabilitarConstraints(tablasCiclo));
                sb.AppendLine();
            }

            // ── Limpiar temporales ──────────────────────────────────────
            sb.AppendLine($"-- ── {et.Limpieza} ─────────────────────────");
            foreach (var nombre in ordenCaptura)
                if (conConjunto.Contains(nombre))
                    sb.AppendLine(DropTemporal(NombreTemporal(nombre, prefijo)));
            sb.AppendLine();

            return ordenCaptura.Where(conConjunto.Contains).ToList();
        }

        // ── Barrido de huérfanos ──────────────────────────────────────────

        /// <summary>
        /// Depura las relaciones truncadas: elimina las filas cuya FK apunta a un padre inexistente
        /// y arrastra a las que queden huérfanas por ese mismo borrado, hasta cerrar la cadena.
        ///
        /// No hace falta iterar: la captura recorre el cierre en orden topológico de padres a hijos,
        /// así que cuando le toca a una hija el conjunto condenado de su padre ya está armado. Los
        /// ciclos de FK los cubre el tratamiento aparte de <see cref="TablasEnCiclo"/>.
        ///
        /// Una tabla sin FKs — un catálogo fijo — no aparece por ningún lado: el barrido sólo
        /// condena filas identificadas positivamente por una relación rota.
        /// </summary>
        private void GenerarBarridoHuerfanos(
            StringBuilder sb,
            List<TablaConfigLimpiador> enAlcance,
            List<FKRelacionLimpiador> relaciones,
            List<TablaConfigLimpiador> universoTablas,
            OpcionesBarrido opciones,
            Dictionary<string, List<ColumnaInfoLimpiador>> info,
            int nroPaso)
        {
            var semillas = SemillasHuerfanos(enAlcance, relaciones, info, opciones, out var fksDentro, out var fksFuera);

            sb.AppendLine($"-- ══ PASO {nroPaso}: DEPURACIÓN DE RELACIONES TRUNCADAS ═══════════════════════");
            sb.AppendLine("-- Elimina las filas cuya FK apunta a un padre que ya no existe, y arrastra a las");
            sb.AppendLine("-- que queden huérfanas por ese borrado, hasta que la cadena cierre.");
            sb.AppendLine($"-- Centinelas 0 y '' tratados como 'sin referencia': {(opciones.CentinelasComoSinReferencia ? "SÍ" : "NO")}.");
            if (!opciones.CentinelasComoSinReferencia)
                sb.AppendLine("-- ⚠ Con esta opción destildada, una FK en 0 cuenta como referencia rota y la fila se borra.");
            sb.AppendLine();

            if (fksFuera.Any())
            {
                sb.AppendLine("-- ⚠ FUERA DEL LÍMITE DE ESQUEMA: estas FKs apuntan a un padre de otro esquema.");
                sb.AppendLine("--   Se informan pero NO se depuran. Para incluirlas, ampliá el alcance.");
                foreach (var g in fksFuera)
                {
                    var c = g.ToList();
                    sb.AppendLine($"--   • {c[0].OrigenCompleto} → {c[0].DestinoCompleto} ({string.Join(", ", c.Select(f => f.ColumnaOrigen))})");
                }
                sb.AppendLine();
            }

            if (!semillas.Any())
            {
                sb.AppendLine("-- Sin FKs que depurar dentro del alcance: nada que hacer en este paso.");
                sb.AppendLine();
                return;
            }

            GenerarBarridoConjuntos(sb, semillas, enAlcance, relaciones, universoTablas, "orf",
                new EtiquetasBarrido
                {
                    Captura  = $"PASO {nroPaso}a: Capturar los huérfanos (padres → hijos)",
                    Intro    = "-- Cada #orf_… junta las claves de las filas colgando. Una fila entra sólo si\n" +
                               "-- REFERENCIA a alguien y ese alguien no existe: la que no referencia a nadie\n" +
                               "-- (NULL, o centinela si la opción está tildada) no es huérfana y sobrevive.",
                    Freno    = $"PASO {nroPaso}b",
                    Borrado  = $"PASO {nroPaso}c: Eliminar huérfanos (hijos → padres)",
                    Limpieza = $"PASO {nroPaso}d: Descartar las tablas temporales"
                },
                opciones.FrenoSeguridad);

            sb.Append(GenerarVerificacionHuerfanos(fksDentro, info, opciones, $"PASO {nroPaso}e"));
        }

        /// <summary>
        /// Recuenta los huérfanos después de borrar y aborta si queda alguno. Es la garantía de
        /// "completamente depurado": si el cierre quedó incompleto la transacción revierte, en vez
        /// de dejar la base a medio depurar sin que se note.
        /// </summary>
        private string GenerarVerificacionHuerfanos(
            List<IGrouping<string, FKRelacionLimpiador>> fks,
            Dictionary<string, List<ColumnaInfoLimpiador>> info,
            OpcionesBarrido opciones,
            string etiquetaPaso)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"-- ── {etiquetaPaso}: Verificación final ────────────────────────────────");
            if (_conn.Motor != TipoMotor.MS_SQL)
            {
                sb.AppendLine("-- (sólo implementada en MS SQL — verificá a mano que no quedaron huérfanos)");
                sb.AppendLine();
                return sb.ToString();
            }

            sb.AppendLine("-- Si alguna FK sigue rota acá, algo del cierre falló: corta y revierte todo.");
            foreach (var grupoFK in fks)
            {
                var cols = grupoFK.ToList();
                string qHija = Quote(cols[0].OrigenCompleto);
                string expr = ExprHuerfanaPorFK(cols, info, opciones.CentinelasComoSinReferencia, "h");
                string detalle = Esc($"{cols[0].OrigenCompleto} → {cols[0].DestinoCompleto} " +
                                     $"({string.Join(", ", cols.Select(f => f.ColumnaOrigen))})");
                // Mismo corte que el freno, y por el mismo motivo: una sola sentencia, sin bloque.
                sb.AppendLine($"IF EXISTS (SELECT 1 FROM {qHija} h WHERE {expr}) SELECT CAST('QUEDAN HUERFANOS: {detalle}' AS INT);");
            }
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>
        /// Cierre transitivo hacia abajo: partiendo de las tablas semilla agrega toda tabla que
        /// tenga una FK apuntando a alguna del conjunto, y repite hasta que deje de crecer. Es el
        /// universo de tablas que el borrado en cascada puede llegar a tocar.
        ///
        /// El arrastre NO cruza el límite de esquema: si el usuario eligió SIEP, una hija de dbo
        /// no entra al cierre y por lo tanto nunca se toca. Las referencias que quedan del otro
        /// lado se resuelven con las guardas de <see cref="ReferenciasExternas"/>, reteniendo la
        /// fila del padre en vez de borrar afuera.
        ///
        /// El HashSet corta los ciclos solo.
        /// </summary>
        private HashSet<string> CierreCascada(
            IEnumerable<string> semilla,
            List<FKRelacionLimpiador> relaciones,
            HashSet<string> limiteEsquemas)
        {
            var cierre = new HashSet<string>(semilla, StringComparer.OrdinalIgnoreCase);
            var pendientes = new Queue<string>(cierre);
            while (pendientes.Count > 0)
            {
                string actual = pendientes.Dequeue();
                foreach (var r in relaciones.Where(x =>
                    string.Equals(x.DestinoCompleto, actual, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!DentroDelLimite(r.SchemaOrigen, limiteEsquemas)) continue;
                    if (cierre.Add(r.OrigenCompleto)) pendientes.Enqueue(r.OrigenCompleto);
                }
            }
            return cierre;
        }

        /// <summary>
        /// Esquemas que el usuario está limpiando. Vacío = sin límite: con el selector en
        /// "(Todos)" el alcance es la base entera y la cascada cruza esquemas como antes.
        /// </summary>
        private static HashSet<string> EsquemasEnAlcance(List<TablaConfigLimpiador> configuradas)
            => new HashSet<string>(configuradas.Select(c => c.Schema ?? ""), StringComparer.OrdinalIgnoreCase);

        private static bool DentroDelLimite(string schema, HashSet<string> limite)
            => limite == null || limite.Count == 0 || limite.Contains(schema ?? "");

        /// <summary>
        /// FKs que apuntan a una tabla del cierre DESDE FUERA del límite de esquema, agrupadas
        /// por la tabla del cierre a la que apuntan. Nunca generan borrado — generan la guarda
        /// NOT EXISTS que protege esas filas, para no tocar nada del otro esquema.
        /// </summary>
        private Dictionary<string, List<IGrouping<string, FKRelacionLimpiador>>> ReferenciasExternas(
            HashSet<string> cierre,
            List<FKRelacionLimpiador> relaciones,
            HashSet<string> limiteEsquemas)
            => relaciones
                .Where(r => cierre.Contains(r.DestinoCompleto) && !DentroDelLimite(r.SchemaOrigen, limiteEsquemas))
                .GroupBy(r => r.ClaveFK, StringComparer.OrdinalIgnoreCase)
                .GroupBy(g => g.First().DestinoCompleto, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Guardas que protegen las filas referenciadas desde fuera del esquema. Se pegan al
        /// WHERE del DELETE de la tabla, para que esas filas puntuales sobrevivan.
        /// </summary>
        private string GuardasExternasAlias(
            string tablaCompleta,
            Dictionary<string, List<IGrouping<string, FKRelacionLimpiador>>> externas,
            string aliasPadre)
        {
            if (externas == null || !externas.TryGetValue(tablaCompleta, out var grupos)) return "";
            var sb = new StringBuilder();
            foreach (var grupoFK in grupos)
            {
                var cols = grupoFK.ToList();
                string qHija = Quote(cols[0].OrigenCompleto);
                string join = JoinFK(cols, aliasPadre, "e");
                sb.Append($"\n      AND NOT EXISTS (SELECT 1 FROM {qHija} e WHERE {join})");
            }
            return sb.ToString();
        }

        // ── Conjuntos de borrado (lógica positiva) ────────────────────────

        /// <summary>
        /// Nombre de la temporal que junta las claves condenadas de una tabla. El prefijo separa
        /// las fases: el barrido de huérfanos corre DESPUÉS del de bajas en el mismo script y con
        /// un solo prefijo se pisarían las temporales entre sí.
        /// </summary>
        private string NombreTemporal(string tablaCompleta, string prefijo)
        {
            string limpio = new string(tablaCompleta.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
            return _conn.Motor == TipoMotor.POSTGRES ? $"{prefijo}_{limpio}" : $"#{prefijo}_{limpio}";
        }

        private string DropTemporal(string tmp)
            => _conn.Motor == TipoMotor.MS_SQL
                ? $"IF OBJECT_ID('tempdb..{tmp}') IS NOT NULL DROP TABLE {tmp};"
                : $"DROP TABLE IF EXISTS {tmp};";

        /// <summary>Columnas clave con las que se identifica una fila dentro del conjunto.</summary>
        private List<string> ClaveConjunto(TablaConfigLimpiador cfg) => cfg.CamposPK ?? new List<string>();

        /// <summary>
        /// Captura en una temporal las claves de las filas condenadas de una tabla: las que cumplen
        /// la semilla (si la tabla es semilla) más las que cuelgan de un conjunto ya armado.
        /// El primer aporte crea la temporal con SELECT INTO; los siguientes agregan con INSERT.
        /// </summary>
        /// <param name="sqlSemilla">
        /// Expresión WHERE calificada con el alias "h" que identifica las filas semilla, o null si
        /// esta tabla sólo entra por arrastre. Es lo único que distingue el barrido de bajas
        /// lógicas del de huérfanos: el resto de la mecánica es idéntico.
        /// </param>
        /// <param name="enlacesPorPadre">
        /// Columnas por las que las hijas van a joinear contra la temporal de cada tabla. Se suman
        /// a la PK porque una FK puede referenciar un UNIQUE que no es la PK del padre.
        /// </param>
        private string GenerarCapturaConjunto(
            string tablaCompleta,
            Dictionary<string, TablaConfigLimpiador> resolver,
            string sqlSemilla,
            List<IGrouping<string, FKRelacionLimpiador>> gruposPadre,
            HashSet<string> conConjunto,
            Dictionary<string, List<IGrouping<string, FKRelacionLimpiador>>> externas,
            Dictionary<string, HashSet<string>> enlacesPorPadre,
            string prefijo)
        {
            var cfg = resolver[tablaCompleta];
            var pk = ClaveConjunto(cfg);
            if (!pk.Any()) return $"-- OMITIDA {tablaCompleta}: sin PK detectada, no se puede armar el conjunto.\n\n";

            string tmp = NombreTemporal(tablaCompleta, prefijo);
            string q = Quote(tablaCompleta);

            // La PK va primero y siempre: la usan el anti-duplicado, el freno y el DELETE final.
            // Detrás, las columnas de enlace que no sean ya parte de la PK — repetir un nombre
            // haría fallar el SELECT INTO por columnas duplicadas.
            var colsConjunto = new List<string>(pk);
            if (enlacesPorPadre != null && enlacesPorPadre.TryGetValue(tablaCompleta, out var enlaces))
                foreach (var c in enlaces.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    if (!colsConjunto.Contains(c, StringComparer.OrdinalIgnoreCase))
                        colsConjunto.Add(c);

            string cols = string.Join(", ", colsConjunto.Select(c => $"h.{QuoteCampo(c)}"));
            // Las filas referenciadas desde otro esquema no entran al conjunto, y como los hijos
            // se capturan desde el conjunto del padre, la retención se propaga sola hacia abajo.
            string guardas = GuardasExternasAlias(tablaCompleta, externas, "h");
            string noDuplicado = string.Join(" AND ", pk.Select(c => $"x.{QuoteCampo(c)} = h.{QuoteCampo(c)}"));

            var sb = new StringBuilder();
            sb.AppendLine($"-- Conjunto de {tablaCompleta}");
            sb.AppendLine(DropTemporal(tmp));

            bool creada = false;

            if (!string.IsNullOrEmpty(sqlSemilla))
            {
                sb.AppendLine(SelectInto(cols, tmp, $"{q} h"));
                sb.AppendLine($"    WHERE {sqlSemilla}{guardas};");
                creada = true;
            }

            foreach (var grupoFK in gruposPadre ?? new List<IGrouping<string, FKRelacionLimpiador>>())
            {
                var colsFK = grupoFK.ToList();
                string padre = colsFK[0].DestinoCompleto;
                if (!conConjunto.Contains(padre)) continue;
                if (string.Equals(padre, tablaCompleta, StringComparison.OrdinalIgnoreCase)) continue; // auto-ref: aparte

                var pkPadre = ClaveConjunto(resolver[padre]);
                if (!pkPadre.Any()) continue;
                string tmpPadre = NombreTemporal(padre, prefijo);
                string join = string.Join(" AND ", colsFK.Select(f => $"d.{QuoteCampo(f.ColumnaDestino)} = h.{QuoteCampo(f.ColumnaOrigen)}"));

                if (!creada)
                {
                    sb.AppendLine(SelectInto(cols, tmp, $"{q} h"));
                    sb.AppendLine($"    INNER JOIN {tmpPadre} d ON {join}");
                    sb.AppendLine($"    WHERE 1=1{guardas};");
                    creada = true;
                }
                else
                {
                    // Sin el anti-join, una fila alcanzada por varios padres entra varias veces.
                    // No rompe el DELETE (usa EXISTS) pero sí el freno de seguridad, que cuenta
                    // filas del conjunto: los duplicados lo inflan y lo hacen abortar de más.
                    sb.AppendLine($"INSERT INTO {tmp}");
                    sb.AppendLine($"SELECT {cols} FROM {q} h");
                    sb.AppendLine($"    INNER JOIN {tmpPadre} d ON {join}");
                    sb.AppendLine($"    WHERE NOT EXISTS (SELECT 1 FROM {tmp} x WHERE {noDuplicado}){guardas};");
                }
            }

            // Auto-referencia real: cada pasada suma un nivel de la jerarquía.
            var auto = (gruposPadre ?? new List<IGrouping<string, FKRelacionLimpiador>>())
                .FirstOrDefault(g => string.Equals(g.First().DestinoCompleto, tablaCompleta, StringComparison.OrdinalIgnoreCase));
            if (creada && auto != null)
            {
                var colsFK = auto.ToList();
                string joinAuto = string.Join(" AND ", colsFK.Select(f => $"d.{QuoteCampo(f.ColumnaDestino)} = h.{QuoteCampo(f.ColumnaOrigen)}"));
                string noEsta = noDuplicado;
                sb.AppendLine($"-- Auto-referencia en {tablaCompleta}: se repite hasta que el conjunto deja de crecer.");
                if (_conn.Motor == TipoMotor.MS_SQL)
                {
                    sb.AppendLine("WHILE 1 = 1 BEGIN");
                    sb.AppendLine($"    INSERT INTO {tmp}");
                    sb.AppendLine($"    SELECT {cols} FROM {q} h");
                    sb.AppendLine($"        INNER JOIN {tmp} d ON {joinAuto}");
                    sb.AppendLine($"        WHERE NOT EXISTS (SELECT 1 FROM {tmp} x WHERE {noEsta})");
                    sb.AppendLine("    IF @@ROWCOUNT = 0 BREAK");
                    sb.AppendLine("END;");
                }
                else
                {
                    sb.AppendLine("-- (sin loop en este motor: repetir a mano si la jerarquía tiene más niveles)");
                    sb.AppendLine($"INSERT INTO {tmp}");
                    sb.AppendLine($"SELECT {cols} FROM {q} h");
                    sb.AppendLine($"    INNER JOIN {tmp} d ON {joinAuto}");
                    sb.AppendLine($"    WHERE NOT EXISTS (SELECT 1 FROM {tmp} x WHERE {noEsta});");
                }
            }

            sb.AppendLine();
            return sb.ToString();
        }

        private string SelectInto(string cols, string tmp, string desde)
            => _conn.Motor == TipoMotor.POSTGRES
                ? $"CREATE TEMP TABLE {tmp} AS SELECT {cols} FROM {desde}"
                : $"SELECT {cols} INTO {tmp} FROM {desde}";

        /// <summary>
        /// Freno de seguridad: corta el script si el conjunto de alguna tabla supera el 90% de sus
        /// filas. Es la red que faltó cuando el barrido negativo vació PreguntasPorCuestionario.
        ///
        /// Cada chequeo va en UNA sentencia autocontenida, sin variables ni BEGIN…END: el script se
        /// ejecuta sentencia por sentencia (ver <see cref="ParsearSentencias"/>), así que una
        /// @variable no sobrevive a la línea siguiente y un bloque con ';' adentro se parte en
        /// pedazos inválidos.
        ///
        /// El corte se provoca con un CAST condenado a fallar. Es la única forma de meter los
        /// conteos en el mensaje sin variables, y sirve en cualquier versión del motor. Frena en los
        /// dos contextos: el ejecutor de la app aborta y revierte ante cualquier error, y en SSMS lo
        /// hace el SET XACT_ABORT ON de la cabecera.
        ///
        /// Va apagado salvo que se lo pida: el umbral es porcentual y en una tabla de dos filas lo
        /// alcanza cualquier limpieza legítima. Cuando está apagado igual se emite el encabezado del
        /// paso, para que el script deje constancia y la numeración no se descoloque.
        /// </summary>
        private string GenerarFrenoSeguridad(IEnumerable<string> tablas, Dictionary<string, TablaConfigLimpiador> resolver,
                                             string prefijo, string etiquetaPaso, bool activo)
        {
            if (!activo)
                return $"-- ── {etiquetaPaso}: Freno de seguridad — DESACTIVADO ──────────────────────\n" +
                       "-- Tildá \"Frenar si una tabla pierde más del 90% de sus filas\" en la configuración\n" +
                       "-- global si querés que el script aborte ante un borrado desmedido.\n\n";

            if (_conn.Motor != TipoMotor.MS_SQL)
                return $"-- ── {etiquetaPaso}: Freno de seguridad ──\n-- (sólo implementado en MS SQL)\n\n";

            var sb = new StringBuilder();
            sb.AppendLine($"-- ── {etiquetaPaso}: Freno de seguridad ──────────────────────────────────────");
            sb.AppendLine("-- Corta el script y revierte si alguna tabla perdería más del 90% de sus filas.");
            sb.AppendLine("-- El mensaje dice cuántas filas de cuántas alcanzaba el borrado.");
            sb.AppendLine("-- Ajustá o quitá el umbral si un borrado masivo es lo que esperás.");

            foreach (var nombre in tablas)
            {
                if (!ClaveConjunto(resolver[nombre]).Any()) continue;
                string tmp = NombreTemporal(nombre, prefijo);
                string q = Quote(nombre);
                string conj = $"(SELECT COUNT(*) FROM {tmp})";
                string total = $"(SELECT COUNT(*) FROM {q})";
                // Todo en una línea: el parser corta en cada línea terminada en ';'.
                sb.AppendLine($"IF {conj} * 100 > {total} * 90 SELECT CAST('ABORTADO: {Esc(nombre)} pierde ' + CAST({conj} AS VARCHAR(20)) + ' de ' + CAST({total} AS VARCHAR(20)) + ' filas (mas del 90 por ciento). No se borro nada.' AS INT);");
            }
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>DELETE por JOIN contra el conjunto capturado. Condición positiva.</summary>
        private string GenerarDeleteDesdeConjunto(string tablaCompleta, Dictionary<string, TablaConfigLimpiador> resolver, string prefijo)
        {
            var pk = ClaveConjunto(resolver[tablaCompleta]);
            if (!pk.Any()) return "";
            string tmp = NombreTemporal(tablaCompleta, prefijo);
            string q = Quote(tablaCompleta);
            string join = string.Join(" AND ", pk.Select(c => $"d.{QuoteCampo(c)} = {q}.{QuoteCampo(c)}"));
            var sb = new StringBuilder();
            sb.AppendLine($"DELETE FROM {q}");
            sb.AppendLine($"    WHERE EXISTS (SELECT 1 FROM {tmp} d WHERE {join});");
            return sb.ToString();
        }

        /// <summary>Tablas que participan de un ciclo de FK dentro del cierre.</summary>
        private HashSet<string> TablasEnCiclo(HashSet<string> cierre, List<FKRelacionLimpiador> relaciones)
        {
            // Kahn: lo que sobra después de pelar las hojas repetidamente está en ciclo.
            var restantes = new HashSet<string>(cierre, StringComparer.OrdinalIgnoreCase);
            var aristas = relaciones
                .Where(r => cierre.Contains(r.OrigenCompleto) && cierre.Contains(r.DestinoCompleto) &&
                            !string.Equals(r.OrigenCompleto, r.DestinoCompleto, StringComparison.OrdinalIgnoreCase))
                .Select(r => new { Hija = r.OrigenCompleto, Padre = r.DestinoCompleto })
                .ToList();

            bool cambio = true;
            while (cambio)
            {
                cambio = false;
                foreach (var t in restantes.ToList())
                {
                    bool tieneHijasVivas = aristas.Any(a =>
                        string.Equals(a.Padre, t, StringComparison.OrdinalIgnoreCase) && restantes.Contains(a.Hija));
                    if (!tieneHijasVivas) { restantes.Remove(t); cambio = true; }
                }
            }

            // Las auto-referencias reales también necesitan el tratamiento aparte.
            foreach (var r in relaciones.Where(x =>
                cierre.Contains(x.OrigenCompleto) &&
                string.Equals(x.OrigenCompleto, x.DestinoCompleto, StringComparison.OrdinalIgnoreCase)))
            {
                var cols = relaciones.Where(x => string.Equals(x.ClaveFK, r.ClaveFK, StringComparison.OrdinalIgnoreCase)).ToList();
                if (!EsAutoFKDegenerada(cols)) restantes.Add(r.OrigenCompleto);
            }

            return restantes;
        }

        /// <summary>
        /// Config de cada tabla del cierre, resuelta por nombre calificado contra el universo.
        /// Con el grafo de FKs calificado no hay nada que adivinar: dos tablas homónimas en
        /// esquemas distintos son claves distintas. <paramref name="noResueltos"/> sólo debería
        /// poblarse si el catálogo devuelve una tabla que GetTablas no listó — conviene que eso
        /// falle ruidoso y no en silencio.
        /// </summary>
        private Dictionary<string, TablaConfigLimpiador> ResolverCierre(
            HashSet<string> cierre,
            List<TablaConfigLimpiador> configuradas,
            List<TablaConfigLimpiador> universoTablas,
            out List<string> noResueltos)
        {
            var dict = new Dictionary<string, TablaConfigLimpiador>(StringComparer.OrdinalIgnoreCase);
            noResueltos = new List<string>();

            foreach (var c in configuradas)
                if (cierre.Contains(c.NombreCompleto) && !dict.ContainsKey(c.NombreCompleto))
                    dict[c.NombreCompleto] = c;

            if (universoTablas != null)
                foreach (var t in universoTablas)
                    if (cierre.Contains(t.NombreCompleto) && !dict.ContainsKey(t.NombreCompleto))
                        dict[t.NombreCompleto] = t;

            foreach (var n in cierre)
                if (!dict.ContainsKey(n))
                {
                    int p = n.LastIndexOf('.');
                    dict[n] = p > 0
                        ? new TablaConfigLimpiador { Schema = n.Substring(0, p), Nombre = n.Substring(p + 1) }
                        : new TablaConfigLimpiador { Nombre = n };
                    noResueltos.Add(n);
                }

            return dict;
        }

        /// <summary>
        /// FKs a barrer: las que quedan enteramente dentro del cierre. Agrupadas por constraint
        /// (una FK compuesta llega como una fila por columna) y después deduplicadas por juego de
        /// columnas, porque dos constraints distintos sobre las mismas columnas darían la misma
        /// sentencia dos veces.
        /// </summary>
        private List<IGrouping<string, FKRelacionLimpiador>> FKsDelBarrido(
            HashSet<string> cierre, List<FKRelacionLimpiador> relaciones)
            => relaciones
                .Where(r => cierre.Contains(r.DestinoCompleto) && cierre.Contains(r.OrigenCompleto))
                .GroupBy(r => r.ClaveFK, StringComparer.OrdinalIgnoreCase)
                .GroupBy(g => string.Join("|", g.OrderBy(r => r.ColumnaOrigen, StringComparer.OrdinalIgnoreCase)
                                                .Select(r => $"{r.OrigenCompleto}.{r.ColumnaOrigen}→{r.DestinoCompleto}.{r.ColumnaDestino}")),
                         StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

        /// <summary>
        /// Una FK de una tabla a sí misma sobre su propia columna: la fila satisface el constraint
        /// consigo misma, así que nunca puede quedar colgando y barrerla borraría la tabla entera.
        /// </summary>
        private bool EsAutoFKDegenerada(List<FKRelacionLimpiador> cols)
            => string.Equals(cols[0].OrigenCompleto, cols[0].DestinoCompleto, StringComparison.OrdinalIgnoreCase)
               && cols.All(f => string.Equals(f.ColumnaOrigen, f.ColumnaDestino, StringComparison.OrdinalIgnoreCase));

        private string DescripcionModo(ModoConflicto modo)
        {
            switch (modo)
            {
                case ModoConflicto.BorradoEnCascada:
                    return " — borrado físico en cascada, NO marca campos de baja";
                case ModoConflicto.BorradoSeguro:
                    return " — borrado físico sólo de lo que nadie vivo referencia, NO marca campos de baja";
                case ModoConflicto.BorradoIterativo:
                    return " — borra TODAS las bajas y después lo que quede desconectado. ARRASTRA FILAS ACTIVAS";
                default:
                    return "";
            }
        }

        // ── Helpers de script ─────────────────────────────────────────────

        /// <summary>
        /// Condición de correlación padre/hija de una FK, simple o compuesta.
        /// Los calificadores pueden ser un alias ("p") o un nombre de tabla ya quoteado.
        /// </summary>
        private string JoinFK(IEnumerable<FKRelacionLimpiador> colsFK, string califPadre, string califHija)
            => string.Join(" AND ", colsFK.Select(fk =>
                $"{califPadre}.{QuoteCampo(fk.ColumnaDestino)} = {califHija}.{QuoteCampo(fk.ColumnaOrigen)}"));

        /// <summary>
        /// Quoter de campo calificado, para condiciones dentro de un EXISTS donde
        /// una columna sin calificar sería ambigua.
        /// </summary>
        private Func<string, string> QuoteCampoAlias(string calificador)
            => campo => $"{calificador}.{QuoteCampo(campo)}";

        /// <summary>
        /// Apertura de la transacción. En MS SQL va precedida de XACT_ABORT: es lo que hace que un
        /// error —el del freno, entre otros— corte el script y revierta cuando se lo ejecuta como
        /// batch en SSMS. El ejecutor propio no lo necesita (aborta y revierte por su cuenta), pero
        /// tampoco le molesta. Postgres ya aborta la transacción ante cualquier error.
        /// </summary>
        private string InicioTransaccion()
            => _conn.Motor == TipoMotor.POSTGRES
                ? "BEGIN;"
                : "SET XACT_ABORT ON;\nBEGIN TRANSACTION;";

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

        /// <param name="revalidar">
        /// Con true (el default, y lo que usan la cascada y el borrado seguro) emite WITH CHECK, que
        /// revalida las filas que ya están: si algo quedó colgando el ALTER falla y revierte toda la
        /// corrida. Es la verificación final gratis de esos dos modos.
        ///
        /// BorradoIterativo lo pasa en false, y no por comodidad: su criterio de desconexión deja a
        /// propósito las filas que están rotas por un dato pero sanas por otro, así que SIEMPRE
        /// puede quedar alguna FK violada. Con WITH CHECK, una sola de esas filas revertiría la
        /// corrida entera y el modo no serviría para nada. El precio es que esas constraints quedan
        /// "untrusted" — por eso ese modo cierra con InformeFKsVioladas, que las muestra antes de
        /// que el usuario confirme.
        /// </param>
        private string RehabilitarConstraints(List<TablaConfigLimpiador> tablas, bool revalidar = true)
        {
            var sb = new StringBuilder();
            switch (_conn.Motor)
            {
                case TipoMotor.MS_SQL:
                    string verbo = revalidar ? "WITH CHECK CHECK" : "CHECK";
                    foreach (var t in tablas) sb.AppendLine($"ALTER TABLE {Quote(t.NombreCompleto)} {verbo} CONSTRAINT ALL;");
                    break;
                case TipoMotor.POSTGRES:
                    sb.AppendLine("SET CONSTRAINTS ALL IMMEDIATE;");
                    break;
                case TipoMotor.SQLite:
                    sb.AppendLine("PRAGMA foreign_keys = ON;");
                    break;
                case TipoMotor.DB2:
                    sb.AppendLine("-- DB2: rehabilitar los FK constraints deshabilitados más arriba");
                    foreach (var t in tablas)
                        sb.AppendLine($"-- ALTER TABLE {Quote(t.NombreCompleto)} ALTER FOREIGN KEY <nombre_fk> ENFORCED;");
                    break;
            }
            return sb.ToString();
        }

        /// <summary>
        /// Toda tabla que el remapeo de IDs escribe: las que se renumeran más cada hija que
        /// apunta a la PK renumerada, esté tildada o no. Es el conjunto al que hay que suspenderle
        /// la validación de FK, porque el UPDATE deja valores colgando hasta que termina el bloque.
        /// </summary>
        private List<TablaConfigLimpiador> TablasTocadasPorReorden(
            List<TablaConfigLimpiador> conReorden,
            List<FKRelacionLimpiador> relaciones,
            Dictionary<string, TablaConfigLimpiador> dictCfg)
        {
            var tocadas = new Dictionary<string, TablaConfigLimpiador>(StringComparer.OrdinalIgnoreCase);
            foreach (var cfg in conReorden)
            {
                tocadas[cfg.NombreCompleto] = cfg;
                string pk = cfg.CamposPK[0];
                foreach (var fk in relaciones.Where(r =>
                    string.Equals(r.DestinoCompleto, cfg.NombreCompleto, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.ColumnaDestino, pk, StringComparison.OrdinalIgnoreCase)))
                {
                    if (tocadas.ContainsKey(fk.OrigenCompleto)) continue;
                    tocadas[fk.OrigenCompleto] = dictCfg.TryGetValue(fk.OrigenCompleto, out var cfgH)
                        ? cfgH
                        : DesdeNombreCompleto(fk.OrigenCompleto);
                }
            }
            return tocadas.Values.ToList();
        }

        /// <summary>Config mínima —sólo Schema y Nombre— para una tabla que sólo se conoce por nombre calificado.</summary>
        private static TablaConfigLimpiador DesdeNombreCompleto(string nombreCompleto)
        {
            int p = nombreCompleto.LastIndexOf('.');
            return p > 0
                ? new TablaConfigLimpiador { Schema = nombreCompleto.Substring(0, p), Nombre = nombreCompleto.Substring(p + 1) }
                : new TablaConfigLimpiador { Nombre = nombreCompleto };
        }

        /// <summary>Nombre de variable T-SQL derivado de un nombre de tabla.</summary>
        private static string NombreVar(string nombreCompleto)
            => new string(nombreCompleto.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

        /// <summary>
        /// Reposiciona el contador de IDENTITY después de renumerar, para que el próximo INSERT
        /// tome el siguiente número y no vuelva a abrir un hueco.
        ///
        /// Va en tres líneas SIN punto y coma internos a propósito: ParsearSentencias corta por el
        /// ';' de fin de línea, y con uno adentro el DECLARE viajaría en un comando distinto del
        /// que usa la variable.
        /// </summary>
        private string ResetSecuencias(List<TablaConfigLimpiador> tablas, Dictionary<string, List<ColumnaInfoLimpiador>> infoCols)
        {
            var sb = new StringBuilder();
            switch (_conn.Motor)
            {
                case TipoMotor.MS_SQL:
                    var conIdentity = tablas
                        .Where(t => InfoDe(infoCols, t.NombreCompleto, t.CamposPK[0])?.EsIdentity == true)
                        .ToList();
                    if (!conIdentity.Any()) return "";
                    sb.AppendLine("-- Reposicionar el contador de IDENTITY al último ID renumerado:");
                    foreach (var t in conIdentity)
                    {
                        string v = "@n_" + NombreVar(t.NombreCompleto);
                        sb.AppendLine($"DECLARE {v} INT");
                        sb.AppendLine($"SELECT {v} = ISNULL(MAX({QuoteCampo(t.CamposPK[0])}), 0) FROM {Quote(t.NombreCompleto)}");
                        sb.AppendLine($"DBCC CHECKIDENT('{Esc(Quote(t.NombreCompleto))}', RESEED, {v});");
                    }
                    break;
                case TipoMotor.POSTGRES:
                    // Sin tocar: pg_get_serial_sequence devuelve NULL si la columna no tiene
                    // secuencia y setval(NULL, …) falla. Queda para ajustar a mano.
                    sb.AppendLine("-- Resetear secuencias:");
                    foreach (var t in tablas) sb.AppendLine($"-- SELECT setval(pg_get_serial_sequence('{t.NombreCompleto}', '{t.CamposPK[0]}'), MAX({t.CamposPK[0]})) FROM {Quote(t.NombreCompleto)};");
                    break;
            }
            return sb.ToString();
        }

        private string GenerarBloqueReordenamiento(TablaConfigLimpiador cfg, List<FKRelacionLimpiador> relaciones,
                                                   Dictionary<string, TablaConfigLimpiador> dictCfg,
                                                   Dictionary<string, List<ColumnaInfoLimpiador>> infoCols)
        {
            var sb = new StringBuilder();
            string q = Quote(cfg.NombreCompleto);
            string pk = cfg.CamposPK[0];   // garantizado único por el filtro PKSimple del PASO 3
            // Por nombre calificado: con el nombre corto, dos tablas homónimas de esquemas
            // distintos compartirían la temporal del mapeo y se pisarían el remapeo entre sí.
            string tmpMap = NombreTemporal(cfg.NombreCompleto, "map");
            // Sólo las FKs que apuntan a la columna que se está renumerando. Una FK a otra
            // clave única de la misma tabla usa valores de OTRA columna: aplicarle este mapeo
            // reescribiría los datos de la hija con números que no le corresponden.
            // UNA sola vez por columna hija. Un remapeo old_id → new_id NO es idempotente:
            // aplicarlo dos veces vuelve a mapear los valores ya renumerados y corrompe los
            // datos (o choca contra un UNIQUE, si la tabla tiene la suerte de tener uno).
            // El catálogo puede devolver la misma FK repetida, o dos constraints distintos
            // sobre las mismas columnas.
            var fksHaciaEsta = relaciones.Where(r =>
                string.Equals(r.DestinoCompleto, cfg.NombreCompleto, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.ColumnaDestino, pk, StringComparison.OrdinalIgnoreCase))
                .GroupBy(r => $"{r.OrigenCompleto}|{r.ColumnaOrigen}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var fksOtraClave = relaciones.Where(r =>
                string.Equals(r.DestinoCompleto, cfg.NombreCompleto, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(r.ColumnaDestino, pk, StringComparison.OrdinalIgnoreCase)).ToList();

            sb.AppendLine($"-- Reordenar IDs en {cfg.NombreCompleto}");
            foreach (var grupo in fksOtraClave.GroupBy(r => $"{r.OrigenCompleto}.{r.ColumnaOrigen} → {r.ColumnaDestino}"))
                sb.AppendLine($"-- NO se remapea {grupo.Key}: la FK apunta a una clave distinta de la PK [{pk}]");
            switch (_conn.Motor)
            {
                case TipoMotor.MS_SQL:
                    sb.AppendLine($"SELECT [{pk}] AS old_id, ROW_NUMBER() OVER (ORDER BY [{pk}]) AS new_id INTO {tmpMap} FROM {q};");
                    foreach (var fk in fksHaciaEsta)
                    {
                        string qHija = Quote(dictCfg.TryGetValue(fk.OrigenCompleto, out var cfgH) ? cfgH.NombreCompleto : fk.OrigenCompleto);
                        sb.AppendLine($"UPDATE h SET h.[{fk.ColumnaOrigen}] = m.new_id FROM {qHija} h INNER JOIN {tmpMap} m ON h.[{fk.ColumnaOrigen}] = m.old_id;");
                    }
                    sb.Append(ReasignarPKMsSql(cfg, pk, q, tmpMap, infoCols));
                    sb.AppendLine($"DROP TABLE {tmpMap};");
                    break;
                case TipoMotor.POSTGRES:
                    sb.AppendLine($"WITH mapping AS (SELECT \"{pk}\" AS old_id, ROW_NUMBER() OVER (ORDER BY \"{pk}\") AS new_id FROM {q})");
                    foreach (var fk in fksHaciaEsta)
                    {
                        string qHija = Quote(dictCfg.TryGetValue(fk.OrigenCompleto, out var cfgH) ? cfgH.NombreCompleto : fk.OrigenCompleto);
                        sb.AppendLine($"UPDATE {qHija} h SET \"{fk.ColumnaOrigen}\" = m.new_id FROM mapping m WHERE h.\"{fk.ColumnaOrigen}\" = m.old_id;");
                    }
                    sb.AppendLine($"UPDATE {q} t SET \"{pk}\" = m.new_id FROM mapping m WHERE t.\"{pk}\" = m.old_id;");
                    break;
                default:
                    sb.AppendLine($"-- Reordenamiento para {cfg.NombreCompleto}: actualizar FKs y luego PK");
                    foreach (var fk in fksHaciaEsta)
                        sb.AppendLine($"-- UPDATE {Quote(fk.OrigenCompleto)} SET [{fk.ColumnaOrigen}] = <nuevo_id> WHERE [{fk.ColumnaOrigen}] = <viejo_id>;");
                    sb.AppendLine($"-- UPDATE {q} SET [{pk}] = <nuevo_id> WHERE [{pk}] = <viejo_id>;");
                    break;
            }
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>
        /// Reasigna la PK de la tabla que se está renumerando, en MS SQL.
        ///
        /// Una columna IDENTITY no admite UPDATE — el camino directo falla siempre, no a veces —
        /// así que hay que rehacer la tabla: copiar las filas con el ID ya mapeado, vaciarla y
        /// reinsertarlas con IDENTITY_INSERT. El CAST de new_id es el que impide que la temporal
        /// herede la propiedad IDENTITY y quede tan intocable como el original.
        ///
        /// Si la tabla tiene columnas que no se pueden insertar (calculadas, rowversion) el bloque
        /// sale comentado: un INSERT explícito que las incluya falla y uno que las omita las
        /// perdería en silencio. Mejor que el usuario lo resuelva a mano y sepa por qué.
        /// </summary>
        private string ReasignarPKMsSql(TablaConfigLimpiador cfg, string pk, string q, string tmpMap,
                                        Dictionary<string, List<ColumnaInfoLimpiador>> infoCols)
        {
            var sb = new StringBuilder();
            var infoPK = InfoDe(infoCols, cfg.NombreCompleto, pk);

            if (infoPK == null || !infoPK.EsIdentity)
            {
                sb.AppendLine($"UPDATE t SET t.[{pk}] = m.new_id FROM {q} t INNER JOIN {tmpMap} m ON t.[{pk}] = m.old_id;");
                if (infoPK == null)
                    sb.AppendLine($"-- (no se pudo leer el catálogo de {cfg.NombreCompleto}: si [{pk}] es IDENTITY este UPDATE falla)");
                return sb.ToString();
            }

            // Las tablas que no admiten este camino ya quedaron fuera de conReorden en GenerarScript,
            // así que acá las columnas están todas disponibles y son todas insertables.
            var insertables = infoCols[cfg.NombreCompleto].Where(c => !c.NoInsertable).ToList();
            string tmpNew = NombreTemporal(cfg.NombreCompleto, "new");
            string tipoCast = infoPK.EsEnteroSimple ? infoPK.Tipo : "bigint";
            string listaCols = string.Join(", ", insertables.Select(c => QuoteCampo(c.Nombre)));
            // El CAST es el que impide que la temporal herede la propiedad IDENTITY: SELECT INTO la
            // transfiere si la columna se copia tal cual, y la temporal quedaría tan intocable
            // como el original.
            string seleccion = string.Join(", ", insertables.Select(c =>
                string.Equals(c.Nombre, pk, StringComparison.OrdinalIgnoreCase)
                    ? $"CAST(m.new_id AS {tipoCast}) AS {QuoteCampo(c.Nombre)}"
                    : $"t.{QuoteCampo(c.Nombre)}"));

            sb.AppendLine($"-- [{pk}] es IDENTITY: no admite UPDATE, hay que rehacer la tabla.");
            sb.AppendLine("-- OJO: hace DELETE + INSERT, así que dispara los triggers de la tabla.");
            sb.AppendLine($"SELECT {seleccion} INTO {tmpNew} FROM {q} t INNER JOIN {tmpMap} m ON t.[{pk}] = m.old_id;");
            sb.AppendLine($"DELETE FROM {q};");
            sb.AppendLine($"SET IDENTITY_INSERT {q} ON;");
            sb.AppendLine($"INSERT INTO {q} ({listaCols}) SELECT {listaCols} FROM {tmpNew};");
            sb.AppendLine($"SET IDENTITY_INSERT {q} OFF;");
            sb.AppendLine($"DROP TABLE {tmpNew};");
            return sb.ToString();
        }

        /// <summary>
        /// Por qué esta tabla no puede renumerarse, o null si sí puede. Sólo aplica al camino
        /// IDENTITY de MS SQL: rehacer la tabla exige poder reinsertar TODAS sus columnas, y un
        /// INSERT que omita una las perdería en silencio.
        /// </summary>
        private string MotivoBloqueoIdentity(TablaConfigLimpiador cfg, Dictionary<string, List<ColumnaInfoLimpiador>> infoCols)
        {
            if (_conn.Motor != TipoMotor.MS_SQL) return null;
            var infoPK = InfoDe(infoCols, cfg.NombreCompleto, cfg.CamposPK[0]);
            if (infoPK == null || !infoPK.EsIdentity) return null;

            List<ColumnaInfoLimpiador> cols;
            if (!infoCols.TryGetValue(cfg.NombreCompleto, out cols) || !cols.Any())
                return $"[{cfg.CamposPK[0]}] es IDENTITY y hay que rehacer la tabla, pero el catálogo no devolvió sus columnas";

            var noInsertables = cols.Where(c => c.NoInsertable).ToList();
            if (!noInsertables.Any()) return null;

            return $"[{cfg.CamposPK[0]}] es IDENTITY —hay que rehacer la tabla— y tiene columnas que no admiten INSERT " +
                   $"({string.Join(", ", noInsertables.Select(c => $"{c.Nombre} [{c.Tipo}]"))}). Renumerala a mano";
        }

        // ── Orden topológico ──────────────────────────────────────────────

        private List<TablaConfigLimpiador> OrdenTopologico(List<TablaConfigLimpiador> tablas, List<FKRelacionLimpiador> relaciones, bool hijosAntes)
        {
            var dictCfg = IndexarPorNombreCompleto(tablas);
            return OrdenTopologicoNombres(tablas.Select(t => t.NombreCompleto).ToList(), relaciones, hijosAntes)
                   .Where(n => dictCfg.ContainsKey(n))
                   .Select(n => dictCfg[n])
                   .ToList();
        }

        /// <summary>
        /// DFS post-orden sobre nombres de tabla. Marca el nodo antes de recursar, así que tolera
        /// ciclos sin desbordar la pila (dentro de un ciclo el orden no es garantizable, pero el
        /// barrido de la cascada termina igual porque el WITH CHECK final valida el resultado).
        /// El borrado en cascada ordena tablas que no tienen TablaConfigLimpiador, por eso la
        /// versión por nombre.
        /// </summary>
        private List<string> OrdenTopologicoNombres(List<string> tablas, List<FKRelacionLimpiador> relaciones, bool hijosAntes)
        {
            var nombres = new HashSet<string>(tablas, StringComparer.OrdinalIgnoreCase);
            var visitados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var resultado = new List<string>();

            Func<string, IEnumerable<string>> dependencias = tabla => hijosAntes
                ? relaciones.Where(r => string.Equals(r.DestinoCompleto, tabla, StringComparison.OrdinalIgnoreCase) && nombres.Contains(r.OrigenCompleto)).Select(r => r.OrigenCompleto)
                : relaciones.Where(r => string.Equals(r.OrigenCompleto, tabla, StringComparison.OrdinalIgnoreCase) && nombres.Contains(r.DestinoCompleto)).Select(r => r.DestinoCompleto);

            void Visitar(string nombre)
            {
                if (visitados.Contains(nombre)) return;
                visitados.Add(nombre);
                foreach (var dep in dependencias(nombre)) Visitar(dep);
                if (nombres.Contains(nombre)) resultado.Add(nombre);
            }

            foreach (var t in tablas) Visitar(t);
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

        /// <summary>
        /// Índice por nombre calificado (schema.tabla), que es como el módulo entero matchea
        /// contra el grafo de FKs. Dos tablas homónimas en esquemas distintos son claves
        /// distintas, así que no colisionan.
        /// </summary>
        private static Dictionary<string, TablaConfigLimpiador> IndexarPorNombreCompleto(IEnumerable<TablaConfigLimpiador> tablas)
        {
            var dict = new Dictionary<string, TablaConfigLimpiador>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tablas)
                if (!dict.ContainsKey(t.NombreCompleto)) dict[t.NombreCompleto] = t;
            return dict;
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
            /// <summary>
            /// Vuelta del barrido iterativo en curso, o la última que corrió. 0 en los demás modos,
            /// que no tienen loop. La barra de progreso no la puede reflejar —el total de sentencias
            /// deja de ser el total de ejecuciones— así que se muestra aparte.
            /// </summary>
            public int    Vuelta          { get; set; }
            /// <summary>
            /// Filas que cada DELETE se llevó, medidas durante la corrida. Es el alcance REAL, a
            /// diferencia del análisis previo, que sólo estima un nivel de arrastre. Se completa
            /// dentro de la transacción, así que todavía se puede decir que no.
            /// </summary>
            public Dictionary<string, int> FilasPorTabla { get; } =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public int FilasTotales => FilasPorTabla.Values.Sum();
        }

        private OdbcTransaction _txnPendiente;
        private DataBase _dbPendiente;

        /// <summary>
        /// FKs a revisar al terminar una corrida iterativa, con el SQL que las cuenta. Se arma al
        /// GENERAR el script, no al ejecutarlo: para saber qué mirar hacen falta el grafo de FKs y
        /// el catálogo de columnas, y consultarlos con la transacción abierta significaría pedirle
        /// metadatos al motor mientras se tienen locks sobre esas mismas tablas.
        /// </summary>
        private List<Tuple<FKVioladaLimpiador, string>> _chequeoFKs;

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
                    progreso.Completadas = Math.Min(i + 1, progreso.Total);

                    // Bloque repetible del modo iterativo: se ejecuta entero una y otra vez hasta
                    // que una vuelta completa no borre nada.
                    if (s.Equals(MarcaLoopInicio, StringComparison.OrdinalIgnoreCase))
                    {
                        int fin = sentencias.FindIndex(i + 1,
                            x => x.Trim().Equals(MarcaLoopFin, StringComparison.OrdinalIgnoreCase));
                        // Sin marca de cierre el bloque llega hasta el final: preferible a ejecutar
                        // el resto una sola vez y dejar el barrido a medio hacer sin avisar.
                        if (fin < 0) fin = sentencias.Count;
                        EjecutarBloqueRepetido(cmd, sentencias.GetRange(i + 1, fin - (i + 1)), progreso, onProgreso);
                        i = fin;
                        continue;
                    }
                    if (s.Equals(MarcaLoopFin, StringComparison.OrdinalIgnoreCase)) continue;

                    // Se saltea acá y no en ParsearSentencias para que quede a la vista en el log:
                    // que el script pierda su BEGIN y su ROLLBACK no puede ser algo que pase callado.
                    bool ignorada = EsControlTransaccion(s);
                    string resumen = s.Length > 80 ? s.Substring(0, 80) + "…" : s;
                    progreso.UltimaSentencia = ignorada
                        ? $"(ignorada: la transacción la maneja la aplicación) {resumen}"
                        : resumen;
                    onProgreso?.Invoke(progreso);
                    if (ignorada) continue;

                    cmd.CommandText = s;
                    AcumularFilas(progreso, s, cmd.ExecuteNonQuery());
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

        /// <summary>
        /// Corre el bloque del barrido iterativo una y otra vez hasta que una vuelta entera no borre
        /// nada. Es lo que reemplaza al orden topológico de los otros modos: cada vuelta libera
        /// filas que la anterior retenía, y el punto fijo se alcanza solo.
        ///
        /// El tope existe para que un error de generación no cuelgue la aplicación contra la base.
        /// Agotarlo es una falla, no una salida silenciosa: si todavía se estaba borrando cuando se
        /// cortó, terminar acá dejaría la base a medio barrer y nadie se enteraría.
        /// </summary>
        private void EjecutarBloqueRepetido(
            OdbcCommand cmd, List<string> bloque, EjecucionProgreso progreso, Action<EjecucionProgreso> onProgreso)
        {
            const int MaxVueltas = 50;

            for (int vuelta = 1; ; vuelta++)
            {
                progreso.Vuelta = vuelta;
                int borradasEnLaVuelta = 0;

                foreach (var raw in bloque)
                {
                    var s = raw.Trim();
                    if (string.IsNullOrWhiteSpace(s) || EsControlTransaccion(s)) continue;

                    string resumen = s.Length > 80 ? s.Substring(0, 80) + "…" : s;
                    progreso.UltimaSentencia = $"(vuelta {vuelta}) {resumen}";
                    onProgreso?.Invoke(progreso);

                    cmd.CommandText = s;
                    int filas = cmd.ExecuteNonQuery();
                    borradasEnLaVuelta += filas;
                    AcumularFilas(progreso, s, filas);
                }

                if (borradasEnLaVuelta == 0) break;

                if (vuelta >= MaxVueltas)
                    throw new InvalidOperationException(
                        $"El barrido no convergió en {MaxVueltas} vueltas: la última todavía borró " +
                        $"{borradasEnLaVuelta} fila(s). No se confirmó nada, la transacción se revierte. " +
                        "Revisá si hay un ciclo de FK que se retroalimenta o una condición de baja que " +
                        "alcanza filas nuevas en cada pasada.");
            }
        }

        /// <summary>
        /// Qué FKs quedaron violadas después de una corrida en modo BorradoIterativo. Corre DENTRO
        /// de la transacción todavía sin confirmar, que es el único momento en que sirve: es el
        /// dato que le falta al usuario para decidir si confirma o revierte.
        ///
        /// Devuelve vacío en los demás modos, que rehabilitan con WITH CHECK y por lo tanto no
        /// pueden dejar nada roto: si lo hubieran hecho, el ALTER habría fallado y no habría
        /// transacción que consultar.
        /// </summary>
        public List<FKVioladaLimpiador> InformeFKsVioladas()
        {
            var result = new List<FKVioladaLimpiador>();
            if (_chequeoFKs == null || _txnPendiente == null || _dbPendiente == null) return result;

            foreach (var par in _chequeoFKs)
            {
                var fk = par.Item1;
                try
                {
                    using (var cmd = new OdbcCommand(par.Item2, _dbPendiente.Connection, _txnPendiente))
                    {
                        object v = cmd.ExecuteScalar();
                        fk.FilasViolando = v == null || v == DBNull.Value ? 0 : Convert.ToInt32(v);
                    }
                }
                catch (Exception ex) { fk.Error = ex.Message; }

                if (fk.FilasViolando > 0 || fk.Error != null) result.Add(fk);
            }

            return result
                .OrderByDescending(f => f.FilasViolando)
                .ThenBy(f => f.TablaHija, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Suma al resumen las filas que se llevó una sentencia, si fue un DELETE.</summary>
        private static void AcumularFilas(EjecucionProgreso progreso, string sentencia, int filas)
        {
            string tabla = TablaDelDelete(sentencia);
            if (tabla == null || filas <= 0) return;
            int previo;
            progreso.FilasPorTabla.TryGetValue(tabla, out previo);
            progreso.FilasPorTabla[tabla] = previo + filas;
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

        /// <summary>
        /// Nombre de la tabla de un DELETE, o null si la sentencia es otra cosa. Se saca del texto
        /// que ya se está por ejecutar, así el resumen de filas eliminadas no depende de reconstruir
        /// nada por afuera. Devuelve el nombre tal como lo escribió el generador, con los corchetes
        /// sacados para que se lea igual que en la grilla del análisis.
        /// </summary>
        private static string TablaDelDelete(string sentencia)
        {
            const string prefijo = "DELETE FROM ";
            string s = (sentencia ?? "").TrimStart();
            if (!s.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase)) return null;

            string resto = s.Substring(prefijo.Length).TrimStart();
            int fin = resto.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            if (fin > 0) resto = resto.Substring(0, fin);
            return resto.Replace("[", "").Replace("]", "").Replace("\"", "");
        }

        /// <summary>
        /// BEGIN/COMMIT/ROLLBACK sueltos. El script los trae para poder pegarlo en SSMS, pero acá la
        /// transacción es la que abre <see cref="EjecutarScript"/> y la confirma el diálogo: dejarlos
        /// correr revertiría —o confirmaría— por su cuenta antes de que el usuario decida. El ROLLBACK
        /// que el generador deja activo, además, se llevaba puesta la transacción de la app entera y
        /// después el COMMIT del diálogo no tenía nada que confirmar.
        ///
        /// SET XACT_ABORT ON no entra acá: es opción de sesión y tiene que ejecutarse.
        ///
        /// Hay que descartar el comentario de cola antes de comparar. ParsearSentencias corta por el
        /// ';' de FIN de línea, así que "ROLLBACK;  -- ← Retirar esta línea" no corta ahí y llega con
        /// el comentario pegado: sin esto no matcheaba, el ROLLBACK del script se ejecutaba igual y
        /// el COMMIT del diálogo confirmaba una transacción ya revertida sin quejarse.
        /// </summary>
        private static bool EsControlTransaccion(string sentencia)
        {
            string sinComentario = sentencia ?? "";
            int corte = sinComentario.IndexOf("--", StringComparison.Ordinal);
            if (corte >= 0) sinComentario = sinComentario.Substring(0, corte);

            string s = string.Join(" ", sinComentario.TrimEnd().TrimEnd(';')
                .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                .ToUpperInvariant();
            return s == "BEGIN" || s == "BEGIN TRAN" || s == "BEGIN TRANSACTION"
                || s == "COMMIT" || s == "COMMIT TRAN" || s == "COMMIT TRANSACTION" || s == "COMMIT WORK"
                || s == "ROLLBACK" || s == "ROLLBACK TRAN" || s == "ROLLBACK TRANSACTION" || s == "ROLLBACK WORK";
        }

        /// <summary>
        /// Parte el script en sentencias: corta en cada línea terminada en ';' (y en cada GO), y las
        /// manda de a una por la misma conexión.
        ///
        /// Eso impone una condición a TODO lo que emite el generador: cada sentencia tiene que ser
        /// autocontenida. Nada de @variables que se usen en la línea siguiente —cada sentencia es su
        /// propio batch y no sobreviven— ni de ';' dentro de un BEGIN…END, porque el bloque se parte
        /// en pedazos inválidos. Las tablas temporales sí persisten: la conexión es la misma.
        /// </summary>
        private List<string> ParsearSentencias(string script)
        {
            var result = new List<string>();
            var lineas = script.Split('\n');
            var bloque = new StringBuilder();
            foreach (var linea in lineas)
            {
                string l = linea.TrimEnd();

                // Los marcadores del loop son comentarios —para que el script siga sirviendo en
                // SSMS— pero acá tienen que sobrevivir: son lo que le dice al ejecutor qué bloque
                // repetir. Cierran el bloque en curso y se emiten solos, igual que un GO.
                string t = l.Trim();
                if (t.Equals(MarcaLoopInicio, StringComparison.OrdinalIgnoreCase) ||
                    t.Equals(MarcaLoopFin, StringComparison.OrdinalIgnoreCase))
                {
                    string previo = bloque.ToString().Trim().TrimEnd(';');
                    if (!string.IsNullOrWhiteSpace(previo)) result.Add(previo);
                    bloque.Clear();
                    result.Add(t);
                    continue;
                }

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
