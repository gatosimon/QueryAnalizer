using Models;
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
using System;
using System.Windows.Media.Animation;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Controls.Primitives;

namespace QueryAnalyzer
{
    public partial class MainWindow : Window
    {
        private const string HISTORY_FILE = "query_history.txt";
        private const string HISTORIAL_XML = "historial.xml";
        private bool iniciarColapasado = true;
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

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (iniciarColapasado)
            {
                btnExpandirColapsar_Click(this, e as RoutedEventArgs);
                iniciarColapasado = false;
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
            btnExcel.IsEnabled = !bloquear;
        }

        private void TxtQuery_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
                BtnExecute_Click(this, new RoutedEventArgs());
        }

        private async void BtnExecute_Click(object sender, RoutedEventArgs e)
        {
            Stopwatch swTotal = Stopwatch.StartNew();

            string connStr = GetConnectionString();
            string sqlCompleto = txtQuery.Text;

            if (string.IsNullOrWhiteSpace(connStr))
            {
                AppendMessage("El string de conexión está vacío.");
                return;
            }
            if (string.IsNullOrWhiteSpace(sqlCompleto))
            {
                AppendMessage("La consulta está vacía.");
                return;
            }

            tcResults.Items.Clear();
            AppendMessage($"Ejecutando... ({DateTime.Now})");

            string[] queries = sqlCompleto.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            var validQueries = queries.Select(q => q.Trim()).Where(q => !string.IsNullOrWhiteSpace(q)).ToList();

            if (validQueries.Count == 0)
            {
                AppendMessage("No se encontraron consultas válidas (separadas por ';').");
                return;
            }

            long totalRows = 0;
            long totalColumns = 0;

            try
            {
                // 🔹 Capturamos los parámetros en una lista tipo pila
                List<QueryParameter> parametrosTotales = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    parametrosTotales = gridParams.Items.OfType<QueryParameter>()
                        .Where(p => !string.IsNullOrWhiteSpace(p.Nombre))
                        .ToList();
                });

                int posicionParametro = 0;

                for (int i = 0; i < validQueries.Count; i++)
                {
                    string sqlIndividual = validQueries[i];
                    Stopwatch swQuery = Stopwatch.StartNew();
                    AppendMessage($"Ejecutando consulta {i + 1}/{validQueries.Count}...");

                    // 🔹 Extraemos los parámetros de esta consulta según la pila
                    var parametrosConsulta = ExtraerParametrosParaConsulta(sqlIndividual, parametrosTotales, ref posicionParametro);

                    var dt = await ExecuteQueryAsync(connStr, sqlIndividual, parametrosConsulta);

                    swQuery.Stop();
                    double elapsedMicroseconds = swQuery.ElapsedTicks * (1000000.0 / Stopwatch.Frequency);
                    totalRows += dt.Rows.Count;
                    totalColumns += dt.Columns.Count;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        // 1. Definir el estilo para centrar los encabezados
                        var headerStyle = new Style(typeof(DataGridColumnHeader));
                        headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));

                        var dataGrid = new DataGrid
                        {
                            IsReadOnly = true,
                            AutoGenerateColumns = true,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            AlternationCount = 2,
                            RowStyle = (Style)this.FindResource("ResultGridRowStyle"),
                            ItemsSource = dt.DefaultView,
                            ColumnHeaderStyle = headerStyle // --- APLICAMOS EL ESTILO ---
                        };

                        dataGrid.AutoGeneratedColumns += (s, ev) =>
                        {
                            foreach (var column in dataGrid.Columns)
                            {
                                string colName = column.Header.ToString();
                                if (!dt.Columns.Contains(colName))
                                    continue;

                                Type tipo = dt.Columns[colName].DataType;

                                // Formato para DateTime
                                if (tipo == typeof(DateTime) || tipo == typeof(DateTime?) || tipo == typeof(DateTimeOffset))
                                {
                                    var boundColumn = column as DataGridBoundColumn;
                                    if (boundColumn != null)
                                    {
                                        var binding = boundColumn.Binding as Binding;
                                        if (binding != null)
                                        {
                                            binding.StringFormat = "dd/MM/yyyy HH:mm:ss";
                                        }
                                    }
                                }

                                // Alineación de celdas
                                bool aDerecha =
                                    tipo == typeof(int) ||
                                    tipo == typeof(long) ||
                                    tipo == typeof(decimal) ||
                                    tipo == typeof(double) ||
                                    tipo == typeof(float) ||
                                    tipo == typeof(short) ||
                                    tipo == typeof(byte) ||
                                    tipo == typeof(DateTime) ||
                                    tipo == typeof(TimeSpan);

                                var estilo = new Style(typeof(DataGridCell));
                                estilo.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, aDerecha ? TextAlignment.Right : TextAlignment.Left));
                                column.CellStyle = estilo;
                            }
                        };

                        var tabItem = new TabItem
                        {
                            Header = $"Resultado {i + 1} ({dt.Columns.Count} cols, {dt.Rows.Count} filas, {elapsedMicroseconds:F0} ms)",
                            Content = dataGrid
                        };

                        tcResults.Items.Add(tabItem);
                        AppendMessage($"Consulta {i + 1} exitosa. {dt.Rows.Count} filas devueltas en {elapsedMicroseconds:F0} ms");
                    });
                }

                // 🔹 Al terminar todas, guardamos el bloque completo en el historial con todos los parámetros
                AddToHistoryWithParams(sqlCompleto, parametrosTotales);

                swTotal.Stop();
                double totalElapsedMicroseconds = swTotal.ElapsedTicks * (1000000.0 / Stopwatch.Frequency);

                txtColumnCount.Text = totalColumns.ToString();
                txtRowCount.Text = totalRows.ToString();
                txtTiempoDeEjecucion.Text = $"{totalElapsedMicroseconds:F0} ms";

                await Dispatcher.InvokeAsync(() =>
                {
                    AppendMessage($"Ejecución total finalizada. {validQueries.Count} consultas ejecutadas en {totalElapsedMicroseconds:F0} ms");
                    if (tcResults.Items.Count > 0)
                        tcResults.SelectedIndex = 0;
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => AppendMessage("Error: " + ex.Message));
            }
        }

        /// <summary>
        /// Devuelve una porción de parámetros desde la posición actual.
        /// Se usa como "pila" compartida entre varias consultas.
        /// </summary>
        private List<QueryParameter> ExtraerParametrosParaConsulta(string query, List<QueryParameter> parametrosTotales, ref int posicionActual)
        {
            List<QueryParameter> restantes = new List<QueryParameter>();
            int cantidadParametros = query.Count(c => c == '?');
            if (cantidadParametros > 0)
            {
                // Si no hay parámetros, devolvemos lista vacía
                if (parametrosTotales == null || parametrosTotales.Count == 0)
                    return new List<QueryParameter>();

                // Si ya consumimos todos, devolvemos vacía
                if (posicionActual >= parametrosTotales.Count)
                    return new List<QueryParameter>();

                // Por ahora, devolvemos todos los restantes (en caso de que no se sepa cuántos necesita cada consulta)
                // Podrías ajustar esto si querés controlar cuántos consume cada query.
                for (int i = posicionActual; i < posicionActual + cantidadParametros; i++)
                {
                    restantes.Add(parametrosTotales[i]);
                }

                // Avanzamos el puntero al final (simulando consumo total)
                posicionActual += cantidadParametros; 
            }

            return restantes;
        }

        private async void BtnExcel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var hojas = new Dictionary<string, System.Data.DataTable>();

                // 🔹 Recorre automáticamente todas las pestañas del TabControl
                foreach (TabItem tab in tcResults.Items)
                {
                    var grid = tab.Content as DataGrid;
                    if (grid?.ItemsSource == null)
                        continue;

                    // Convertir el DataGrid en DataTable
                    var dt = ((System.Data.DataView)grid.ItemsSource).ToTable();

                    // Nombre de la hoja = título del tab (sin caracteres inválidos)
                    string nombreHoja = tab.Header.ToString();
                    hojas[nombreHoja] = dt;
                }

                if (hojas.Count == 0)
                {
                    AppendMessage("No hay datos para exportar.");
                    return;
                }

                // 🔹 Crear el Excel
                var excelService = new ExcelService();
                byte[] archivoExcel = excelService.CrearExcelMultiplesHojas(hojas);

                // 🔹 Guardar en escritorio
                string ruta = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ResultadosConsultas.xlsx");
                excelService.GuardarArchivo(archivoExcel, ruta);

                AppendMessage($"Excel generado correctamente en: {ruta}");
                System.Diagnostics.Process.Start(ruta);
            }
            catch (Exception ex)
            {
                AppendMessage("Error al generar Excel: " + ex.Message);
            }
        }

        /// <summary>
        /// Convierte un DataTable en una lista de diccionarios (nombreColumna -> valor)
        /// </summary>
        private List<Dictionary<string, object>> ConvertirDataTable(System.Data.DataTable dt)
        {
            var lista = new List<Dictionary<string, object>>();

            foreach (System.Data.DataRow row in dt.Rows)
            {
                var dict = new Dictionary<string, object>();
                foreach (System.Data.DataColumn col in dt.Columns)
                {
                    dict[col.ColumnName] = row[col] != DBNull.Value ? row[col] : "";
                }
                lista.Add(dict);
            }

            return lista;
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
                            if (parametros != null)
                            {
                                foreach (var p in parametros)
                                {
                                    var name = p.Nombre.StartsWith("@") ? p.Nombre : "@" + p.Nombre;
                                    var param = new OdbcParameter(name, p.Tipo);
                                    param.Value = string.IsNullOrEmpty(p.Valor) ? DBNull.Value : (object)p.Valor;
                                    cmd.Parameters.Add(param);
                                }
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
            // CAMBIO: Limpiar el TabControl
            tcResults.Items.Clear();
            txtRowCount.Text = "0";
            txtTiempoDeEjecucion.Text = "0";
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
        private void AddToHistoryWithParams(string sqlCompleto, List<QueryParameter> parametros)
        {
            try
            {
                lstHistory.Items.Insert(0, sqlCompleto);
                File.AppendAllText(HISTORY_FILE, sqlCompleto + Environment.NewLine + "---" + Environment.NewLine);

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
                    Consulta = sqlCompleto, // 🔹 todas las consultas en un solo string
                    Parametros = new List<string[]>(),
                    Fecha = DateTime.Now
                };

                if (parametros != null && parametros.Count > 0)
                {
                    foreach (var p in parametros)
                    {
                        string tipoStr = p.Tipo.ToString();
                        h.Parametros.Add(new string[] { p.Nombre, tipoStr, p.Valor });
                    }
                }

                all.Add(h);
                SaveAllHistoriales(all);

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
                if (lstHistory.SelectedItem is ListBoxItem lbi && lbi.Tag is Historial hist)
                {
                    txtQuery.Text = hist.Consulta ?? string.Empty;

                    var nuevos = new List<QueryParameter>();
                    if (hist.Parametros != null)
                    {
                        foreach (var p in hist.Parametros)
                        {
                            string nombre = p.Length > 0 ? p[0] : string.Empty;
                            string tipoStr = p.Length > 1 ? p[1] : string.Empty;
                            string valor = p.Length > 2 ? p[2] : string.Empty;

                            OdbcType tipoEnum = OdbcType.VarChar;
                            if (!string.IsNullOrWhiteSpace(tipoStr))
                            {
                                if (!Enum.TryParse(tipoStr, out tipoEnum))
                                {
                                    try { tipoEnum = (OdbcType)Enum.Parse(typeof(OdbcType), tipoStr, true); }
                                    catch { tipoEnum = OdbcType.VarChar; }
                                }
                            }

                            nuevos.Add(new QueryParameter
                            {
                                Nombre = nombre,
                                Tipo = tipoEnum,
                                Valor = valor
                            });
                        }
                    }

                    Parametros = nuevos;
                    gridParams.ItemsSource = Parametros;
                }
                else
                {
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
        //private async void CargarEsquema(List<string> tablasConsulta = null)
        //{
        //    if (conexionActual == null)
        //    {
        //        AppendMessage("No hay conexión seleccionada.");
        //        return;
        //    }

        //    string connStr = GetConnectionString();

        //    await Task.Run(() =>
        //    {
        //        try
        //        {
        //            using (var conn = new OdbcConnection(connStr))
        //            {
        //                conn.Open();

        //                // Obtiene las tablas
        //                DataTable tablas = conn.GetSchema("Tables");

        //                Dispatcher.Invoke(() => tvSchema.Items.Clear());

        //                bool cargarTabla = true; 
        //                foreach (DataRow tabla in tablas.Rows)
        //                {
        //                    string schema = tabla["TABLE_SCHEM"].ToString();
        //                    string nombreTabla = tabla["TABLE_NAME"].ToString();
        //                    cargarTabla = tablasConsulta == null || (tablasConsulta != null && tablasConsulta.Contains(nombreTabla.ToUpper().Trim()));
        //                    if (cargarTabla)
        //                    {
        //                        string tipo = tabla["TABLE_TYPE"].ToString();

        //                        if (tipo != "TABLE") continue;
        //                        //if (!tablasBaseDatos.Contains(nombreTabla)) continue;

        //                        // 🔹 Creamos datos simples (strings) en el hilo de fondo
        //                        string headerText = string.IsNullOrEmpty(schema) ? nombreTabla : $"{schema}.{nombreTabla}";
        //                        var columnas = conn.GetSchema("Columns", new string[] { null, schema, nombreTabla });

        //                        // 🔹 Ahora toda manipulación de la UI dentro del Dispatcher
        //                        Dispatcher.Invoke(() =>
        //                        {
        //                            var tablaNode = new TreeViewItem
        //                            {
        //                                Header = headerText,
        //                                Tag = nombreTabla
        //                            };

        //                            tvSchema.Items.Add(tablaNode);

        //                        // Agregamos las columnas dentro del hilo de UI
        //                        foreach (DataRow col in columnas.Rows)
        //                            {
        //                                string colName = col["COLUMN_NAME"].ToString();
        //                                string tipoCol = col["TYPE_NAME"].ToString();
        //                                string longitud = col["COLUMN_SIZE"].ToString();

        //                                var colNode = new TreeViewItem
        //                                {
        //                                    Header = $"{colName} ({tipoCol}{(string.IsNullOrEmpty(longitud) ? "" : $" [{longitud}]")})"
        //                                };

        //                                tablaNode.Items.Add(colNode);
        //                            }
        //                        });

        //                        // 🔹 Carga de índices (solo lectura, sin UI)
        //                        try
        //                        {
        //                            using (var cmd = conn.CreateCommand())
        //                            {
        //                                switch (conexionActual.Motor)
        //                                {
        //                                    case TipoMotor.MS_SQL:
        //                                        cmd.CommandText = $@"SELECT 
        //                                                            s.name AS SchemaName, 
        //                                                            t.name AS TableName, 
        //                                                            i.name AS IndexName, 
        //                                                            i.type_desc AS IndexType, 
        //                                                            c.name AS ColumnName, 
        //                                                            ic.key_ordinal AS ColumnOrder,
        //                                                            i.is_primary_key AS IsPrimaryKey,
        //                                                            i.is_unique AS IsUnique
        //                                                        FROM sys.indexes i
        //                                                        INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
        //                                                        INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        //                                                        INNER JOIN sys.tables t ON i.object_id = t.object_id
        //                                                        INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
        //                                                        WHERE t.name = '{nombreTabla}'
        //                                                        ORDER BY i.name, ic.key_ordinal;";
        //                                        break;
        //                                    case TipoMotor.DB2:
        //                                        cmd.CommandText = $@"SELECT
        //                                                            i.TABSCHEMA AS SchemaName,
        //                                                            i.TABNAME AS TableName,
        //                                                            i.INDNAME AS IndexName,
        //                                                            i.UNIQUERULE AS UniqueRule,
        //                                                            c.COLNAME AS ColumnName,
        //                                                            c.COLSEQ AS ColumnOrder,
        //                                                            i.INDEXTYPE AS IndexType
        //                                                        FROM SYSCAT.INDEXES i
        //                                                        JOIN SYSCAT.INDEXCOLUSE c
        //                                                            ON i.INDNAME = c.INDNAME AND i.INDSCHEMA = c.INDSCHEMA
        //                                                        WHERE i.TABNAME = UPPER('{nombreTabla}')
        //                                                        ORDER BY i.INDNAME, c.COLSEQ;";
        //                                        break;
        //                                    case TipoMotor.POSTGRES:
        //                                        cmd.CommandText = $@"SELECT
        //                                                            n.nspname AS SchemaName,
        //                                                            t.relname AS TableName,
        //                                                            i.relname AS IndexName,
        //                                                            a.attname AS ColumnName,
        //                                                            ix.indisunique AS IsUnique,
        //                                                            ix.indisprimary AS IsPrimary
        //                                                        FROM pg_class t
        //                                                        JOIN pg_index ix ON t.oid = ix.indrelid
        //                                                        JOIN pg_class i ON i.oid = ix.indexrelid
        //                                                        JOIN pg_namespace n ON n.oid = t.relnamespace
        //                                                        JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey)
        //                                                        WHERE t.relname = '{nombreTabla}'
        //                                                        ORDER BY i.relname, a.attnum;";
        //                                        break;
        //                                    case TipoMotor.SQLite:
        //                                        cmd.CommandText = $"PRAGMA index_list('{nombreTabla}');";
        //                                        break;
        //                                    default:
        //                                        break;
        //                                }

        //                                using (var adapter = new OdbcDataAdapter(cmd))
        //                                {
        //                                    var dtIndices = new DataTable();
        //                                    adapter.Fill(dtIndices);

        //                                    if (dtIndices.Rows.Count > 0)
        //                                    {
        //                                        // Creamos la estructura para los índices
        //                                        Dispatcher.Invoke(() =>
        //                                        {
        //                                            var tablaNode = tvSchema.Items.OfType<TreeViewItem>()
        //                                                .FirstOrDefault(t => (string)t.Tag == nombreTabla);
        //                                            if (tablaNode == null) return;

        //                                            var indiceRaiz = new TreeViewItem { Header = "Índices" };
        //                                            foreach (DataRow indice in dtIndices.Rows)
        //                                            {
        //                                                try
        //                                                {
        //                                                    string nombreIndice = indice[conexionActual.Motor == TipoMotor.SQLite ? "NAME" : "INDEXNAME"].ToString();
        //                                                    var nodoIndice = new TreeViewItem { Header = nombreIndice };
        //                                                    indiceRaiz.Items.Add(nodoIndice);
        //                                                }
        //                                                catch (Exception)
        //                                                {
        //                                                }
        //                                            }
        //                                            tablaNode.Items.Add(indiceRaiz);
        //                                        });
        //                                    }
        //                                }
        //                            }
        //                        }
        //                        catch { /* Algunos motores no exponen esa vista */ } 
        //                    }
        //                }
        //            }
        //        }
        //        catch (Exception ex)
        //        {
        //            Dispatcher.Invoke(() => AppendMessage("Error al cargar esquema: " + ex.Message));
        //        }
        //    });
        //}


        private async void CargarEsquema(List<string> tablasConsulta = null)
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

                        Dispatcher.Invoke(() => tvSchema.Items.Clear());

                        bool cargarTabla = true;
                        foreach (DataRow tabla in tablas.Rows)
                        {
                            string schema = tabla["TABLE_SCHEM"].ToString();
                            string nombreTabla = tabla["TABLE_NAME"].ToString();


                            // Usamos Any() y EndsWith()
                            cargarTabla = tablasConsulta == null || (tablasConsulta != null &&
                                tablasConsulta.Any(t => t.ToUpper().Trim().EndsWith(nombreTabla.ToUpper().Trim())));
                            if (cargarTabla)
                            {
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
                                        // "COLUMN_SIZE" a veces es precisión (para numéricos) y a veces longitud (para strings)
                                        string longitud = col["COLUMN_SIZE"].ToString();

                                        // --- INICIO MODIFICACIÓN ---

                                        // 1. Obtener Escala (NUMERIC_SCALE)
                                        string escala = string.Empty;
                                        // Verificamos que la columna exista en el schema y no sea nula
                                        if (col.Table.Columns.Contains("NUMERIC_SCALE") && col["NUMERIC_SCALE"] != DBNull.Value)
                                        {
                                            escala = col["NUMERIC_SCALE"].ToString();
                                        }
                                        else if (col.Table.Columns.Contains("COLUMN_SCALE") && col["COLUMN_SCALE"] != DBNull.Value)
                                        {
                                            // Nombre alternativo para algunos drivers ODBC
                                            escala = col["COLUMN_SCALE"].ToString();
                                        }
                                        else if (col.Table.Columns.Contains("COLUMN_SIZE") && col["COLUMN_SIZE"] != DBNull.Value)
                                        {
                                            // Nombre alternativo para algunos drivers ODBC
                                            escala = col["COLUMN_SIZE"].ToString();
                                        }

                                        // 2. Obtener Nulabilidad (IS_NULLABLE)
                                        string aceptaNulos = string.Empty;
                                        if (col.Table.Columns.Contains("IS_NULLABLE") && col["IS_NULLABLE"] != DBNull.Value)
                                        {
                                            // El valor suele ser "YES", "NO" o "" (desconocido)
                                            string nuloStr = col["IS_NULLABLE"].ToString().ToUpper();
                                            if (nuloStr == "YES")
                                            {
                                                aceptaNulos = "NULL";
                                            }
                                            else if (nuloStr == "NO")
                                            {
                                                aceptaNulos = "NOT NULL";
                                            }
                                            // Si es "" (unknown), no mostramos nada.
                                        }

                                        string defecto = string.Empty; 
                                        if (col.Table.Columns.Contains("COLUMN_DEF") && col["COLUMN_DEF"] != DBNull.Value)
                                        {
                                            defecto = col["COLUMN_DEF"].ToString();
                                        }

                                        // 3. Formatear el string del tipo
                                        string tipoCompleto = tipoCol;
                                        string tipoNormalizado = tipoCol.ToUpper();
                                        // Solo mostramos escala para tipos que la usan (DECIMAL, NUMERIC)
                                        bool esNumericoDecimal = tipoNormalizado.Contains("DECIMAL") || tipoNormalizado.Contains("NUMERIC");

                                        if (!string.IsNullOrEmpty(longitud))
                                        {
                                            // Si es DECIMAL/NUMERIC y tiene escala, mostramos [precision, escala]
                                            if (esNumericoDecimal && !string.IsNullOrEmpty(escala))
                                            {
                                                tipoCompleto += $" [{longitud}, {escala}]";
                                            }
                                            else // Para el resto (VARCHAR, INT, etc.) solo mostramos [longitud]
                                            {
                                                tipoCompleto += $" [{longitud}]";
                                            }
                                        }

                                        // 4. Formatear el Header final
                                        var colNode = new TreeViewItem
                                        {
                                            // Formato: Nombre (Tipo [Long, Escala], NULL/NOT NULL)
                                            Header = $"{colName} ({tipoCompleto}{(string.IsNullOrEmpty(aceptaNulos) ? string.Empty : $", {aceptaNulos}")}{(string.IsNullOrEmpty(defecto) ? string.Empty : $", DEFAULT {defecto}")})"
                                        };

                                        // --- FIN MODIFICACIÓN ---

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
                                                    // ... dentro de Dispatcher.Invoke()
                                                    var tablaNode = tvSchema.Items.OfType<TreeViewItem>()
                                                        .FirstOrDefault(t => (string)t.Tag == nombreTabla);
                                                    if (tablaNode == null) return;

                                                    var indiceRaiz = new TreeViewItem { Header = "Índices" };

                                                    // Obtenemos el nombre de la columna que contiene el nombre del índice, 
                                                    // que varía según el motor.
                                                    string indexNameColumn = conexionActual.Motor == TipoMotor.SQLite ? "NAME" : "INDEXNAME";

                                                    // Agrupamos los DataRows por el nombre del índice
                                                    var indicesAgrupados = dtIndices.AsEnumerable()
                                                        .GroupBy(row => row.Field<string>(indexNameColumn))
                                                        .OrderBy(g => g.Key); // Opcional: ordenar por nombre de índice

                                                    foreach (var grupoIndice in indicesAgrupados)
                                                    {
                                                        // El nombre del índice es la clave del grupo
                                                        string nombreIndice = grupoIndice.Key;

                                                        // Creamos un nodo por cada índice único
                                                        var nodoIndice = new TreeViewItem { Header = nombreIndice };

                                                        // Opcional: Podrías añadir las columnas que componen el índice como nodos hijos aquí si el DataRow contiene esa información
                                                        // Esto requiere otra lógica de agrupación o iteración, pero por ahora solo creamos el nodo del índice.

                                                        indiceRaiz.Items.Add(nodoIndice);
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

        private void btnExplorarConsultas_Click(object sender, RoutedEventArgs e)
        {
            List<string> tablasConsulta = ExtraerTablas(txtQuery.Text);
            CargarEsquema(tablasConsulta);
        }

        /// <summary>
        /// Extrae los nombres de tablas de una o varias consultas SQL.
        /// Compatible con MSSQL, DB2, PostgreSQL y SQLite.
        /// </summary>
        /// <param name="sqlText">Texto SQL completo (puede contener varias consultas).</param>
        /// <returns>Lista de nombres de tablas en mayúsculas, sin repetir y en orden de aparición.</returns>
        public List<string> ExtraerTablas(string sqlText)
        {
            if (string.IsNullOrWhiteSpace(sqlText))
                return new List<string>();

            // Normalizamos saltos de línea y espacios
            string sql = Regex.Replace(sqlText, @"[\r\n]+", " ");
            sql = Regex.Replace(sql, @"\s+", " ");

            // Expresiones regulares para capturar nombres de tablas en distintos contextos SQL
            var patrones = new List<string>
        {
            // SELECT ... FROM table
            @"\bFROM\s+([A-Z0-9_.\""\[\]]+)",
            // JOIN table
            @"\bJOIN\s+([A-Z0-9_.\""\[\]]+)",
            // UPDATE table
            @"\bUPDATE\s+([A-Z0-9_.\""\[\]]+)",
            // INSERT INTO table
            @"\bINTO\s+([A-Z0-9_.\""\[\]]+)",
            // DELETE FROM table
            @"\bDELETE\s+FROM\s+([A-Z0-9_.\""\[\]]+)",
            // MERGE INTO table
            @"\bMERGE\s+INTO\s+([A-Z0-9_.\""\[\]]+)"
        };

            var tablas = new List<string>();
            foreach (string patron in patrones)
            {
                foreach (Match match in Regex.Matches(sql.ToUpperInvariant(), patron))
                {
                    string nombre = LimpiarNombreTabla(match.Groups[1].Value);
                    if (!string.IsNullOrEmpty(nombre) && !tablas.Contains(nombre))
                    {
                        tablas.Add(nombre);
                    }
                }
            }

            return tablas;
        }

        /// <summary>
        /// Limpia el nombre de la tabla (quita alias, comillas, corchetes, etc.)
        /// </summary>
        private string LimpiarNombreTabla(string nombre)
        {
            // Elimina alias o terminaciones tipo: "AS x", "x" luego de espacio
            nombre = nombre.Trim();
            nombre = Regex.Replace(nombre, @"[\[\]\""]", ""); // quita [ ] o "
            nombre = Regex.Replace(nombre, @"\s+AS\s+\w+", "", RegexOptions.IgnoreCase);
            nombre = Regex.Replace(nombre, @"\s+\w+$", ""); // elimina alias suelto
            nombre = Regex.Replace(nombre, @"[,;)]$", ""); // quita coma, punto y coma o paréntesis final
            return nombre.Trim().ToUpperInvariant();
        }

        private bool isCollapsed = false;
        private double expandedWidth = 0;
        private double collapsedWidth = 0;

        private void btnExpandirColapsar_Click(object sender, RoutedEventArgs e)
        {
            ExpandirColapasar();
        }

        private void ExpandirColapasar()
        {
            if (!isCollapsed)
            {
                expandedWidth = grdExplorador.ActualWidth;
            }
            var colDef = ((Grid)grdExplorador.Parent).ColumnDefinitions[0]; // solo la columna del TreeView

            double from = colDef.ActualWidth;
            double to = isCollapsed ? expandedWidth : collapsedWidth;

            var anim = new GridLengthAnimation
            {
                From = new GridLength(from, GridUnitType.Pixel),
                To = new GridLength(to, GridUnitType.Pixel),
                Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                FillBehavior = FillBehavior.Stop
            };

            anim.Completed += (s, _) =>
            {
                colDef.Width = new GridLength(to, GridUnitType.Pixel);
                isCollapsed = !isCollapsed;
                btnExpandirColapsar.Content = isCollapsed ? ">>" : "<<";
            };

            colDef.BeginAnimation(ColumnDefinition.WidthProperty, anim);
        }

        private void txtQuery_KeyUp(object sender, KeyEventArgs e)
        {
            // Detecta el signo de interrogación (Shift + /)
            if (e.Key == Key.Oem4 && Keyboard.Modifiers == ModifierKeys.Shift)
            {
                AgregarParametro();
            }
            if (e.Key == Key.Back)
            {
                EliminarParametro();
            }
        }

        private void AgregarParametro()
        {
            try
            {
                // Posición actual del cursor
                int caretIndex = txtQuery.CaretIndex;
                if (caretIndex > 0)
                {
                    caretIndex -= 1;
                }
                // Texto completo hasta el cursor
                string textoHastaCursor = txtQuery.Text.Substring(0, caretIndex);
                int parametrosAntes = CantidadInterrogacionesAntes(textoHastaCursor, caretIndex);

                List<string> palabras = textoHastaCursor.Split(new string[] { " " }, StringSplitOptions.RemoveEmptyEntries).ToList();
                int i = palabras.Count - 1;
                bool esBetween = false;
                bool esAndDelBetween = false;
                if (palabras[i].ToUpper() == "BETWEEN")
                {
                    esBetween = true;
                }
                if (palabras[i].ToUpper() == "AND")
                {
                    i -= 2;
                    esAndDelBetween = true;
                }
                i--;

                if (i >= 0)
                {
                    string campo = palabras[i];
                    if (campo.Contains("\n"))
                    {
                        campo = campo.Split('\n')[1];
                    }

                    // Si viene con alias, nos quedamos solo con el nombre (después del punto)
                    if (campo.Contains("."))
                        campo = campo.Split('.').Last();

                    campo = campo.Trim();

                    if (!string.IsNullOrEmpty(campo))
                    {
                        string nombreParametro = $"@{campo.ToUpper()}" + (esBetween ? "_DESDE" : esAndDelBetween ? "_HASTA" : string.Empty);

                        // Evita duplicados
                        i = 0;
                        while (i <= parametrosAntes && Parametros.Any(p => p.Nombre.Equals(nombreParametro, StringComparison.OrdinalIgnoreCase)))
                        {
                            nombreParametro = $"{nombreParametro}{i}";
                            i++;
                        }
                        Parametros.Insert(parametrosAntes, new QueryParameter
                        {
                            Nombre = nombreParametro,
                            Tipo = OdbcType.VarChar,
                            Valor = ""
                        });

                        gridParams.Items.Refresh();

                        AppendMessage($"Parámetro agregado automáticamente: {nombreParametro}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendMessage("Error al analizar parámetros: " + ex.Message);
            }
        }

        private void EliminarParametro()
        {
            try
            {
                // Comparo la cantidad de parametros en la consulta y en la grilla, si hay menos en la consulta busco el eliminado y lo quito de la grilla de parametros
                int parametrosEnQuery = txtQuery.Text.Count(c => c == '?');
                if ((gridParams.Items.Count - 1) > parametrosEnQuery)
                {
                    // Posición actual del cursor
                    int caretIndex = txtQuery.CaretIndex;
                    if (caretIndex > 0)
                    {
                        caretIndex -= 1;
                    }
                    // Texto completo hasta el cursor
                    string textoHastaCursor = txtQuery.Text.Substring(0, caretIndex);
                    int parametrosAntes = CantidadInterrogacionesAntes(textoHastaCursor, caretIndex);

                    if (caretIndex > 0)
                    {
                        string nombreParametro = Parametros[parametrosAntes].Nombre;
                        Parametros.RemoveAt(parametrosAntes);

                        gridParams.Items.Refresh();

                        AppendMessage($"Se eliminó el parámetro: {nombreParametro}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppendMessage("Error al analizar parámetros: " + ex.Message);
            }
        }

        int CantidadInterrogacionesAntes(string texto, int posicion)
        {
            if (string.IsNullOrEmpty(texto) || posicion <= 0)
                return 0;

            if (posicion > texto.Length)
                posicion = texto.Length;

            // Tomamos solo la parte anterior a la posición indicada
            string anterior = texto.Substring(0, posicion);

            // Contamos los signos de interrogación
            return anterior.Count(c => c == '?');
        }
    }
}
public class GridLengthAnimation : AnimationTimeline
{
    public override Type TargetPropertyType => typeof(GridLength);

    public static readonly DependencyProperty FromProperty =
        DependencyProperty.Register("From", typeof(GridLength), typeof(GridLengthAnimation));

    public static readonly DependencyProperty ToProperty =
        DependencyProperty.Register("To", typeof(GridLength), typeof(GridLengthAnimation));

    public GridLength From
    {
        get => (GridLength)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public GridLength To
    {
        get => (GridLength)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        double fromVal = ((GridLength)GetValue(FromProperty)).Value;
        double toVal = ((GridLength)GetValue(ToProperty)).Value;

        if (fromVal > toVal)
            return new GridLength((1 - animationClock.CurrentProgress.Value) * (fromVal - toVal) + toVal, GridUnitType.Pixel);
        else
            return new GridLength(animationClock.CurrentProgress.Value * (toVal - fromVal) + fromVal, GridUnitType.Pixel);
    }

    protected override Freezable CreateInstanceCore()
    {
        return new GridLengthAnimation();
    }
}