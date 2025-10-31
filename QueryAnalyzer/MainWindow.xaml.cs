﻿using Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Odbc;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Serialization;

namespace QueryAnalyzer
{
    public partial class MainWindow : Window
    {
        private const string HISTORY_FILE = "query_history.txt";
        private const string HISTORIAL_XML = "historial.xml";
        public Dictionary<string, OdbcType> OdbcTypes { get; set; }
        public List<QueryParameter> Parametros { get; set; }

        static public Conexion conexionActual = null;

        public MainWindow()
        {
            InitializeComponent();

            // 🔹 AÑADIDO: esto asegura que los bindings de DataContext funcionen correctamente.
            DataContext = this;

            LoadHistory(); // mantiene compatibilidad con el archivo de texto
            txtQuery.KeyDown += TxtQuery_KeyDown;
            txtQuery.Text = "SELECT * FROM SYSIBM.SYSCOLUMNS FETCH FIRST 10 ROWS ONLY; -- ejemplo para DB2";

            CargarTipos();

            // Configura ItemsSource correctamente
            Parametros = new List<QueryParameter>();
            gridParams.ItemsSource = Parametros;
            InicializarConexiones();
            BloquearUI(true);
        }
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (e.Key == Key.F5)
            {
                TxtQuery_KeyDown(this, e);
                e.Handled = true;
            }
        }

        private void CargarTipos()
        {
            OdbcTypes = Enum.GetValues(typeof(OdbcType))
                .Cast<OdbcType>()
                .ToDictionary(t => t.ToString(), t => t);
        }

        private void InicializarConexiones()
        {
            var conexiones = ConexionesManager.Cargar();
            cbDriver.ItemsSource = conexiones.Values.ToList();
            cbDriver.DisplayMemberPath = "Nombre";
        }

        private void cbDriver_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbDriver.SelectedItem is Conexion conexion)
            {
                conexionActual = conexion;
                BloquearUI(false);
                AppendMessage($"Conexión seleccionada: {conexion.Motor}");

                // NUEVO: al seleccionar conexión, filtramos historial para esa conexión
                LoadHistoryForConnection(conexion);
            }
        }

        private void BloquearUI(bool bloquear)
        {
            txtQuery.IsEnabled = !bloquear;
            btnExecute.IsEnabled = !bloquear;
            btnExecuteScalar.IsEnabled = !bloquear;
            btnTest.IsEnabled = !bloquear;
            btnClear.IsEnabled = !bloquear;
        }

        private void TxtQuery_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
                BtnExecute_Click(this, new RoutedEventArgs());
        }

        private async void BtnExecute_Click(object sender, RoutedEventArgs e)
        {
            Stopwatch sw = Stopwatch.StartNew();

            string connStr = GetConnectionString();
            string sql = txtQuery.Text;

            if (string.IsNullOrWhiteSpace(connStr))
            {
                AppendMessage("El string de conexión está vacío.");
                return;
            }
            if (string.IsNullOrWhiteSpace(sql))
            {
                AppendMessage("La consulta está vacía.");
                return;
            }

            AppendMessage($"Ejecutando... ({DateTime.Now})");

            try
            {
                // Captura los parámetros de UI en el hilo de UI
                List<QueryParameter> parametros = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    parametros = gridParams.Items.OfType<QueryParameter>()
                        .Where(p => !string.IsNullOrWhiteSpace(p.Nombre))
                        .ToList();
                });

                var dt = await ExecuteQueryAsync(connStr, sql, parametros);

                dgResults.ItemsSource = dt.DefaultView;
                // Asigno formato a las columnas numéricas
                foreach (var column in dgResults.Columns)
                {
                    // Obtiene el tipo de datos real de la columna desde el DataTable
                    var colName = column.Header.ToString();
                    var dataType = dt.Columns[colName].DataType;

                    if (dataType == typeof(int) ||
                        dataType == typeof(long) ||
                        dataType == typeof(decimal) ||
                        dataType == typeof(double) ||
                        dataType == typeof(float))
                    {
                        column.CellStyle = new Style(typeof(DataGridCell))
                        {
                            Setters =
                            {
                                new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right)
                            }
                        };
                    }
                    else
                    {
                        column.CellStyle = new Style(typeof(DataGridCell))
                        {
                            Setters =
                            {
                                new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Left)
                            }
                        };
                    }
                }

                txtRowCount.Text = dt.Rows.Count.ToString();
                sw.Stop();
                double elapsedMicroseconds = sw.ElapsedTicks * (1000000.0 / Stopwatch.Frequency);

                txtTiempoDeEjecucion.Text = $"{elapsedMicroseconds} ms";

                await Dispatcher.InvokeAsync(() =>
                    AppendMessage($"Ejecución exitosa!. {dt.Rows.Count} filas devueltas en {elapsedMicroseconds} ms"));

                // NUEVO: guardo la consulta y sus parámetros asociados a la conexión actual
                AddToHistoryWithParams(sql, parametros);
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                    AppendMessage("Error: " + ex.Message));
            }
        }

        private async Task<DataTable> ExecuteQueryAsync(string connStr, string sql, List<QueryParameter> parametros)
        {
            return await Task.Run(() =>
            {
                var dt = new DataTable();

                try
                {
                    using (var conn = new OdbcConnection(connStr))
                    {
                        conn.Open();
                        using (var cmd = new OdbcCommand(sql, conn))
                        {
                            foreach (var p in parametros)
                            {
                                var name = p.Nombre.StartsWith("@") ? p.Nombre : "@" + p.Nombre;
                                var param = new OdbcParameter(name, p.Tipo);
                                param.Value = string.IsNullOrEmpty(p.Valor) ? DBNull.Value : (object)p.Valor;
                                cmd.Parameters.Add(param);
                            }

                            using (var adapter = new OdbcDataAdapter(cmd))
                            {
                                adapter.Fill(dt);
                            }
                        }
                    }
                }
                catch (Exception err)
                {
                    AppendMessage($"Ocurrió un error al ejecutar la consulta: {err.Message}");
                }

                return dt;
            });
        }

        private async void BtnExecuteScalar_Click(object sender, RoutedEventArgs e)
        {
            Stopwatch sw = Stopwatch.StartNew();

            string connStr = GetConnectionString();
            string sql = txtQuery.Text;

            AppendMessage("Ejecutando escalar...");

            try
            {
                var result = await Task.Run(() =>
                {
                    using (var conn = new OdbcConnection(connStr))
                    {
                        conn.Open();
                        using (var cmd = new OdbcCommand(sql, conn))
                            return cmd.ExecuteScalar();
                    }
                });

                sw.Stop();
                double elapsedMicroseconds = sw.ElapsedTicks * (1000000.0 / Stopwatch.Frequency);

                txtTiempoDeEjecucion.Text = $"{elapsedMicroseconds} ms";

                await Dispatcher.InvokeAsync(() =>
                    AppendMessage($"Resultado del escalar: {(result?.ToString() ?? "(null) en {}")} en {elapsedMicroseconds} ms"));
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                    AppendMessage("Error: " + ex.Message));
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            dgResults.ItemsSource = null;
            txtRowCount.Text = "0";
            AppendMessage("Resultados borrados.");
        }

        private string GetConnectionString()
        {
            string stringConnection = string.Empty;
            if (conexionActual != null)
            {
                switch (conexionActual.Motor)
                {
                    case TipoMotor.MS_SQL:
                        stringConnection = $@"Driver={{ODBC Driver 17 for SQL Server}};Server=SQL{conexionActual.Servidor}\{conexionActual.Servidor};Database={conexionActual.BaseDatos};Uid={conexionActual.Usuario};Pwd={conexionActual.Contrasena};TrustServerCertificate=yes;";
                        break;
                    case TipoMotor.DB2:
                        stringConnection = $"Driver={{IBM DB2 ODBC DRIVER}};Database={conexionActual.BaseDatos};Hostname={conexionActual.Servidor};Port=50000; Protocol=TCPIP;Uid={conexionActual.Usuario};Pwd={conexionActual.Contrasena};";
                        break;
                    case TipoMotor.POSTGRES:
                        stringConnection = $"Driver={{PostgreSQL Unicode}};Server={conexionActual.Servidor};Port=5432;Database={conexionActual.BaseDatos};Uid={conexionActual.Usuario};Pwd={conexionActual.Contrasena};";
                        break;
                    case TipoMotor.SQLite:
                        stringConnection = $"Driver={{SQLite3 ODBC Driver}};Database={conexionActual.Servidor};"; //"Data Source={conexionActual.Servidor};Version=3;";
                        break;
                    default:
                        break;
                }
            }
            return stringConnection;
        }
        private void AppendMessage(string text)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendMessage(text));
                return;
            }

            txtMessages.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n");
            txtMessages.ScrollToEnd();
        }

        // ────────────────────────────────────────────────────────
        // HISTORIAL: nuevo manejo con parámetros y asociación a conexión
        // ────────────────────────────────────────────────────────

        // Mantengo compatibilidad leyendo el archivo de texto (si estaba)
        private void LoadHistory()
        {
            try
            {
                if (File.Exists(HISTORY_FILE))
                {
                    var content = File.ReadAllText(HISTORY_FILE);
                    var parts = content.Split(new string[] { "---\n", "---\r\n" },
                                              StringSplitOptions.RemoveEmptyEntries);
                    foreach (var p in parts)
                    {
                        var t = p.Trim();
                        if (!string.IsNullOrEmpty(t))
                            lstHistory.Items.Add(t);
                    }
                }
            }
            catch { }
        }

        // Guarda en historial.xml una entrada con parámetros
        private void AddToHistoryWithParams(string sql, List<QueryParameter> parametros)
        {
            try
            {
                // 1) Guardar en el archivo de texto como antes (compatibilidad)
                try
                {
                    lstHistory.Items.Insert(0, sql);
                    File.AppendAllText(HISTORY_FILE, sql + Environment.NewLine + "---" + Environment.NewLine);
                }
                catch { /* no bloqueante */ }

                // 2) Guardar en el XML estructurado (historial por conexión)
                var all = LoadAllHistoriales();
                var h = new Historial
                {
                    conexion = conexionActual != null ? new Conexion
                    {
                        Nombre = conexionActual.Nombre,
                        Motor = conexionActual.Motor,
                        Servidor = conexionActual.Servidor,
                        BaseDatos = conexionActual.BaseDatos,
                        Usuario = conexionActual.Usuario,
                        Contrasena = conexionActual.Contrasena
                    } : null,
                    Consulta = sql,
                    Parametros = new List<string[]>(),
                    Fecha = DateTime.Now
                };

                if (parametros != null)
                {
                    foreach (var p in parametros)
                    {
                        // Guardamos [Nombre, Tipo, Valor]
                        var tipoStr = p.Tipo.ToString();
                        h.Parametros.Add(new string[] { p.Nombre, tipoStr, p.Valor });
                    }
                }

                all.Add(h);
                SaveAllHistoriales(all);

                // Si la consulta pertenece a la conexión actualmente seleccionada, recargo la lista visual
                if (conexionActual != null)
                    LoadHistoryForConnection(conexionActual);
            }
            catch (Exception ex)
            {
                AppendMessage("No se pudo guardar el historial: " + ex.Message);
            }
        }

        private List<Historial> LoadAllHistoriales()
        {
            try
            {
                if (!File.Exists(HISTORIAL_XML))
                    return new List<Historial>();

                using (var fs = new FileStream(HISTORIAL_XML, FileMode.Open))
                {
                    var serializer = new XmlSerializer(typeof(List<Historial>));
                    var list = (List<Historial>)serializer.Deserialize(fs);
                    return list ?? new List<Historial>();
                }
            }
            catch
            {
                return new List<Historial>();
            }
        }

        private void SaveAllHistoriales(List<Historial> list)
        {
            try
            {
                using (var fs = new FileStream(HISTORIAL_XML, FileMode.Create))
                {
                    var serializer = new XmlSerializer(typeof(List<Historial>));
                    serializer.Serialize(fs, list);
                }
            }
            catch (Exception ex)
            {
                AppendMessage("Error guardando historial XML: " + ex.Message);
            }
        }

        // Carga en lstHistory *sólo* las consultas asociadas a la conexión dada (más recientes primero)
        private void LoadHistoryForConnection(Conexion conexion)
        {
            try
            {
                lstHistory.Items.Clear();

                if (conexion == null) return;

                var all = LoadAllHistoriales();

                // Filtramos por nombre exacto de conexión (podés adaptar a otra comparación si prefieres)
                var relacionados = all
                    .Where(h => h.conexion != null && h.conexion.Nombre == conexion.Nombre)
                    .OrderByDescending(h => h.Fecha)
                    .ToList();

                if (relacionados.Count == 0)
                {
                    // Si no hay historial asociado, dejamos la lista vacía
                    return;
                }

                // Inserto ListBoxItems con Tag = Historial para poder cargar parámetros luego
                foreach (var h in relacionados)
                {
                    var item = new ListBoxItem
                    {
                        Content = $"{h.Consulta}    [{h.Fecha:yyyy-MM-dd HH:mm:ss}]",
                        Tag = h
                    };
                    lstHistory.Items.Add(item);
                }
            }
            catch (Exception ex)
            {
                AppendMessage("Error cargando historial para la conexión: " + ex.Message);
            }
        }

        // Cuando seleccionan un elemento en el historial, carga consulta y parámetros (si existen)
        private void LstHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstHistory.SelectedItem == null)
                return;

            try
            {
                // Si el item tiene Tag = Historial (nuestro nuevo formato), usamos eso
                if (lstHistory.SelectedItem is ListBoxItem lbi && lbi.Tag is Historial hist)
                {
                    txtQuery.Text = hist.Consulta ?? string.Empty;

                    // Cargar parámetros en la grilla
                    var nuevos = new List<QueryParameter>();
                    if (hist.Parametros != null)
                    {
                        foreach (var p in hist.Parametros)
                        {
                            // p = [Nombre, Tipo, Valor]
                            string nombre = p.Length > 0 ? p[0] : string.Empty;
                            string tipoStr = p.Length > 1 ? p[1] : string.Empty;
                            string valor = p.Length > 2 ? p[2] : string.Empty;

                            OdbcType tipoEnum = OdbcType.VarChar;
                            if (!string.IsNullOrWhiteSpace(tipoStr))
                            {
                                if (!Enum.TryParse(tipoStr, out tipoEnum))
                                {
                                    // intenta parsear por nombre completo (por si el tipo vino como "Int32" u otro)
                                    try
                                    {
                                        tipoEnum = (OdbcType)Enum.Parse(typeof(OdbcType), tipoStr, true);
                                    }
                                    catch { tipoEnum = OdbcType.VarChar; }
                                }
                            }

                            var qp = new QueryParameter
                            {
                                Nombre = nombre,
                                Tipo = tipoEnum,
                                Valor = valor
                            };
                            nuevos.Add(qp);
                        }
                    }

                    // Reemplazamos la lista de parámetros y reasignamos ItemsSource para forzar refresco
                    Parametros = nuevos;
                    gridParams.ItemsSource = Parametros;
                }
                else
                {
                    // compatibilidad con el historial de texto plano (string)
                    if (lstHistory.SelectedItem is string s)
                        txtQuery.Text = s;
                    else if (lstHistory.SelectedItem is ListBoxItem li && li.Content is string cs)
                        txtQuery.Text = cs;
                }
            }
            catch (Exception ex)
            {
                AppendMessage("Error al cargar selección del historial: " + ex.Message);
            }
        }

        // ────────────────────────────────────────────────────────
        // Resto del código (Explorador, botones, etc.) sin cambios estructurales
        // ────────────────────────────────────────────────────────

        private void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            string conn = GetConnectionString();
            if (string.IsNullOrWhiteSpace(conn))
            {
                AppendMessage("El string de conexión está vacío.");
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    using (var c = new OdbcConnection(conn))
                    {
                        c.Open();
                        Dispatcher.Invoke(() => AppendMessage("Conexión exitosa."));
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendMessage("Conexión fallida: " + ex.Message));
                }
            });
        }

        private void BtnEditConn_Click(object sender, RoutedEventArgs e)
        {
            DatosConexion datosConexion = new DatosConexion();
            datosConexion.ShowDialog();
            InicializarConexiones();
            foreach (var item in cbDriver.Items)
            {
                try
                {
                    if (conexionActual != null && ((Conexion)item).Nombre == conexionActual.Nombre)
                    {
                        cbDriver.SelectedItem = item;
                        break;
                    }
                }
                catch (Exception err)
                {

                }
            }
        }

        private void BtnNewConn_Click(object sender, RoutedEventArgs e)
        {
            DatosConexion datosConexion = new DatosConexion();
            datosConexion.ShowDialog();
            InicializarConexiones();
            foreach (var item in cbDriver.Items)
            {
                try
                {
                    if (conexionActual != null && ((Conexion)item).Nombre == conexionActual.Nombre)
                    {
                        cbDriver.SelectedItem = item;
                        break;
                    }
                }
                catch (Exception err)
                {

                }
            }
        }

        private void btnDeleteConn_Click(object sender, RoutedEventArgs e)
        {
            if (cbDriver.SelectedItem is Conexion conexion)
            {
                conexionActual = conexion;
                var conexiones = ConexionesManager.Cargar();
                conexiones.Remove(conexionActual.Nombre);
                ConexionesManager.Guardar(conexiones);
                cbDriver.ItemsSource = conexiones.Values.ToList();
                cbDriver.DisplayMemberPath = "Nombre";
            }
        }

        // 🔹 NUEVOS MÉTODOS: Explorador de tablas
        private async void CargarEsquema()
        {
            if (conexionActual == null)
            {
                AppendMessage("No hay conexión seleccionada.");
                return;
            }

            string connStr = GetConnectionString();

            await Task.Run(() =>
            {
                try
                {
                    using (var conn = new OdbcConnection(connStr))
                    {
                        conn.Open();

                        // Obtiene las tablas
                        DataTable tablas = conn.GetSchema("Tables");
                        //List<string> tablasBaseDatos = new List<string>();
                        //if (conexionActual.Motor == TipoMotor.DB2)
                        //{
                        //    OdbcCommand consultaTablas = conn.CreateCommand();
                        //    consultaTablas.CommandText = "SELECT LTRIM(RTRIM(TBNAME)) AS Nombre, COUNT(NAME) FROM SYSIBM.SYSCOLUMNS WHERE TBCREATOR = 'DB2ADMIN' GROUP BY LTRIM(RTRIM(TBNAME)) ORDER BY Nombre";
                        //    IDataReader lector = consultaTablas.ExecuteReader();
                        //    while (lector.Read())
                        //    {
                        //        tablasBaseDatos.Add(lector.GetString(0));
                        //    }
                        //    lector.Close();
                        //}

                        Dispatcher.Invoke(() => tvSchema.Items.Clear());

                        foreach (DataRow tabla in tablas.Rows)
                        {
                            string schema = tabla["TABLE_SCHEM"].ToString();
                            string nombreTabla = tabla["TABLE_NAME"].ToString();
                            string tipo = tabla["TABLE_TYPE"].ToString();

                            if (tipo != "TABLE") continue;
                            //if (!tablasBaseDatos.Contains(nombreTabla)) continue;

                            // 🔹 Creamos datos simples (strings) en el hilo de fondo
                            string headerText = string.IsNullOrEmpty(schema) ? nombreTabla : $"{schema}.{nombreTabla}";
                            var columnas = conn.GetSchema("Columns", new string[] { null, schema, nombreTabla });

                            // 🔹 Ahora toda manipulación de la UI dentro del Dispatcher
                            Dispatcher.Invoke(() =>
                            {
                                var tablaNode = new TreeViewItem
                                {
                                    Header = headerText,
                                    Tag = nombreTabla
                                };

                                tvSchema.Items.Add(tablaNode);

                                // Agregamos las columnas dentro del hilo de UI
                                foreach (DataRow col in columnas.Rows)
                                {
                                    string colName = col["COLUMN_NAME"].ToString();
                                    string tipoCol = col["TYPE_NAME"].ToString();
                                    string longitud = col["COLUMN_SIZE"].ToString();

                                    var colNode = new TreeViewItem
                                    {
                                        Header = $"{colName} ({tipoCol}{(string.IsNullOrEmpty(longitud) ? "" : $"[{longitud}]")})"
                                    };

                                    tablaNode.Items.Add(colNode);
                                }
                            });

                            // 🔹 Carga de índices (solo lectura, sin UI)
                            try
                            {
                                using (var cmd = conn.CreateCommand())
                                {
                                    switch (conexionActual.Motor)
                                    {
                                        case TipoMotor.MS_SQL:
                                            cmd.CommandText = $@"SELECT 
                                                                    s.name AS SchemaName, 
                                                                    t.name AS TableName, 
                                                                    i.name AS IndexName, 
                                                                    i.type_desc AS IndexType, 
                                                                    c.name AS ColumnName, 
                                                                    ic.key_ordinal AS ColumnOrder,
                                                                    i.is_primary_key AS IsPrimaryKey,
                                                                    i.is_unique AS IsUnique
                                                                FROM sys.indexes i
                                                                INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                                                                INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                                                                INNER JOIN sys.tables t ON i.object_id = t.object_id
                                                                INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                                                                WHERE t.name = '{nombreTabla}'
                                                                ORDER BY i.name, ic.key_ordinal;";
                                            break;
                                        case TipoMotor.DB2:
                                            cmd.CommandText = $@"SELECT
                                                                    i.TABSCHEMA AS SchemaName,
                                                                    i.TABNAME AS TableName,
                                                                    i.INDNAME AS IndexName,
                                                                    i.UNIQUERULE AS UniqueRule,
                                                                    c.COLNAME AS ColumnName,
                                                                    c.COLSEQ AS ColumnOrder,
                                                                    i.INDEXTYPE AS IndexType
                                                                FROM SYSCAT.INDEXES i
                                                                JOIN SYSCAT.INDEXCOLUSE c
                                                                    ON i.INDNAME = c.INDNAME AND i.INDSCHEMA = c.INDSCHEMA
                                                                WHERE i.TABNAME = UPPER('{nombreTabla}')
                                                                ORDER BY i.INDNAME, c.COLSEQ;";
                                            break;
                                        case TipoMotor.POSTGRES:
                                            cmd.CommandText = $@"SELECT
                                                                    n.nspname AS SchemaName,
                                                                    t.relname AS TableName,
                                                                    i.relname AS IndexName,
                                                                    a.attname AS ColumnName,
                                                                    ix.indisunique AS IsUnique,
                                                                    ix.indisprimary AS IsPrimary
                                                                FROM pg_class t
                                                                JOIN pg_index ix ON t.oid = ix.indrelid
                                                                JOIN pg_class i ON i.oid = ix.indexrelid
                                                                JOIN pg_namespace n ON n.oid = t.relnamespace
                                                                JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey)
                                                                WHERE t.relname = '{nombreTabla}'
                                                                ORDER BY i.relname, a.attnum;";
                                            break;
                                        case TipoMotor.SQLite:
                                            cmd.CommandText = $"PRAGMA index_list('{nombreTabla}');";
                                            break;
                                        default:
                                            break;
                                    }

                                    using (var adapter = new OdbcDataAdapter(cmd))
                                    {
                                        var dtIndices = new DataTable();
                                        adapter.Fill(dtIndices);

                                        if (dtIndices.Rows.Count > 0)
                                        {
                                            // Creamos la estructura para los índices
                                            Dispatcher.Invoke(() =>
                                            {
                                                var tablaNode = tvSchema.Items.OfType<TreeViewItem>()
                                                    .FirstOrDefault(t => (string)t.Tag == nombreTabla);
                                                if (tablaNode == null) return;

                                                var indiceRaiz = new TreeViewItem { Header = "Índices" };
                                                foreach (DataRow indice in dtIndices.Rows)
                                                {
                                                    try
                                                    {
                                                        string nombreIndice = indice[conexionActual.Motor == TipoMotor.SQLite ? "NAME" : "INDEXNAME"].ToString();
                                                        var nodoIndice = new TreeViewItem { Header = nombreIndice };
                                                        indiceRaiz.Items.Add(nodoIndice);
                                                    }
                                                    catch (Exception)
                                                    {
                                                    }
                                                }
                                                tablaNode.Items.Add(indiceRaiz);
                                            });
                                        }
                                    }
                                }
                            }
                            catch { /* Algunos motores no exponen esa vista */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendMessage("Error al cargar esquema: " + ex.Message));
                }
            });
        }


        private void tvSchema_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (tvSchema.SelectedItem is TreeViewItem item && item.Tag != null)
            {
                string tableName = item.Header.ToString();
                txtQuery.Text = $"SELECT * FROM {tableName} FETCH FIRST 100 ROWS ONLY;";
            }
        }

        private void btnExplorar_Click(object sender, RoutedEventArgs e)
        {
            CargarEsquema(); // 🔹 NUEVO
        }
    }
}




//using Models;
//using System;
//using System.Collections.Generic;
//using System.Data;
//using System.Data.Odbc;
//using System.Diagnostics;
//using System.IO;
//using System.Linq;
//using System.Threading.Tasks;
//using System.Windows;
//using System.Windows.Controls;
//using System.Windows.Input;

//namespace QueryAnalyzer
//{
//    public partial class MainWindow : Window
//    {
//        private const string HISTORY_FILE = "query_history.txt";
//        public Dictionary<string, OdbcType> OdbcTypes { get; set; }
//        public List<QueryParameter> Parametros { get; set; }

//        static public Conexion conexionActual = null;

//        public MainWindow()
//        {
//            InitializeComponent();

//            // 🔹 AÑADIDO: esto asegura que los bindings de DataContext funcionen correctamente.
//            DataContext = this;

//            LoadHistory();
//            txtQuery.KeyDown += TxtQuery_KeyDown;
//            txtQuery.Text = "SELECT * FROM SYSIBM.SYSCOLUMNS FETCH FIRST 10 ROWS ONLY; -- ejemplo para DB2";

//            CargarTipos();

//            // Configura ItemsSource correctamente
//            Parametros = new List<QueryParameter>();
//            gridParams.ItemsSource = Parametros;
//            InicializarConexiones();
//            BloquearUI(true);
//        }
//        protected override void OnPreviewKeyDown(KeyEventArgs e)
//        {
//            base.OnPreviewKeyDown(e);

//            if (e.Key == Key.F5)
//            {
//                TxtQuery_KeyDown(this, e);
//                e.Handled = true;
//            }
//        }

//        private void CargarTipos()
//        {
//            OdbcTypes = Enum.GetValues(typeof(OdbcType))
//                .Cast<OdbcType>()
//                .ToDictionary(t => t.ToString(), t => t);
//        }

//        private void InicializarConexiones()
//        {
//            var conexiones = ConexionesManager.Cargar();
//            cbDriver.ItemsSource = conexiones.Values.ToList();
//            cbDriver.DisplayMemberPath = "Nombre";
//        }

//        private void cbDriver_SelectionChanged(object sender, SelectionChangedEventArgs e)
//        {
//            if (cbDriver.SelectedItem is Conexion conexion)
//            {
//                conexionActual = conexion;
//                BloquearUI(false);
//                AppendMessage($"Conexión seleccionada: {conexion.Motor}");
//            }
//        }

//        private void BloquearUI(bool bloquear)
//        {
//            txtQuery.IsEnabled = !bloquear;
//            btnExecute.IsEnabled = !bloquear;
//            btnExecuteScalar.IsEnabled = !bloquear;
//            btnTest.IsEnabled = !bloquear;
//            btnClear.IsEnabled = !bloquear;
//        }

//        private void TxtQuery_KeyDown(object sender, KeyEventArgs e)
//        {
//            if (e.Key == Key.F5)
//                BtnExecute_Click(this, new RoutedEventArgs());
//        }

//        private async void BtnExecute_Click(object sender, RoutedEventArgs e)
//        {
//            Stopwatch sw = Stopwatch.StartNew();

//            string connStr = GetConnectionString();
//            string sql = txtQuery.Text;

//            if (string.IsNullOrWhiteSpace(connStr))
//            {
//                AppendMessage("El string de conexión está vacío.");
//                return;
//            }
//            if (string.IsNullOrWhiteSpace(sql))
//            {
//                AppendMessage("La consulta está vacía.");
//                return;
//            }

//            AppendMessage($"Ejecutando... ({DateTime.Now})");

//            try
//            {
//                // Captura los parámetros de UI en el hilo de UI
//                List<QueryParameter> parametros = null;
//                await Dispatcher.InvokeAsync(() =>
//                {
//                    parametros = gridParams.Items.OfType<QueryParameter>()
//                        .Where(p => !string.IsNullOrWhiteSpace(p.Nombre))
//                        .ToList();
//                });

//                var dt = await ExecuteQueryAsync(connStr, sql, parametros);

//                dgResults.ItemsSource = dt.DefaultView;
//                // Asigno formato a las columnas numéricas
//                foreach (var column in dgResults.Columns)
//                {
//                    // Obtiene el tipo de datos real de la columna desde el DataTable
//                    var colName = column.Header.ToString();
//                    var dataType = dt.Columns[colName].DataType;

//                    if (dataType == typeof(int) ||
//                        dataType == typeof(long) ||
//                        dataType == typeof(decimal) ||
//                        dataType == typeof(double) ||
//                        dataType == typeof(float))
//                    {
//                        column.CellStyle = new Style(typeof(DataGridCell))
//                        {
//                            Setters =
//                            {
//                                new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right)
//                            }
//                        };
//                    }
//                    else
//                    {
//                        column.CellStyle = new Style(typeof(DataGridCell))
//                        {
//                            Setters =
//                            {
//                                new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Left)
//                            }
//                        };
//                    }
//                }

//                txtRowCount.Text = dt.Rows.Count.ToString();
//                sw.Stop();
//                double elapsedMicroseconds = sw.ElapsedTicks * (1000000.0 / Stopwatch.Frequency);

//                txtTiempoDeEjecucion.Text = $"{elapsedMicroseconds} ms";

//                await Dispatcher.InvokeAsync(() =>
//                    AppendMessage($"Ejecución exitosa!. {dt.Rows.Count} filas devueltas en {elapsedMicroseconds} ms"));

//                AddToHistory(sql);
//            }
//            catch (Exception ex)
//            {
//                await Dispatcher.InvokeAsync(() =>
//                    AppendMessage("Error: " + ex.Message));
//            }
//        }

//        private async Task<DataTable> ExecuteQueryAsync(string connStr, string sql, List<QueryParameter> parametros)
//        {
//            return await Task.Run(() =>
//            {
//                var dt = new DataTable();

//                try
//                {
//                    using (var conn = new OdbcConnection(connStr))
//                    {
//                        conn.Open();
//                        using (var cmd = new OdbcCommand(sql, conn))
//                        {
//                            foreach (var p in parametros)
//                            {
//                                var name = p.Nombre.StartsWith("@") ? p.Nombre : "@" + p.Nombre;
//                                var param = new OdbcParameter(name, p.Tipo);
//                                param.Value = string.IsNullOrEmpty(p.Valor) ? DBNull.Value : (object)p.Valor;
//                                cmd.Parameters.Add(param);
//                            }

//                            using (var adapter = new OdbcDataAdapter(cmd))
//                            {
//                                adapter.Fill(dt);
//                            }
//                        }
//                    }
//                }
//                catch (Exception err)
//                {
//                    AppendMessage($"Ocurrió un error al ejecutar la consulta: {err.Message}");
//                }

//                return dt;
//            });
//        }

//        private async void BtnExecuteScalar_Click(object sender, RoutedEventArgs e)
//        {
//            Stopwatch sw = Stopwatch.StartNew();

//            string connStr = GetConnectionString();
//            string sql = txtQuery.Text;

//            AppendMessage("Ejecutando escalar...");

//            try
//            {
//                var result = await Task.Run(() =>
//                {
//                    using (var conn = new OdbcConnection(connStr))
//                    {
//                        conn.Open();
//                        using (var cmd = new OdbcCommand(sql, conn))
//                            return cmd.ExecuteScalar();
//                    }
//                });

//                sw.Stop();
//                double elapsedMicroseconds = sw.ElapsedTicks * (1000000.0 / Stopwatch.Frequency);

//                txtTiempoDeEjecucion.Text = $"{elapsedMicroseconds} ms";

//                await Dispatcher.InvokeAsync(() =>
//                    AppendMessage($"Resultado del escalar: {(result?.ToString() ?? "(null) en {}")} en {elapsedMicroseconds} ms"));
//            }
//            catch (Exception ex)
//            {
//                await Dispatcher.InvokeAsync(() =>
//                    AppendMessage("Error: " + ex.Message));
//            }
//        }

//        private void BtnClear_Click(object sender, RoutedEventArgs e)
//        {
//            dgResults.ItemsSource = null;
//            txtRowCount.Text = "0";
//            AppendMessage("Resultados borrados.");
//        }

//        private string GetConnectionString()
//        {
//            string stringConnection = string.Empty;
//            if (conexionActual != null)
//            {
//                switch (conexionActual.Motor)
//                {
//                    case TipoMotor.MS_SQL:
//                        stringConnection = $@"Driver={{ODBC Driver 17 for SQL Server}};Server=SQL{conexionActual.Servidor}\{conexionActual.Servidor};Database={conexionActual.BaseDatos};Uid={conexionActual.Usuario};Pwd={conexionActual.Contrasena};TrustServerCertificate=yes;";
//                        break;
//                    case TipoMotor.DB2:
//                        stringConnection = $"Driver={{IBM DB2 ODBC DRIVER}};Database={conexionActual.BaseDatos};Hostname={conexionActual.Servidor};Port=50000; Protocol=TCPIP;Uid={conexionActual.Usuario};Pwd={conexionActual.Contrasena};";
//                        break;
//                    case TipoMotor.POSTGRES:
//                        stringConnection = $"Driver={{PostgreSQL Unicode}};Server={conexionActual.Servidor};Port=5432;Database={conexionActual.BaseDatos};Uid={conexionActual.Usuario};Pwd={conexionActual.Contrasena};";
//                        break;
//                    case TipoMotor.SQLite:
//                        stringConnection = $"Driver={{SQLite3 ODBC Driver}};Database={conexionActual.Servidor};"; //"Data Source={conexionActual.Servidor};Version=3;";
//                        break;
//                    default:
//                        break;
//                }
//            }
//            return stringConnection;
//        }
//        private void AppendMessage(string text)
//        {
//            if (!Dispatcher.CheckAccess())
//            {
//                Dispatcher.Invoke(() => AppendMessage(text));
//                return;
//            }

//            txtMessages.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n");
//            txtMessages.ScrollToEnd();
//        }

//        private void AddToHistory(string sql)
//        {
//            try
//            {
//                lstHistory.Items.Insert(0, sql);
//                File.AppendAllText(HISTORY_FILE, sql + Environment.NewLine + "---" + Environment.NewLine);
//            }
//            catch { }
//        }

//        private void LoadHistory()
//        {
//            try
//            {
//                if (File.Exists(HISTORY_FILE))
//                {
//                    var content = File.ReadAllText(HISTORY_FILE);
//                    var parts = content.Split(new string[] { "---\n", "---\r\n" },
//                                              StringSplitOptions.RemoveEmptyEntries);
//                    foreach (var p in parts)
//                    {
//                        var t = p.Trim();
//                        if (!string.IsNullOrEmpty(t))
//                            lstHistory.Items.Add(t);
//                    }
//                }
//            }
//            catch { }
//        }

//        private void LstHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
//        {
//            if (lstHistory.SelectedItem != null)
//                txtQuery.Text = lstHistory.SelectedItem.ToString();
//        }

//        private void BtnTest_Click(object sender, RoutedEventArgs e)
//        {
//            string conn = GetConnectionString();
//            if (string.IsNullOrWhiteSpace(conn))
//            {
//                AppendMessage("El string de conexión está vacío.");
//                return;
//            }

//            Task.Run(() =>
//            {
//                try
//                {
//                    using (var c = new OdbcConnection(conn))
//                    {
//                        c.Open();
//                        Dispatcher.Invoke(() => AppendMessage("Conexión exitosa."));
//                    }
//                }
//                catch (Exception ex)
//                {
//                    Dispatcher.Invoke(() => AppendMessage("Conexión fallida: " + ex.Message));
//                }
//            });
//        }

//        private void BtnEditConn_Click(object sender, RoutedEventArgs e)
//        {
//            DatosConexion datosConexion = new DatosConexion();
//            datosConexion.ShowDialog();
//            InicializarConexiones();
//            foreach (var item in cbDriver.Items)
//            {
//                try
//                {
//                    if (conexionActual != null && ((Conexion)item).Nombre == conexionActual.Nombre)
//                    {
//                        cbDriver.SelectedItem = item;
//                        break;
//                    }
//                }
//                catch (Exception err)
//                {

//                }
//            }
//        }

//        private void BtnNewConn_Click(object sender, RoutedEventArgs e)
//        {
//            DatosConexion datosConexion = new DatosConexion();
//            datosConexion.ShowDialog();
//            InicializarConexiones();
//            foreach (var item in cbDriver.Items)
//            {
//                try
//                {
//                    if (conexionActual != null && ((Conexion)item).Nombre == conexionActual.Nombre)
//                    {
//                        cbDriver.SelectedItem = item;
//                        break;
//                    }
//                }
//                catch (Exception err)
//                {

//                }
//            }
//        }

//        private void btnDeleteConn_Click(object sender, RoutedEventArgs e)
//        {
//            if (cbDriver.SelectedItem is Conexion conexion)
//            {
//                conexionActual = conexion;
//                var conexiones = ConexionesManager.Cargar();
//                conexiones.Remove(conexionActual.Nombre);
//                ConexionesManager.Guardar(conexiones);
//                cbDriver.ItemsSource = conexiones.Values.ToList();
//                cbDriver.DisplayMemberPath = "Nombre";
//            }
//        }

//        // 🔹 NUEVOS MÉTODOS: Explorador de tablas
//        private async void CargarEsquema()
//        {
//            if (conexionActual == null)
//            {
//                AppendMessage("No hay conexión seleccionada.");
//                return;
//            }

//            string connStr = GetConnectionString();

//            await Task.Run(() =>
//            {
//                try
//                {
//                    using (var conn = new OdbcConnection(connStr))
//                    {
//                        conn.Open();

//                        // Obtiene las tablas
//                        DataTable tablas = conn.GetSchema("Tables");
//                        //List<string> tablasBaseDatos = new List<string>();
//                        //if (conexionActual.Motor == TipoMotor.DB2)
//                        //{
//                        //    OdbcCommand consultaTablas = conn.CreateCommand();
//                        //    consultaTablas.CommandText = "SELECT LTRIM(RTRIM(TBNAME)) AS Nombre, COUNT(NAME) FROM SYSIBM.SYSCOLUMNS WHERE TBCREATOR = 'DB2ADMIN' GROUP BY LTRIM(RTRIM(TBNAME)) ORDER BY Nombre";
//                        //    IDataReader lector = consultaTablas.ExecuteReader();
//                        //    while (lector.Read())
//                        //    {
//                        //        tablasBaseDatos.Add(lector.GetString(0));
//                        //    }
//                        //    lector.Close();
//                        //}

//                        Dispatcher.Invoke(() => tvSchema.Items.Clear());

//                        foreach (DataRow tabla in tablas.Rows)
//                        {
//                            string schema = tabla["TABLE_SCHEM"].ToString();
//                            string nombreTabla = tabla["TABLE_NAME"].ToString();
//                            string tipo = tabla["TABLE_TYPE"].ToString();

//                            if (tipo != "TABLE") continue;
//                            //if (!tablasBaseDatos.Contains(nombreTabla)) continue;

//                            // 🔹 Creamos datos simples (strings) en el hilo de fondo
//                            string headerText = string.IsNullOrEmpty(schema) ? nombreTabla : $"{schema}.{nombreTabla}";
//                            var columnas = conn.GetSchema("Columns", new string[] { null, schema, nombreTabla });

//                            // 🔹 Ahora toda manipulación de la UI dentro del Dispatcher
//                            Dispatcher.Invoke(() =>
//                            {
//                                var tablaNode = new TreeViewItem
//                                {
//                                    Header = headerText,
//                                    Tag = nombreTabla
//                                };

//                                tvSchema.Items.Add(tablaNode);

//                                // Agregamos las columnas dentro del hilo de UI
//                                foreach (DataRow col in columnas.Rows)
//                                {
//                                    string colName = col["COLUMN_NAME"].ToString();
//                                    string tipoCol = col["TYPE_NAME"].ToString();
//                                    string longitud = col["COLUMN_SIZE"].ToString();

//                                    var colNode = new TreeViewItem
//                                    {
//                                        Header = $"{colName} ({tipoCol}{(string.IsNullOrEmpty(longitud) ? "" : $"[{longitud}]")})"
//                                    };

//                                    tablaNode.Items.Add(colNode);
//                                }
//                            });

//                            // 🔹 Carga de índices (solo lectura, sin UI)
//                            try
//                            {
//                                using (var cmd = conn.CreateCommand())
//                                {
//                                    switch (conexionActual.Motor)
//                                    {
//                                        case TipoMotor.MS_SQL:
//                                            cmd.CommandText = $@"SELECT 
//                                                                    s.name AS SchemaName, 
//                                                                    t.name AS TableName, 
//                                                                    i.name AS IndexName, 
//                                                                    i.type_desc AS IndexType, 
//                                                                    c.name AS ColumnName, 
//                                                                    ic.key_ordinal AS ColumnOrder,
//                                                                    i.is_primary_key AS IsPrimaryKey,
//                                                                    i.is_unique AS IsUnique
//                                                                FROM sys.indexes i
//                                                                INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
//                                                                INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
//                                                                INNER JOIN sys.tables t ON i.object_id = t.object_id
//                                                                INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
//                                                                WHERE t.name = '{nombreTabla}'
//                                                                ORDER BY i.name, ic.key_ordinal;";
//                                            break;
//                                        case TipoMotor.DB2:
//                                            cmd.CommandText = $@"SELECT
//                                                                    i.TABSCHEMA AS SchemaName,
//                                                                    i.TABNAME AS TableName,
//                                                                    i.INDNAME AS IndexName,
//                                                                    i.UNIQUERULE AS UniqueRule,
//                                                                    c.COLNAME AS ColumnName,
//                                                                    c.COLSEQ AS ColumnOrder,
//                                                                    i.INDEXTYPE AS IndexType
//                                                                FROM SYSCAT.INDEXES i
//                                                                JOIN SYSCAT.INDEXCOLUSE c
//                                                                    ON i.INDNAME = c.INDNAME AND i.INDSCHEMA = c.INDSCHEMA
//                                                                WHERE i.TABNAME = UPPER('{nombreTabla}')
//                                                                ORDER BY i.INDNAME, c.COLSEQ;";
//                                            break;
//                                        case TipoMotor.POSTGRES:
//                                            cmd.CommandText = $@"SELECT
//                                                                    n.nspname AS SchemaName,
//                                                                    t.relname AS TableName,
//                                                                    i.relname AS IndexName,
//                                                                    a.attname AS ColumnName,
//                                                                    ix.indisunique AS IsUnique,
//                                                                    ix.indisprimary AS IsPrimary
//                                                                FROM pg_class t
//                                                                JOIN pg_index ix ON t.oid = ix.indrelid
//                                                                JOIN pg_class i ON i.oid = ix.indexrelid
//                                                                JOIN pg_namespace n ON n.oid = t.relnamespace
//                                                                JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey)
//                                                                WHERE t.relname = '{nombreTabla}'
//                                                                ORDER BY i.relname, a.attnum;";
//                                            break;
//                                        case TipoMotor.SQLite:
//                                            cmd.CommandText = $"PRAGMA index_list('{nombreTabla}');";
//                                            break;
//                                        default:
//                                            break;
//                                    }

//                                    using (var adapter = new OdbcDataAdapter(cmd))
//                                    {
//                                        var dtIndices = new DataTable();
//                                        adapter.Fill(dtIndices);

//                                        if (dtIndices.Rows.Count > 0)
//                                        {
//                                            // Creamos la estructura para los índices
//                                            Dispatcher.Invoke(() =>
//                                            {
//                                                var tablaNode = tvSchema.Items.OfType<TreeViewItem>()
//                                                    .FirstOrDefault(t => (string)t.Tag == nombreTabla);
//                                                if (tablaNode == null) return;

//                                                var indiceRaiz = new TreeViewItem { Header = "Índices" };
//                                                foreach (DataRow indice in dtIndices.Rows)
//                                                {
//                                                    try
//                                                    {
//                                                        string nombreIndice = indice[conexionActual.Motor == TipoMotor.SQLite ? "NAME" : "INDEXNAME"].ToString();
//                                                        var nodoIndice = new TreeViewItem { Header = nombreIndice };
//                                                        indiceRaiz.Items.Add(nodoIndice);
//                                                    }
//                                                    catch (Exception)
//                                                    {
//                                                    }
//                                                }
//                                                tablaNode.Items.Add(indiceRaiz);
//                                            });
//                                        }
//                                    }
//                                }
//                            }
//                            catch { /* Algunos motores no exponen esa vista */ }
//                        }
//                    }
//                }
//                catch (Exception ex)
//                {
//                    Dispatcher.Invoke(() => AppendMessage("Error al cargar esquema: " + ex.Message));
//                }
//            });
//        }


//        private void tvSchema_MouseDoubleClick(object sender, MouseButtonEventArgs e)
//        {
//            if (tvSchema.SelectedItem is TreeViewItem item && item.Tag != null)
//            {
//                string tableName = item.Header.ToString();
//                txtQuery.Text = $"SELECT * FROM {tableName} FETCH FIRST 100 ROWS ONLY;";
//            }
//        }

//        private void btnExplorar_Click(object sender, RoutedEventArgs e)
//        {
//            CargarEsquema(); // 🔹 NUEVO
//        }
//    }
//}
