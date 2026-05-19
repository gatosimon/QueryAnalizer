using System;
using System.Collections.Generic;
using System.Linq;
using CapiDL;
using System.Data;

namespace QueryAnalyzer
{
    public class EsquematizadorService
    {
        private Conexion conexion = null;

        // ────────────────────────────────────────────────────────────────
        // Clases auxiliares para el esquematizador
        // ────────────────────────────────────────────────────────────────

        public EsquematizadorService(Conexion conexionActual)
        {
            conexion = conexionActual;
        }

        public class EsquemaTablaInfo
        {
            public string Nombre { get; set; }
            public string Schema { get; set; }
            public string Tipo { get; set; }   // "TABLE" | "VIEW"
            public List<EsquemaColumnaInfo> Columnas { get; } = new List<EsquemaColumnaInfo>();
            public List<EsquemaRelacionInfo> Relaciones { get; } = new List<EsquemaRelacionInfo>();
        }

        public class EsquemaColumnaInfo
        {
            public string Nombre { get; set; }
            public string Tipo { get; set; }
            public string Longitud { get; set; }
        }

        public class EsquemaRelacionInfo
        {
            public string TablaOrigen { get; set; }
            public string ColumnaOrigen { get; set; }
            public string TablaDestino { get; set; }
            public string ColumnaDestino { get; set; }
        }
        // ────────────────────────────────────────────────────────────────
        // Obtener FKs según motor
        // ────────────────────────────────────────────────────────────────

        public List<EsquemaRelacionInfo> ObtenerRelacionesFK(DataBase db, List<string> tablas)
        {
            var resultado = new List<EsquemaRelacionInfo>();
            if (conexion == null) return resultado;

            // Intentar primero vía GetSchema("ForeignKeys") — soportado por algunos drivers ODBC
            try
            {
                var fkSchema = db.GetSchema("ForeignKeys");
                foreach (DataRow row in fkSchema.Rows)
                {
                    string tablaOrigen = ObtenerCampo(row, "FK_TABLE_NAME", "FKTABLE_NAME");
                    string columnaOrigen = ObtenerCampo(row, "FK_COLUMN_NAME", "FKCOLUMN_NAME");
                    string tablaDestino = ObtenerCampo(row, "PK_TABLE_NAME", "PKTABLE_NAME");
                    string columnaDestino = ObtenerCampo(row, "PK_COLUMN_NAME", "PKCOLUMN_NAME");

                    if (!string.IsNullOrEmpty(tablaOrigen) && !string.IsNullOrEmpty(tablaDestino))
                    {
                        resultado.Add(new EsquemaRelacionInfo
                        {
                            TablaOrigen = tablaOrigen,
                            ColumnaOrigen = columnaOrigen,
                            TablaDestino = tablaDestino,
                            ColumnaDestino = columnaDestino
                        });
                    }
                }
                if (resultado.Count > 0) return resultado;
            }
            catch { }

            // Fallback: consulta SQL específica por motor
            string sql = null;
            switch (conexion.Motor)
            {
                case TipoMotor.MS_SQL:
                    sql = @"SELECT
                                fk.TABLE_NAME  AS FK_TABLE,
                                cu.COLUMN_NAME AS FK_COL,
                                pk.TABLE_NAME  AS PK_TABLE,
                                pt.COLUMN_NAME AS PK_COL
                            FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
                            JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS fk ON rc.CONSTRAINT_NAME  = fk.CONSTRAINT_NAME
                            JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS pk ON rc.UNIQUE_CONSTRAINT_NAME = pk.CONSTRAINT_NAME
                            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE  cu ON rc.CONSTRAINT_NAME  = cu.CONSTRAINT_NAME
                            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE  pt ON rc.UNIQUE_CONSTRAINT_NAME = pt.CONSTRAINT_NAME
                                                                         AND cu.ORDINAL_POSITION = pt.ORDINAL_POSITION";
                    break;

                case TipoMotor.POSTGRES:
                    sql = @"SELECT
                                kcu.table_name  AS FK_TABLE,
                                kcu.column_name AS FK_COL,
                                ccu.table_name  AS PK_TABLE,
                                ccu.column_name AS PK_COL
                            FROM information_schema.table_constraints tc
                            JOIN information_schema.key_column_usage kcu
                                ON tc.constraint_name = kcu.constraint_name
                            JOIN information_schema.constraint_column_usage ccu
                                ON ccu.constraint_name = tc.constraint_name
                            WHERE tc.constraint_type = 'FOREIGN KEY'";
                    break;

                case TipoMotor.DB2:
                    sql = @"SELECT
                                R.TABNAME  AS FK_TABLE,
                                K.COLNAME  AS FK_COL,
                                R.REFTABNAME AS PK_TABLE,
                                F.COLNAME  AS PK_COL
                            FROM SYSCAT.REFERENCES R
                            JOIN SYSCAT.KEYCOLUSE  K ON R.CONSTNAME = K.CONSTNAME AND R.TABSCHEMA = K.TABSCHEMA AND R.TABNAME = K.TABNAME
                            JOIN SYSCAT.KEYCOLUSE  F ON R.REFKEYNAME= F.CONSTNAME AND R.REFTABSCHEMA = F.TABSCHEMA AND R.REFTABNAME= F.TABNAME
                                                     AND K.COLSEQ = F.COLSEQ";
                    break;

                case TipoMotor.SQLite:
                    // SQLite: iterar por tabla y usar PRAGMA foreign_key_list
                    foreach (string tabla in tablas)
                    {
                        try
                        {
                            db.CommandText = $"PRAGMA foreign_key_list('{tabla}')";
                            while (db.Read())
                            {
                                resultado.Add(new EsquemaRelacionInfo
                                {
                                    TablaOrigen = tabla,
                                    ColumnaOrigen = db.Reader["from"].ToString(),
                                    TablaDestino = db.Reader["table"].ToString(),
                                    ColumnaDestino = db.Reader["to"].ToString()
                                });
                            }
                        }
                        catch { }
                    }
                    return resultado;
            }

            if (!string.IsNullOrEmpty(sql))
            {
                try
                {
                    db.CommandText = sql;
                    while (db.Read())
                    {
                        resultado.Add(new EsquemaRelacionInfo
                        {
                            TablaOrigen = db.Reader[0].ToString(),
                            ColumnaOrigen = db.Reader[1].ToString(),
                            TablaDestino = db.Reader[2].ToString(),
                            ColumnaDestino = db.Reader[3].ToString()
                        });
                    }
                }
                catch { }
            }

            return resultado;
        }

        /// <summary>Lee un campo de un DataRow probando múltiples nombres de columna posibles.</summary>
        private string ObtenerCampo(DataRow row, params string[] candidatos)
        {
            foreach (string c in candidatos)
            {
                if (row.Table.Columns.Contains(c) && row[c] != DBNull.Value)
                    return row[c].ToString();
            }
            return string.Empty;
        }

        // ────────────────────────────────────────────────────────────────
        // Generador de XML Draw.io
        // ────────────────────────────────────────────────────────────────

        public string GenerarXmlDrawio(Dictionary<string, EsquematizadorService.EsquemaTablaInfo> tablasMeta, Dictionary<string, Tuple<int, int>> posiciones)
        {
            const int ANCHO_TABLA = 260;
            const int ALTO_CABECERA = 32;
            const int ALTO_FILA = 22;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<mxGraphModel dx=\"1422\" dy=\"762\" grid=\"0\" gridSize=\"10\" guides=\"1\" " +
                          "tooltips=\"1\" connect=\"1\" arrows=\"1\" fold=\"1\" page=\"0\" pageScale=\"1\" " +
                          "pageWidth=\"1169\" pageHeight=\"827\" math=\"0\" shadow=\"0\">");
            sb.AppendLine("<root>");
            sb.AppendLine("<mxCell id=\"0\"/>");
            sb.AppendLine("<mxCell id=\"1\" parent=\"0\"/>");

            int idBase = 2;
            var idPorTabla = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in tablasMeta)
            {
                EsquemaTablaInfo t = kvp.Value;
                int id = idBase;
                idPorTabla[t.Nombre] = id;

                int altoTotal = ALTO_CABECERA + Math.Max(1, t.Columnas.Count) * ALTO_FILA;
                Tuple<int, int> pos;
                if (!posiciones.TryGetValue(t.Nombre, out pos)) pos = Tuple.Create(0, 0);
                int x = pos.Item1;
                int y = pos.Item2;

                bool esVista = t.Tipo == "VIEW";

                string estiloContenedor = esVista
                    ? "swimlane;fontStyle=2;align=center;startSize=32;fillColor=#dae8fc;strokeColor=#6c8ebf;rounded=1;arcSize=4;"
                    : "swimlane;fontStyle=1;align=center;startSize=32;fillColor=#fff2cc;strokeColor=#d6b656;rounded=1;arcSize=4;";

                string etiqueta = string.IsNullOrEmpty(t.Schema)
                    ? t.Nombre
                    : $"{t.Schema}.{t.Nombre}";
                if (esVista) etiqueta = "«view» " + etiqueta;

                sb.AppendLine($"<mxCell id=\"{id}\" value=\"{EscXml(etiqueta)}\" " +
                              $"style=\"{estiloContenedor}\" vertex=\"1\" parent=\"1\">");
                sb.AppendLine($"  <mxGeometry x=\"{x}\" y=\"{y}\" width=\"{ANCHO_TABLA}\" " +
                              $"height=\"{altoTotal}\" as=\"geometry\"/>");
                sb.AppendLine("</mxCell>");

                // Filas de columnas
                for (int i = 0; i < t.Columnas.Count; i++)
                {
                    var col2 = t.Columnas[i];
                    int childId = id + 1 + i;
                    string label = col2.Nombre + "  :  " + col2.Tipo;
                    if (!string.IsNullOrEmpty(col2.Longitud) && col2.Longitud != "0")
                        label += "(" + col2.Longitud + ")";

                    sb.AppendLine($"<mxCell id=\"{childId}\" value=\"{EscXml(label)}\" " +
                                  "style=\"text;align=left;spacingLeft=8;fontSize=11;fontFamily=Courier New;\" " +
                                  $"vertex=\"1\" parent=\"{id}\">");
                    sb.AppendLine($"  <mxGeometry x=\"0\" y=\"{ALTO_CABECERA + i * ALTO_FILA}\" " +
                                  $"width=\"{ANCHO_TABLA}\" height=\"{ALTO_FILA}\" as=\"geometry\"/>");
                    sb.AppendLine("</mxCell>");
                }

                idBase += 1 + t.Columnas.Count;
            }

            // ── Conectores FK (auto-routing, sin puntos de anclaje fijos) ────────
            int connId = idBase;
            var relacionesYaVistas = new HashSet<string>();

            foreach (var kvp in tablasMeta)
            {
                foreach (var rel in kvp.Value.Relaciones)
                {
                    if (!idPorTabla.ContainsKey(rel.TablaOrigen) ||
                        !idPorTabla.ContainsKey(rel.TablaDestino)) continue;

                    string clave = rel.TablaOrigen + "|" + rel.ColumnaOrigen + "|" +
                                   rel.TablaDestino + "|" + rel.ColumnaDestino;
                    if (!relacionesYaVistas.Add(clave)) continue;

                    int idOrigen = idPorTabla[rel.TablaOrigen];
                    int idDestino = idPorTabla[rel.TablaDestino];
                    string label = rel.ColumnaOrigen + " → " + rel.ColumnaDestino;

                    // Estilo auto-routing: draw.io elige el camino óptimo
                    // Sin anclas fijas: draw.io elige el mejor punto de conexión
                    string estiloArco =
                        "edgeStyle=orthogonalEdgeStyle;rounded=1;orthogonalLoop=1;" +
                        "jettySize=auto;" +
                        "endArrow=ERone;endFill=0;startArrow=ERmanyToOne;startFill=0;";

                    sb.AppendLine($"<mxCell id=\"{connId}\" value=\"{EscXml(label)}\" " +
                                  $"style=\"{estiloArco}\" " +
                                  $"edge=\"1\" source=\"{idOrigen}\" target=\"{idDestino}\" parent=\"1\">");
                    sb.AppendLine("  <mxGeometry relative=\"1\" as=\"geometry\"/>");
                    sb.AppendLine("</mxCell>");
                    connId++;
                }
            }

            sb.AppendLine("</root></mxGraphModel>");
            return sb.ToString();
        }

        private string EscXml(string s)
        {
            return System.Security.SecurityElement.Escape(s) ?? string.Empty;
        }

        // ────────────────────────────────────────────────────────────────
        // Compresión Draw.io: Deflate raw + Base64 (formato que acepta la URL de draw.io)
        // ────────────────────────────────────────────────────────────────

        public string ComprimirDrawio(string xml)
        {
            byte[] datos = System.Text.Encoding.UTF8.GetBytes(xml);

            using (var ms = new System.IO.MemoryStream())
            {
                // DeflateStream escribe Deflate sin cabecera zlib (raw deflate)
                using (var deflate = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
                {
                    deflate.Write(datos, 0, datos.Length);
                }
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Layout jerárquico para diagramas ER
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Calcula las posiciones (x, y) de cada tabla usando un layout jerárquico
        /// guiado por las relaciones FK.<br/>
        /// • Las tablas padre (referenciadas) se colocan arriba.<br/>
        /// • Las tablas hijo (con FK columns) se colocan debajo, con separación generosa.<br/>
        /// • Las tablas sin FK se agrupan en una grilla al final.
        /// </summary>
        public Dictionary<string, Tuple<int, int>> CalcularLayoutER(Dictionary<string, EsquematizadorService.EsquemaTablaInfo> tablasMeta)
        {
            const int ANCHO = 260;
            const int ALTO_CAB = 32;
            const int ALTO_FILA = 22;
            const int H_GAP = 200;   // espacio horizontal entre tablas del mismo nivel
            const int V_GAP = 200;   // espacio vertical entre niveles
            const int COMP_GAP = 380;   // espacio entre componentes desconectados
            const int COLS_MAX = 4;     // máx columnas para grilla sin FK

            var posiciones = new Dictionary<string, Tuple<int, int>>(StringComparer.OrdinalIgnoreCase);
            var tablas = tablasMeta.Keys.ToList();
            if (tablas.Count == 0) return posiciones;

            // ── 1. Construir grafo de adyacencia ─────────────────────────────────
            var inEdges = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var outEdges = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var adjUnd = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in tablas)
            {
                inEdges[t] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                outEdges[t] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                adjUnd[t] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var kvp in tablasMeta)
            {
                foreach (var rel in kvp.Value.Relaciones)
                {
                    // src = tabla con FK column (hijo), dst = tabla referenciada (padre)
                    string src = rel.TablaOrigen;
                    string dst = rel.TablaDestino;
                    if (!tablasMeta.ContainsKey(src) || !tablasMeta.ContainsKey(dst)) continue;
                    outEdges[src].Add(dst);
                    inEdges[dst].Add(src);
                    adjUnd[src].Add(dst);
                    adjUnd[dst].Add(src);
                }
            }

            // ── 2. Componentes conectados (BFS no dirigido) ──────────────────────
            var visitados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var componentes = new List<List<string>>();

            foreach (var t in tablas)
            {
                if (visitados.Contains(t)) continue;
                var comp = new List<string>();
                var cola = new Queue<string>();
                cola.Enqueue(t); visitados.Add(t);
                while (cola.Count > 0)
                {
                    var cur = cola.Dequeue();
                    comp.Add(cur);
                    foreach (var v in adjUnd[cur])
                        if (!visitados.Contains(v)) { visitados.Add(v); cola.Enqueue(v); }
                }
                componentes.Add(comp);
            }

            // Ordenar: componentes más grandes primero para que queden a la izquierda
            componentes.Sort((a, b) => b.Count.CompareTo(a.Count));

            // ── 3. Posicionar cada componente ────────────────────────────────────
            int xGlobal = 0;

            foreach (var comp in componentes)
            {
                var tablasComp = new HashSet<string>(comp, StringComparer.OrdinalIgnoreCase);
                bool tieneFKs = comp.Any(t =>
                   outEdges[t].Any(d => tablasComp.Contains(d)) ||
                   inEdges[t].Any(s => tablasComp.Contains(s)));

                if (!tieneFKs)
                {
                    // ── Grilla simple para componentes sin FKs ──────────────────
                    int cols = Math.Min(COLS_MAX, comp.Count);
                    comp.Sort(StringComparer.OrdinalIgnoreCase);
                    int c = 0, yOff = 0, xOff = 0;
                    int maxAltoFila = 0;

                    for (int i = 0; i < comp.Count; i++)
                    {
                        posiciones[comp[i]] = Tuple.Create(xGlobal + xOff, yOff);
                        int altoT = ALTO_CAB + tablasMeta[comp[i]].Columnas.Count * ALTO_FILA;
                        if (altoT > maxAltoFila) maxAltoFila = altoT;

                        c++;
                        if (c >= cols) { c = 0; xOff = 0; yOff += maxAltoFila + V_GAP; maxAltoFila = 0; }
                        else { xOff += ANCHO + H_GAP; }
                    }

                    int anchoComp = Math.Min(comp.Count, cols) * (ANCHO + H_GAP) - H_GAP;
                    xGlobal += anchoComp + COMP_GAP;
                }
                else
                {
                    // ── Layout jerárquico guiado por FK — orden topológico de Kahn ──
                    //
                    // "Padre de layout" de t  = tablas que t referencia (outEdges[t] ∩ comp)
                    // "Hijo  de layout" de t  = tablas que referencian a t (inEdges[t] ∩ comp)
                    //
                    // Kahn encola cada nodo exactamente UNA vez, cuando todos sus padres ya
                    // fueron procesados, por lo que el nivel asignado es siempre el máximo
                    // posible y el algoritmo termina en O(V+E) incluso con ciclos.
                    // Los nodos que forman ciclos (in-degree nunca llega a 0) quedan sin nivel
                    // y se les asigna 0 en el fallback final.

                    // in-degree de Kahn = cantidad de padres de layout dentro del componente
                    var inDeg = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var t in comp)
                        inDeg[t] = outEdges[t].Count(d => tablasComp.Contains(d));

                    var nivel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    var colaL = new Queue<string>();

                    // Semilla: nodos sin padres (tablas "raíz" puras)
                    foreach (var t in comp)
                        if (inDeg[t] == 0) { nivel[t] = 0; colaL.Enqueue(t); }

                    // Ciclo puro (todos tienen padres): elegir el más referenciado como raíz
                    if (colaL.Count == 0)
                    {
                        var root = comp
                            .OrderByDescending(t => inEdges[t].Count(s => tablasComp.Contains(s)))
                            .First();
                        nivel[root] = 0;
                        inDeg[root] = 0;
                        colaL.Enqueue(root);
                    }

                    while (colaL.Count > 0)
                    {
                        var cur = colaL.Dequeue();
                        foreach (var hijo in inEdges[cur])
                        {
                            if (!tablasComp.Contains(hijo)) continue;

                            // Propagar nivel máximo
                            int nNivel = nivel[cur] + 1;
                            if (!nivel.ContainsKey(hijo) || nivel[hijo] < nNivel)
                                nivel[hijo] = nNivel;

                            // Cuando todos los padres del hijo ya se procesaron, encolar
                            inDeg[hijo]--;
                            if (inDeg[hijo] == 0)
                                colaL.Enqueue(hijo);
                        }
                    }

                    // Fallback: nodos que quedaron en ciclos sin asignar → nivel 0
                    foreach (var t in comp) if (!nivel.ContainsKey(t)) nivel[t] = 0;

                    // Agrupar por nivel y ordenar cada nivel por nombre
                    var porNivel = new SortedDictionary<int, List<string>>();
                    foreach (var kvp in nivel)
                    {
                        if (!porNivel.ContainsKey(kvp.Value)) porNivel[kvp.Value] = new List<string>();
                        porNivel[kvp.Value].Add(kvp.Key);
                    }
                    foreach (var kvp in porNivel) kvp.Value.Sort(StringComparer.OrdinalIgnoreCase);

                    // ── Barycenter heuristic (un sweep top-down) ─────────────────────
                    // Ordena las tablas dentro de cada nivel según la posición promedio
                    // de sus padres en FK para minimizar el cruce de aristas.
                    int maxNivLayout = porNivel.Keys.Max();
                    for (int niv2 = 0; niv2 < maxNivLayout; niv2++)
                    {
                        if (!porNivel.ContainsKey(niv2) || !porNivel.ContainsKey(niv2 + 1)) continue;
                        var nivActual = porNivel[niv2];
                        var nivHijos = porNivel[niv2 + 1];

                        // Índice posicional de cada tabla en el nivel actual
                        var idxPadre = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        for (int ki = 0; ki < nivActual.Count; ki++) idxPadre[nivActual[ki]] = ki;

                        // Baricentro de cada hijo = promedio del índice de sus padres
                        var bary = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                        for (int ki = 0; ki < nivHijos.Count; ki++)
                        {
                            string hijo = nivHijos[ki];
                            var padresEnNivel = outEdges[hijo]
                                .Where(p => idxPadre.ContainsKey(p))
                                .ToList();
                            bary[hijo] = padresEnNivel.Count > 0
                                ? padresEnNivel.Average(p => (double)idxPadre[p])
                                : (double)ki; // sin padres en este nivel: mantener posición
                        }
                        nivHijos.Sort((a, b) => bary[a].CompareTo(bary[b]));
                    }

                    // Ancho del componente = nivel más ancho
                    int maxCols = porNivel.Values.Max(lst => lst.Count);
                    int anchoComp = maxCols * (ANCHO + H_GAP) - H_GAP;

                    // Acumular yOffset por nivel según la altura máxima de cada nivel
                    int yOff = 0;
                    var yPorNivel = new Dictionary<int, int>();
                    foreach (var kvp in porNivel)
                    {
                        yPorNivel[kvp.Key] = yOff;
                        int maxAlto = kvp.Value.Max(t => ALTO_CAB + tablasMeta[t].Columnas.Count * ALTO_FILA);
                        yOff += maxAlto + V_GAP;
                    }

                    // Asignar posiciones centradas dentro del ancho del componente
                    foreach (var kvp in porNivel)
                    {
                        var lst = kvp.Value;
                        int anchoNiv = lst.Count * (ANCHO + H_GAP) - H_GAP;
                        int xStart = xGlobal + (anchoComp - anchoNiv) / 2;

                        for (int i = 0; i < lst.Count; i++)
                            posiciones[lst[i]] = Tuple.Create(xStart + i * (ANCHO + H_GAP), yPorNivel[kvp.Key]);
                    }

                    xGlobal += anchoComp + COMP_GAP;
                }
            }

            return posiciones;
        }
    }
}
