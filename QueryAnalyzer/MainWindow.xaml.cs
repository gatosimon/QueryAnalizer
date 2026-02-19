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
using System.Windows.Media.Animation;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.Xml;
using System.Threading;
using System.Windows.Documents;

namespace QueryAnalyzer
{
    public partial class MainWindow : Window
    {
        private const string HISTORY_FILE = "query_history.txt";
        private const string HISTORIAL_XML = "historial.xml";
        private bool iniciarColapasado = true;
        private CancellationTokenSource _explorarCTS;

        public Dictionary<string, OdbcType> OdbcTypes { get; set; }
        public List<QueryParameter> Parametros { get; set; }

        static public Conexion conexionActual = null;

        public MainWindow()
        {
            InitializeComponent();

            // 🔹 AÑADIDO: esto asegura que los bindings de DataContext funcionen correctamente.
            DataContext = this;

            // 🔹 NUEVO: Registrar definición de SQL si no existe nativamente
            RegistrarResaltadoSQL();
            txtQuery.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("SQL");

            LoadHistory(); // mantiene compatibilidad con el archivo de texto
            //txtQuery.KeyDown += TxtQuery_KeyDown;
            txtQuery.Text = "SELECT * FROM SYSIBM.SYSCOLUMNS FETCH FIRST 10 ROWS ONLY; -- ejemplo para DB2";

            CargarTipos();

            // Configura ItemsSource correctamente
            Parametros = new List<QueryParameter>();
            gridParams.ItemsSource = Parametros;
            InicializarConexiones();
            BloquearUI(true);
            ConfigurarMenuContextualAvalonEdit();
        }
        private void ConfigurarMenuContextualAvalonEdit()
        {
            // Crear el menú contextual
            var contextMenu = new ContextMenu();

            // Opción Copiar
            var menuCopiar = new MenuItem { Header = "Copiar" };
            menuCopiar.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(txtQuery.SelectedText))
                    Clipboard.SetText(txtQuery.SelectedText);
            };
            contextMenu.Items.Add(menuCopiar);

            // Opción Cortar
            var menuCortar = new MenuItem { Header = "Cortar" };
            menuCortar.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(txtQuery.SelectedText))
                {
                    Clipboard.SetText(txtQuery.SelectedText);
                    txtQuery.SelectedText = "";
                }
            };
            contextMenu.Items.Add(menuCortar);

            // Opción Pegar
            var menuPegar = new MenuItem { Header = "Pegar" };
            menuPegar.Click += (s, e) =>
            {
                if (Clipboard.ContainsText())
                {
                    txtQuery.SelectedText = Clipboard.GetText();
                }
            };
            contextMenu.Items.Add(menuPegar);

            // Separador
            contextMenu.Items.Add(new Separator());

            // Opción Seleccionar todo
            var menuSeleccionarTodo = new MenuItem { Header = "Seleccionar todo" };
            menuSeleccionarTodo.Click += (s, e) => txtQuery.SelectAll();
            contextMenu.Items.Add(menuSeleccionarTodo);

            // Asignar el menú contextual al TextEditor
            txtQuery.ContextMenu = contextMenu;
        }

        private void RegistrarResaltadoSQL()
        {
            // Definición XML manual para asegurar que funcione sin archivos externos
            string sqlXshd = @"
                            <SyntaxDefinition name='SQL' extensions='.sql' xmlns='http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008'>
                                <Color name='String' foreground='Red' />
                                <Color name='Comment' foreground='Green' />
                                <Color name='Keyword' foreground='Blue' fontWeight='bold' />
                                <Color name='Function' foreground='Magenta' />
                                <Color name='Connector' foreground='#0094FF'  />
                                <RuleSet ignoreCase='true'>
                                    <Span color='Comment' begin='--' />
                                    <Span color='Comment' multiline='true' begin='/\*' end='\*/' />
                                    <Span color='String'><Begin>'</Begin><End>'</End></Span>
                                    <Keywords color='Keyword'>
                                        <Word>SELECT</Word><Word>FROM</Word><Word>WHERE</Word><Word>GROUP</Word><Word>BY</Word>
                                        <Word>ORDER</Word><Word>HAVING</Word><Word>FETCH</Word>
                                        <Word>FIRST</Word><Word>ROWS</Word><Word>INSERT</Word><Word>INTO</Word>
                                        <Word>VALUES</Word><Word>SET</Word><Word>DELETE</Word>
                                        <Word>ON</Word><Word>CASE</Word><Word>ADD</Word>
                                        <Word>WHEN</Word><Word>THEN</Word><Word>ELSE</Word><Word>END</Word><Word>AS</Word>
                                        <Word>DISTINCT</Word><Word>UNION</Word><Word>CREATE</Word><Word>TABLE</Word>
                                        <Word>DROP</Word><Word>ALTER</Word><Word>VIEW</Word><Word>PROCEDURE</Word><Word>TRIGGER</Word>
                                        <Word>ASC</Word><Word>DESC</Word><Word>SCHEMA</Word>
                                    </Keywords>
                                    <Keywords color='Function'>
                                        <Word>SUM</Word><Word>COUNT</Word><Word>UPDATE</Word><Word>CAST</Word><Word>CONVERT</Word>
                                        <Word>COALESCE</Word><Word>NULLIF</Word><Word>ISNULL</Word><Word>ROW_NUMBER</Word>
                                        <Word>RANK</Word><Word>DENSE_RANK</Word><Word>LAG</Word><Word>LEAD</Word><Word>MAX</Word><Word>MIN</Word>
                                    </Keywords>
                                    <Keywords color='Connector'>
                                        <Word>JOIN</Word><Word>LIMIT</Word><Word>OFFSET</Word><Word>ONLY</Word>
                                        <Word>LEFT</Word><Word>RIGHT</Word><Word>INNER</Word><Word>OUTER</Word>
                                        <Word>AND</Word><Word>OR</Word><Word>NOT</Word><Word>NULL</Word><Word>IS</Word>
                                        <Word>IN</Word><Word>BETWEEN</Word><Word>LIKE</Word><Word>EXISTS</Word><Word>ALL</Word>
                                    </Keywords>
                                </RuleSet>
                            </SyntaxDefinition>";


            using (var reader = new XmlTextReader(new StringReader(sqlXshd)))
            {
                var customHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                HighlightingManager.Instance.RegisterHighlighting("SQL", new[] { ".sql" }, customHighlighting);
            }
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
                btnExplorar_Click(sender, e);
            }
        }

        private void BloquearUI(bool bloquear)
        {
            txtQuery.IsEnabled = !bloquear;
            btnExecute.IsEnabled = !bloquear;
            btnExecuteScalar.IsEnabled = !bloquear;
            btnTest.IsEnabled = !bloquear;
            btnClear.IsEnabled = !bloquear;
            btnLimpiarLog.IsEnabled = !bloquear;
            btnExcel.IsEnabled = !bloquear;
        }

        private void TxtQuery_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
                BtnExecute_Click(this, new RoutedEventArgs());
        }

        private string LimpiarConsulta(string sql)
        {
            // Divide la consulta por líneas
            var lineas = sql.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            // Filtra las líneas que NO empiezan con -- (ignorando espacios en blanco al inicio)
            var lineasLimpias = lineas.Where(linea => !linea.TrimStart().StartsWith("--"));

            // Une todo de nuevo con espacios para que el motor SQL lo reciba en una sola línea o limpia
            return string.Join("\r\n", lineasLimpias);
        }

        private async void BtnExecute_Click(object sender, RoutedEventArgs e)
        {
            Stopwatch swTotal = Stopwatch.StartNew();

            string connStr = GetConnectionString();
            string sqlCompleto = txtQuery.SelectedText.Length > 0 ? txtQuery.SelectedText : txtQuery.Text;
            string sqlHistorial = sqlCompleto;
            sqlCompleto = LimpiarConsulta(sqlCompleto);

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
                            ColumnHeaderStyle = headerStyle,// --- APLICAMOS EL ESTILO ---
                            SelectionMode = DataGridSelectionMode.Single,     // NUEVO
                            SelectionUnit = DataGridSelectionUnit.FullRow     // NUEVO
                        };

                        // Click simple: selecciona la fila completa
                        dataGrid.PreviewMouseLeftButtonDown += (s, mouseEvent) =>
                        {
                            var clickedElement = mouseEvent.OriginalSource as DependencyObject;

                            var cell = FindVisualParent<DataGridCell>(clickedElement);
                            if (cell != null && mouseEvent.ClickCount == 1)
                            {
                                var row = FindVisualParent<DataGridRow>(cell);
                                if (row != null)
                                {
                                    dataGrid.SelectedItem = row.Item;
                                    dataGrid.CurrentCell = new DataGridCellInfo(cell);
                                }
                            }
                        };

                        // Doble click: selecciona el contenido de la celda para copiado
                        dataGrid.MouseDoubleClick += (s, mouseEvent) =>
                        {
                            var clickedElement = mouseEvent.OriginalSource as DependencyObject;

                            var cell = FindVisualParent<DataGridCell>(clickedElement);
                            if (cell != null && cell.Content is TextBlock textBlock)
                            {
                                // Entrar en modo de edición (aunque sea ReadOnly, podemos seleccionar el texto)
                                dataGrid.CurrentCell = new DataGridCellInfo(cell);
                                dataGrid.BeginEdit();

                                // Si tiene contenido, seleccionarlo
                                if (!string.IsNullOrEmpty(textBlock.Text))
                                {
                                    // Seleccionar todo el texto del TextBlock
                                    var range = new TextRange(textBlock.ContentStart, textBlock.ContentEnd);

                                    // Copiar al portapapeles automáticamente
                                    Clipboard.SetText(textBlock.Text);

                                    // Opcional: mostrar mensaje
                                    AppendMessage($"Celda seleccionada y copiada: {textBlock.Text}");
                                }
                            }
                        };

                        // Manejador de teclado para Ctrl+C en las celdas
                        dataGrid.PreviewKeyDown += (s, keyEvent) =>
                        {
                            if (keyEvent.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                            {
                                if (dataGrid.CurrentCell.Item != null && dataGrid.CurrentCell.Column != null)
                                {
                                    var cellContent = dataGrid.CurrentCell.Column.GetCellContent(dataGrid.CurrentCell.Item);
                                    if (cellContent is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                                    {
                                        Clipboard.SetText(tb.Text);
                                        AppendMessage($"Celda copiada: {tb.Text}");
                                        keyEvent.Handled = true;
                                    }
                                }
                            }
                        };

                        // Menú contextual para las celdas
                        var cellContextMenu = new ContextMenu();

                        var menuCopiarCelda = new MenuItem { Header = "Copiar celda" };
                        menuCopiarCelda.Click += (s, x) =>
                        {
                            if (dataGrid.CurrentCell.Item != null && dataGrid.CurrentCell.Column != null)
                            {
                                var cellContent = dataGrid.CurrentCell.Column.GetCellContent(dataGrid.CurrentCell.Item);
                                if (cellContent is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                                {
                                    Clipboard.SetText(tb.Text);
                                    AppendMessage($"Celda copiada: {tb.Text}");
                                }
                            }
                        };
                        cellContextMenu.Items.Add(menuCopiarCelda);

                        var menuCopiarFila = new MenuItem { Header = "Copiar fila" };
                        menuCopiarFila.Click += (s, x) =>
                        {
                            if (dataGrid.SelectedItem != null)
                            {
                                var row = dataGrid.SelectedItem as DataRowView;
                                if (row != null)
                                {
                                    var valores = new List<string>();
                                    foreach (DataColumn col in row.Row.Table.Columns)
                                    {
                                        valores.Add(row[col.ColumnName]?.ToString() ?? "");
                                    }
                                    string filaCompleta = string.Join("\t", valores);
                                    Clipboard.SetText(filaCompleta);
                                    AppendMessage("Fila completa copiada al portapapeles");
                                }
                            }
                        };
                        cellContextMenu.Items.Add(menuCopiarFila);

                        cellContextMenu.Items.Add(new Separator());

                        var menuCopiarTodo = new MenuItem { Header = "Copiar todo (con encabezados)" };
                        menuCopiarTodo.Click += (s, x) =>
                        {
                            var view = dataGrid.ItemsSource as DataView;
                            if (view != null)
                            {
                                var tabla = view.ToTable();
                                var sb = new System.Text.StringBuilder();

                                // Encabezados
                                var headers = tabla.Columns.Cast<DataColumn>().Select(c => c.ColumnName);
                                sb.AppendLine(string.Join("\t", headers));

                                // Filas
                                foreach (DataRow row in tabla.Rows)
                                {
                                    var valores = row.ItemArray.Select(v => v?.ToString() ?? "");
                                    sb.AppendLine(string.Join("\t", valores));
                                }

                                Clipboard.SetText(sb.ToString());
                                AppendMessage($"Tabla completa copiada ({tabla.Rows.Count} filas)");
                            }
                        };
                        cellContextMenu.Items.Add(menuCopiarTodo);

                        dataGrid.ContextMenu = cellContextMenu;

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
                            Header = $"Resultado {i + 1} ({dt.Columns.Count} cols, {dt.Rows.Count} filas, {FormatoNumero(elapsedMicroseconds)} µs ({FormatoNumero(elapsedMicroseconds / 1000000)}) s)",
                            Content = dataGrid
                        };

                        tcResults.Items.Add(tabItem);
                        AppendMessage($"Consulta {i + 1} exitosa. {dt.Rows.Count} filas devueltas en {FormatoNumero(elapsedMicroseconds)} µs ({FormatoNumero(elapsedMicroseconds / 1000000)}) s");
                    });
                }

                // 🔹 Al terminar todas, guardamos el bloque completo en el historial con todos los parámetros
                AddToHistoryWithParams(sqlHistorial, parametrosTotales);

                swTotal.Stop();
                double totalElapsedMicroseconds = swTotal.ElapsedTicks * (1000000.0 / Stopwatch.Frequency);

                txtColumnCount.Text = totalColumns.ToString();
                txtRowCount.Text = totalRows.ToString();
                txtTiempoDeEjecucion.Text = $"{FormatoNumero(totalElapsedMicroseconds)} µs ({FormatoNumero(totalElapsedMicroseconds / 1000000)}) s";

                await Dispatcher.InvokeAsync(() =>
                {
                    AppendMessage($"Ejecución total finalizada. {validQueries.Count} consultas ejecutadas en {FormatoNumero(totalElapsedMicroseconds)} µs ({FormatoNumero(totalElapsedMicroseconds / 1000000)}) s");
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
                System.Windows.Forms.FolderBrowserDialog fbdExcel = new System.Windows.Forms.FolderBrowserDialog();
                fbdExcel.ShowDialog();
                string ruta = Path.Combine(fbdExcel.SelectedPath, "ResultadosConsultas.xlsx");
                excelService.GuardarArchivo(archivoExcel, ruta);

                AppendMessage($"Excel generado correctamente en: {ruta}");
                System.Diagnostics.Process.Start(ruta);
            }
            catch (Exception ex)
            {
                AppendMessage("Error al generar Excel: " + ex.Message);
            }
        }

        private async void BtnLimpiarLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                txtMessages.Text = string.Empty;
            }
            catch (Exception ex)
            {
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

                txtTiempoDeEjecucion.Text = $"{FormatoNumero(elapsedMicroseconds)} µs ({FormatoNumero(elapsedMicroseconds / 1000000)}) s";

                await Dispatcher.InvokeAsync(() =>
                    AppendMessage($"Resultado del escalar: {(result?.ToString() ?? "(null) en {}")} en {FormatoNumero(elapsedMicroseconds)} µs ({FormatoNumero(elapsedMicroseconds / 1000000)}) s"));
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
                stringConnection = ConexionesManager.GetConnectionString(conexionActual.Motor, conexionActual.Servidor, conexionActual.BaseDatos, conexionActual.Usuario, conexionActual.Contrasena);
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

        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parentObject = System.Windows.Media.VisualTreeHelper.GetParent(child);

            if (parentObject == null)
                return null;

            if (parentObject is T parent)
                return parent;

            return FindVisualParent<T>(parentObject);
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

                    SincronizarParametros();

                    //var nuevos = new List<QueryParameter>();
                    //if (hist.Parametros != null)
                    //{
                    //    foreach (var p in hist.Parametros)
                    //    {
                    //        string nombre = p.Length > 0 ? p[0] : string.Empty;
                    //        string tipoStr = p.Length > 1 ? p[1] : string.Empty;
                    //        string valor = p.Length > 2 ? p[2] : string.Empty;

                    //        OdbcType tipoEnum = OdbcType.VarChar;
                    //        if (!string.IsNullOrWhiteSpace(tipoStr))
                    //        {
                    //            if (!Enum.TryParse(tipoStr, out tipoEnum))
                    //            {
                    //                try { tipoEnum = (OdbcType)Enum.Parse(typeof(OdbcType), tipoStr, true); }
                    //                catch { tipoEnum = OdbcType.VarChar; }
                    //            }
                    //        }

                    //        nuevos.Add(new QueryParameter
                    //        {
                    //            Nombre = nombre,
                    //            Tipo = tipoEnum,
                    //            Valor = valor
                    //        });
                    //    }
                    //}

                    //Parametros = nuevos;
                    //gridParams.ItemsSource = Parametros;
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
            DatosConexion datosConexion = new DatosConexion(conexionActual);
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

        private string FormatoNumero(double numero)
        {
            string formato = "N";
            return numero.ToString(formato, System.Globalization.CultureInfo.InvariantCulture);
        }

        private async void CargarEsquema(string filtrado = "", List<string> tablasConsulta = null, CancellationToken token = default(CancellationToken))
        {
            TreeView tvCargar = filtrado.Length == 0 ? tvSchema : tvSearch;
            if (conexionActual == null)
            {
                AppendMessage("No hay conexión seleccionada.");
                return;
            }

            string connStr = GetConnectionString();

            // 🎨 INICIO DE MODIFICACIÓN: Definición de colores de fondo alternados
            //var evenRowBrush = System.Windows.Media.Brushes.Cornsilk;
            //var oddRowColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A7D7F0");
            //var oddRowBrush = new System.Windows.Media.SolidColorBrush(oddRowColor);
            // 🎨 FIN DE MODIFICACIÓN

            // 🖼️ INICIO DE MODIFICACIÓN: Cargar íconos
            var tablaIconUri = new Uri("pack://application:,,,/Assets/tabla.png");
            var columnaIconUri = new Uri("pack://application:,,,/Assets/columna.png");
            var columnaClaveIconUri = new Uri("pack://application:,,,/Assets/columnaClave.png");
            var claveIconUri = new Uri("pack://application:,,,/Assets/clave.png");

            var tablaIcon = new System.Windows.Media.Imaging.BitmapImage(tablaIconUri);
            var columnaIcon = new System.Windows.Media.Imaging.BitmapImage(columnaIconUri);
            var columnaClaveIcon = new System.Windows.Media.Imaging.BitmapImage(columnaClaveIconUri);
            var claveIcon = new System.Windows.Media.Imaging.BitmapImage(claveIconUri);
            int tamañoIconos = 20;
            // 🖼️ FIN DE MODIFICACIÓN

            await Task.Run(() =>
            {
                try
                {
                    using (var conn = new OdbcConnection(connStr))
                    {
                        conn.Open();

                        // Obtiene las tablas
                        DataTable tablas = conn.GetSchema("Tables");

                        Dispatcher.Invoke(() => tvCargar.Items.Clear());

                        // Filtrar SOLO las filas cuyo tipo sea "TABLE"
                        DataRow[] tablasFiltradas = tablas.Select($"TABLE_TYPE = 'TABLE'");

                        // Si querés seguir usando un DataTable:
                        DataTable tablasSolo = tablasFiltradas.Length > 0 ? tablasFiltradas.CopyToDataTable() : tablas.Clone();

                        bool cargarTabla = true;
                        int tablasLeidas = 0;
                        int cantidadDeTablas = tablasConsulta == null ? tablasSolo.Rows.Count : tablasConsulta.Count;

                        // Ahora usás tablasSolo.Rows en el foreach
                        foreach (DataRow tabla in tablasSolo.Rows)
                        {
                            token.ThrowIfCancellationRequested();

                            string schema = tabla["TABLE_SCHEM"].ToString();
                            string nombreTabla = tabla["TABLE_NAME"].ToString();

                            cargarTabla = tablasConsulta == null || (tablasConsulta != null &&
                                tablasConsulta.Any(t => t.ToUpper().Trim().EndsWith(nombreTabla.ToUpper().Trim())));
                            if (cargarTabla)
                            {
                                string tipo = tabla["TABLE_TYPE"].ToString();
                                if (tipo != "TABLE") continue;

                                string headerText = string.IsNullOrEmpty(schema) ? nombreTabla : $"{schema}.{nombreTabla}";
                                var columnas = conn.GetSchema("Columns", new string[] { null, schema, nombreTabla });

                                // 🔑 Obtener columnas que son clave primaria mediante SQL según el motor
                                var columnasClaveSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                try
                                {
                                    string sqlPK = null;
                                    switch (conexionActual.Motor)
                                    {
                                        case TipoMotor.MS_SQL:
                                            sqlPK = $@"SELECT c.name AS COLUMN_NAME
                                                FROM sys.indexes i
                                                INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                                                INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                                                INNER JOIN sys.tables t ON i.object_id = t.object_id
                                                WHERE i.is_primary_key = 1 AND t.name = '{nombreTabla}'";
                                            break;
                                        case TipoMotor.DB2:
                                            sqlPK = $@"SELECT UPPER(COLNAMES) AS COLUMN_NAME FROM SYSCAT.INDEXES WHERE TABNAME = '{nombreTabla}' AND UNIQUERULE IN ('U')";
                                            break;
                                        case TipoMotor.POSTGRES:
                                            sqlPK = $@"SELECT a.attname AS COLUMN_NAME
                                                FROM pg_index ix
                                                JOIN pg_class t ON t.oid = ix.indrelid
                                                JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey)
                                                WHERE ix.indisprimary = true AND t.relname = '{nombreTabla}'";
                                            break;
                                        case TipoMotor.SQLite:
                                            sqlPK = $"PRAGMA table_info('{nombreTabla}')";
                                            break;
                                        default:
                                            break;
                                    }

                                    if (!string.IsNullOrEmpty(sqlPK))
                                    {
                                        using (var cmdPK = conn.CreateCommand())
                                        {
                                            cmdPK.CommandText = sqlPK;
                                            using (var rdrPK = cmdPK.ExecuteReader())
                                            {
                                                if (conexionActual.Motor == TipoMotor.SQLite)
                                                {
                                                    // PRAGMA table_info devuelve una fila por columna con campo "pk" > 0 si es PK
                                                    while (rdrPK.Read())
                                                    {
                                                        int pkOrdinal = rdrPK.GetOrdinal("pk");
                                                        int nameOrdinal = rdrPK.GetOrdinal("name");
                                                        if (!rdrPK.IsDBNull(pkOrdinal) && rdrPK.GetInt32(pkOrdinal) > 0)
                                                            columnasClaveSet.Add(rdrPK.GetString(nameOrdinal));
                                                    }
                                                }
                                                else if (conexionActual.Motor == TipoMotor.DB2)
                                                {
                                                    List<string> claves = new List<string>();
                                                    while (rdrPK.Read())
                                                    {
                                                        claves.Add(rdrPK.GetString(0));
                                                    }
                                                    if (claves.Count > 0)
                                                    {
                                                        int minCantidad = claves.Min(s => s.Count(c => c == '+'));

                                                        // Paso 2: filtrar los strings con esa cantidad mínima
                                                        List<string> clave = claves
                                                            .Where(s => s.Count(c => c == '+') == minCantidad)
                                                            .Select(s => s.Split('+'))
                                                            .FirstOrDefault().ToList();
                                                        // Eliminar los elementos vacíos
                                                        clave.RemoveAll(s => string.IsNullOrWhiteSpace(s));

                                                        foreach (string item in clave)
                                                        {
                                                            columnasClaveSet.Add(item);
                                                        }
                                                    }
                                                }
                                                else
                                                {
                                                    while (rdrPK.Read())
                                                        columnasClaveSet.Add(rdrPK["COLUMN_NAME"].ToString());
                                                }

                                            }
                                        }
                                    }
                                }
                                catch { /* Si el motor no soporta la consulta, se muestra icono normal para todas */ }

                                // 🎨 INICIO DE MODIFICACIÓN: Cálculo del fondo alternado
                                //System.Windows.Media.Brush currentTableBackground = (tablasLeidas % 2 == 0) ? evenRowBrush : oddRowBrush;
                                // 🎨 FIN DE MODIFICACIÓN

                                Dispatcher.Invoke(() =>
                                {
                                    // 🖼️ INICIO DE MODIFICACIÓN: nodo de tabla con icono
                                    var tablaHeader = new StackPanel { Orientation = Orientation.Horizontal };
                                    tablaHeader.Children.Add(new System.Windows.Controls.Image
                                    {
                                        Source = tablaIcon,
                                        Width = tamañoIconos,
                                        Height = tamañoIconos,
                                        Margin = new System.Windows.Thickness(0, 0, 5, 0)
                                    });
                                    tablaHeader.Children.Add(new System.Windows.Controls.TextBlock { Text = headerText });

                                    var tablaNode = new TreeViewItem
                                    {
                                        Header = tablaHeader,
                                        Tag = nombreTabla,
                                        //Background = currentTableBackground
                                    };
                                    // 🖼️ FIN DE MODIFICACIÓN

                                    if (filtrado.Length == 0 || (filtrado.Length > 0 && nombreTabla.ToUpper().Contains(filtrado.ToUpper())))
                                    {
                                        tvCargar.Items.Add(tablaNode);
                                        tablaNode.MouseDoubleClick += TablaNode_MouseDoubleClick;
                                    }

                                    // Agregar columnas
                                    foreach (DataRow col in columnas.Rows)
                                    {
                                        token.ThrowIfCancellationRequested();

                                        string colName = col["COLUMN_NAME"].ToString();
                                        string tipoCol = col["TYPE_NAME"].ToString();
                                        string longitud = col["COLUMN_SIZE"].ToString();

                                        string escala = string.Empty;
                                        if (col.Table.Columns.Contains("NUMERIC_SCALE") && col["NUMERIC_SCALE"] != DBNull.Value)
                                            escala = col["NUMERIC_SCALE"].ToString();
                                        else if (col.Table.Columns.Contains("COLUMN_SCALE") && col["COLUMN_SCALE"] != DBNull.Value)
                                            escala = col["COLUMN_SCALE"].ToString();
                                        else if (col.Table.Columns.Contains("COLUMN_SIZE") && col["COLUMN_SIZE"] != DBNull.Value)
                                            escala = col["COLUMN_SIZE"].ToString();

                                        string aceptaNulos = string.Empty;
                                        if (col.Table.Columns.Contains("IS_NULLABLE") && col["IS_NULLABLE"] != DBNull.Value)
                                        {
                                            string nuloStr = col["IS_NULLABLE"].ToString().ToUpper();
                                            aceptaNulos = nuloStr == "YES" ? "NULL" : nuloStr == "NO" ? "NOT NULL" : string.Empty;
                                        }

                                        string defecto = string.Empty;
                                        if (col.Table.Columns.Contains("COLUMN_DEF") && col["COLUMN_DEF"] != DBNull.Value)
                                            defecto = col["COLUMN_DEF"].ToString();

                                        string tipoCompleto = tipoCol;
                                        string tipoNormalizado = tipoCol.ToUpper();
                                        bool esNumericoDecimal = tipoNormalizado.Contains("DECIMAL") || tipoNormalizado.Contains("NUMERIC");

                                        if (!string.IsNullOrEmpty(longitud))
                                        {
                                            if (esNumericoDecimal && !string.IsNullOrEmpty(escala))
                                                tipoCompleto += $" [{longitud}, {escala}]";
                                            else
                                                tipoCompleto += $" [{longitud}]";
                                        }

                                        // 🖼️ INICIO DE MODIFICACIÓN: nodo de columna con icono (clave o normal)
                                        bool esClavePrimaria = columnasClaveSet.Contains(colName);
                                        var colHeader = new StackPanel { Orientation = Orientation.Horizontal };
                                        colHeader.Children.Add(new System.Windows.Controls.Image
                                        {
                                            Source = esClavePrimaria ? columnaClaveIcon : columnaIcon,
                                            Width = tamañoIconos,
                                            Height = tamañoIconos,
                                            Margin = new System.Windows.Thickness(0, 0, 5, 0)
                                        });
                                        colHeader.Children.Add(new System.Windows.Controls.TextBlock
                                        {
                                            Text = $"{colName} ({tipoCompleto}{(string.IsNullOrEmpty(aceptaNulos) ? string.Empty : $", {aceptaNulos}")}{(string.IsNullOrEmpty(defecto) ? string.Empty : $", DEFAULT {defecto}")})"
                                        });

                                        var colNode = new TreeViewItem { Header = colHeader };
                                        // 🖼️ FIN DE MODIFICACIÓN

                                        tablaNode.Items.Add(colNode);
                                    }
                                });

                                // 🔹 Carga de índices (sin UI pesada)
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
                                                Dispatcher.Invoke(() =>
                                                {
                                                    var tablaNode = tvCargar.Items.OfType<TreeViewItem>()
                                                        .FirstOrDefault(t => (string)t.Tag == nombreTabla);
                                                    if (tablaNode == null) return;

                                                    var indiceRaiz = new TreeViewItem
                                                    {
                                                        Header = "Índices",
                                                    };

                                                    string indexNameColumn = conexionActual.Motor == TipoMotor.SQLite ? "NAME" : "INDEXNAME";

                                                    var indicesAgrupados = dtIndices.AsEnumerable()
                                                        .GroupBy(row => row.Field<string>(indexNameColumn))
                                                        .OrderBy(g => g.Key);

                                                    foreach (var grupoIndice in indicesAgrupados)
                                                    {
                                                        string nombreIndice = grupoIndice.Key;

                                                        // 🖼️ INICIO DE MODIFICACIÓN: nodo de índice con icono
                                                        var indiceHeader = new StackPanel { Orientation = Orientation.Horizontal };
                                                        indiceHeader.Children.Add(new System.Windows.Controls.Image
                                                        {
                                                            Source = claveIcon,
                                                            Width = tamañoIconos,
                                                            Height = tamañoIconos,
                                                            Margin = new System.Windows.Thickness(0, 0, 5, 0)
                                                        });
                                                        indiceHeader.Children.Add(new System.Windows.Controls.TextBlock { Text = nombreIndice });

                                                        var nodoIndice = new TreeViewItem { Header = indiceHeader };
                                                        // 🖼️ FIN DE MODIFICACIÓN

                                                        indiceRaiz.Items.Add(nodoIndice);
                                                    }

                                                    tablaNode.Items.Add(indiceRaiz);
                                                });
                                            }
                                        }
                                    }
                                }
                                catch { /* Algunos motores no exponen esa vista */ }

                                tablasLeidas++;
                            }

                            Dispatcher.Invoke(() => txtExplorar.Text = $"{tablasLeidas} tablas leídas de {cantidadDeTablas}");
                            if (tablasLeidas == cantidadDeTablas) break;
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                    Dispatcher.Invoke(() => AppendMessage("Tarea de exploración cancelada."));
                    return;
                }
                catch (OperationCanceledException)
                {
                    Dispatcher.Invoke(() => AppendMessage("Exploración cancelada."));
                    return;
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendMessage("Error al cargar esquema: " + ex.Message));
                }
            });
        }

        private void TablaNode_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            string tableName = (sender as TreeViewItem).Header.ToString();
            txtQuery.Text.Insert(txtQuery.SelectionStart, tableName);
        }

        private void tvSchema_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if ((sender as TreeView).SelectedItem is TreeViewItem item)
            {
                string texto = ObtenerNombrePuro(item);

                if (!string.IsNullOrEmpty(texto))
                    InsertarEnQuery(texto);
            }
        }

        private string ObtenerNombrePuro(TreeViewItem item)
        {
            string texto = ExtraerTextoDesdeHeader(item);

            if (string.IsNullOrEmpty(texto))
                return null;

            // Si es tabla, no tiene paréntesis → va completo
            if (!texto.Contains("("))
                return texto.Trim();

            // Si es columna → tomar solo lo previo al '('
            return texto.Split('(')[0].Trim();
        }

        private string ExtraerTextoDesdeHeader(TreeViewItem item)
        {
            if (item.Header is string s)
                return s;

            if (item.Header is StackPanel sp)
            {
                foreach (var child in sp.Children)
                {
                    if (child is TextBlock tb)
                        return tb.Text;
                }
            }

            return null;
        }

        private void InsertarEnQuery(string texto)
        {
            if (string.IsNullOrEmpty(texto)) return;

            // AvalonEdit usa CaretOffset y SelectionLength
            int length = txtQuery.SelectionLength;
            int offset = txtQuery.CaretOffset - length;

            // Reemplaza el texto seleccionado o inserta en la posición del cursor
            try
            {
                txtQuery.Document.Replace(offset, length, texto);
            }
            catch (Exception err)
            {
                MessageBox.Show(err.Message);
            }

            // Reposicionar el cursor al final de lo insertado y dar foco
            txtQuery.CaretOffset = offset + texto.Length;
            txtQuery.Focus();
        }

        private void btnExplorar_Click(object sender, RoutedEventArgs e)
        {
            // Si había un proceso anterior, se cancela
            _explorarCTS?.Cancel();

            // Se crea uno nuevo
            _explorarCTS = new CancellationTokenSource();

            CargarEsquema(string.Empty, null, _explorarCTS.Token);
        }

        private void btnExplorarConsultas_Click(object sender, RoutedEventArgs e)
        {
            // Cancelar si el otro sigue corriendo
            _explorarCTS?.Cancel();

            _explorarCTS = new CancellationTokenSource();

            List<string> tablasConsulta = ExtraerTablas(txtQuery.Text);
            CargarEsquema(string.Empty, tablasConsulta, _explorarCTS.Token);
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

        //private void txtQuery_KeyUp(object sender, KeyEventArgs e)
        //{
        //    // Detecta el signo de interrogación (Shift + /)
        //    if (e.Key == Key.Oem4 && Keyboard.Modifiers == ModifierKeys.Shift)
        //    {
        //        AgregarParametro();
        //    }
        //    if (e.Key == Key.Back || e.Key == Key.Delete)
        //    {
        //        EliminarParametro();
        //    }
        //}

        private void txtQuery_KeyUp(object sender, KeyEventArgs e)
        {
            // Detecta: '?', Backspace, Delete o Pegar (Ctrl+V)
            if (e.Key == Key.Oem4 || e.Key == Key.Back || e.Key == Key.Delete ||
               (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control))
            {
                SincronizarParametros();
            }
        }

        private void SincronizarParametros()
        {
            string textoActual = txtQuery.Text;
            var matches = Regex.Matches(textoActual, @"\?");
            var nuevaLista = new List<QueryParameter>();

            for (int i = 0; i < matches.Count; i++)
            {
                string valorExistente = (i < Parametros.Count) ? Parametros[i].Valor : "";

                // Esta llamada ahora es mucho más potente porque consulta la DB
                ContextoParametro info = ObtenerContextoDeParametro(textoActual, matches[i].Index);

                nuevaLista.Add(new QueryParameter
                {
                    Nombre = info.Nombre,
                    Tipo = info.Tipo,
                    Valor = valorExistente
                });
            }

            Parametros.Clear();
            foreach (var p in nuevaLista) Parametros.Add(p);
            gridParams.Items.Refresh();
        }

        public class ContextoParametro
        {
            public string Nombre { get; set; }
            public OdbcType Tipo { get; set; }
        }

        private OdbcType ObtenerTipoRealDesdeDB(string nombreColumna, string queryCompleta)
        {
            if (conexionActual == null) return OdbcType.VarChar;

            try
            {
                // 1. Intentamos extraer el nombre de la tabla de la consulta (lógica simple)
                string tabla = "TABLA_DESCONOCIDA";
                var matchTabla = Regex.Match(queryCompleta, @"FROM\s+([^\s\s,;]+)", RegexOptions.IgnoreCase);
                if (matchTabla.Success) tabla = matchTabla.Groups[1].Value;

                using (OdbcConnection conn = new OdbcConnection(ConexionesManager.GetConnectionString(conexionActual)))
                {
                    conn.Open();
                    // 2. Pedimos solo el esquema de la tabla para no traer datos
                    string schemaQuery = string.Format("SELECT {0} FROM {1} WHERE 1=0", nombreColumna, tabla);
                    using (OdbcCommand cmd = new OdbcCommand(schemaQuery, conn))
                    {
                        using (OdbcDataReader reader = cmd.ExecuteReader(CommandBehavior.SchemaOnly))
                        {
                            DataTable schemaTable = reader.GetSchemaTable();
                            if (schemaTable != null && schemaTable.Rows.Count > 0)
                            {
                                // 3. Mapeamos el tipo de .NET al OdbcType
                                Type type = (Type)schemaTable.Rows[0]["DataType"];
                                return MapearTipoADotNet(type);
                            }
                        }
                    }
                }
            }
            catch { /* Si falla la consulta de metadatos, cae al default */ }

            return OdbcType.VarChar;
        }

        private OdbcType MapearTipoADotNet(Type t)
        {
            return Mapeo[t.Name.ToUpper()];
        }

        public Dictionary<string, OdbcType> Mapeo = new Dictionary<string, OdbcType>
        {
            { "BOOL",       OdbcType.Bit},            //"OdbcType.Smallint"}, 	// DB2 no tiene BOOLEAN real en versiones antiguas
            { "BYTE",       OdbcType.TinyInt},        // SMALLINT usado como byte en DB2
            { "BYTE[]",     OdbcType.VarBinary},	    // BLOB, VARBINARY, BYTEA
            { "CHAR",       OdbcType.Char},           // CHAR(1)
            { "CHAR[]",     OdbcType.VarBinary},	    // BLOB, VARBINARY, BYTEA
            { "DATETIME",   OdbcType.DateTime},	    // TIMESTAMP (Date si sólo fecha)
            { "DECIMAL",    OdbcType.Numeric },       // DECIMAL(p,s), NUMERIC
            { "DOUBLE",     OdbcType.Double},	        // DOUBLE
            { "FLOAT",      OdbcType.Real }, 	        // REAL
            { "GUID",       OdbcType.Char},           // DB2 no tiene UNIQUEIDENTIFIER → usar CHAR(36)
            { "INT",        OdbcType.Int},            // INTEGER
            { "INT16",      OdbcType.SmallInt },      // SMALLINT	
            { "INT64",      OdbcType.BigInt },	    // BIGINT
            { "LONG",       OdbcType.BigInt },	    // BIGINT
            { "SBYTE",      OdbcType.Double},	        // DOUBLE
            { "SHORT",      OdbcType.SmallInt },      // SMALLINT	
            { "SINGLE",     OdbcType.Double},	        // DOUBLE
            { "STRING",     OdbcType.VarChar},	    // VARCHAR, usar NVarChar si es Unicode	
            { "TIMESPAN",   OdbcType.Time},	        // TIME
            { "UINT",       OdbcType.BigInt},          // BIGINT
            { "ULONG",      OdbcType.BigInt},          // BIGINT
            { "USHORT",     OdbcType.BigInt}          // BIGINT
        };

        private ContextoParametro ObtenerContextoDeParametro(string texto, int posicion)
        {
            int inicio = Math.Max(0, posicion - 60);
            string contexto = texto.Substring(inicio, posicion - inicio).ToUpper();
            var palabras = contexto.Split(new[] { ' ', '\r', '\n', '\t', '=', '>', '<' }, StringSplitOptions.RemoveEmptyEntries);

            string campoReal = "param";
            if (palabras.Length > 0)
            {
                string ultimaPalabra = palabras.Last();
                campoReal = ultimaPalabra;
                if (ultimaPalabra == "AND" || ultimaPalabra == "BETWEEN")
                {
                    int idx = Array.LastIndexOf(palabras, "BETWEEN");
                    if (idx > 0) campoReal = palabras[idx - 1];
                }
                if (campoReal.Contains(".")) campoReal = campoReal.Split('.').Last();
                campoReal = Regex.Replace(campoReal, @"[\[\]\""]", "");
            }

            // --- LLAMADA A LA DB PARA TIPO REAL ---
            OdbcType tipoSugerido = ObtenerTipoRealDesdeDB(campoReal, texto);

            // --- CONSTRUCCIÓN DEL NOMBRE ---
            string sufijo = "";
            if (contexto.TrimEnd().EndsWith("BETWEEN")) sufijo = "_DESDE";
            else if (contexto.TrimEnd().EndsWith("AND") && contexto.Contains("BETWEEN")) sufijo = "_HASTA";

            return new ContextoParametro
            {
                Nombre = "@" + campoReal + sufijo,
                Tipo = tipoSugerido
            };
        }

        private void AgregarParametro()
        {
            try
            {
                // Posición actual del cursor
                int caretIndex = txtQuery.CaretOffset;
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
                if ((gridParams.Items.Count - 1) >= parametrosEnQuery)
                {
                    // Posición actual del cursor
                    int caretIndex = txtQuery.CaretOffset;
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

        private void lstHistory_KeyDown(object sender, KeyEventArgs e)
        {
            // 1. Verificar si la tecla presionada es Delete (Suprimir)
            if (e.Key == Key.Delete)
            {
                // 2. Obtener el elemento seleccionado
                if (lstHistory.SelectedItem is ListBoxItem selectedItem && selectedItem.Tag is Historial histParaEliminar)
                {
                    try
                    {
                        // 3. Cargar todos los historiales del XML
                        var todosLosHistoriales = LoadAllHistoriales();

                        // 4. Buscar y remover el historial coincidente (por fecha o consulta)
                        // Usamos la fecha como identificador único en este caso
                        var itemEnLista = todosLosHistoriales.FirstOrDefault(h => h.Fecha == histParaEliminar.Fecha && h.Consulta == histParaEliminar.Consulta);

                        if (itemEnLista != null)
                        {
                            todosLosHistoriales.Remove(itemEnLista);

                            // 5. Guardar la lista actualizada en el XML
                            SaveAllHistoriales(todosLosHistoriales);

                            // 6. Refrescar la UI cargando solo el historial de la conexión actual
                            if (conexionActual != null)
                            {
                                LoadHistoryForConnection(conexionActual);
                                AppendMessage("Elemento eliminado del historial.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendMessage("Error al eliminar del historial: " + ex.Message);
                    }
                }
            }
        }

        private void btnBuscar_Click(object sender, RoutedEventArgs e)
        {
            string filtro = txtBuscar.Text.Trim().ToLower();

            if (string.IsNullOrEmpty(filtro))
            {
                // Si está vacío, volvemos al original
                tvSearch.Visibility = Visibility.Collapsed;
                tvSchema.Visibility = Visibility.Visible;
            }
            else
            {
                // Si hay texto, filtramos
                //FiltrarArbol(filtro);
                CargarEsquema(filtro, null, _explorarCTS.Token);
                tvSchema.Visibility = Visibility.Collapsed;
                tvSearch.Visibility = Visibility.Visible;
            }
        }

        private void FiltrarArbol(string filtro)
        {
            tvSearch.Items.Clear();

            foreach (TreeViewItem esquema in tvSchema.Items)
            {
                // 1. Extraemos el nombre del Esquema/Base de Datos
                string nombreEsquema = ExtraerTextoDeHeader(esquema.Header);
                TreeViewItem nuevoEsquema = new TreeViewItem { Header = nombreEsquema, IsExpanded = true };

                bool tieneCoincidencias = false;

                foreach (TreeViewItem tabla in esquema.Items)
                {
                    // 2. Extraemos el nombre real de la tabla (ej: "albums")
                    string nombreTabla = ExtraerTextoDeHeader(tabla.Header);

                    if (nombreTabla.ToLower().Contains(filtro))
                    {
                        // Creamos el nuevo item con el nombre limpio
                        // Si quieres que el buscador también tenga iconos, 
                        // tendrías que recrear el StackPanel aquí.
                        TreeViewItem copiaTabla = new TreeViewItem { Header = nombreTabla };
                        nuevoEsquema.Items.Add(copiaTabla);
                        tieneCoincidencias = true;
                    }
                }

                if (tieneCoincidencias)
                {
                    tvSearch.Items.Add(nuevoEsquema);
                }
            }
        }

        // Esta función ayuda a obtener el texto sin importar si el Header es un String o un StackPanel
        private string ExtraerTextoDeHeader(object header)
        {
            if (header is string) return (string)header;

            if (header is StackPanel sp)
            {
                // Buscamos el TextBlock dentro del StackPanel
                foreach (var hijo in sp.Children)
                {
                    if (hijo is TextBlock tb) return tb.Text;
                }
            }

            return header.ToString(); // Caso de respaldo
        }

        private void txtBuscar_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                btnBuscar_Click(sender, e);
            }
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
//using System.Xml.Serialization;
//using System.Windows.Media.Animation;
//using System.Text.RegularExpressions;
//using System.Windows.Data;
//using System.Windows.Controls.Primitives;
//using ICSharpCode.AvalonEdit.Highlighting;
//using ICSharpCode.AvalonEdit.Highlighting.Xshd;
//using System.Xml;
//using System.Threading;
//using System.Windows.Documents;

//namespace QueryAnalyzer
//{
//    public partial class MainWindow : Window
//    {
//        private const string HISTORY_FILE = "query_history.txt";
//        private const string HISTORIAL_XML = "historial.xml";
//        private bool iniciarColapasado = true;
//        private CancellationTokenSource _explorarCTS;

//        public Dictionary<string, OdbcType> OdbcTypes { get; set; }
//        public List<QueryParameter> Parametros { get; set; }

//        static public Conexion conexionActual = null;

//        public MainWindow()
//        {
//            InitializeComponent();

//            // 🔹 AÑADIDO: esto asegura que los bindings de DataContext funcionen correctamente.
//            DataContext = this;

//            // 🔹 NUEVO: Registrar definición de SQL si no existe nativamente
//            RegistrarResaltadoSQL();
//            txtQuery.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("SQL");

//            LoadHistory(); // mantiene compatibilidad con el archivo de texto
//            //txtQuery.KeyDown += TxtQuery_KeyDown;
//            txtQuery.Text = "SELECT * FROM SYSIBM.SYSCOLUMNS FETCH FIRST 10 ROWS ONLY; -- ejemplo para DB2";

//            CargarTipos();

//            // Configura ItemsSource correctamente
//            Parametros = new List<QueryParameter>();
//            gridParams.ItemsSource = Parametros;
//            InicializarConexiones();
//            BloquearUI(true);
//            ConfigurarMenuContextualAvalonEdit();
//        }
//        private void ConfigurarMenuContextualAvalonEdit()
//        {
//            // Crear el menú contextual
//            var contextMenu = new ContextMenu();

//            // Opción Copiar
//            var menuCopiar = new MenuItem { Header = "Copiar" };
//            menuCopiar.Click += (s, e) =>
//            {
//                if (!string.IsNullOrEmpty(txtQuery.SelectedText))
//                    Clipboard.SetText(txtQuery.SelectedText);
//            };
//            contextMenu.Items.Add(menuCopiar);

//            // Opción Cortar
//            var menuCortar = new MenuItem { Header = "Cortar" };
//            menuCortar.Click += (s, e) =>
//            {
//                if (!string.IsNullOrEmpty(txtQuery.SelectedText))
//                {
//                    Clipboard.SetText(txtQuery.SelectedText);
//                    txtQuery.SelectedText = "";
//                }
//            };
//            contextMenu.Items.Add(menuCortar);

//            // Opción Pegar
//            var menuPegar = new MenuItem { Header = "Pegar" };
//            menuPegar.Click += (s, e) =>
//            {
//                if (Clipboard.ContainsText())
//                {
//                    txtQuery.SelectedText = Clipboard.GetText();
//                }
//            };
//            contextMenu.Items.Add(menuPegar);

//            // Separador
//            contextMenu.Items.Add(new Separator());

//            // Opción Seleccionar todo
//            var menuSeleccionarTodo = new MenuItem { Header = "Seleccionar todo" };
//            menuSeleccionarTodo.Click += (s, e) => txtQuery.SelectAll();
//            contextMenu.Items.Add(menuSeleccionarTodo);

//            // Asignar el menú contextual al TextEditor
//            txtQuery.ContextMenu = contextMenu;
//        }

//        private void RegistrarResaltadoSQL()
//        {
//            // Definición XML manual para asegurar que funcione sin archivos externos
//            string sqlXshd = @"
//                            <SyntaxDefinition name='SQL' extensions='.sql' xmlns='http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008'>
//                                <Color name='String' foreground='Red' />
//                                <Color name='Comment' foreground='Green' />
//                                <Color name='Keyword' foreground='Blue' fontWeight='bold' />
//                                <Color name='Function' foreground='Magenta' />
//                                <Color name='Connector' foreground='#0094FF'  />
//                                <RuleSet ignoreCase='true'>
//                                    <Span color='Comment' begin='--' />
//                                    <Span color='Comment' multiline='true' begin='/\*' end='\*/' />
//                                    <Span color='String'><Begin>'</Begin><End>'</End></Span>
//                                    <Keywords color='Keyword'>
//                                        <Word>SELECT</Word><Word>FROM</Word><Word>WHERE</Word><Word>GROUP</Word><Word>BY</Word>
//                                        <Word>ORDER</Word><Word>HAVING</Word><Word>FETCH</Word>
//                                        <Word>FIRST</Word><Word>ROWS</Word><Word>INSERT</Word><Word>INTO</Word>
//                                        <Word>VALUES</Word><Word>SET</Word><Word>DELETE</Word>
//                                        <Word>ON</Word><Word>CASE</Word><Word>ADD</Word>
//                                        <Word>WHEN</Word><Word>THEN</Word><Word>ELSE</Word><Word>END</Word><Word>AS</Word>
//                                        <Word>DISTINCT</Word><Word>UNION</Word><Word>CREATE</Word><Word>TABLE</Word>
//                                        <Word>DROP</Word><Word>ALTER</Word><Word>VIEW</Word><Word>PROCEDURE</Word><Word>TRIGGER</Word>
//                                        <Word>ASC</Word><Word>DESC</Word><Word>SCHEMA</Word>
//                                    </Keywords>
//                                    <Keywords color='Function'>
//                                        <Word>SUM</Word><Word>COUNT</Word><Word>UPDATE</Word><Word>CAST</Word><Word>CONVERT</Word>
//                                        <Word>COALESCE</Word><Word>NULLIF</Word><Word>ISNULL</Word><Word>ROW_NUMBER</Word>
//                                        <Word>RANK</Word><Word>DENSE_RANK</Word><Word>LAG</Word><Word>LEAD</Word><Word>MAX</Word><Word>MIN</Word>
//                                    </Keywords>
//                                    <Keywords color='Connector'>
//                                        <Word>JOIN</Word><Word>LIMIT</Word><Word>OFFSET</Word><Word>ONLY</Word>
//                                        <Word>LEFT</Word><Word>RIGHT</Word><Word>INNER</Word><Word>OUTER</Word>
//                                        <Word>AND</Word><Word>OR</Word><Word>NOT</Word><Word>NULL</Word><Word>IS</Word>
//                                        <Word>IN</Word><Word>BETWEEN</Word><Word>LIKE</Word><Word>EXISTS</Word><Word>ALL</Word>
//                                    </Keywords>
//                                </RuleSet>
//                            </SyntaxDefinition>";


//            using (var reader = new XmlTextReader(new StringReader(sqlXshd)))
//            {
//                var customHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
//                HighlightingManager.Instance.RegisterHighlighting("SQL", new[] { ".sql" }, customHighlighting);
//            }
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

//        protected override void OnActivated(EventArgs e)
//        {
//            base.OnActivated(e);
//            if (iniciarColapasado)
//            {
//                btnExpandirColapsar_Click(this, e as RoutedEventArgs);
//                iniciarColapasado = false;
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

//                // NUEVO: al seleccionar conexión, filtramos historial para esa conexión
//                LoadHistoryForConnection(conexion);
//                btnExplorar_Click(sender, e);
//            }
//        }

//        private void BloquearUI(bool bloquear)
//        {
//            txtQuery.IsEnabled = !bloquear;
//            btnExecute.IsEnabled = !bloquear;
//            btnExecuteScalar.IsEnabled = !bloquear;
//            btnTest.IsEnabled = !bloquear;
//            btnClear.IsEnabled = !bloquear;
//            btnLimpiarLog.IsEnabled = !bloquear;
//            btnExcel.IsEnabled = !bloquear;
//        }

//        private void TxtQuery_KeyDown(object sender, KeyEventArgs e)
//        {
//            if (e.Key == Key.F5)
//                BtnExecute_Click(this, new RoutedEventArgs());
//        }

//        private string LimpiarConsulta(string sql)
//        {
//            // Divide la consulta por líneas
//            var lineas = sql.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

//            // Filtra las líneas que NO empiezan con -- (ignorando espacios en blanco al inicio)
//            var lineasLimpias = lineas.Where(linea => !linea.TrimStart().StartsWith("--"));

//            // Une todo de nuevo con espacios para que el motor SQL lo reciba en una sola línea o limpia
//            return string.Join("\r\n", lineasLimpias);
//        }

//        private async void BtnExecute_Click(object sender, RoutedEventArgs e)
//        {
//            Stopwatch swTotal = Stopwatch.StartNew();

//            string connStr = GetConnectionString();
//            string sqlCompleto = txtQuery.SelectedText.Length > 0 ? txtQuery.SelectedText : txtQuery.Text;
//            string sqlHistorial = sqlCompleto;
//            sqlCompleto = LimpiarConsulta(sqlCompleto);

//            if (string.IsNullOrWhiteSpace(connStr))
//            {
//                AppendMessage("El string de conexión está vacío.");
//                return;
//            }
//            if (string.IsNullOrWhiteSpace(sqlCompleto))
//            {
//                AppendMessage("La consulta está vacía.");
//                return;
//            }

//            tcResults.Items.Clear();
//            AppendMessage($"Ejecutando... ({DateTime.Now})");

//            string[] queries = sqlCompleto.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
//            var validQueries = queries.Select(q => q.Trim()).Where(q => !string.IsNullOrWhiteSpace(q)).ToList();

//            if (validQueries.Count == 0)
//            {
//                AppendMessage("No se encontraron consultas válidas (separadas por ';').");
//                return;
//            }

//            long totalRows = 0;
//            long totalColumns = 0;

//            try
//            {
//                // 🔹 Capturamos los parámetros en una lista tipo pila
//                List<QueryParameter> parametrosTotales = null;
//                await Dispatcher.InvokeAsync(() =>
//                {
//                    parametrosTotales = gridParams.Items.OfType<QueryParameter>()
//                        .Where(p => !string.IsNullOrWhiteSpace(p.Nombre))
//                        .ToList();
//                });

//                int posicionParametro = 0;

//                for (int i = 0; i < validQueries.Count; i++)
//                {
//                    string sqlIndividual = validQueries[i];
//                    Stopwatch swQuery = Stopwatch.StartNew();
//                    AppendMessage($"Ejecutando consulta {i + 1}/{validQueries.Count}...");

//                    // 🔹 Extraemos los parámetros de esta consulta según la pila
//                    var parametrosConsulta = ExtraerParametrosParaConsulta(sqlIndividual, parametrosTotales, ref posicionParametro);

//                    var dt = await ExecuteQueryAsync(connStr, sqlIndividual, parametrosConsulta);

//                    swQuery.Stop();
//                    double elapsedMicroseconds = swQuery.ElapsedTicks * (1000000.0 / Stopwatch.Frequency);
//                    totalRows += dt.Rows.Count;
//                    totalColumns += dt.Columns.Count;

//                    await Dispatcher.InvokeAsync(() =>
//                    {
//                        // 1. Definir el estilo para centrar los encabezados
//                        var headerStyle = new Style(typeof(DataGridColumnHeader));
//                        headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));

//                        var dataGrid = new DataGrid
//                        {
//                            IsReadOnly = true,
//                            AutoGenerateColumns = true,
//                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
//                            AlternationCount = 2,
//                            RowStyle = (Style)this.FindResource("ResultGridRowStyle"),
//                            ItemsSource = dt.DefaultView,
//                            ColumnHeaderStyle = headerStyle,// --- APLICAMOS EL ESTILO ---
//                            SelectionMode = DataGridSelectionMode.Single,     // NUEVO
//                            SelectionUnit = DataGridSelectionUnit.FullRow     // NUEVO
//                        };

//                        // Click simple: selecciona la fila completa
//                        dataGrid.PreviewMouseLeftButtonDown += (s, mouseEvent) =>
//                        {
//                            var clickedElement = mouseEvent.OriginalSource as DependencyObject;

//                            var cell = FindVisualParent<DataGridCell>(clickedElement);
//                            if (cell != null && mouseEvent.ClickCount == 1)
//                            {
//                                var row = FindVisualParent<DataGridRow>(cell);
//                                if (row != null)
//                                {
//                                    dataGrid.SelectedItem = row.Item;
//                                    dataGrid.CurrentCell = new DataGridCellInfo(cell);
//                                }
//                            }
//                        };

//                        // Doble click: selecciona el contenido de la celda para copiado
//                        dataGrid.MouseDoubleClick += (s, mouseEvent) =>
//                        {
//                            var clickedElement = mouseEvent.OriginalSource as DependencyObject;

//                            var cell = FindVisualParent<DataGridCell>(clickedElement);
//                            if (cell != null && cell.Content is TextBlock textBlock)
//                            {
//                                // Entrar en modo de edición (aunque sea ReadOnly, podemos seleccionar el texto)
//                                dataGrid.CurrentCell = new DataGridCellInfo(cell);
//                                dataGrid.BeginEdit();

//                                // Si tiene contenido, seleccionarlo
//                                if (!string.IsNullOrEmpty(textBlock.Text))
//                                {
//                                    // Seleccionar todo el texto del TextBlock
//                                    var range = new TextRange(textBlock.ContentStart, textBlock.ContentEnd);

//                                    // Copiar al portapapeles automáticamente
//                                    Clipboard.SetText(textBlock.Text);

//                                    // Opcional: mostrar mensaje
//                                    AppendMessage($"Celda seleccionada y copiada: {textBlock.Text}");
//                                }
//                            }
//                        };

//                        // Manejador de teclado para Ctrl+C en las celdas
//                        dataGrid.PreviewKeyDown += (s, keyEvent) =>
//                        {
//                            if (keyEvent.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
//                            {
//                                if (dataGrid.CurrentCell.Item != null && dataGrid.CurrentCell.Column != null)
//                                {
//                                    var cellContent = dataGrid.CurrentCell.Column.GetCellContent(dataGrid.CurrentCell.Item);
//                                    if (cellContent is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
//                                    {
//                                        Clipboard.SetText(tb.Text);
//                                        AppendMessage($"Celda copiada: {tb.Text}");
//                                        keyEvent.Handled = true;
//                                    }
//                                }
//                            }
//                        };

//                        // Menú contextual para las celdas
//                        var cellContextMenu = new ContextMenu();

//                        var menuCopiarCelda = new MenuItem { Header = "Copiar celda" };
//                        menuCopiarCelda.Click += (s, x) =>
//                        {
//                            if (dataGrid.CurrentCell.Item != null && dataGrid.CurrentCell.Column != null)
//                            {
//                                var cellContent = dataGrid.CurrentCell.Column.GetCellContent(dataGrid.CurrentCell.Item);
//                                if (cellContent is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
//                                {
//                                    Clipboard.SetText(tb.Text);
//                                    AppendMessage($"Celda copiada: {tb.Text}");
//                                }
//                            }
//                        };
//                        cellContextMenu.Items.Add(menuCopiarCelda);

//                        var menuCopiarFila = new MenuItem { Header = "Copiar fila" };
//                        menuCopiarFila.Click += (s, x) =>
//                        {
//                            if (dataGrid.SelectedItem != null)
//                            {
//                                var row = dataGrid.SelectedItem as DataRowView;
//                                if (row != null)
//                                {
//                                    var valores = new List<string>();
//                                    foreach (DataColumn col in row.Row.Table.Columns)
//                                    {
//                                        valores.Add(row[col.ColumnName]?.ToString() ?? "");
//                                    }
//                                    string filaCompleta = string.Join("\t", valores);
//                                    Clipboard.SetText(filaCompleta);
//                                    AppendMessage("Fila completa copiada al portapapeles");
//                                }
//                            }
//                        };
//                        cellContextMenu.Items.Add(menuCopiarFila);

//                        cellContextMenu.Items.Add(new Separator());

//                        var menuCopiarTodo = new MenuItem { Header = "Copiar todo (con encabezados)" };
//                        menuCopiarTodo.Click += (s, x) =>
//                        {
//                            var view = dataGrid.ItemsSource as DataView;
//                            if (view != null)
//                            {
//                                var tabla = view.ToTable();
//                                var sb = new System.Text.StringBuilder();

//                                // Encabezados
//                                var headers = tabla.Columns.Cast<DataColumn>().Select(c => c.ColumnName);
//                                sb.AppendLine(string.Join("\t", headers));

//                                // Filas
//                                foreach (DataRow row in tabla.Rows)
//                                {
//                                    var valores = row.ItemArray.Select(v => v?.ToString() ?? "");
//                                    sb.AppendLine(string.Join("\t", valores));
//                                }

//                                Clipboard.SetText(sb.ToString());
//                                AppendMessage($"Tabla completa copiada ({tabla.Rows.Count} filas)");
//                            }
//                        };
//                        cellContextMenu.Items.Add(menuCopiarTodo);

//                        dataGrid.ContextMenu = cellContextMenu;

//                        dataGrid.AutoGeneratedColumns += (s, ev) =>
//                        {
//                            foreach (var column in dataGrid.Columns)
//                            {
//                                string colName = column.Header.ToString();
//                                if (!dt.Columns.Contains(colName))
//                                    continue;

//                                Type tipo = dt.Columns[colName].DataType;

//                                // Formato para DateTime
//                                if (tipo == typeof(DateTime) || tipo == typeof(DateTime?) || tipo == typeof(DateTimeOffset))
//                                {
//                                    var boundColumn = column as DataGridBoundColumn;
//                                    if (boundColumn != null)
//                                    {
//                                        var binding = boundColumn.Binding as Binding;
//                                        if (binding != null)
//                                        {
//                                            binding.StringFormat = "dd/MM/yyyy HH:mm:ss";
//                                        }
//                                    }
//                                }

//                                // Alineación de celdas
//                                bool aDerecha =
//                                    tipo == typeof(int) ||
//                                    tipo == typeof(long) ||
//                                    tipo == typeof(decimal) ||
//                                    tipo == typeof(double) ||
//                                    tipo == typeof(float) ||
//                                    tipo == typeof(short) ||
//                                    tipo == typeof(byte) ||
//                                    tipo == typeof(DateTime) ||
//                                    tipo == typeof(TimeSpan);

//                                var estilo = new Style(typeof(DataGridCell));
//                                estilo.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, aDerecha ? TextAlignment.Right : TextAlignment.Left));
//                                column.CellStyle = estilo;
//                            }
//                        };

//                        var tabItem = new TabItem
//                        {
//                            Header = $"Resultado {i + 1} ({dt.Columns.Count} cols, {dt.Rows.Count} filas, {FormatoNumero(elapsedMicroseconds)} µs ({FormatoNumero(elapsedMicroseconds / 1000000)}) s)",
//                            Content = dataGrid
//                        };

//                        tcResults.Items.Add(tabItem);
//                        AppendMessage($"Consulta {i + 1} exitosa. {dt.Rows.Count} filas devueltas en {FormatoNumero(elapsedMicroseconds)} µs ({FormatoNumero(elapsedMicroseconds / 1000000)}) s");
//                    });
//                }

//                // 🔹 Al terminar todas, guardamos el bloque completo en el historial con todos los parámetros
//                AddToHistoryWithParams(sqlHistorial, parametrosTotales);

//                swTotal.Stop();
//                double totalElapsedMicroseconds = swTotal.ElapsedTicks * (1000000.0 / Stopwatch.Frequency);

//                txtColumnCount.Text = totalColumns.ToString();
//                txtRowCount.Text = totalRows.ToString();
//                txtTiempoDeEjecucion.Text = $"{FormatoNumero(totalElapsedMicroseconds)} µs ({FormatoNumero(totalElapsedMicroseconds / 1000000)}) s";

//                await Dispatcher.InvokeAsync(() =>
//                {
//                    AppendMessage($"Ejecución total finalizada. {validQueries.Count} consultas ejecutadas en {FormatoNumero(totalElapsedMicroseconds)} µs ({FormatoNumero(totalElapsedMicroseconds / 1000000)}) s");
//                    if (tcResults.Items.Count > 0)
//                        tcResults.SelectedIndex = 0;
//                });
//            }
//            catch (Exception ex)
//            {
//                await Dispatcher.InvokeAsync(() => AppendMessage("Error: " + ex.Message));
//            }
//        }

//        /// <summary>
//        /// Devuelve una porción de parámetros desde la posición actual.
//        /// Se usa como "pila" compartida entre varias consultas.
//        /// </summary>
//        private List<QueryParameter> ExtraerParametrosParaConsulta(string query, List<QueryParameter> parametrosTotales, ref int posicionActual)
//        {
//            List<QueryParameter> restantes = new List<QueryParameter>();
//            int cantidadParametros = query.Count(c => c == '?');
//            if (cantidadParametros > 0)
//            {
//                // Si no hay parámetros, devolvemos lista vacía
//                if (parametrosTotales == null || parametrosTotales.Count == 0)
//                    return new List<QueryParameter>();

//                // Si ya consumimos todos, devolvemos vacía
//                if (posicionActual >= parametrosTotales.Count)
//                    return new List<QueryParameter>();

//                // Por ahora, devolvemos todos los restantes (en caso de que no se sepa cuántos necesita cada consulta)
//                // Podrías ajustar esto si querés controlar cuántos consume cada query.
//                for (int i = posicionActual; i < posicionActual + cantidadParametros; i++)
//                {
//                    restantes.Add(parametrosTotales[i]);
//                }

//                // Avanzamos el puntero al final (simulando consumo total)
//                posicionActual += cantidadParametros;
//            }

//            return restantes;
//        }

//        private async void BtnExcel_Click(object sender, RoutedEventArgs e)
//        {
//            try
//            {
//                var hojas = new Dictionary<string, System.Data.DataTable>();

//                // 🔹 Recorre automáticamente todas las pestañas del TabControl
//                foreach (TabItem tab in tcResults.Items)
//                {
//                    var grid = tab.Content as DataGrid;
//                    if (grid?.ItemsSource == null)
//                        continue;

//                    // Convertir el DataGrid en DataTable
//                    var dt = ((System.Data.DataView)grid.ItemsSource).ToTable();

//                    // Nombre de la hoja = título del tab (sin caracteres inválidos)
//                    string nombreHoja = tab.Header.ToString();
//                    hojas[nombreHoja] = dt;
//                }

//                if (hojas.Count == 0)
//                {
//                    AppendMessage("No hay datos para exportar.");
//                    return;
//                }

//                // 🔹 Crear el Excel
//                var excelService = new ExcelService();
//                byte[] archivoExcel = excelService.CrearExcelMultiplesHojas(hojas);

//                // 🔹 Guardar en escritorio
//                System.Windows.Forms.FolderBrowserDialog fbdExcel = new System.Windows.Forms.FolderBrowserDialog();
//                fbdExcel.ShowDialog();
//                string ruta = Path.Combine(fbdExcel.SelectedPath, "ResultadosConsultas.xlsx");
//                excelService.GuardarArchivo(archivoExcel, ruta);

//                AppendMessage($"Excel generado correctamente en: {ruta}");
//                System.Diagnostics.Process.Start(ruta);
//            }
//            catch (Exception ex)
//            {
//                AppendMessage("Error al generar Excel: " + ex.Message);
//            }
//        }

//        private async void BtnLimpiarLog_Click(object sender, RoutedEventArgs e)
//        {
//            try
//            {
//                txtMessages.Text = string.Empty;
//            }
//            catch (Exception ex)
//            {
//            }
//        }

//        /// <summary>
//        /// Convierte un DataTable en una lista de diccionarios (nombreColumna -> valor)
//        /// </summary>
//        private List<Dictionary<string, object>> ConvertirDataTable(System.Data.DataTable dt)
//        {
//            var lista = new List<Dictionary<string, object>>();

//            foreach (System.Data.DataRow row in dt.Rows)
//            {
//                var dict = new Dictionary<string, object>();
//                foreach (System.Data.DataColumn col in dt.Columns)
//                {
//                    dict[col.ColumnName] = row[col] != DBNull.Value ? row[col] : "";
//                }
//                lista.Add(dict);
//            }

//            return lista;
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
//                            if (parametros != null)
//                            {
//                                foreach (var p in parametros)
//                                {
//                                    var name = p.Nombre.StartsWith("@") ? p.Nombre : "@" + p.Nombre;
//                                    var param = new OdbcParameter(name, p.Tipo);
//                                    param.Value = string.IsNullOrEmpty(p.Valor) ? DBNull.Value : (object)p.Valor;
//                                    cmd.Parameters.Add(param);
//                                }
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

//                txtTiempoDeEjecucion.Text = $"{FormatoNumero(elapsedMicroseconds)} µs ({FormatoNumero(elapsedMicroseconds / 1000000)}) s";

//                await Dispatcher.InvokeAsync(() =>
//                    AppendMessage($"Resultado del escalar: {(result?.ToString() ?? "(null) en {}")} en {FormatoNumero(elapsedMicroseconds)} µs ({FormatoNumero(elapsedMicroseconds / 1000000)}) s"));
//            }
//            catch (Exception ex)
//            {
//                await Dispatcher.InvokeAsync(() =>
//                    AppendMessage("Error: " + ex.Message));
//            }
//        }

//        private void BtnClear_Click(object sender, RoutedEventArgs e)
//        {
//            // CAMBIO: Limpiar el TabControl
//            tcResults.Items.Clear();
//            txtRowCount.Text = "0";
//            txtTiempoDeEjecucion.Text = "0";
//            AppendMessage("Resultados borrados.");
//        }

//        private string GetConnectionString()
//        {
//            string stringConnection = string.Empty;
//            if (conexionActual != null)
//            {
//                stringConnection = ConexionesManager.GetConnectionString(conexionActual.Motor, conexionActual.Servidor, conexionActual.BaseDatos, conexionActual.Usuario, conexionActual.Contrasena);
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

//        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
//        {
//            var parentObject = System.Windows.Media.VisualTreeHelper.GetParent(child);

//            if (parentObject == null)
//                return null;

//            if (parentObject is T parent)
//                return parent;

//            return FindVisualParent<T>(parentObject);
//        }

//        // ────────────────────────────────────────────────────────
//        // HISTORIAL: nuevo manejo con parámetros y asociación a conexión
//        // ────────────────────────────────────────────────────────

//        // Mantengo compatibilidad leyendo el archivo de texto (si estaba)
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

//        // Guarda en historial.xml una entrada con parámetros
//        private void AddToHistoryWithParams(string sqlCompleto, List<QueryParameter> parametros)
//        {
//            try
//            {
//                lstHistory.Items.Insert(0, sqlCompleto);
//                File.AppendAllText(HISTORY_FILE, sqlCompleto + Environment.NewLine + "---" + Environment.NewLine);

//                var all = LoadAllHistoriales();
//                var h = new Historial
//                {
//                    conexion = conexionActual != null ? new Conexion
//                    {
//                        Nombre = conexionActual.Nombre,
//                        Motor = conexionActual.Motor,
//                        Servidor = conexionActual.Servidor,
//                        BaseDatos = conexionActual.BaseDatos,
//                        Usuario = conexionActual.Usuario,
//                        Contrasena = conexionActual.Contrasena
//                    } : null,
//                    Consulta = sqlCompleto, // 🔹 todas las consultas en un solo string
//                    Parametros = new List<string[]>(),
//                    Fecha = DateTime.Now
//                };

//                if (parametros != null && parametros.Count > 0)
//                {
//                    foreach (var p in parametros)
//                    {
//                        string tipoStr = p.Tipo.ToString();
//                        h.Parametros.Add(new string[] { p.Nombre, tipoStr, p.Valor });
//                    }
//                }

//                all.Add(h);
//                SaveAllHistoriales(all);

//                if (conexionActual != null)
//                    LoadHistoryForConnection(conexionActual);
//            }
//            catch (Exception ex)
//            {
//                AppendMessage("No se pudo guardar el historial: " + ex.Message);
//            }
//        }

//        private List<Historial> LoadAllHistoriales()
//        {
//            try
//            {
//                if (!File.Exists(HISTORIAL_XML))
//                    return new List<Historial>();

//                using (var fs = new FileStream(HISTORIAL_XML, FileMode.Open))
//                {
//                    var serializer = new XmlSerializer(typeof(List<Historial>));
//                    var list = (List<Historial>)serializer.Deserialize(fs);
//                    return list ?? new List<Historial>();
//                }
//            }
//            catch
//            {
//                return new List<Historial>();
//            }
//        }

//        private void SaveAllHistoriales(List<Historial> list)
//        {
//            try
//            {
//                using (var fs = new FileStream(HISTORIAL_XML, FileMode.Create))
//                {
//                    var serializer = new XmlSerializer(typeof(List<Historial>));
//                    serializer.Serialize(fs, list);
//                }
//            }
//            catch (Exception ex)
//            {
//                AppendMessage("Error guardando historial XML: " + ex.Message);
//            }
//        }

//        // Carga en lstHistory *sólo* las consultas asociadas a la conexión dada (más recientes primero)
//        private void LoadHistoryForConnection(Conexion conexion)
//        {
//            try
//            {
//                lstHistory.Items.Clear();

//                if (conexion == null) return;

//                var all = LoadAllHistoriales();

//                // Filtramos por nombre exacto de conexión (podés adaptar a otra comparación si prefieres)
//                var relacionados = all
//                    .Where(h => h.conexion != null && h.conexion.Nombre == conexion.Nombre)
//                    .OrderByDescending(h => h.Fecha)
//                    .ToList();

//                if (relacionados.Count == 0)
//                {
//                    // Si no hay historial asociado, dejamos la lista vacía
//                    return;
//                }

//                // Inserto ListBoxItems con Tag = Historial para poder cargar parámetros luego
//                foreach (var h in relacionados)
//                {
//                    var item = new ListBoxItem
//                    {
//                        Content = $"{h.Consulta}    [{h.Fecha:yyyy-MM-dd HH:mm:ss}]",
//                        Tag = h
//                    };
//                    lstHistory.Items.Add(item);
//                }
//            }
//            catch (Exception ex)
//            {
//                AppendMessage("Error cargando historial para la conexión: " + ex.Message);
//            }
//        }

//        // Cuando seleccionan un elemento en el historial, carga consulta y parámetros (si existen)
//        private void LstHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
//        {
//            if (lstHistory.SelectedItem == null)
//                return;

//            try
//            {
//                if (lstHistory.SelectedItem is ListBoxItem lbi && lbi.Tag is Historial hist)
//                {
//                    txtQuery.Text = hist.Consulta ?? string.Empty;

//                    SincronizarParametros();

//                    //var nuevos = new List<QueryParameter>();
//                    //if (hist.Parametros != null)
//                    //{
//                    //    foreach (var p in hist.Parametros)
//                    //    {
//                    //        string nombre = p.Length > 0 ? p[0] : string.Empty;
//                    //        string tipoStr = p.Length > 1 ? p[1] : string.Empty;
//                    //        string valor = p.Length > 2 ? p[2] : string.Empty;

//                    //        OdbcType tipoEnum = OdbcType.VarChar;
//                    //        if (!string.IsNullOrWhiteSpace(tipoStr))
//                    //        {
//                    //            if (!Enum.TryParse(tipoStr, out tipoEnum))
//                    //            {
//                    //                try { tipoEnum = (OdbcType)Enum.Parse(typeof(OdbcType), tipoStr, true); }
//                    //                catch { tipoEnum = OdbcType.VarChar; }
//                    //            }
//                    //        }

//                    //        nuevos.Add(new QueryParameter
//                    //        {
//                    //            Nombre = nombre,
//                    //            Tipo = tipoEnum,
//                    //            Valor = valor
//                    //        });
//                    //    }
//                    //}

//                    //Parametros = nuevos;
//                    //gridParams.ItemsSource = Parametros;
//                }
//                else
//                {
//                    if (lstHistory.SelectedItem is string s)
//                        txtQuery.Text = s;
//                    else if (lstHistory.SelectedItem is ListBoxItem li && li.Content is string cs)
//                        txtQuery.Text = cs;
//                }
//            }
//            catch (Exception ex)
//            {
//                AppendMessage("Error al cargar selección del historial: " + ex.Message);
//            }
//        }

//        // ────────────────────────────────────────────────────────
//        // Resto del código (Explorador, botones, etc.) sin cambios estructurales
//        // ────────────────────────────────────────────────────────

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
//            DatosConexion datosConexion = new DatosConexion(conexionActual);
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

//        private string FormatoNumero(double numero)
//        {
//            string formato = "N";
//            return numero.ToString(formato, System.Globalization.CultureInfo.InvariantCulture);
//        }

//        private async void CargarEsquema(string filtrado = "", List<string> tablasConsulta = null, CancellationToken token = default(CancellationToken))
//        {
//            TreeView tvCargar = filtrado.Length == 0 ? tvSchema : tvSearch;
//            if (conexionActual == null)
//            {
//                AppendMessage("No hay conexión seleccionada.");
//                return;
//            }

//            string connStr = GetConnectionString();

//            // 🎨 INICIO DE MODIFICACIÓN: Definición de colores de fondo alternados
//            //var evenRowBrush = System.Windows.Media.Brushes.Cornsilk;
//            //var oddRowColor = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#A7D7F0");
//            //var oddRowBrush = new System.Windows.Media.SolidColorBrush(oddRowColor);
//            // 🎨 FIN DE MODIFICACIÓN

//            // 🖼️ INICIO DE MODIFICACIÓN: Cargar íconos
//            var tablaIconUri = new Uri("pack://application:,,,/Assets/tabla.png");
//            var columnaIconUri = new Uri("pack://application:,,,/Assets/columna.png");
//            var columnaClaveIconUri = new Uri("pack://application:,,,/Assets/columnaClave.png");
//            var claveIconUri = new Uri("pack://application:,,,/Assets/clave.png");

//            var tablaIcon = new System.Windows.Media.Imaging.BitmapImage(tablaIconUri);
//            var columnaIcon = new System.Windows.Media.Imaging.BitmapImage(columnaIconUri);
//            var columnaClaveIcon = new System.Windows.Media.Imaging.BitmapImage(columnaClaveIconUri);
//            var claveIcon = new System.Windows.Media.Imaging.BitmapImage(claveIconUri);
//            // 🖼️ FIN DE MODIFICACIÓN

//            await Task.Run(() =>
//            {
//                try
//                {
//                    using (var conn = new OdbcConnection(connStr))
//                    {
//                        conn.Open();

//                        // Obtiene las tablas
//                        DataTable tablas = conn.GetSchema("Tables");

//                        Dispatcher.Invoke(() => tvCargar.Items.Clear());

//                        // Filtrar SOLO las filas cuyo tipo sea "TABLE"
//                        DataRow[] tablasFiltradas = tablas.Select($"TABLE_TYPE = 'TABLE'");

//                        // Si querés seguir usando un DataTable:
//                        DataTable tablasSolo = tablasFiltradas.Length > 0 ? tablasFiltradas.CopyToDataTable() : tablas.Clone();

//                        bool cargarTabla = true;
//                        int tablasLeidas = 0;
//                        int cantidadDeTablas = tablasConsulta == null ? tablasSolo.Rows.Count : tablasConsulta.Count;

//                        // Ahora usás tablasSolo.Rows en el foreach
//                        foreach (DataRow tabla in tablasSolo.Rows)
//                        {
//                            token.ThrowIfCancellationRequested();

//                            string schema = tabla["TABLE_SCHEM"].ToString();
//                            string nombreTabla = tabla["TABLE_NAME"].ToString();

//                            cargarTabla = tablasConsulta == null || (tablasConsulta != null &&
//                                tablasConsulta.Any(t => t.ToUpper().Trim().EndsWith(nombreTabla.ToUpper().Trim())));
//                            if (cargarTabla)
//                            {
//                                string tipo = tabla["TABLE_TYPE"].ToString();
//                                if (tipo != "TABLE") continue;

//                                string headerText = string.IsNullOrEmpty(schema) ? nombreTabla : $"{schema}.{nombreTabla}";
//                                var columnas = conn.GetSchema("Columns", new string[] { null, schema, nombreTabla });

//                                // 🎨 INICIO DE MODIFICACIÓN: Cálculo del fondo alternado
//                                //System.Windows.Media.Brush currentTableBackground = (tablasLeidas % 2 == 0) ? evenRowBrush : oddRowBrush;
//                                // 🎨 FIN DE MODIFICACIÓN

//                                Dispatcher.Invoke(() =>
//                                {
//                                    // 🖼️ INICIO DE MODIFICACIÓN: nodo de tabla con icono
//                                    var tablaHeader = new StackPanel { Orientation = Orientation.Horizontal };
//                                    tablaHeader.Children.Add(new System.Windows.Controls.Image
//                                    {
//                                        Source = tablaIcon,
//                                        Width = 16,
//                                        Height = 16,
//                                        Margin = new System.Windows.Thickness(0, 0, 5, 0)
//                                    });
//                                    tablaHeader.Children.Add(new System.Windows.Controls.TextBlock { Text = headerText });

//                                    var tablaNode = new TreeViewItem
//                                    {
//                                        Header = tablaHeader,
//                                        Tag = nombreTabla,
//                                        //Background = currentTableBackground
//                                    };
//                                    // 🖼️ FIN DE MODIFICACIÓN

//                                    if (filtrado.Length == 0 || (filtrado.Length > 0 && nombreTabla.ToUpper().Contains(filtrado.ToUpper())))
//                                    {
//                                        tvCargar.Items.Add(tablaNode);
//                                        tablaNode.MouseDoubleClick += TablaNode_MouseDoubleClick;
//                                    }

//                                    // Agregar columnas
//                                    foreach (DataRow col in columnas.Rows)
//                                    {
//                                        token.ThrowIfCancellationRequested();

//                                        string colName = col["COLUMN_NAME"].ToString();
//                                        string tipoCol = col["TYPE_NAME"].ToString();
//                                        string longitud = col["COLUMN_SIZE"].ToString();

//                                        string escala = string.Empty;
//                                        if (col.Table.Columns.Contains("NUMERIC_SCALE") && col["NUMERIC_SCALE"] != DBNull.Value)
//                                            escala = col["NUMERIC_SCALE"].ToString();
//                                        else if (col.Table.Columns.Contains("COLUMN_SCALE") && col["COLUMN_SCALE"] != DBNull.Value)
//                                            escala = col["COLUMN_SCALE"].ToString();
//                                        else if (col.Table.Columns.Contains("COLUMN_SIZE") && col["COLUMN_SIZE"] != DBNull.Value)
//                                            escala = col["COLUMN_SIZE"].ToString();

//                                        string aceptaNulos = string.Empty;
//                                        if (col.Table.Columns.Contains("IS_NULLABLE") && col["IS_NULLABLE"] != DBNull.Value)
//                                        {
//                                            string nuloStr = col["IS_NULLABLE"].ToString().ToUpper();
//                                            aceptaNulos = nuloStr == "YES" ? "NULL" : nuloStr == "NO" ? "NOT NULL" : string.Empty;
//                                        }

//                                        string defecto = string.Empty;
//                                        if (col.Table.Columns.Contains("COLUMN_DEF") && col["COLUMN_DEF"] != DBNull.Value)
//                                            defecto = col["COLUMN_DEF"].ToString();

//                                        string tipoCompleto = tipoCol;
//                                        string tipoNormalizado = tipoCol.ToUpper();
//                                        bool esNumericoDecimal = tipoNormalizado.Contains("DECIMAL") || tipoNormalizado.Contains("NUMERIC");

//                                        if (!string.IsNullOrEmpty(longitud))
//                                        {
//                                            if (esNumericoDecimal && !string.IsNullOrEmpty(escala))
//                                                tipoCompleto += $" [{longitud}, {escala}]";
//                                            else
//                                                tipoCompleto += $" [{longitud}]";
//                                        }

//                                        // 🖼️ INICIO DE MODIFICACIÓN: nodo de columna con icono
//                                        var colHeader = new StackPanel { Orientation = Orientation.Horizontal };
//                                        colHeader.Children.Add(new System.Windows.Controls.Image
//                                        {
//                                            Source = columnaIcon,
//                                            Width = 14,
//                                            Height = 14,
//                                            Margin = new System.Windows.Thickness(0, 0, 5, 0)
//                                        });
//                                        colHeader.Children.Add(new System.Windows.Controls.TextBlock
//                                        {
//                                            Text = $"{colName} ({tipoCompleto}{(string.IsNullOrEmpty(aceptaNulos) ? string.Empty : $", {aceptaNulos}")}{(string.IsNullOrEmpty(defecto) ? string.Empty : $", DEFAULT {defecto}")})"
//                                        });

//                                        var colNode = new TreeViewItem { Header = colHeader };
//                                        // 🖼️ FIN DE MODIFICACIÓN

//                                        tablaNode.Items.Add(colNode);
//                                    }
//                                });

//                                // 🔹 Carga de índices (sin UI pesada)
//                                try
//                                {
//                                    using (var cmd = conn.CreateCommand())
//                                    {
//                                        switch (conexionActual.Motor)
//                                        {
//                                            case TipoMotor.MS_SQL:
//                                                cmd.CommandText = $@"SELECT
//                                            s.name AS SchemaName,
//                                            t.name AS TableName,
//                                            i.name AS IndexName,
//                                            i.type_desc AS IndexType,
//                                            c.name AS ColumnName,
//                                            ic.key_ordinal AS ColumnOrder,
//                                            i.is_primary_key AS IsPrimaryKey,
//                                            i.is_unique AS IsUnique
//                                        FROM sys.indexes i
//                                        INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
//                                        INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
//                                        INNER JOIN sys.tables t ON i.object_id = t.object_id
//                                        INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
//                                        WHERE t.name = '{nombreTabla}'
//                                        ORDER BY i.name, ic.key_ordinal;";
//                                                break;
//                                            case TipoMotor.DB2:
//                                                cmd.CommandText = $@"SELECT
//                                            i.TABSCHEMA AS SchemaName,
//                                            i.TABNAME AS TableName,
//                                            i.INDNAME AS IndexName,
//                                            i.UNIQUERULE AS UniqueRule,
//                                            c.COLNAME AS ColumnName,
//                                            c.COLSEQ AS ColumnOrder,
//                                            i.INDEXTYPE AS IndexType
//                                        FROM SYSCAT.INDEXES i
//                                        JOIN SYSCAT.INDEXCOLUSE c
//                                            ON i.INDNAME = c.INDNAME AND i.INDSCHEMA = c.INDSCHEMA
//                                        WHERE i.TABNAME = UPPER('{nombreTabla}')
//                                        ORDER BY i.INDNAME, c.COLSEQ;";
//                                                break;
//                                            case TipoMotor.POSTGRES:
//                                                cmd.CommandText = $@"SELECT
//                                            n.nspname AS SchemaName,
//                                            t.relname AS TableName,
//                                            i.relname AS IndexName,
//                                            a.attname AS ColumnName,
//                                            ix.indisunique AS IsUnique,
//                                            ix.indisprimary AS IsPrimary
//                                        FROM pg_class t
//                                        JOIN pg_index ix ON t.oid = ix.indrelid
//                                        JOIN pg_class i ON i.oid = ix.indexrelid
//                                        JOIN pg_namespace n ON n.oid = t.relnamespace
//                                        JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey)
//                                        WHERE t.relname = '{nombreTabla}'
//                                        ORDER BY i.relname, a.attnum;";
//                                                break;
//                                            case TipoMotor.SQLite:
//                                                cmd.CommandText = $"PRAGMA index_list('{nombreTabla}');";
//                                                break;
//                                            default:
//                                                break;
//                                        }

//                                        using (var adapter = new OdbcDataAdapter(cmd))
//                                        {
//                                            var dtIndices = new DataTable();
//                                            adapter.Fill(dtIndices);

//                                            if (dtIndices.Rows.Count > 0)
//                                            {
//                                                Dispatcher.Invoke(() =>
//                                                {
//                                                    var tablaNode = tvCargar.Items.OfType<TreeViewItem>()
//                                                        .FirstOrDefault(t => (string)t.Tag == nombreTabla);
//                                                    if (tablaNode == null) return;

//                                                    var indiceRaiz = new TreeViewItem
//                                                    {
//                                                        Header = "Índices",
//                                                    };

//                                                    string indexNameColumn = conexionActual.Motor == TipoMotor.SQLite ? "NAME" : "INDEXNAME";

//                                                    var indicesAgrupados = dtIndices.AsEnumerable()
//                                                        .GroupBy(row => row.Field<string>(indexNameColumn))
//                                                        .OrderBy(g => g.Key);

//                                                    foreach (var grupoIndice in indicesAgrupados)
//                                                    {
//                                                        string nombreIndice = grupoIndice.Key;

//                                                        // 🖼️ INICIO DE MODIFICACIÓN: nodo de índice con icono
//                                                        var indiceHeader = new StackPanel { Orientation = Orientation.Horizontal };
//                                                        indiceHeader.Children.Add(new System.Windows.Controls.Image
//                                                        {
//                                                            Source = claveIcon,
//                                                            Width = 14,
//                                                            Height = 14,
//                                                            Margin = new System.Windows.Thickness(0, 0, 5, 0)
//                                                        });
//                                                        indiceHeader.Children.Add(new System.Windows.Controls.TextBlock { Text = nombreIndice });

//                                                        var nodoIndice = new TreeViewItem { Header = indiceHeader };
//                                                        // 🖼️ FIN DE MODIFICACIÓN

//                                                        indiceRaiz.Items.Add(nodoIndice);
//                                                    }

//                                                    tablaNode.Items.Add(indiceRaiz);
//                                                });
//                                            }
//                                        }
//                                    }
//                                }
//                                catch { /* Algunos motores no exponen esa vista */ }

//                                tablasLeidas++;
//                            }

//                            Dispatcher.Invoke(() => txtExplorar.Text = $"{tablasLeidas} tablas leídas de {cantidadDeTablas}");
//                            if (tablasLeidas == cantidadDeTablas) break;
//                        }
//                    }
//                }
//                catch (TaskCanceledException)
//                {
//                    Dispatcher.Invoke(() => AppendMessage("Tarea de exploración cancelada."));
//                    return;
//                }
//                catch (OperationCanceledException)
//                {
//                    Dispatcher.Invoke(() => AppendMessage("Exploración cancelada."));
//                    return;
//                }
//                catch (Exception ex)
//                {
//                    Dispatcher.Invoke(() => AppendMessage("Error al cargar esquema: " + ex.Message));
//                }
//            });
//        }

//        private void TablaNode_MouseDoubleClick(object sender, MouseButtonEventArgs e)
//        {
//            string tableName = (sender as TreeViewItem).Header.ToString();
//            txtQuery.Text.Insert(txtQuery.SelectionStart, tableName);
//        }

//        private void tvSchema_MouseDoubleClick(object sender, MouseButtonEventArgs e)
//        {
//            if ((sender as TreeView).SelectedItem is TreeViewItem item)
//            {
//                string texto = ObtenerNombrePuro(item);

//                if (!string.IsNullOrEmpty(texto))
//                    InsertarEnQuery(texto);
//            }
//        }

//        private string ObtenerNombrePuro(TreeViewItem item)
//        {
//            string texto = ExtraerTextoDesdeHeader(item);

//            if (string.IsNullOrEmpty(texto))
//                return null;

//            // Si es tabla, no tiene paréntesis → va completo
//            if (!texto.Contains("("))
//                return texto.Trim();

//            // Si es columna → tomar solo lo previo al '('
//            return texto.Split('(')[0].Trim();
//        }

//        private string ExtraerTextoDesdeHeader(TreeViewItem item)
//        {
//            if (item.Header is string s)
//                return s;

//            if (item.Header is StackPanel sp)
//            {
//                foreach (var child in sp.Children)
//                {
//                    if (child is TextBlock tb)
//                        return tb.Text;
//                }
//            }

//            return null;
//        }

//        private void InsertarEnQuery(string texto)
//        {
//            if (string.IsNullOrEmpty(texto)) return;

//            // AvalonEdit usa CaretOffset y SelectionLength
//            int length = txtQuery.SelectionLength;
//            int offset = txtQuery.CaretOffset - length;

//            // Reemplaza el texto seleccionado o inserta en la posición del cursor
//            try
//            {
//                txtQuery.Document.Replace(offset, length, texto);
//            }
//            catch (Exception err)
//            {
//                MessageBox.Show(err.Message);
//            }

//            // Reposicionar el cursor al final de lo insertado y dar foco
//            txtQuery.CaretOffset = offset + texto.Length;
//            txtQuery.Focus();
//        }

//        private void btnExplorar_Click(object sender, RoutedEventArgs e)
//        {
//            // Si había un proceso anterior, se cancela
//            _explorarCTS?.Cancel();

//            // Se crea uno nuevo
//            _explorarCTS = new CancellationTokenSource();

//            CargarEsquema(string.Empty, null, _explorarCTS.Token);
//        }

//        private void btnExplorarConsultas_Click(object sender, RoutedEventArgs e)
//        {
//            // Cancelar si el otro sigue corriendo
//            _explorarCTS?.Cancel();

//            _explorarCTS = new CancellationTokenSource();

//            List<string> tablasConsulta = ExtraerTablas(txtQuery.Text);
//            CargarEsquema(string.Empty, tablasConsulta, _explorarCTS.Token);
//        }

//        /// <summary>
//        /// Extrae los nombres de tablas de una o varias consultas SQL.
//        /// Compatible con MSSQL, DB2, PostgreSQL y SQLite.
//        /// </summary>
//        /// <param name="sqlText">Texto SQL completo (puede contener varias consultas).</param>
//        /// <returns>Lista de nombres de tablas en mayúsculas, sin repetir y en orden de aparición.</returns>
//        public List<string> ExtraerTablas(string sqlText)
//        {
//            if (string.IsNullOrWhiteSpace(sqlText))
//                return new List<string>();

//            // Normalizamos saltos de línea y espacios
//            string sql = Regex.Replace(sqlText, @"[\r\n]+", " ");
//            sql = Regex.Replace(sql, @"\s+", " ");

//            // Expresiones regulares para capturar nombres de tablas en distintos contextos SQL
//            var patrones = new List<string>
//        {
//            // SELECT ... FROM table
//            @"\bFROM\s+([A-Z0-9_.\""\[\]]+)",
//            // JOIN table
//            @"\bJOIN\s+([A-Z0-9_.\""\[\]]+)",
//            // UPDATE table
//            @"\bUPDATE\s+([A-Z0-9_.\""\[\]]+)",
//            // INSERT INTO table
//            @"\bINTO\s+([A-Z0-9_.\""\[\]]+)",
//            // DELETE FROM table
//            @"\bDELETE\s+FROM\s+([A-Z0-9_.\""\[\]]+)",
//            // MERGE INTO table
//            @"\bMERGE\s+INTO\s+([A-Z0-9_.\""\[\]]+)"
//        };

//            var tablas = new List<string>();
//            foreach (string patron in patrones)
//            {
//                foreach (Match match in Regex.Matches(sql.ToUpperInvariant(), patron))
//                {
//                    string nombre = LimpiarNombreTabla(match.Groups[1].Value);
//                    if (!string.IsNullOrEmpty(nombre) && !tablas.Contains(nombre))
//                    {
//                        tablas.Add(nombre);
//                    }
//                }
//            }

//            return tablas;
//        }

//        /// <summary>
//        /// Limpia el nombre de la tabla (quita alias, comillas, corchetes, etc.)
//        /// </summary>
//        private string LimpiarNombreTabla(string nombre)
//        {
//            // Elimina alias o terminaciones tipo: "AS x", "x" luego de espacio
//            nombre = nombre.Trim();
//            nombre = Regex.Replace(nombre, @"[\[\]\""]", ""); // quita [ ] o "
//            nombre = Regex.Replace(nombre, @"\s+AS\s+\w+", "", RegexOptions.IgnoreCase);
//            nombre = Regex.Replace(nombre, @"\s+\w+$", ""); // elimina alias suelto
//            nombre = Regex.Replace(nombre, @"[,;)]$", ""); // quita coma, punto y coma o paréntesis final
//            return nombre.Trim().ToUpperInvariant();
//        }

//        private bool isCollapsed = false;
//        private double expandedWidth = 0;
//        private double collapsedWidth = 0;

//        private void btnExpandirColapsar_Click(object sender, RoutedEventArgs e)
//        {
//            ExpandirColapasar();
//        }

//        private void ExpandirColapasar()
//        {
//            if (!isCollapsed)
//            {
//                expandedWidth = grdExplorador.ActualWidth;
//            }
//            var colDef = ((Grid)grdExplorador.Parent).ColumnDefinitions[0]; // solo la columna del TreeView

//            double from = colDef.ActualWidth;
//            double to = isCollapsed ? expandedWidth : collapsedWidth;

//            var anim = new GridLengthAnimation
//            {
//                From = new GridLength(from, GridUnitType.Pixel),
//                To = new GridLength(to, GridUnitType.Pixel),
//                Duration = new Duration(TimeSpan.FromMilliseconds(250)),
//                FillBehavior = FillBehavior.Stop
//            };

//            anim.Completed += (s, _) =>
//            {
//                colDef.Width = new GridLength(to, GridUnitType.Pixel);
//                isCollapsed = !isCollapsed;
//                btnExpandirColapsar.Content = isCollapsed ? ">>" : "<<";
//            };

//            colDef.BeginAnimation(ColumnDefinition.WidthProperty, anim);
//        }

//        //private void txtQuery_KeyUp(object sender, KeyEventArgs e)
//        //{
//        //    // Detecta el signo de interrogación (Shift + /)
//        //    if (e.Key == Key.Oem4 && Keyboard.Modifiers == ModifierKeys.Shift)
//        //    {
//        //        AgregarParametro();
//        //    }
//        //    if (e.Key == Key.Back || e.Key == Key.Delete)
//        //    {
//        //        EliminarParametro();
//        //    }
//        //}

//        private void txtQuery_KeyUp(object sender, KeyEventArgs e)
//        {
//            // Detecta: '?', Backspace, Delete o Pegar (Ctrl+V)
//            if (e.Key == Key.Oem4 || e.Key == Key.Back || e.Key == Key.Delete ||
//               (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control))
//            {
//                SincronizarParametros();
//            }
//        }

//        private void SincronizarParametros()
//        {
//            string textoActual = txtQuery.Text;
//            var matches = Regex.Matches(textoActual, @"\?");
//            var nuevaLista = new List<QueryParameter>();

//            for (int i = 0; i < matches.Count; i++)
//            {
//                string valorExistente = (i < Parametros.Count) ? Parametros[i].Valor : "";

//                // Esta llamada ahora es mucho más potente porque consulta la DB
//                ContextoParametro info = ObtenerContextoDeParametro(textoActual, matches[i].Index);

//                nuevaLista.Add(new QueryParameter
//                {
//                    Nombre = info.Nombre,
//                    Tipo = info.Tipo,
//                    Valor = valorExistente
//                });
//            }

//            Parametros.Clear();
//            foreach (var p in nuevaLista) Parametros.Add(p);
//            gridParams.Items.Refresh();
//        }

//        public class ContextoParametro
//        {
//            public string Nombre { get; set; }
//            public OdbcType Tipo { get; set; }
//        }

//        private OdbcType ObtenerTipoRealDesdeDB(string nombreColumna, string queryCompleta)
//        {
//            if (conexionActual == null) return OdbcType.VarChar;

//            try
//            {
//                // 1. Intentamos extraer el nombre de la tabla de la consulta (lógica simple)
//                string tabla = "TABLA_DESCONOCIDA";
//                var matchTabla = Regex.Match(queryCompleta, @"FROM\s+([^\s\s,;]+)", RegexOptions.IgnoreCase);
//                if (matchTabla.Success) tabla = matchTabla.Groups[1].Value;

//                using (OdbcConnection conn = new OdbcConnection(ConexionesManager.GetConnectionString(conexionActual)))
//                {
//                    conn.Open();
//                    // 2. Pedimos solo el esquema de la tabla para no traer datos
//                    string schemaQuery = string.Format("SELECT {0} FROM {1} WHERE 1=0", nombreColumna, tabla);
//                    using (OdbcCommand cmd = new OdbcCommand(schemaQuery, conn))
//                    {
//                        using (OdbcDataReader reader = cmd.ExecuteReader(CommandBehavior.SchemaOnly))
//                        {
//                            DataTable schemaTable = reader.GetSchemaTable();
//                            if (schemaTable != null && schemaTable.Rows.Count > 0)
//                            {
//                                // 3. Mapeamos el tipo de .NET al OdbcType
//                                Type type = (Type)schemaTable.Rows[0]["DataType"];
//                                return MapearTipoADotNet(type);
//                            }
//                        }
//                    }
//                }
//            }
//            catch { /* Si falla la consulta de metadatos, cae al default */ }

//            return OdbcType.VarChar;
//        }

//        private OdbcType MapearTipoADotNet(Type t)
//        {
//            return Mapeo[t.Name.ToUpper()];
//        }

//        public Dictionary<string, OdbcType> Mapeo = new Dictionary<string, OdbcType>
//        {
//            { "BOOL",       OdbcType.Bit},            //"OdbcType.Smallint"}, 	// DB2 no tiene BOOLEAN real en versiones antiguas
//            { "BYTE",       OdbcType.TinyInt},        // SMALLINT usado como byte en DB2
//            { "BYTE[]",     OdbcType.VarBinary},	    // BLOB, VARBINARY, BYTEA
//            { "CHAR",       OdbcType.Char},           // CHAR(1)
//            { "CHAR[]",     OdbcType.VarBinary},	    // BLOB, VARBINARY, BYTEA
//            { "DATETIME",   OdbcType.DateTime},	    // TIMESTAMP (Date si sólo fecha)
//            { "DECIMAL",    OdbcType.Numeric },       // DECIMAL(p,s), NUMERIC
//            { "DOUBLE",     OdbcType.Double},	        // DOUBLE
//            { "FLOAT",      OdbcType.Real }, 	        // REAL
//            { "GUID",       OdbcType.Char},           // DB2 no tiene UNIQUEIDENTIFIER → usar CHAR(36)
//            { "INT",        OdbcType.Int},            // INTEGER
//            { "INT16",      OdbcType.SmallInt },      // SMALLINT	
//            { "INT64",      OdbcType.BigInt },	    // BIGINT
//            { "LONG",       OdbcType.BigInt },	    // BIGINT
//            { "SBYTE",      OdbcType.Double},	        // DOUBLE
//            { "SHORT",      OdbcType.SmallInt },      // SMALLINT	
//            { "SINGLE",     OdbcType.Double},	        // DOUBLE
//            { "STRING",     OdbcType.VarChar},	    // VARCHAR, usar NVarChar si es Unicode	
//            { "TIMESPAN",   OdbcType.Time},	        // TIME
//            { "UINT",       OdbcType.BigInt},          // BIGINT
//            { "ULONG",      OdbcType.BigInt},          // BIGINT
//            { "USHORT",     OdbcType.BigInt}          // BIGINT
//        };

//        private ContextoParametro ObtenerContextoDeParametro(string texto, int posicion)
//        {
//            int inicio = Math.Max(0, posicion - 60);
//            string contexto = texto.Substring(inicio, posicion - inicio).ToUpper();
//            var palabras = contexto.Split(new[] { ' ', '\r', '\n', '\t', '=', '>', '<' }, StringSplitOptions.RemoveEmptyEntries);

//            string campoReal = "param";
//            if (palabras.Length > 0)
//            {
//                string ultimaPalabra = palabras.Last();
//                campoReal = ultimaPalabra;
//                if (ultimaPalabra == "AND" || ultimaPalabra == "BETWEEN")
//                {
//                    int idx = Array.LastIndexOf(palabras, "BETWEEN");
//                    if (idx > 0) campoReal = palabras[idx - 1];
//                }
//                if (campoReal.Contains(".")) campoReal = campoReal.Split('.').Last();
//                campoReal = Regex.Replace(campoReal, @"[\[\]\""]", "");
//            }

//            // --- LLAMADA A LA DB PARA TIPO REAL ---
//            OdbcType tipoSugerido = ObtenerTipoRealDesdeDB(campoReal, texto);

//            // --- CONSTRUCCIÓN DEL NOMBRE ---
//            string sufijo = "";
//            if (contexto.TrimEnd().EndsWith("BETWEEN")) sufijo = "_DESDE";
//            else if (contexto.TrimEnd().EndsWith("AND") && contexto.Contains("BETWEEN")) sufijo = "_HASTA";

//            return new ContextoParametro
//            {
//                Nombre = "@" + campoReal + sufijo,
//                Tipo = tipoSugerido
//            };
//        }

//        private void AgregarParametro()
//        {
//            try
//            {
//                // Posición actual del cursor
//                int caretIndex = txtQuery.CaretOffset;
//                if (caretIndex > 0)
//                {
//                    caretIndex -= 1;
//                }
//                // Texto completo hasta el cursor
//                string textoHastaCursor = txtQuery.Text.Substring(0, caretIndex);
//                int parametrosAntes = CantidadInterrogacionesAntes(textoHastaCursor, caretIndex);

//                List<string> palabras = textoHastaCursor.Split(new string[] { " " }, StringSplitOptions.RemoveEmptyEntries).ToList();
//                int i = palabras.Count - 1;
//                bool esBetween = false;
//                bool esAndDelBetween = false;
//                if (palabras[i].ToUpper() == "BETWEEN")
//                {
//                    esBetween = true;
//                }
//                if (palabras[i].ToUpper() == "AND")
//                {
//                    i -= 2;
//                    esAndDelBetween = true;
//                }
//                i--;

//                if (i >= 0)
//                {
//                    string campo = palabras[i];
//                    if (campo.Contains("\n"))
//                    {
//                        campo = campo.Split('\n')[1];
//                    }

//                    // Si viene con alias, nos quedamos solo con el nombre (después del punto)
//                    if (campo.Contains("."))
//                        campo = campo.Split('.').Last();

//                    campo = campo.Trim();

//                    if (!string.IsNullOrEmpty(campo))
//                    {
//                        string nombreParametro = $"@{campo.ToUpper()}" + (esBetween ? "_DESDE" : esAndDelBetween ? "_HASTA" : string.Empty);

//                        // Evita duplicados
//                        i = 0;
//                        while (i <= parametrosAntes && Parametros.Any(p => p.Nombre.Equals(nombreParametro, StringComparison.OrdinalIgnoreCase)))
//                        {
//                            nombreParametro = $"{nombreParametro}{i}";
//                            i++;
//                        }
//                        Parametros.Insert(parametrosAntes, new QueryParameter
//                        {
//                            Nombre = nombreParametro,
//                            Tipo = OdbcType.VarChar,
//                            Valor = ""
//                        });

//                        gridParams.Items.Refresh();

//                        AppendMessage($"Parámetro agregado automáticamente: {nombreParametro}");
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                AppendMessage("Error al analizar parámetros: " + ex.Message);
//            }
//        }

//        private void EliminarParametro()
//        {
//            try
//            {
//                // Comparo la cantidad de parametros en la consulta y en la grilla, si hay menos en la consulta busco el eliminado y lo quito de la grilla de parametros
//                int parametrosEnQuery = txtQuery.Text.Count(c => c == '?');
//                if ((gridParams.Items.Count - 1) >= parametrosEnQuery)
//                {
//                    // Posición actual del cursor
//                    int caretIndex = txtQuery.CaretOffset;
//                    if (caretIndex > 0)
//                    {
//                        caretIndex -= 1;
//                    }
//                    // Texto completo hasta el cursor
//                    string textoHastaCursor = txtQuery.Text.Substring(0, caretIndex);
//                    int parametrosAntes = CantidadInterrogacionesAntes(textoHastaCursor, caretIndex);

//                    if (caretIndex > 0)
//                    {
//                        string nombreParametro = Parametros[parametrosAntes].Nombre;
//                        Parametros.RemoveAt(parametrosAntes);

//                        gridParams.Items.Refresh();

//                        AppendMessage($"Se eliminó el parámetro: {nombreParametro}");
//                    }
//                }
//            }
//            catch (Exception ex)
//            {
//                AppendMessage("Error al analizar parámetros: " + ex.Message);
//            }
//        }

//        int CantidadInterrogacionesAntes(string texto, int posicion)
//        {
//            if (string.IsNullOrEmpty(texto) || posicion <= 0)
//                return 0;

//            if (posicion > texto.Length)
//                posicion = texto.Length;

//            // Tomamos solo la parte anterior a la posición indicada
//            string anterior = texto.Substring(0, posicion);

//            // Contamos los signos de interrogación
//            return anterior.Count(c => c == '?');
//        }

//        private void lstHistory_KeyDown(object sender, KeyEventArgs e)
//        {
//            // 1. Verificar si la tecla presionada es Delete (Suprimir)
//            if (e.Key == Key.Delete)
//            {
//                // 2. Obtener el elemento seleccionado
//                if (lstHistory.SelectedItem is ListBoxItem selectedItem && selectedItem.Tag is Historial histParaEliminar)
//                {
//                    try
//                    {
//                        // 3. Cargar todos los historiales del XML
//                        var todosLosHistoriales = LoadAllHistoriales();

//                        // 4. Buscar y remover el historial coincidente (por fecha o consulta)
//                        // Usamos la fecha como identificador único en este caso
//                        var itemEnLista = todosLosHistoriales.FirstOrDefault(h => h.Fecha == histParaEliminar.Fecha && h.Consulta == histParaEliminar.Consulta);

//                        if (itemEnLista != null)
//                        {
//                            todosLosHistoriales.Remove(itemEnLista);

//                            // 5. Guardar la lista actualizada en el XML
//                            SaveAllHistoriales(todosLosHistoriales);

//                            // 6. Refrescar la UI cargando solo el historial de la conexión actual
//                            if (conexionActual != null)
//                            {
//                                LoadHistoryForConnection(conexionActual);
//                                AppendMessage("Elemento eliminado del historial.");
//                            }
//                        }
//                    }
//                    catch (Exception ex)
//                    {
//                        AppendMessage("Error al eliminar del historial: " + ex.Message);
//                    }
//                }
//            }
//        }

//        private void btnBuscar_Click(object sender, RoutedEventArgs e)
//        {
//            string filtro = txtBuscar.Text.Trim().ToLower();

//            if (string.IsNullOrEmpty(filtro))
//            {
//                // Si está vacío, volvemos al original
//                tvSearch.Visibility = Visibility.Collapsed;
//                tvSchema.Visibility = Visibility.Visible;
//            }
//            else
//            {
//                // Si hay texto, filtramos
//                //FiltrarArbol(filtro);
//                CargarEsquema(filtro, null, _explorarCTS.Token);
//                tvSchema.Visibility = Visibility.Collapsed;
//                tvSearch.Visibility = Visibility.Visible;
//            }
//        }

//        private void FiltrarArbol(string filtro)
//        {
//            tvSearch.Items.Clear();

//            foreach (TreeViewItem esquema in tvSchema.Items)
//            {
//                // 1. Extraemos el nombre del Esquema/Base de Datos
//                string nombreEsquema = ExtraerTextoDeHeader(esquema.Header);
//                TreeViewItem nuevoEsquema = new TreeViewItem { Header = nombreEsquema, IsExpanded = true };

//                bool tieneCoincidencias = false;

//                foreach (TreeViewItem tabla in esquema.Items)
//                {
//                    // 2. Extraemos el nombre real de la tabla (ej: "albums")
//                    string nombreTabla = ExtraerTextoDeHeader(tabla.Header);

//                    if (nombreTabla.ToLower().Contains(filtro))
//                    {
//                        // Creamos el nuevo item con el nombre limpio
//                        // Si quieres que el buscador también tenga iconos, 
//                        // tendrías que recrear el StackPanel aquí.
//                        TreeViewItem copiaTabla = new TreeViewItem { Header = nombreTabla };
//                        nuevoEsquema.Items.Add(copiaTabla);
//                        tieneCoincidencias = true;
//                    }
//                }

//                if (tieneCoincidencias)
//                {
//                    tvSearch.Items.Add(nuevoEsquema);
//                }
//            }
//        }

//        // Esta función ayuda a obtener el texto sin importar si el Header es un String o un StackPanel
//        private string ExtraerTextoDeHeader(object header)
//        {
//            if (header is string) return (string)header;

//            if (header is StackPanel sp)
//            {
//                // Buscamos el TextBlock dentro del StackPanel
//                foreach (var hijo in sp.Children)
//                {
//                    if (hijo is TextBlock tb) return tb.Text;
//                }
//            }

//            return header.ToString(); // Caso de respaldo
//        }

//        private void txtBuscar_KeyDown(object sender, KeyEventArgs e)
//        {
//            if (e.Key == Key.Enter)
//            {
//                btnBuscar_Click(sender, e);
//            }
//        }
//    }
//}
//public class GridLengthAnimation : AnimationTimeline
//{
//    public override Type TargetPropertyType => typeof(GridLength);

//    public static readonly DependencyProperty FromProperty =
//        DependencyProperty.Register("From", typeof(GridLength), typeof(GridLengthAnimation));

//    public static readonly DependencyProperty ToProperty =
//        DependencyProperty.Register("To", typeof(GridLength), typeof(GridLengthAnimation));

//    public GridLength From
//    {
//        get => (GridLength)GetValue(FromProperty);
//        set => SetValue(FromProperty, value);
//    }

//    public GridLength To
//    {
//        get => (GridLength)GetValue(ToProperty);
//        set => SetValue(ToProperty, value);
//    }

//    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
//    {
//        double fromVal = ((GridLength)GetValue(FromProperty)).Value;
//        double toVal = ((GridLength)GetValue(ToProperty)).Value;

//        if (fromVal > toVal)
//            return new GridLength((1 - animationClock.CurrentProgress.Value) * (fromVal - toVal) + toVal, GridUnitType.Pixel);
//        else
//            return new GridLength(animationClock.CurrentProgress.Value * (toVal - fromVal) + fromVal, GridUnitType.Pixel);
//    }

//    protected override Freezable CreateInstanceCore()
//    {
//        return new GridLengthAnimation();
//    }
//}