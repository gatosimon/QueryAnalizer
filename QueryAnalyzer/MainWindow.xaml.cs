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
using ICSharpCode.AvalonEdit.CodeCompletion;
using System.Xml;
using System.Threading;
using System.Windows.Documents;
using System.Windows.Media;

namespace QueryAnalyzer
{
    public partial class MainWindow : Window
    {
        private static readonly string HISTORY_FILE = Path.Combine(App.AppDataFolder, "query_history.txt");
        private static readonly string HISTORIAL_XML = Path.Combine(App.AppDataFolder, "historial.xml");
        private bool iniciarColapasado = true;
        private CancellationTokenSource _explorarCTS;
        // Filtros del explorador (persisten entre llamadas a CargarEsquema)
        private string _filtroTipo = "BOTH";   // "BOTH" | "TABLE" | "VIEW"
        private string _filtroSchema = "";       // "" = todos

        // Isla de tablas activa (null = sin filtro)
        private ConjuntoTablas _islaActiva = null;

        // Resultado de la última búsqueda profunda (null = sin búsqueda activa)
        private List<string> _resultadoBusqueda = null;  // lista de "schema.tabla" o "tabla"
        private string _terminoBusqueda = "";

        public Dictionary<string, OdbcType> OdbcTypes { get; set; }
        public List<QueryParameter> Parametros { get; set; }

        static public Conexion conexionActual = null;

        // ── Tema ────────────────────────────────────────────────────────
        private bool _modoOscuro = false;
        private ResourceDictionary _temaClaro = null;
        private ResourceDictionary _temaOscuro = null;
        // Carpeta de temas en AppData: tiene permisos de escritura aunque la app
        // este instalada en Program Files
        private static readonly string ThemesFolder =
            System.IO.Path.Combine(App.AppDataFolder, "Themes");

        // ── Intellisense ────────────────────────────────────────────────
        private CompletionWindow _completionWindow;
        private Dictionary<string, List<ColumnInfo>> _cacheColumnas = new Dictionary<string, List<ColumnInfo>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> _mapaAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _tablasEnCarga = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Caché de tablas de la base de datos activa (se carga al conectar y se invalida al cambiar conexión)
        private List<TablaInfo> _cacheTablas = new List<TablaInfo>();
        private bool _tablasEnCargaGlobal = false;

        // ── Configuración de la aplicación (config.xml) ─────────────────
        private AppConfig _configApp = new AppConfig();

        public MainWindow()
        {
            InitializeComponent();
            // en MainWindow() después de InitializeComponent()
            txtVersion.Text = $"Versión: {UpdateHelper.GetInstalledVersion()}";

            // 🔹 AÑADIDO: esto asegura que los bindings de DataContext funcionen correctamente.
            DataContext = this;

            // 🔹 NUEVO: Registrar definición de SQL si no existe nativamente
            RegistrarResaltadoSQL();
            txtQuery.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("SQL");

            LoadHistory(); // mantiene compatibilidad con el archivo de texto
                           //txtQuery.KeyDown += TxtQuery_KeyDown;

            txtQuery.Text = PhraseManager.ObtenerFraseCualquiera();

            CargarTipos();

            // Configura ItemsSource correctamente
            Parametros = new List<QueryParameter>();
            gridParams.ItemsSource = Parametros;
            InicializarDrivers();
            // Cargar configuración persistida antes de inicializar conexiones
            _configApp = ConfigManager.ObtenerConfiguracion();
            InicializarConexiones();
            BloquearUI(true);
            InicializarTemas();
            ConfigurarMenuContextualAvalonEdit();

            // Intellisense: suscripción a eventos de AvalonEdit
            txtQuery.TextArea.TextEntering += TxtQuery_TextEntering;
            txtQuery.TextArea.TextEntered += TxtQuery_TextEntered;
            // Ctrl+Space: disparar intellisense manualmente
            // Ctrl+Shift+Home: corregir selección hasta el inicio del documento
            txtQuery.TextArea.PreviewKeyDown += TxtQueryArea_PreviewKeyDown;
        }

        /// <summary>
        /// Primera vez: extrae los .xaml de tema desde recursos embebidos a disco (carpeta Themes\ junto al .exe).
        /// Luego siempre lee desde disco, permitiendo al usuario editar los colores.
        /// </summary>
        private void InicializarTemas()
        {
            if (!System.IO.Directory.Exists(ThemesFolder))
                System.IO.Directory.CreateDirectory(ThemesFolder);

            ExtraerTemaADisco("ThemeLight.xaml");
            ExtraerTemaADisco("ThemeDark.xaml");

            _temaClaro = LeerTemaDesdeDisco("ThemeLight.xaml");
            _temaOscuro = LeerTemaDesdeDisco("ThemeDark.xaml");

            AplicarTema(_temaClaro);
        }

        /// <summary>
        /// Copia el .xaml embebido a disco solo si no existe todavia.
        /// Si ya existe (el usuario lo modifico) no lo toca.
        /// </summary>
        private void ExtraerTemaADisco(string archivo)
        {
            string destino = System.IO.Path.Combine(ThemesFolder, archivo);
            if (System.IO.File.Exists(destino)) return;

            // Leer el recurso embebido y escribirlo en disco
            var uri = new Uri($"pack://application:,,,/{archivo}", UriKind.Absolute);
            var info = Application.GetResourceStream(uri);
            if (info == null) return;
            using (var src = info.Stream)
            using (var dst = System.IO.File.Create(destino))
                src.CopyTo(dst);
        }

        /// <summary>
        /// Lee y parsea el .xaml de tema desde la carpeta Themes\ en disco.
        /// </summary>
        private ResourceDictionary LeerTemaDesdeDisco(string archivo)
        {
            string ruta = System.IO.Path.Combine(ThemesFolder, archivo);
            using (var stream = System.IO.File.OpenRead(ruta))
                return (ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
        }

        /// <summary>
        /// Aplica un ResourceDictionary a esta ventana y a todas las ventanas hijas abiertas.
        /// Tambien actualiza AvalonEdit y los headers de grillas ya creadas.
        /// </summary>
        private void AplicarTema(ResourceDictionary tema)
        {
            var merged = this.Resources.MergedDictionaries;
            if (merged.Count > 0) merged[0] = tema;
            else merged.Add(tema);

            foreach (Window w in Application.Current.Windows)
            {
                if (w == this) continue;
                var wd = w.Resources.MergedDictionaries;
                if (wd.Count > 0) wd[0] = tema;
                else wd.Add(tema);
            }

            // AvalonEdit no responde a DynamicResource
            if (tema.Contains("BrushEditor") && tema.Contains("BrushEditorFG"))
            {
                txtQuery.Background = (System.Windows.Media.Brush)tema["BrushEditor"];
                txtQuery.Foreground = (System.Windows.Media.Brush)tema["BrushEditorFG"];
            }

            ActualizarHeadersGrillas();
        }

        private void BtnToggleTema_Click(object sender, RoutedEventArgs e)
        {
            _modoOscuro = !_modoOscuro;

            AplicarTema();
        }

        private void AplicarTema()
        {
            // Recargar desde disco por si el usuario modifico el archivo mientras la app estaba abierta
            if (_modoOscuro)
                _temaOscuro = LeerTemaDesdeDisco("ThemeDark.xaml");
            else
                _temaClaro = LeerTemaDesdeDisco("ThemeLight.xaml");

            AplicarTema(_modoOscuro ? _temaOscuro : _temaClaro);
            btnToggleTema.Content = _modoOscuro ? "☀" : "🌙";
        }

        /// <summary>
        /// Recorre los DataGrids ya creados en tcResults y les actualiza el ColumnHeaderStyle
        /// con los colores del tema activo. Necesario porque FindResource resuelve el color
        /// una sola vez al crear el estilo, no dinamicamente.
        /// </summary>
        private void ActualizarHeadersGrillas()
        {
            foreach (TabItem tab in tcResults.Items)
            {
                if (tab.Content is DataGrid dg)
                {
                    var hs = new Style(typeof(DataGridColumnHeader));
                    hs.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
                    hs.Setters.Add(new Setter(Control.BackgroundProperty, (System.Windows.Media.Brush)this.FindResource("BrushHeaderBG")));
                    hs.Setters.Add(new Setter(Control.ForegroundProperty, (System.Windows.Media.Brush)this.FindResource("BrushHeaderFG")));
                    hs.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
                    hs.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
                    hs.Setters.Add(new Setter(DataGridColumnHeader.SeparatorBrushProperty, (System.Windows.Media.Brush)this.FindResource("BrushBorder")));
                    dg.ColumnHeaderStyle = hs;
                }
            }
        }

        /// <summary>Aplica fondo y foreground del tema activo a un ContextMenu creado en code-behind.</summary>
        private void AplicarEstiloContextMenu(ContextMenu menu)
        {
            // SetResourceReference es el equivalente a DynamicResource en code-behind:
            // actualiza el color automaticamente cuando cambia el tema.
            menu.SetResourceReference(ContextMenu.BackgroundProperty, "BrushMenuBG");
            menu.SetResourceReference(ContextMenu.ForegroundProperty, "BrushFG");
            menu.SetResourceReference(ContextMenu.BorderBrushProperty, "BrushBorder");
        }

        /// <summary>Aplica fondo y foreground del tema activo a un MenuItem creado en code-behind.</summary>
        private void AplicarEstiloMenuItem(MenuItem item)
        {
            // SetResourceReference es el equivalente a DynamicResource en code-behind.
            item.SetResourceReference(MenuItem.BackgroundProperty, "BrushMenuBG");
            item.SetResourceReference(MenuItem.ForegroundProperty, "BrushFG");
        }

        private void ConfigurarMenuContextualAvalonEdit()
        {
            // Crear el menú contextual
            var contextMenu = new ContextMenu();
            AplicarEstiloContextMenu(contextMenu);

            // Opción Copiar
            var menuCopiar = new MenuItem { Header = "Copiar" };
            AplicarEstiloMenuItem(menuCopiar);
            menuCopiar.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(txtQuery.SelectedText))
                    Clipboard.SetText(txtQuery.SelectedText);
            };
            contextMenu.Items.Add(menuCopiar);

            // Opción Cortar
            var menuCortar = new MenuItem { Header = "Cortar" };
            AplicarEstiloMenuItem(menuCortar);
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
            AplicarEstiloMenuItem(menuPegar);
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
            AplicarEstiloMenuItem(menuSeleccionarTodo);
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
                btnToggleDerecho_Click(this, e as RoutedEventArgs);
                iniciarColapasado = false;
            }
        }

        private void CargarTipos()
        {
            OdbcTypes = Enum.GetValues(typeof(OdbcType))
                .Cast<OdbcType>()
                .ToDictionary(t => t.ToString(), t => t);
        }

        private void InicializarDrivers()
        {
            // Proyectamos el enum a una lista de objetos con nombre amigable y el valor real
            cbDriver.ItemsSource = Enum.GetValues(typeof(TipoMotor))
                .Cast<TipoMotor>()
                .Select(m => new {
                    NombreAmigable = m.ToString().Replace("_", " "),
                    ValorReal = m
                }).ToList();

            // Indicamos qué propiedad se muestra y cuál es el valor de fondo
            cbDriver.DisplayMemberPath = "NombreAmigable";
            cbDriver.SelectedValuePath = "ValorReal";
        }

        private void InicializarConexiones()
        {
            var conexiones = ConfigManager.CargarConexiones();
            cbConnectionName.ItemsSource = conexiones.Values.ToList();
            cbConnectionName.DisplayMemberPath = "Nombre";
        }

        private void FiltrarConexiones(TipoMotor motor)
        {
            var conexiones = ConfigManager.CargarConexiones();
            cbConnectionName.ItemsSource = conexiones.Values.ToList().Where(c => c.Motor == motor);
            cbConnectionName.DisplayMemberPath = "Nombre";
        }

        private void cbdriver_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                // Usamos SelectedValue para obtener el TipoMotor (definido en SelectedValuePath)
                if (cbDriver.SelectedValue is TipoMotor driver)
                {
                    FiltrarConexiones(driver);
                }
            }
            catch (Exception ex)
            {
                AppendMessage("Error al filtrar conexiones: " + ex.Message);
            }
        }

        private void cbConnectionName_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbConnectionName.SelectedItem is Conexion conexion)
            {
                conexionActual = conexion;
                BloquearUI(false);
                AppendMessage($"Conexión seleccionada: {conexion.Motor}");

                // NUEVO: al seleccionar conexión, filtramos historial para esa conexión
                LoadHistoryForConnection(conexion);
                // Limpiamos el caché de intellisense al cambiar de conexión
                _cacheColumnas.Clear();
                _mapaAliases.Clear();
                _tablasEnCarga.Clear();
                // Invalidamos y recargamos el caché de tablas para la nueva conexión
                _cacheTablas.Clear();
                CargarTablasEnBackground(conexion);

                // Resetear filtros de esquema, tipo, isla y búsqueda al cambiar de conexión
                _filtroSchema = "";
                _filtroTipo = "BOTH";
                _islaActiva = null;
                _resultadoBusqueda = null;
                _terminoBusqueda = "";
                txtBuscar.Text = "";

                // Cargar islas guardadas para esta conexión
                CargarComboIslas();

                btnExplorar_Click(sender, e);
                if (_configApp.CargarUltimaConsulta && lstHistory.Items.Count > 0)
                {
                    CargarHistoriaEnConsultas(lstHistory.Items[0]);
                }
            }
        }

        private void CargarHistoriaEnConsultas(object itemCargar)
        {
            try
            {
                if (itemCargar is ListBoxItem lbi && lbi.Tag is Historial hist)
                {
                    txtQuery.Text = hist.Consulta ?? string.Empty;

                    SincronizarParametros();
                }
                else
                {
                    if (itemCargar is string s)
                        txtQuery.Text = s;
                    else if (itemCargar is ListBoxItem li && li.Content is string cs)
                        txtQuery.Text = cs;
                    else
                    {
                        switch (conexionActual.Motor)
                        {
                            case TipoMotor.MS_SQL:
                                txtQuery.Text = "SELECT TOP 10 * FROM INFORMATION_SCHEMA.COLUMNS;";
                                break;
                            case TipoMotor.DB2:
                                txtQuery.Text = "SELECT * FROM SYSIBM.SYSCOLUMNS FETCH FIRST 10 ROWS ONLY;";
                                break;
                            case TipoMotor.POSTGRES:
                                txtQuery.Text = "SELECT * FROM INFORMATION_SCHEMA.COLUMNS LIMIT 10;";
                                break;
                            case TipoMotor.SQLite:
                                txtQuery.Text = "SELECT * FROM pragma_table_list AS l JOIN pragma_table_info(l.name) LIMIT 10;";
                                break;
                            default:
                                break;
                        }
                    }

                }
            }
            catch (Exception ex)
            {
                AppendMessage("Error al cargar selección del historial: " + ex.Message);
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
                        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, (System.Windows.Media.Brush)this.FindResource("BrushHeaderBG")));
                        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, (System.Windows.Media.Brush)this.FindResource("BrushHeaderFG")));
                        headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
                        headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 4, 6, 4)));
                        headerStyle.Setters.Add(new Setter(DataGridColumnHeader.SeparatorBrushProperty, (System.Windows.Media.Brush)this.FindResource("BrushBorder")));

                        var dataGrid = new DataGrid
                        {
                            IsReadOnly = true,
                            AutoGenerateColumns = true,
                            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                            AlternationCount = 2,
                            RowStyle = (Style)this.FindResource("ResultGridRowStyle"),
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

                            // Si el doble click viene de un header, no interceptar:
                            // el DataGrid necesita procesar ese evento para ordenar la columna.
                            if (FindVisualParent<DataGridColumnHeader>(clickedElement) != null)
                                return;

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
                        AplicarEstiloContextMenu(cellContextMenu);

                        var menuCopiarCelda = new MenuItem { Header = "Copiar celda" };
                        AplicarEstiloMenuItem(menuCopiarCelda);
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
                        AplicarEstiloMenuItem(menuCopiarFila);
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
                        AplicarEstiloMenuItem(menuCopiarTodo);
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

                        // AutoGeneratingColumn se dispara ANTES de que cada columna sea añadida al DataGrid,
                        // lo que permite modificar el Header con el nombre real (con guiones bajos) antes
                        // de cualquier renderizado. AutoGeneratedColumns (plural) se dispara demasiado tarde.
                        dataGrid.AutoGeneratingColumn += (s, ev) =>
                        {
                            // ev.Column.Header en este punto ya tiene el nombre que WPF asignó
                            // (puede tener espacios en lugar de _). El nombre real está en ev.PropertyName.
                            string colName = ev.PropertyName;
                            if (!dt.Columns.Contains(colName))
                                return;

                            // Forzar el header con el nombre real de la columna (preserva guiones bajos)
                            // Asignar como TextBlock para evitar que WPF interprete _ como mnemónico
                            // de tecla de acceso (AccessText) y lo oculte. Ej: COMUNA_J -> COMUNAJ.
                            // Se setean Foreground y Background explícitamente con los recursos del tema
                            // porque el TextBlock creado en code-behind no hereda el estilo del header.
                            var tbHeader = new System.Windows.Controls.TextBlock
                            {
                                Text = colName,
                                TextAlignment = System.Windows.TextAlignment.Center,
                            };
                            tbHeader.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BrushHeaderFG");
                            ev.Column.Header = tbHeader;

                            Type tipo = dt.Columns[colName].DataType;

                            // Formato para DateTime
                            if (tipo == typeof(DateTime) || tipo == typeof(DateTime?) || tipo == typeof(DateTimeOffset))
                            {
                                var boundColumn = ev.Column as DataGridBoundColumn;
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
                            ev.Column.CellStyle = estilo;
                        };

                        // ItemsSource se asigna DESPUÉS de suscribir AutoGeneratingColumn,
                        // así WPF ya tiene el handler activo cuando genera las columnas.
                        dataGrid.ItemsSource = dt.DefaultView;

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

                // 🔹 Guardar: permite al usuario elegir carpeta y nombre de archivo
                System.Windows.Forms.SaveFileDialog sfdExcel = new System.Windows.Forms.SaveFileDialog();
                sfdExcel.Title = "Guardar Excel";
                sfdExcel.Filter = "Archivos Excel (*.xlsx)|*.xlsx";
                sfdExcel.FileName = "ResultadosConsultas.xlsx";
                sfdExcel.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                if (sfdExcel.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                    return;

                string ruta = sfdExcel.FileName;
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

                            // Ejecutamos una única vez con el reader real para preservar los nombres
                            // de columna tal como los devuelve el motor (con guiones bajos incluidos).
                            // El doble-execute anterior (SchemaOnly + Fill) causaba que algunos drivers
                            // ODBC (ej. DB2) alteraran los nombres en el segundo paso.
                            using (var reader = cmd.ExecuteReader())
                            {
                                // Detectar columnas BIT DATA [1] de DB2: el driver ODBC las entrega
                                // como byte[] de longitud 1. Las marcamos para tratarlas como bool.
                                var esBitData = new bool[reader.FieldCount];
                                for (int i = 0; i < reader.FieldCount; i++)
                                {
                                    string nombreReal = reader.GetName(i);
                                    Type tipoCol = reader.GetFieldType(i) ?? typeof(string);

                                    // byte[] de 1 byte = BIT DATA [1] en DB2 → se expone como bool
                                    if (tipoCol == typeof(byte[]))
                                    {
                                        int colSize = reader.GetSchemaTable()?.Rows[i]?["ColumnSize"] is int cs ? cs : -1;
                                        if (colSize == 1)
                                        {
                                            esBitData[i] = true;
                                            tipoCol = typeof(bool);
                                        }
                                    }

                                    // Si ya existe una columna con ese nombre (duplicado), agregamos sufijo
                                    string nombreFinal = nombreReal;
                                    int sufijo = 1;
                                    while (dt.Columns.Contains(nombreFinal))
                                        nombreFinal = nombreReal + "_" + sufijo++;
                                    dt.Columns.Add(nombreFinal, tipoCol);
                                }

                                // Llenar filas
                                while (reader.Read())
                                {
                                    var row = dt.NewRow();
                                    for (int i = 0; i < reader.FieldCount; i++)
                                    {
                                        if (reader.IsDBNull(i))
                                        {
                                            row[i] = DBNull.Value;
                                        }
                                        else if (esBitData[i])
                                        {
                                            // BIT DATA [1]: convertir byte[] {0} -> false, {1} -> true
                                            var bytes = reader.GetValue(i) as byte[];
                                            row[i] = bytes != null && bytes.Length > 0 && bytes[0] != 0;
                                        }
                                        else
                                        {
                                            row[i] = reader.GetValue(i);
                                        }
                                    }
                                    dt.Rows.Add(row);
                                }
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
                stringConnection = ConexionesManager.GetConnectionString(conexionActual.Motor, conexionActual.Servidor, conexionActual.Puerto, conexionActual.BaseDatos, conexionActual.Usuario, conexionActual.Contrasena, conexionActual.EsWeb);
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
            // VisualTreeHelper.GetParent solo acepta Visual o Visual3D.
            // Elementos como System.Windows.Documents.Run no lo son y lanzan InvalidOperationException.
            if (child == null || (!(child is Visual) && !(child is System.Windows.Media.Media3D.Visual3D)))
                return null;

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

            CargarHistoriaEnConsultas(lstHistory.SelectedItem);
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
            foreach (var item in cbConnectionName.Items)
            {
                try
                {
                    if (conexionActual != null && ((Conexion)item).Nombre == conexionActual.Nombre)
                    {
                        cbConnectionName.SelectedItem = item;
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
            foreach (var item in cbConnectionName.Items)
            {
                try
                {
                    if (conexionActual != null && ((Conexion)item).Nombre == conexionActual.Nombre)
                    {
                        cbConnectionName.SelectedItem = item;
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
            if (cbConnectionName.SelectedItem is Conexion conexion)
            {
                conexionActual = conexion;
                var conexiones = ConexionesManager.Cargar();
                conexiones.Remove(conexionActual.Nombre);
                ConexionesManager.Guardar(conexiones);
                cbConnectionName.ItemsSource = conexiones.Values.ToList();
                cbConnectionName.DisplayMemberPath = "Nombre";
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
            var vistaIconUri = new Uri("pack://application:,,,/Assets/vista.png"); // 👈 NUEVO

            var tablaIcon = new System.Windows.Media.Imaging.BitmapImage(tablaIconUri);
            var columnaIcon = new System.Windows.Media.Imaging.BitmapImage(columnaIconUri);
            var columnaClaveIcon = new System.Windows.Media.Imaging.BitmapImage(columnaClaveIconUri);
            var claveIcon = new System.Windows.Media.Imaging.BitmapImage(claveIconUri);
            var vistaIcon = new System.Windows.Media.Imaging.BitmapImage(vistaIconUri); // 👈 NUEVO
            int tamañoIconos = 20;
            // 🖼️ FIN DE MODIFICACIÓN

            await Task.Run(() =>
            {
                try
                {
                    Cargar(new string[] { "TABLE", "VIEW" }, filtrado, tablasConsulta, tvCargar, connStr, tablaIcon, columnaIcon, columnaClaveIcon, claveIcon, vistaIcon, tamañoIconos, token);
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

        private void Cargar(string[] tiposTabla, string filtrado, List<string> tablasConsulta, TreeView tvCargar, string connStr, System.Windows.Media.Imaging.BitmapImage tablaIcon, System.Windows.Media.Imaging.BitmapImage columnaIcon, System.Windows.Media.Imaging.BitmapImage columnaClaveIcon, System.Windows.Media.Imaging.BitmapImage claveIcon, System.Windows.Media.Imaging.BitmapImage vistaIcon, int tamañoIconos, CancellationToken token)
        {
            using (var conn = new OdbcConnection(connStr))
            {
                conn.Open();

                // Obtiene las tablas
                DataTable tablas = new DataTable();

                foreach (string tipo in tiposTabla)
                {
                    // Obtenemos el esquema actual (ej: "Tables", "Views")
                    DataTable esquemaTemporal = conn.GetSchema($"{tipo}s");

                    // Fusionamos el contenido en nuestra tabla principal
                    tablas.Merge(esquemaTemporal);
                }

                string selecciones = string.Join(" OR ", tiposTabla.Select(t => $"TABLE_TYPE = '{t}'").ToArray());

                Dispatcher.Invoke(() => tvCargar.Items.Clear());

                // Filtrar las filas cuyo tipo sea "TABLE" o "VIEW" 👈 CAMBIADO
                DataRow[] tablasFiltradas = tablas.Select(selecciones);

                // Si querés seguir usando un DataTable:
                DataTable tablasSolo = tablasFiltradas.Length > 0 ? tablasFiltradas.CopyToDataTable() : tablas.Clone();
                if (_islaActiva != null)
                {
                    tablasSolo = tablasFiltradas.Length > 0 ? tablasFiltradas.Where(t=> _islaActiva.Tablas.Contains(t.Table.TableName)).ToArray().CopyToDataTable() : tablas.Clone();
                }
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
                        //if (tipo != "TABLE" && tipo != "VIEW") continue; // 👈 CAMBIADO

                        string headerText = string.IsNullOrEmpty(schema) ? nombreTabla : $"{schema}.{nombreTabla}";

                        // 🎨 INICIO DE MODIFICACIÓN: Cálculo del fondo alternado
                        //System.Windows.Media.Brush currentTableBackground = (tablasLeidas % 2 == 0) ? evenRowBrush : oddRowBrush;
                        // 🎨 FIN DE MODIFICACIÓN

                        // 🔹 CARGA DIFERIDA: capturamos los datos de esta tabla para usarlos en el closure
                        string capSchema = schema;
                        string capTabla = nombreTabla;
                        string capTipo = tipo;

                        Dispatcher.Invoke(() =>
                        {
                            // 🖼️ INICIO DE MODIFICACIÓN: nodo de tabla/vista con icono
                            var tablaHeader = new StackPanel { Orientation = Orientation.Horizontal };

                            // ── CheckBox de selección múltiple ─────────────────────────────
                            var chkNodo = new CheckBox
                            {
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new System.Windows.Thickness(0, 0, 4, 0),
                                ToolTip = "Seleccionar para Documentar / Esquematizar"
                            };
                            chkNodo.Checked += (cs, ce) => ActualizarContadorSeleccion();
                            chkNodo.Unchecked += (cs, ce) => ActualizarContadorSeleccion();
                            tablaHeader.Children.Add(chkNodo);

                            tablaHeader.Children.Add(new System.Windows.Controls.Image
                            {
                                Source = capTipo == "VIEW" ? vistaIcon : tablaIcon, // 👈 CAMBIADO
                                Width = tamañoIconos,
                                Height = tamañoIconos,
                                Margin = new System.Windows.Thickness(0, 0, 5, 0)
                            });
                            tablaHeader.Children.Add(new System.Windows.Controls.TextBlock { Text = headerText });

                            var tablaNode = new TreeViewItem
                            {
                                Header = tablaHeader,
                                Tag = new NodoTablaTag(capTabla, capTipo),
                                //Background = currentTableBackground
                            };
                            // 🖼️ FIN DE MODIFICACIÓN

                            // Filtro de isla activa: ocultar nodos que no pertenecen a la isla
                            //if (_islaActiva != null)
                            //{
                            //    string idNodo = string.IsNullOrEmpty(capSchema)
                            //        ? capTabla
                            //        : $"{capSchema}.{capTabla}";
                            //    bool enIsla = _islaActiva.Tablas.Any(t =>
                            //        string.Equals(t, idNodo, StringComparison.OrdinalIgnoreCase) ||
                            //        string.Equals(t, capTabla, StringComparison.OrdinalIgnoreCase));
                            //    tablaNode.Visibility = enIsla
                            //        ? System.Windows.Visibility.Visible
                            //        : System.Windows.Visibility.Collapsed;
                            //}

                            if (filtrado.Length == 0 || (filtrado.Length > 0 && capTabla.ToUpper().Contains(filtrado.ToUpper())))
                            {
                                // 🔹 CARGA DIFERIDA: placeholder para que el nodo sea expandible sin cargar nada todavía
                                tablaNode.Items.Add(new TreeViewItem { Header = "Cargando..." });

                                // 🔹 CARGA DIFERIDA: al expandir por primera vez se cargan columnas, PKs e índices
                                tablaNode.Expanded += async (s, ev) =>
                                {
                                    var nodo = s as TreeViewItem;
                                    // Solo actuar si todavía tiene el placeholder (evita recargar)
                                    if (nodo.Items.Count == 1 && nodo.Items[0] is TreeViewItem ph && ph.Header != null && ph.Header.ToString() == "Cargando...")
                                    {
                                        nodo.Items.Clear();
                                        try
                                        {
                                            await Task.Run(() => CargarDetallesTabla(nodo, capSchema, capTabla, capTipo, connStr, columnaIcon, columnaClaveIcon, claveIcon, tamañoIconos));
                                        }
                                        catch (Exception ex)
                                        {
                                            Dispatcher.Invoke(() => AppendMessage($"Error cargando detalles de {capTabla}: " + ex.Message));
                                        }
                                    }
                                };

                                tvCargar.Items.Add(tablaNode);
                                tablaNode.MouseDoubleClick += TablaNode_MouseDoubleClick;

                                // ── Menú contextual de scripts ───────────────────────────
                                var ctxMenu = new ContextMenu();
                                AplicarEstiloContextMenu(ctxMenu);

                                Action<string, Func<string>> agregarOpcion = (hdr, gen) =>
                                {
                                    var menuItem = new MenuItem { Header = hdr };
                                    AplicarEstiloMenuItem(menuItem);
                                    menuItem.Click += (s, ev) =>
                                    {
                                        try { InsertarEnQuery(gen()); }
                                        catch (Exception ex) { AppendMessage("Error generando script: " + ex.Message); }
                                    };
                                    ctxMenu.Items.Add(menuItem);
                                };

                                agregarOpcion("📋 SELECT TOP 10", () => GenerarSelectTop10(capSchema, capTabla));
                                // 🔹 CARGA DIFERIDA: los scripts que necesitan columnas las obtienen en el momento del click
                                agregarOpcion("📋 SELECT (todas las cols)", () =>
                                {
                                    using (var c = new OdbcConnection(connStr)) { c.Open(); return GenerarSelectAllCols(capSchema, capTabla, c.GetSchema("Columns", new string[] { null, capSchema, capTabla })); }
                                });
                                ctxMenu.Items.Add(new Separator());
                                agregarOpcion("📄 CREATE TABLE", () =>
                                {
                                    using (var c = new OdbcConnection(connStr)) { c.Open(); return GenerarCreateTable(capSchema, capTabla, c.GetSchema("Columns", new string[] { null, capSchema, capTabla })); }
                                });
                                agregarOpcion("✏️  ALTER TABLE (add col)", () => GenerarAlterTableAddColumn(capSchema, capTabla));
                                agregarOpcion("🗑️  DROP TABLE", () => GenerarDropTable(capSchema, capTabla));
                                ctxMenu.Items.Add(new Separator());
                                agregarOpcion("➕ INSERT INTO", () =>
                                {
                                    using (var c = new OdbcConnection(connStr)) { c.Open(); return GenerarInsert(capSchema, capTabla, c.GetSchema("Columns", new string[] { null, capSchema, capTabla })); }
                                });
                                agregarOpcion("✏️  UPDATE ... SET", () =>
                                {
                                    using (var c = new OdbcConnection(connStr)) { c.Open(); return GenerarUpdate(capSchema, capTabla, c.GetSchema("Columns", new string[] { null, capSchema, capTabla })); }
                                });
                                agregarOpcion("🗑️  DELETE FROM", () => GenerarDelete(capSchema, capTabla));
                                ctxMenu.Items.Add(new Separator());
                                agregarOpcion("🔑 CREATE INDEX", () => GenerarCreateIndex(capSchema, capTabla));
                                agregarOpcion("🔑 DROP INDEX", () => GenerarDropIndex(capSchema, capTabla));
                                agregarOpcion("📊 COUNT(*)", () => GenerarCount(capSchema, capTabla));
                                agregarOpcion("⚒︎ DESIGN", () => Diseñar(capSchema, capTabla));

                                // ── Documentar tabla individual ────────────────────────────────
                                ctxMenu.Items.Add(new Separator());
                                var menuDocTabla = new MenuItem { Header = $"📝 Documentar {capTabla}" };
                                AplicarEstiloMenuItem(menuDocTabla);
                                menuDocTabla.Click += async (s, ev) =>
                                {
                                    await DocumentarTablaAsync(capSchema, capTabla, capTipo);
                                };
                                ctxMenu.Items.Add(menuDocTabla);

                                tablaNode.ContextMenu = ctxMenu;
                                // ─────────────────────────────────────────────────────────
                            }
                        });

                        tablasLeidas++;
                    }

                    Dispatcher.Invoke(() =>
                    {
                        txtExplorar.Text = $"{tablasLeidas} tablas leídas de {cantidadDeTablas}";
                        ActualizarContadorExplorador();
                    });
                    if (tablasLeidas == cantidadDeTablas) break;
                }
            }
        }

        /// <summary>
        /// Carga columnas, PKs e índices de una tabla específica cuando el usuario expande su nodo.
        /// Se ejecuta en un hilo de fondo y actualiza la UI vía Dispatcher.
        /// </summary>
        private void CargarDetallesTabla(TreeViewItem tablaNode, string schema, string nombreTabla, string tipo, string connStr, System.Windows.Media.Imaging.BitmapImage columnaIcon, System.Windows.Media.Imaging.BitmapImage columnaClaveIcon, System.Windows.Media.Imaging.BitmapImage claveIcon, int tamañoIconos)
        {
            using (var conn = new OdbcConnection(connStr))
            {
                conn.Open();

                var columnas = conn.GetSchema("Columns", new string[] { null, schema, nombreTabla });

                // 🔑 Obtener columnas que son clave primaria mediante SQL según el motor
                var columnasClaveSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                //if (tipo == "TABLE") // 👈 NUEVO: las vistas no tienen PK
                {
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
                } // 👈 NUEVO: fin del if (tipo == "TABLE") para PK

                // Agregar columnas en UI
                Dispatcher.Invoke(() =>
                {
                    foreach (DataRow col in columnas.Rows)
                    {
                        string colName = col["COLUMN_NAME"].ToString();
                        string tipoCol = col["TYPE_NAME"].ToString();
                        string longitud = col["COLUMN_SIZE"].ToString();

                        string escala = string.Empty;
                        if (col.Table.Columns.Contains("DECIMAL_DIGITS") && col["DECIMAL_DIGITS"] != DBNull.Value)
                            escala = col["DECIMAL_DIGITS"].ToString();
                        else if (col.Table.Columns.Contains("NUMERIC_SCALE") && col["NUMERIC_SCALE"] != DBNull.Value)
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
                if (tipo == "TABLE") // 👈 NUEVO: las vistas no tienen índices
                {
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
                } // 👈 NUEVO: fin del if (tipo == "TABLE") para índices
            }
        }

        private void TablaNode_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            string tableName = (sender as TreeViewItem).Header.ToString();
            txtQuery.Text.Insert(txtQuery.SelectionStart, tableName);
        }

        // ════════════════════════════════════════════════════════════════
        // GENERADORES DE SCRIPTS — adaptados por TipoMotor
        // ════════════════════════════════════════════════════════════════

        /// Calificador de nombre: schema.tabla o solo tabla (SQLite no usa schemas)
        private string NombreCompleto(string schema, string tabla)
        {
            TipoMotor motor = conexionActual?.Motor ?? TipoMotor.DB2;
            if (string.IsNullOrEmpty(schema) || motor == TipoMotor.SQLite) return tabla;
            return schema + "." + tabla;
        }

        /// Comillas de identificador según motor
        private string Q(string nombre)
        {
            TipoMotor motor = conexionActual?.Motor ?? TipoMotor.DB2;
            if (motor == TipoMotor.MS_SQL) return "[" + nombre + "]";
            if (motor == TipoMotor.POSTGRES) return "\"" + nombre + "\"";
            return nombre; // DB2 y SQLite sin comillas por defecto
        }

        private string GenerarSelectTop10(string schema, string tabla)
        {
            TipoMotor motor = conexionActual?.Motor ?? TipoMotor.DB2;
            string t = NombreCompleto(schema, tabla);
            switch (motor)
            {
                case TipoMotor.MS_SQL: return "SELECT TOP 10 *\r\nFROM " + t + ";";
                case TipoMotor.DB2: return "SELECT *\r\nFROM " + t + "\r\nFETCH FIRST 10 ROWS ONLY;";
                case TipoMotor.POSTGRES: return "SELECT *\r\nFROM " + t + "\r\nLIMIT 10;";
                case TipoMotor.SQLite: return "SELECT *\r\nFROM " + tabla + "\r\nLIMIT 10;";
                default: return "SELECT *\r\nFROM " + t + ";";
            }
        }

        private string GenerarSelectAllCols(string schema, string tabla, DataTable columnas)
        {
            string t = NombreCompleto(schema, tabla);
            var cols = new System.Text.StringBuilder();
            var rows = columnas.Rows.Cast<DataRow>().ToList();
            for (int i = 0; i < rows.Count; i++)
            {
                cols.Append("    " + Q(rows[i]["COLUMN_NAME"].ToString()));
                if (i < rows.Count - 1) cols.Append(",\r\n");
            }
            return "SELECT\r\n" + cols.ToString() + "\r\nFROM " + t + ";";
        }

        private string GenerarCreateTable(string schema, string tabla, DataTable columnas)
        {
            TipoMotor motor = conexionActual?.Motor ?? TipoMotor.DB2;
            string t = (motor == TipoMotor.SQLite) ? tabla : NombreCompleto(schema, tabla);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("CREATE TABLE " + t + " (");

            var filas = columnas.Rows.Cast<DataRow>().ToList();
            for (int i = 0; i < filas.Count; i++)
            {
                DataRow col = filas[i];
                string colName = col["COLUMN_NAME"].ToString();
                string tipoDB = col["TYPE_NAME"].ToString();
                string longitud = col["COLUMN_SIZE"].ToString();

                string nullable = "";
                if (col.Table.Columns.Contains("IS_NULLABLE") && col["IS_NULLABLE"] != DBNull.Value)
                {
                    string n = col["IS_NULLABLE"].ToString().ToUpper();
                    nullable = (n == "NO") ? " NOT NULL" : " NULL";
                }

                string defVal = "";
                if (col.Table.Columns.Contains("COLUMN_DEF") && col["COLUMN_DEF"] != DBNull.Value
                    && !string.IsNullOrWhiteSpace(col["COLUMN_DEF"].ToString()))
                    defVal = " DEFAULT " + col["COLUMN_DEF"].ToString();

                string tipoUpper = tipoDB.ToUpper();
                bool tieneLogitud = tipoUpper.Contains("CHAR") || tipoUpper.Contains("BINARY");
                string tipoCompleto = (tieneLogitud && !string.IsNullOrEmpty(longitud))
                    ? tipoDB + "(" + longitud + ")" : tipoDB;

                string coma = (i < filas.Count - 1) ? "," : "";
                sb.AppendLine("    " + Q(colName) + " " + tipoCompleto + nullable + defVal + coma);
            }
            sb.Append(");");
            return sb.ToString();
        }

        private string GenerarAlterTableAddColumn(string schema, string tabla)
        {
            TipoMotor motor = conexionActual?.Motor ?? TipoMotor.DB2;
            string t = NombreCompleto(schema, tabla);

            switch (motor)
            {
                case TipoMotor.MS_SQL:
                    // En SQL Server, si agregas NOT NULL con DEFAULT, llena automáticamente las filas existentes.
                    return $"ALTER TABLE {t}\r\nADD nueva_columna NVARCHAR(100) NOT NULL DEFAULT '';";
                case TipoMotor.DB2:
                    // DB2 requiere la palabra COLUMN y el DEFAULT va después del tipo.
                    return $"ALTER TABLE {t}\r\nADD COLUMN NUEVA_COLUMNA NVARCHAR(100) DEFAULT '';";
                case TipoMotor.POSTGRES:
                    // Postgres es similar al estándar pero permite explícitamente ADD COLUMN.
                    return $"ALTER TABLE {t}\r\nADD COLUMN nueva_columna NVARCHAR(100) DEFAULT '';";
                case TipoMotor.SQLite:
                    // SQLite usa TEXT normalmente y permite el DEFAULT en el ADD COLUMN.
                    return $"ALTER TABLE {t}\r\nADD COLUMN nueva_columna TEXT DEFAULT '';";
                default:
                    return $"ALTER TABLE {t} ADD COLUMN nueva_columna NVARCHAR(100) DEFAULT '';";
            }
        }

        private string GenerarDropTable(string schema, string tabla)
        {
            TipoMotor motor = conexionActual?.Motor ?? TipoMotor.DB2;
            string t = NombreCompleto(schema, tabla);
            switch (motor)
            {
                case TipoMotor.MS_SQL: return "IF OBJECT_ID(N'" + t + "', N'U') IS NOT NULL\r\n    DROP TABLE " + t + ";";
                case TipoMotor.DB2: return "DROP TABLE " + t + ";";
                case TipoMotor.POSTGRES: return "DROP TABLE IF EXISTS " + t + ";";
                case TipoMotor.SQLite: return "DROP TABLE IF EXISTS " + tabla + ";";
                default: return "DROP TABLE " + t + ";";
            }
        }

        private string GenerarInsert(string schema, string tabla, DataTable columnas)
        {
            string t = NombreCompleto(schema, tabla);
            var colNames = columnas.Rows.Cast<DataRow>().Select(r => Q(r["COLUMN_NAME"].ToString())).ToList();
            var vals = columnas.Rows.Cast<DataRow>().Select(r => "?").ToList();
            return "INSERT INTO " + t + "\r\n    (" + string.Join(", ", colNames) + ")\r\nVALUES\r\n    (" + string.Join(", ", vals) + ");";
        }

        private string GenerarUpdate(string schema, string tabla, DataTable columnas)
        {
            string t = NombreCompleto(schema, tabla);
            var sets = columnas.Rows.Cast<DataRow>()
                .Select(r => "    " + Q(r["COLUMN_NAME"].ToString()) + " = ?")
                .ToList();
            return "UPDATE " + t + "\r\nSET\r\n" + string.Join(",\r\n", sets) + "\r\nWHERE <condicion>;";
        }

        private string GenerarDelete(string schema, string tabla)
        {
            string t = NombreCompleto(schema, tabla);
            return "DELETE FROM " + t + "\r\nWHERE <condicion>;";
        }

        private string GenerarCreateIndex(string schema, string tabla)
        {
            TipoMotor motor = conexionActual?.Motor ?? TipoMotor.DB2;
            string t = NombreCompleto(schema, tabla);
            string idxName = "IDX_" + tabla + "_COL";
            switch (motor)
            {
                case TipoMotor.MS_SQL: return "CREATE INDEX " + idxName + "\r\nON " + t + " (columna ASC);";
                case TipoMotor.DB2: return "CREATE INDEX " + idxName + "\r\nON " + t + " (COLUMNA ASC);";
                case TipoMotor.POSTGRES: return "CREATE INDEX " + idxName.ToLower() + "\r\nON " + t + " (columna ASC);";
                case TipoMotor.SQLite: return "CREATE INDEX " + idxName.ToLower() + "\r\nON " + tabla + " (columna ASC);";
                default: return "CREATE INDEX " + idxName + " ON " + t + " (columna);";
            }
        }

        private string GenerarDropIndex(string schema, string tabla)
        {
            TipoMotor motor = conexionActual?.Motor ?? TipoMotor.DB2;
            string idxName = "IDX_" + tabla + "_COL";
            string t = NombreCompleto(schema, tabla);
            switch (motor)
            {
                case TipoMotor.MS_SQL: return "DROP INDEX " + t + "." + idxName + ";";
                case TipoMotor.DB2: return "DROP INDEX " + idxName + ";";
                case TipoMotor.POSTGRES: return "DROP INDEX IF EXISTS " + idxName.ToLower() + ";";
                case TipoMotor.SQLite: return "DROP INDEX IF EXISTS " + idxName.ToLower() + ";";
                default: return "DROP INDEX " + idxName + ";";
            }
        }
        private string GenerarCount(string schema, string tabla)
        {
            return "SELECT COUNT(*) FROM " + NombreCompleto(schema, tabla) + ";";
        }

        // ════════════════════════════════════════════════════════════════
        // SELECCIÓN MÚLTIPLE DEL TREEVIEW
        // ════════════════════════════════════════════════════════════════

        /// <summary>Obtiene el CheckBox del primer hijo del StackPanel header de un nodo.</summary>
        private CheckBox ObtenerCheckboxNodo(TreeViewItem nodo)
        {
            if (nodo.Header is StackPanel sp)
                foreach (var child in sp.Children)
                    if (child is CheckBox chk) return chk;
            return null;
        }

        /// <summary>
        /// Devuelve los nodos visibles que tienen el CheckBox marcado.
        /// </summary>
        private List<TreeViewItem> GetNodosSeleccionados()
        {
            return tvSchema.Items
                .OfType<TreeViewItem>()
                .Where(n => n.Visibility == System.Windows.Visibility.Visible &&
                            ObtenerCheckboxNodo(n)?.IsChecked == true)
                .ToList();
        }

        /// <summary>Actualiza el contador "N sel." en la barra de selección múltiple.</summary>
        private void ActualizarContadorSeleccion()
        {
            if (txtSeleccionContador == null) return;
            int n = tvSchema.Items
                .OfType<TreeViewItem>()
                .Count(nodo => nodo.Visibility == System.Windows.Visibility.Visible &&
                               ObtenerCheckboxNodo(nodo)?.IsChecked == true);
            txtSeleccionContador.Text = $"{n} sel.";
        }

        private void btnSelTodos_Click(object sender, RoutedEventArgs e)
        {
            foreach (TreeViewItem nodo in tvSchema.Items)
                if (nodo.Visibility == System.Windows.Visibility.Visible)
                {
                    var chk = ObtenerCheckboxNodo(nodo);
                    if (chk != null) chk.IsChecked = true;
                }
            ActualizarContadorSeleccion();
        }

        private void btnDeselTodos_Click(object sender, RoutedEventArgs e)
        {
            foreach (TreeViewItem nodo in tvSchema.Items)
            {
                var chk = ObtenerCheckboxNodo(nodo);
                if (chk != null) chk.IsChecked = false;
            }
            ActualizarContadorSeleccion();
        }

        // ════════════════════════════════════════════════════════════════
        // CONJUNTOS DE TABLAS
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Carga el ComboBox de islas con las islas guardadas para la conexión activa.
        /// </summary>
        private void CargarComboIslas()
        {
            cbIslas.SelectionChanged -= cbIslas_SelectionChanged;
            cbIslas.Items.Clear();
            cbIslas.Items.Add(new ComboBoxItem { Content = "(ninguno)", Tag = null });

            if (conexionActual != null)
            {
                var cfg = ConfigManager.Cargar();
                var cc = cfg.ConjuntosTablas
                    .FirstOrDefault(c => string.Equals(c.NombreConexion, conexionActual.Nombre, StringComparison.OrdinalIgnoreCase));

                if (cc != null)
                    foreach (var conj in cc.Conjuntos)
                        cbIslas.Items.Add(new ComboBoxItem { Content = conj.Nombre, Tag = conj });
            }

            cbIslas.SelectedIndex = 0;
            cbIslas.SelectionChanged += cbIslas_SelectionChanged;
        }

        private void cbIslas_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = cbIslas.SelectedItem as ComboBoxItem;
            _islaActiva = item?.Tag as ConjuntoTablas;

            // El filtro de isla solo requiere un cambio de visibilidad en UI; sin recarga de BD
            AplicarFiltroSchemaEnUI();
        }

        private void btnGuardarIsla_Click(object sender, RoutedEventArgs e)
        {
            if (conexionActual == null)
            {
                MessageBox.Show("Seleccione una conexión primero.", "Sin conexión", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var seleccionados = GetNodosSeleccionados();
            if (seleccionados.Count == 0)
            {
                MessageBox.Show("Marque al menos una tabla o vista con su checkbox antes de guardar la isla.",
                    "Sin selección", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Pedir nombre de la isla
            var dlg = new NombreConjuntoDialog { Owner = this };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.NombreIngresado))
                return;

            string nombre = dlg.NombreIngresado.Trim();

            // Construir lista de identificadores
            var tablas = seleccionados
                .Select(n =>
                {
                    var tag = n.Tag as NodoTablaTag;
                    if (tag == null) return null;
                    // Obtener el texto visible del header para incluir schema si corresponde
                    if (n.Header is StackPanel sp)
                    {
                        var tb = sp.Children.OfType<System.Windows.Controls.TextBlock>().FirstOrDefault();
                        return tb?.Text ?? tag.Nombre;
                    }
                    return tag.Nombre;
                })
                .Where(t => t != null)
                .ToList();

            // Guardar en config
            var cfg = ConfigManager.Cargar();
            var cc = cfg.ConjuntosTablas
                .FirstOrDefault(c => string.Equals(c.NombreConexion, conexionActual.Nombre, StringComparison.OrdinalIgnoreCase));

            if (cc == null)
            {
                cc = new ConjuntosConexion { NombreConexion = conexionActual.Nombre };
                cfg.ConjuntosTablas.Add(cc);
            }

            // Si ya existe una isla con ese nombre, reemplazarla
            var existente = cc.Conjuntos.FirstOrDefault(c => string.Equals(c.Nombre, nombre, StringComparison.OrdinalIgnoreCase));
            if (existente != null)
            {
                if (MessageBox.Show($"Ya existe una isla llamada '{nombre}'. ¿Reemplazarla?",
                        "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                cc.Conjuntos.Remove(existente);
            }

            cc.Conjuntos.Add(new ConjuntoTablas { Nombre = nombre, Tablas = tablas });
            ConfigManager.Guardar(cfg);

            AppendMessage($"Isla '{nombre}' guardada con {tablas.Count} tabla(s)/vista(s).");

            // Recargar el combo y seleccionar la isla recién guardada
            CargarComboIslas();
            for (int i = 0; i < cbIslas.Items.Count; i++)
            {
                if (cbIslas.Items[i] is ComboBoxItem ci && ci.Content?.ToString() == nombre)
                {
                    _islaActiva = ci.Tag as ConjuntoTablas;
                    cbIslas.SelectionChanged -= cbIslas_SelectionChanged;
                    cbIslas.SelectedIndex = i;
                    cbIslas.SelectionChanged += cbIslas_SelectionChanged;
                    break;
                }
            }

            // Aplicar el filtro de isla sobre el árbol ya cargado (sin ir a la BD)
            AplicarFiltroSchemaEnUI();
        }

        private void btnLimpiarIsla_Click(object sender, RoutedEventArgs e)
        {
            _islaActiva = null;
            cbIslas.SelectionChanged -= cbIslas_SelectionChanged;
            cbIslas.SelectedIndex = 0;
            cbIslas.SelectionChanged += cbIslas_SelectionChanged;
            AplicarFiltroSchemaEnUI();
        }

        private void btnEliminarIsla_Click(object sender, RoutedEventArgs e)
        {
            var item = cbIslas.SelectedItem as ComboBoxItem;
            var conj = item?.Tag as ConjuntoTablas;
            if (conj == null)
            {
                MessageBox.Show("Seleccione una isla para eliminar.", "Sin selección", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show($"¿Eliminar la isla '{conj.Nombre}'?",
                    "Confirmar eliminación", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var cfg = ConfigManager.Cargar();
            var cc = cfg.ConjuntosTablas
                .FirstOrDefault(c => string.Equals(c.NombreConexion, conexionActual?.Nombre, StringComparison.OrdinalIgnoreCase));

            cc?.Conjuntos.RemoveAll(c => string.Equals(c.Nombre, conj.Nombre, StringComparison.OrdinalIgnoreCase));
            ConfigManager.Guardar(cfg);

            _islaActiva = null;
            AppendMessage($"Isla '{conj.Nombre}' eliminada.");
            CargarComboIslas();
            AplicarFiltroSchemaEnUI();
        }

        // ════════════════════════════════════════════════════════════════
        // PREFERENCIAS
        // ════════════════════════════════════════════════════════════════

        private void BtnPreferencias_Click(object sender, RoutedEventArgs e)
        {
            var ventana = new PreferenciasWindow { Owner = this };
            ventana.ConfigGuardada += cfg =>
            {
                _configApp = cfg;
                // Aplicar cambio de tema si es necesario
                bool oscuroNuevo = cfg.TemaOscuro;
                if (oscuroNuevo != _modoOscuro)
                {
                    _modoOscuro = oscuroNuevo;
                    if (_modoOscuro) _temaOscuro = LeerTemaDesdeDisco("ThemeDark.xaml");
                    else _temaClaro = LeerTemaDesdeDisco("ThemeLight.xaml");
                    AplicarTema(_modoOscuro ? _temaOscuro : _temaClaro);
                    btnToggleTema.Content = _modoOscuro ? "☀" : "🌙";
                }
            };
            ventana.ShowDialog();
            AplicarTema();
        }
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Genera un diagrama de entidad-relación en formato Draw.io con todas las
        /// tablas/vistas visibles en tvSchema y abre el navegador en app.diagrams.net
        /// con el XML comprimido (formato nativo de Draw.io vía URL).
        /// </summary>
        private async void btnEsquematizar_Click(object sender, RoutedEventArgs e)
        {
            if (conexionActual == null)
            {
                AppendMessage("No hay conexión seleccionada.");
                return;
            }

            // Si hay nodos con checkbox marcado, operar solo sobre ellos.
            // Si no hay ninguno seleccionado, usar todos los visibles (comportamiento original).
            var seleccionadosEsq = GetNodosSeleccionados();
            var nodosVisibles = seleccionadosEsq.Count > 0
                ? seleccionadosEsq
                : tvSchema.Items
                    .OfType<TreeViewItem>()
                    .Where(n => n.Visibility == System.Windows.Visibility.Visible)
                    .ToList();

            if (nodosVisibles.Count == 0)
            {
                AppendMessage("No hay tablas/vistas visibles para esquematizar.");
                return;
            }

            btnEsquematizar.IsEnabled = false;
            AppendMessage($"Esquematizando {nodosVisibles.Count} tabla(s)/vista(s)" +
                          (seleccionadosEsq.Count > 0 ? " (seleccionadas)" : "") + "...");

            try
            {
                string connStr = GetConnectionString();

                // ── 1. Recopilar metadatos de columnas y FKs ─────────────────────────
                // Estructura: nombre → (tipo, columnas, relaciones)
                var tablasMeta = new Dictionary<string, EsquemaTablaInfo>(StringComparer.OrdinalIgnoreCase);

                // Leer los nodos visibles para obtener nombre/schema/tipo
                var nodosInfo = new List<Tuple<string, string, string>>(); // schema, nombre, tipo
                foreach (var nodo in nodosVisibles)
                {
                    var nodoTag = nodo.Tag as NodoTablaTag;
                    string nombreTabla = nodoTag?.Nombre ?? nodo.Tag?.ToString() ?? string.Empty;
                    if (string.IsNullOrEmpty(nombreTabla)) continue;

                    string headerText = ObtenerHeaderText(nodo);
                    string schema = string.Empty;
                    if (headerText.Contains("."))
                        schema = headerText.Substring(0, headerText.IndexOf('.'));

                    string tipo = nodoTag?.Tipo ?? "TABLE";
                    nodosInfo.Add(Tuple.Create(schema, nombreTabla, tipo));
                }

                // Consultar columnas y FKs en background
                await Task.Run(() =>
                {
                    using (var conn = new OdbcConnection(connStr))
                    {
                        conn.Open();

                        // ── Columnas de cada tabla ──────────────────────────────────
                        foreach (var t in nodosInfo)
                        {
                            string schema = t.Item1;
                            string nombre = t.Item2;
                            string tipoObj = t.Item3;

                            var info = new EsquemaTablaInfo { Nombre = nombre, Schema = schema, Tipo = tipoObj };

                            try
                            {
                                var cols = conn.GetSchema("Columns", new string[] { null, string.IsNullOrEmpty(schema) ? null : schema, nombre });
                                foreach (DataRow col in cols.Rows)
                                {
                                    string colName = col["COLUMN_NAME"].ToString();
                                    string colTipo = col["TYPE_NAME"].ToString();
                                    string colSize = col["COLUMN_SIZE"].ToString();
                                    info.Columnas.Add(new EsquemaColumnaInfo { Nombre = colName, Tipo = colTipo, Longitud = colSize });
                                }
                            }
                            catch { /* Si falla para alguna tabla, continuar */ }

                            tablasMeta[nombre] = info;
                        }

                        // ── Relaciones FK según motor ───────────────────────────────
                        try
                        {
                            var relaciones = ObtenerRelacionesFK(conn, tablasMeta.Keys.ToList());
                            foreach (var rel in relaciones)
                            {
                                if (tablasMeta.ContainsKey(rel.TablaOrigen))
                                    tablasMeta[rel.TablaOrigen].Relaciones.Add(rel);
                            }
                        }
                        catch { /* Algunos motores no exponen FKs vía ODBC */ }
                    }
                });

                // ── 2. Preguntar plataforma ───────────────────────────────────────
                PlataformaEsquema plataforma = PlataformaEsquema.DrawIO;
                //PlataformaEsquema? plataforma = null;
                //Dispatcher.Invoke(() => plataforma = ElegirPlataformaEsquema());
                //if (plataforma == null)
                //{
                //    Dispatcher.Invoke(() => AppendMessage("Esquematización cancelada."));
                //    return;
                //}

                // ── 3. Generar y abrir según plataforma ──────────────────────────
                if (plataforma == PlataformaEsquema.DrawIO)
                {
                    // Layout jerárquico solo para Draw.io (Mermaid lo hace automáticamente)
                    var posiciones = CalcularLayoutER(tablasMeta);
                    string xmlDrawio = GenerarXmlDrawio(tablasMeta, posiciones);
                    string encoded   = ComprimirDrawio(xmlDrawio);
                    string url       = "https://app.diagrams.net/?src=about#R" + Uri.EscapeDataString(encoded);
                    Dispatcher.Invoke(() => System.Diagnostics.Process.Start(url));
                    Dispatcher.Invoke(() => AppendMessage("Diagrama generado y abierto en Draw.io."));
                }
                //else
                //{
                //    // Tldraw: mismo layout jerárquico que Draw.io, abierto como HTML local
                //    var posicionesTld   = CalcularLayoutER(tablasMeta);
                //    string htmlContent  = GenerarHtmlTldraw(tablasMeta, posicionesTld, conexionActual.Nombre);
                //    string tempPath     = System.IO.Path.Combine(
                //        System.IO.Path.GetTempPath(),
                //        "ERD_" + conexionActual.Nombre + "_" +
                //        DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".html");
                //    System.IO.File.WriteAllText(tempPath, htmlContent, System.Text.Encoding.UTF8);

                //    Dispatcher.Invoke(() =>
                //    {
                //        System.Diagnostics.Process.Start(tempPath);
                //        AppendMessage("Diagrama Tldraw abierto en el navegador.\nArchivo: " + tempPath);
                //    });
                //}
            }
            catch (Exception ex)
            {
                AppendMessage("Error generando esquema: " + ex.Message);
            }
            finally
            {
                btnEsquematizar.IsEnabled = true;
            }
        }

        // ────────────────────────────────────────────────────────────────
        // Clases auxiliares para el esquematizador
        // ────────────────────────────────────────────────────────────────

        private class EsquemaTablaInfo
        {
            public string Nombre { get; set; }
            public string Schema { get; set; }
            public string Tipo { get; set; }   // "TABLE" | "VIEW"
            public List<EsquemaColumnaInfo> Columnas { get; } = new List<EsquemaColumnaInfo>();
            public List<EsquemaRelacionInfo> Relaciones { get; } = new List<EsquemaRelacionInfo>();
        }

        private class EsquemaColumnaInfo
        {
            public string Nombre { get; set; }
            public string Tipo { get; set; }
            public string Longitud { get; set; }
        }

        private class EsquemaRelacionInfo
        {
            public string TablaOrigen { get; set; }
            public string ColumnaOrigen { get; set; }
            public string TablaDestino { get; set; }
            public string ColumnaDestino { get; set; }
        }

        // ────────────────────────────────────────────────────────────────
        // Obtener FKs según motor
        // ────────────────────────────────────────────────────────────────

        private List<EsquemaRelacionInfo> ObtenerRelacionesFK(OdbcConnection conn, List<string> tablas)
        {
            var resultado = new List<EsquemaRelacionInfo>();
            if (conexionActual == null) return resultado;

            // Intentar primero vía GetSchema("ForeignKeys") — soportado por algunos drivers ODBC
            try
            {
                var fkSchema = conn.GetSchema("ForeignKeys");
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
            switch (conexionActual.Motor)
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
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = $"PRAGMA foreign_key_list('{tabla}')";
                                using (var rdr = cmd.ExecuteReader())
                                {
                                    while (rdr.Read())
                                    {
                                        resultado.Add(new EsquemaRelacionInfo
                                        {
                                            TablaOrigen = tabla,
                                            ColumnaOrigen = rdr["from"].ToString(),
                                            TablaDestino = rdr["table"].ToString(),
                                            ColumnaDestino = rdr["to"].ToString()
                                        });
                                    }
                                }
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
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = sql;
                        using (var rdr = cmd.ExecuteReader())
                        {
                            while (rdr.Read())
                            {
                                resultado.Add(new EsquemaRelacionInfo
                                {
                                    TablaOrigen = rdr[0].ToString(),
                                    ColumnaOrigen = rdr[1].ToString(),
                                    TablaDestino = rdr[2].ToString(),
                                    ColumnaDestino = rdr[3].ToString()
                                });
                            }
                        }
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

        private string GenerarXmlDrawio(
            Dictionary<string, EsquemaTablaInfo> tablasMeta,
            Dictionary<string, Tuple<int, int>> posiciones)
        {
            const int ANCHO_TABLA   = 260;
            const int ALTO_CABECERA = 32;
            const int ALTO_FILA     = 22;

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

                    int idOrigen  = idPorTabla[rel.TablaOrigen];
                    int idDestino = idPorTabla[rel.TablaDestino];
                    string label  = rel.ColumnaOrigen + " → " + rel.ColumnaDestino;

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

        private string ComprimirDrawio(string xml)
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
        // Selección de plataforma de diagramación
        // ────────────────────────────────────────────────────────────────

        private enum PlataformaEsquema { DrawIO, Tldraw }

        /// <summary>
        /// Muestra un diálogo modal para elegir entre Draw.io y Tldraw.
        /// Devuelve null si el usuario cancela.
        /// </summary>
        private PlataformaEsquema? ElegirPlataformaEsquema()
        {
            PlataformaEsquema? resultado = null;

            // Leer colores reales del tema activo para aplicarlos a los controles
            // creados en código (los estilos implícitos no se propagan a ventanas
            // instanciadas programáticamente sin ResourceDictionary propio).
            var bgBrush   = (System.Windows.Media.Brush)this.FindResource("BrushWindowBG");
            var fgBrush   = (System.Windows.Media.Brush)this.FindResource("BrushFG");
            var btnBG     = (System.Windows.Media.Brush)this.FindResource("BrushBtnBG");
            var btnBorder = (System.Windows.Media.Brush)this.FindResource("BrushBtnBorder");

            var dlg = new Window
            {
                Title                 = "Abrir diagrama en…",
                Width                 = 400,
                Height                = 170,
                ResizeMode            = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = this,
                Background            = bgBrush
            };

            var root = new Grid { Margin = new Thickness(24, 20, 24, 20) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lbl = new TextBlock
            {
                Text         = "¿En qué plataforma querés abrir el esquema?",
                TextWrapping = TextWrapping.Wrap,
                Foreground   = fgBrush,
                Margin       = new Thickness(0, 0, 0, 18)
            };
            Grid.SetRow(lbl, 0);

            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetRow(btnRow, 2);

            var mkBtn = new Func<string, Button>(txt => new Button
            {
                Content         = txt,
                Width           = 96,
                Margin          = new Thickness(8, 0, 8, 0),
                Padding         = new Thickness(4, 6, 4, 6),
                Background      = btnBG,
                Foreground      = fgBrush,
                BorderBrush     = btnBorder,
                BorderThickness = new Thickness(1)
            });

            var btnDrawio   = mkBtn("Draw.io");
            var btnTldraw   = mkBtn("Tldraw");
            var btnCancelar = mkBtn("Cancelar");

            btnDrawio.Click   += (s, ev) => { resultado = PlataformaEsquema.DrawIO; dlg.Close(); };
            btnTldraw.Click   += (s, ev) => { resultado = PlataformaEsquema.Tldraw; dlg.Close(); };
            btnCancelar.Click += (s, ev) => dlg.Close();

            btnRow.Children.Add(btnDrawio);
            btnRow.Children.Add(btnTldraw);
            btnRow.Children.Add(btnCancelar);

            root.Children.Add(lbl);
            root.Children.Add(btnRow);
            dlg.Content = root;
            dlg.ShowDialog();
            return resultado;
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
        private Dictionary<string, Tuple<int, int>> CalcularLayoutER(
            Dictionary<string, EsquemaTablaInfo> tablasMeta)
        {
            const int ANCHO     = 260;
            const int ALTO_CAB  = 32;
            const int ALTO_FILA = 22;
            const int H_GAP     = 200;   // espacio horizontal entre tablas del mismo nivel
            const int V_GAP     = 200;   // espacio vertical entre niveles
            const int COMP_GAP  = 380;   // espacio entre componentes desconectados
            const int COLS_MAX  = 4;     // máx columnas para grilla sin FK

            var posiciones = new Dictionary<string, Tuple<int, int>>(StringComparer.OrdinalIgnoreCase);
            var tablas = tablasMeta.Keys.ToList();
            if (tablas.Count == 0) return posiciones;

            // ── 1. Construir grafo de adyacencia ─────────────────────────────────
            var inEdges  = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var outEdges = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var adjUnd   = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var t in tablas)
            {
                inEdges[t]  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                outEdges[t] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                adjUnd[t]   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            var visitados   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                bool tieneFKs  = comp.Any(t =>
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
                        else           { xOff += ANCHO + H_GAP; }
                    }

                    int anchoComp = Math.Min(comp.Count, cols) * (ANCHO + H_GAP) - H_GAP;
                    xGlobal += anchoComp + COMP_GAP;
                }
                else
                {
                    // ── Layout jerárquico guiado por FK ─────────────────────────

                    // Raíces de layout = tablas padre puras (sin outEdges dentro del comp)
                    var raices = comp
                        .Where(t => !outEdges[t].Any(d => tablasComp.Contains(d)))
                        .ToList();
                    // Fallback: tabla con más hijos (en ciclos)
                    if (raices.Count == 0)
                        raices = new List<string>
                        {
                            comp.OrderByDescending(t => inEdges[t].Count(s => tablasComp.Contains(s))).First()
                        };

                    // BFS desde raíces siguiendo inEdges (hijos en layout = nivel+1)
                    var nivel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    var colaL = new Queue<string>();
                    foreach (var r in raices) { nivel[r] = 0; colaL.Enqueue(r); }

                    while (colaL.Count > 0)
                    {
                        var cur = colaL.Dequeue();
                        foreach (var hijo in inEdges[cur])
                        {
                            if (!tablasComp.Contains(hijo)) continue;
                            int nNivel = nivel[cur] + 1;
                            if (!nivel.ContainsKey(hijo) || nivel[hijo] < nNivel)
                            {
                                nivel[hijo] = nNivel;
                                colaL.Enqueue(hijo);
                            }
                        }
                    }
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
                        var nivHijos  = porNivel[niv2 + 1];

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
                    int maxCols   = porNivel.Values.Max(lst => lst.Count);
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
                        var lst       = kvp.Value;
                        int anchoNiv  = lst.Count * (ANCHO + H_GAP) - H_GAP;
                        int xStart    = xGlobal + (anchoComp - anchoNiv) / 2;

                        for (int i = 0; i < lst.Count; i++)
                            posiciones[lst[i]] = Tuple.Create(xStart + i * (ANCHO + H_GAP), yPorNivel[kvp.Key]);
                    }

                    xGlobal += anchoComp + COMP_GAP;
                }
            }

            return posiciones;
        }

        // ────────────────────────────────────────────────────────────────
        // Generador de HTML con Tldraw (ERD editable en el navegador)
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Genera un archivo HTML autocontenido con el ERD cargado en Tldraw vía CDN.
        /// Las formas se crean programáticamente con editor.createShapes(), evitando
        /// por completo el paso de importar un archivo .tldr (y sus problemas de versión).
        /// Se abre directamente en el navegador predeterminado; el diagrama es totalmente editable.
        /// </summary>
        private string GenerarHtmlTldraw(
            Dictionary<string, EsquemaTablaInfo> tablasMeta,
            Dictionary<string, Tuple<int, int>> posiciones,
            string nombreConexion)
        {
            const int ANCHO     = 260;
            const int ALTO_CAB  = 32;
            const int ALTO_FILA = 22;
            const int ALTO_MIN  = 80;

            // ── Serializar tablas + relaciones como JSON embebido ─────────────────
            var sbTablas = new System.Text.StringBuilder();
            sbTablas.Append("[\n");
            bool pt = true;
            foreach (var kvp in tablasMeta)
            {
                EsquemaTablaInfo t = kvp.Value;
                if (!pt) sbTablas.Append(",\n");
                pt = false;

                string display = string.IsNullOrEmpty(t.Schema)
                    ? t.Nombre
                    : t.Schema + "." + t.Nombre;

                sbTablas.Append("  {");
                sbTablas.Append("\"name\":"    + TldrJsStr(t.Nombre)  + ",");
                sbTablas.Append("\"display\":" + TldrJsStr(display)   + ",");
                sbTablas.Append("\"tipo\":"    + TldrJsStr(t.Tipo)    + ",");
                sbTablas.Append("\"cols\":[");
                bool pc = true;
                foreach (var col in t.Columnas)
                {
                    if (!pc) sbTablas.Append(",");
                    pc = false;
                    string tipoCom = col.Tipo +
                        (string.IsNullOrEmpty(col.Longitud) || col.Longitud == "0"
                            ? "" : "(" + col.Longitud + ")");
                    sbTablas.Append("{\"n\":" + TldrJsStr(col.Nombre) +
                                    ",\"t\":" + TldrJsStr(tipoCom)    + "}");
                }
                sbTablas.Append("],\"rels\":[");
                bool pr = true;
                foreach (var rel in t.Relaciones)
                {
                    if (!pr) sbTablas.Append(",");
                    pr = false;
                    sbTablas.Append("{\"from\":" + TldrJsStr(rel.TablaOrigen)  +
                                    ",\"col\":"  + TldrJsStr(rel.ColumnaOrigen) +
                                    ",\"to\":"   + TldrJsStr(rel.TablaDestino)  + "}");
                }
                sbTablas.Append("]}");
            }
            sbTablas.Append("\n]");

            // ── Serializar posiciones como JSON embebido ──────────────────────────
            var sbPos = new System.Text.StringBuilder();
            sbPos.Append("{\n");
            bool pp = true;
            foreach (var kvp in posiciones)
            {
                if (!pp) sbPos.Append(",\n");
                pp = false;
                sbPos.Append("  " + TldrJsStr(kvp.Key) +
                             ":{\"x\":" + kvp.Value.Item1 +
                             ",\"y\":"  + kvp.Value.Item2 + "}");
            }
            sbPos.Append("\n}");

            string titulo = (nombreConexion ?? "")
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace("\"", "&quot;");
            string fecha  = DateTime.Now.ToString("dd/MM/yyyy HH:mm");

            // ── HTML final ────────────────────────────────────────────────────────
            // Las formas se crean con editor.createShapes() en el callback onMount,
            // usando la API de la versión de tldraw que sirve esm.sh en ese momento.
            // Esto garantiza compatibilidad total sin depender de versiones de schema.
            return
"<!DOCTYPE html>\n" +
"<html lang=\"es\">\n" +
"<head>\n" +
"  <meta charset=\"UTF-8\"/>\n" +
"  <meta name=\"viewport\" content=\"width=device-width,initial-scale=1.0\"/>\n" +
"  <title>ERD \u2014 " + titulo + "</title>\n" +
"  <link rel=\"stylesheet\" href=\"https://unpkg.com/@tldraw/tldraw/tldraw.css\"/>\n" +
"  <style>\n" +
"    *,*::before,*::after{box-sizing:border-box;margin:0;padding:0}\n" +
"    html,body,#root{width:100%;height:100%;overflow:hidden}\n" +
"  </style>\n" +
"</head>\n" +
"<body>\n" +
"  <div id=\"root\"></div>\n" +
"  <script type=\"module\">\n" +
"    // ?deps=react@18,react-dom@18 fuerza a tldraw Y a todas sus sub-dependencias\n" +
"    // (radix-ui, etc.) a usar la misma instancia de React que importamos abajo.\n" +
"    // Sin esto, esm.sh puede resolver react@19-canary para algunas deps,\n" +
"    // creando dos instancias de React y el error 'Cannot read useState of null'.\n" +
"    import { createElement } from 'https://esm.sh/react@18';\n" +
"    import { createRoot }    from 'https://esm.sh/react-dom@18/client';\n" +
"    import { Tldraw, createShapeId }\n" +
"        from 'https://esm.sh/@tldraw/tldraw@2?deps=react@18,react-dom@18';\n" +
"\n" +
"    // ── Datos del ERD (generados por QueryAnalyzer " + fecha + ") ──────────\n" +
"    const TABLAS     = " + sbTablas.ToString() + ";\n" +
"    const POSICIONES = " + sbPos.ToString()    + ";\n" +
"    const ANCHO      = " + ANCHO     + ";\n" +
"    const ALTO_CAB   = " + ALTO_CAB  + ";\n" +
"    const ALTO_FILA  = " + ALTO_FILA + ";\n" +
"    const ALTO_MIN   = " + ALTO_MIN  + ";\n" +
"\n" +
"    function altoTabla(cols) {\n" +
"      return Math.max(ALTO_MIN, ALTO_CAB + cols.length * ALTO_FILA + 8);\n" +
"    }\n" +
"\n" +
"    function App() {\n" +
"      function handleMount(editor) {\n" +
"        const tableShapes = [];\n" +
"        const arrowShapes = [];\n" +
"\n" +
"        // 1. Shapes de tablas\n" +
"        TABLAS.forEach(t => {\n" +
"          const id  = createShapeId('tbl_' + t.name.replace(/[^a-zA-Z0-9]/g, '_'));\n" +
"          t._shapeId = id;   // guardar para flechas\n" +
"          const pos = POSICIONES[t.name] || { x: 0, y: 0 };\n" +
"          const h   = altoTabla(t.cols);\n" +
"          const hdr = (t.tipo === 'VIEW' ? '\\u2B21 ' : '') + t.display;\n" +
"          const sep = '\\u2500'.repeat(26);\n" +
"          const body = t.cols.map(c => '  ' + c.n + '  ' + c.t).join('\\n');\n" +
"          const text  = t.cols.length ? hdr + '\\n' + sep + '\\n' + body : hdr;\n" +
"          tableShapes.push({\n" +
"            id, type: 'geo', x: pos.x, y: pos.y,\n" +
"            props: {\n" +
"              geo: 'rectangle', w: ANCHO, h, text,\n" +
"              color: t.tipo === 'VIEW' ? 'blue' : 'orange',\n" +
"              fill: 'semi', font: 'mono', size: 's',\n" +
"              align: 'start', verticalAlign: 'start',\n" +
"              growY: 0, url: ''\n" +
"            }\n" +
"          });\n" +
"        });\n" +
"\n" +
"        editor.createShapes(tableShapes);\n" +
"\n" +
"        // 2. Flechas FK  (point-based: sin binding, sin dependencia de schema)\n" +
"        const yaVistas = new Set();\n" +
"        let fkIdx = 0;\n" +
"        TABLAS.forEach(t => {\n" +
"          (t.rels || []).forEach(rel => {\n" +
"            const key = rel.from + '\\x00' + rel.col + '\\x00' + rel.to;\n" +
"            if (yaVistas.has(key)) return;\n" +
"            yaVistas.add(key);\n" +
"            const pf = POSICIONES[rel.from];\n" +
"            const pd = POSICIONES[rel.to];\n" +
"            if (!pf || !pd) return;\n" +
"            const tFrom = TABLAS.find(x => x.name === rel.from);\n" +
"            const hFrom = tFrom ? altoTabla(tFrom.cols) : ALTO_MIN;\n" +
"            // salida: borde inferior-centro del hijo; llegada: borde superior-centro del padre\n" +
"            const sx = pf.x + ANCHO / 2,  sy = pf.y + hFrom;\n" +
"            const ex = pd.x + ANCHO / 2,  ey = pd.y;\n" +
"            arrowShapes.push({\n" +
"              id: createShapeId('fk_' + (fkIdx++)),\n" +
"              type: 'arrow', x: 0, y: 0,\n" +
"              props: {\n" +
"                start: { x: sx, y: sy },\n" +
"                end:   { x: ex, y: ey },\n" +
"                arrowheadStart: 'none', arrowheadEnd: 'arrow',\n" +
"                text: rel.col,\n" +
"                size: 's', color: 'black', fill: 'none',\n" +
"                font: 'draw', dash: 'solid',\n" +
"                labelPosition: 0.5, bend: 0, labelColor: 'black'\n" +
"              }\n" +
"            });\n" +
"          });\n" +
"        });\n" +
"\n" +
"        if (arrowShapes.length) editor.createShapes(arrowShapes);\n" +
"        editor.zoomToFit();\n" +
"      }\n" +
"      return createElement(Tldraw, { onMount: handleMount });\n" +
"    }\n" +
"\n" +
"    createRoot(document.getElementById('root')).render(createElement(App));\n" +
"  </script>\n" +
"</body>\n" +
"</html>";
        }

        /// <summary>Convierte un string C# en un literal de cadena JSON válido para embeber en JS.</summary>
        private static string TldrJsStr(string s)
        {
            if (s == null) return "\"\"";
            return "\""
                + s.Replace("\\", "\\\\")
                   .Replace("\"", "\\\"")
                   .Replace("\n", "\\n")
                   .Replace("\r", "")
                + "\"";
        }

        /// <summary>
        /// Documenta todas las tablas/vistas visibles en tvSchema (respetando filtros activos).
        /// </summary>
        private async void btnDocumentar_Click(object sender, RoutedEventArgs e)
        {
            if (conexionActual == null)
            {
                AppendMessage("No hay conexión seleccionada.");
                return;
            }

            // Si hay nodos con checkbox marcado, operar solo sobre ellos.
            // Si no hay ninguno seleccionado, usar todos los visibles (comportamiento original).
            var seleccionadosDoc = GetNodosSeleccionados();
            var nodosVisibles = seleccionadosDoc.Count > 0
                ? seleccionadosDoc
                : tvSchema.Items
                    .OfType<TreeViewItem>()
                    .Where(n => n.Visibility == System.Windows.Visibility.Visible)
                    .ToList();

            if (nodosVisibles.Count == 0)
            {
                AppendMessage("No hay tablas/vistas visibles para documentar.");
                return;
            }

            // Proponer nombre de archivo
            string esquemaActivo = string.IsNullOrEmpty(_filtroSchema) ? "Esquema" : _filtroSchema;
            string nombreSugerido = seleccionadosDoc.Count > 0
                ? $"{conexionActual.Nombre}_{seleccionadosDoc.Count}tablas_{DateTime.Now:yyyyMMdd}.docx"
                : $"{conexionActual.Nombre}_{esquemaActivo}_{DateTime.Now:yyyyMMdd}.docx";

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Guardar documentación",
                Filter = "Word Document (*.docx)|*.docx",
                FileName = nombreSugerido,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (sfd.ShowDialog() != true) return;

            btnDocumentar.IsEnabled = false;
            AppendMessage($"Documentando {nodosVisibles.Count} tabla(s)/vista(s)" +
                          (seleccionadosDoc.Count > 0 ? " (seleccionadas)" : "") + "...");

            try
            {
                var tablas = new List<InfoTablaDoc>();
                int procesadas = 0;

                foreach (var nodo in nodosVisibles)
                {
                    // El Tag del nodo contiene NodoTablaTag con nombre y tipo
                    var nodoTag = nodo.Tag as NodoTablaTag;
                    string nombreTabla = nodoTag?.Nombre ?? nodo.Tag?.ToString() ?? string.Empty;
                    if (string.IsNullOrEmpty(nombreTabla)) continue;

                    // Intentar determinar el schema desde el header del nodo
                    string headerText = ObtenerHeaderText(nodo);
                    string schema = string.Empty;
                    string nombre = nombreTabla;
                    if (headerText.Contains("."))
                    {
                        schema = headerText.Substring(0, headerText.IndexOf('.'));
                    }

                    // Determinar tipo (tabla o vista) desde el Tag del nodo
                    string tipo = nodoTag?.Tipo ?? "TABLE";

                    var info = await DocumentadorService.GetInfoTablaAsync(
                        conexionActual, schema, nombre, tipo);
                    tablas.Add(info);
                    procesadas++;

                    if (procesadas % 20 == 0)
                        AppendMessage($"  ... {procesadas}/{nodosVisibles.Count}");
                }

                string titulo = $"Documentación: {conexionActual.Nombre}" +
                                (string.IsNullOrEmpty(_filtroSchema) ? string.Empty : $" — Esquema: {_filtroSchema}");

                DocumentadorService.GenerarDocumento(sfd.FileName, tablas, titulo);
                AppendMessage($"Documentación generada: {sfd.FileName}");
                System.Diagnostics.Process.Start(sfd.FileName);
            }
            catch (Exception ex)
            {
                AppendMessage("Error generando documentación: " + ex.Message);
            }
            finally
            {
                btnDocumentar.IsEnabled = true;
            }
        }

        /// <summary>
        /// Documenta una única tabla/vista (llamado desde el context menu del TreeView).
        /// </summary>
        private async Task DocumentarTablaAsync(string schema, string tabla, string tipo)
        {
            if (conexionActual == null) return;

            string etiqueta = (tipo == "VIEW" || tipo == "V" || tipo == "view") ? "Vista" : "Tabla";
            string nombreSugerido = string.IsNullOrEmpty(schema)
                ? $"{tabla}.docx"
                : $"{schema}_{tabla}.docx";

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                Title = $"Guardar documentación — {etiqueta}: {tabla}",
                Filter = "Word Document (*.docx)|*.docx",
                FileName = nombreSugerido,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (sfd.ShowDialog() != true) return;

            AppendMessage($"Documentando {etiqueta.ToLower()} {tabla}...");

            try
            {
                var info = await DocumentadorService.GetInfoTablaAsync(
                    conexionActual, schema, tabla, tipo);

                string titulo = $"Documentación: {tabla}";
                DocumentadorService.GenerarDocumento(sfd.FileName, new List<InfoTablaDoc> { info }, titulo);
                AppendMessage($"Documentación generada: {sfd.FileName}");
                System.Diagnostics.Process.Start(sfd.FileName);
            }
            catch (Exception ex)
            {
                AppendMessage("Error generando documentación: " + ex.Message);
            }
        }

        // ════════════════════════════════════════════════════════════════
        // (líneas 2037-2052 aprox.) por el siguiente bloque completo.
        // ════════════════════════════════════════════════════════════════

        static public string scriptDiseño = string.Empty;

        // En el handler del ContextMenu del TreeView (o donde ya tenés el click derecho):
        private string Diseñar(string schema, string tabla)
        {
            var w = new TableDesignerWindow(conexionActual, tabla) { Owner = this };
            scriptDiseño = string.Empty;
            w.ShowDialog();

            if (scriptDiseño.Length > 0)
            {
                txtQuery.Text = scriptDiseño;

                if (!w.SoloGenerarScript)
                {
                    // Aplicar directamente: ejecutar y refrescar árbol
                    BtnExecute_Click(this, new RoutedEventArgs());

                    string nuevoNombre = w.NuevoNombreTabla;
                    bool huboRenombre = !string.IsNullOrEmpty(nuevoNombre);
                    string nombreFinalTabla = huboRenombre ? nuevoNombre : tabla;

                    TreeViewItem nodoTabla = EncontrarNodoTabla(tvSchema, tabla, schema);
                    if (nodoTabla != null)
                    {
                        if (huboRenombre)
                        {
                            ActualizarHeaderNodoTabla(nodoTabla, schema, nombreFinalTabla);
                            nodoTabla.Tag = new NodoTablaTag(nombreFinalTabla, (nodoTabla.Tag as NodoTablaTag)?.Tipo ?? "TABLE");
                        }
                        RecargarNodoTabla(nodoTabla, schema, nombreFinalTabla);
                    }
                    else
                    {
                        CargarEsquema();
                    }
                }
                else
                {
                    // Solo generar script: cargar en editor, el usuario lo ejecuta manualmente
                    AppendMessage("Script cargado en el editor. Revisalo, editalo si es necesario y ejecutalo cuando estés listo.");
                }
            }

            return string.Empty;
        }

        // ─────────────────────────────────────────────────────────────────────────────
        // Helpers de recarga parcial del nodo
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Busca en el TreeView el TreeViewItem que corresponde a la tabla dada,
        /// comparando por Tag (nombre de tabla) y opcionalmente por schema en el header.
        /// </summary>
        private TreeViewItem EncontrarNodoTabla(TreeView tv, string tabla, string schema)
        {
            foreach (TreeViewItem item in tv.Items)
            {
                // El Tag del nodo se asigna con NodoTablaTag (nombre + tipo) en Cargar()
                string tagNombre = item.Tag is NodoTablaTag ntt ? ntt.Nombre : item.Tag as string;
                if (tagNombre != null &&
                    string.Equals(tagNombre, tabla, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }
            return null;
        }

        /// <summary>
        /// Actualiza el TextBlock de texto dentro del StackPanel header del nodo
        /// para reflejar el nuevo nombre de la tabla tras un RENAME.
        /// </summary>
        private void ActualizarHeaderNodoTabla(TreeViewItem nodo, string schema, string nuevoNombre)
        {
            if (!(nodo.Header is StackPanel sp)) return;

            string nuevoHeaderText = string.IsNullOrEmpty(schema)
                ? nuevoNombre
                : string.Format("{0}.{1}", schema, nuevoNombre);

            foreach (var child in sp.Children)
            {
                if (child is TextBlock tb)
                {
                    tb.Text = nuevoHeaderText;
                    break;
                }
            }
        }

        /// <summary>
        /// Limpia los hijos del nodo de la tabla y fuerza una recarga inmediata
        /// de sus columnas e índices (igual que al expandir por primera vez,
        /// pero sin esperar a que el usuario colapse y vuelva a expandir).
        /// </summary>
        private void RecargarNodoTabla(TreeViewItem nodoTabla, string schema, string nombreTabla)
        {
            var columnaIcon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/columna.png"));
            var columnaClaveIcon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/columnaClave.png"));
            var claveIcon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/clave.png"));
            int tamañoIconos = 20;

            // Freeze para que los bitmaps sean seguros entre hilos
            columnaIcon.Freeze();
            columnaClaveIcon.Freeze();
            claveIcon.Freeze();

            string connStr = GetConnectionString();

            // ── Clave del fix ──────────────────────────────────────────────────────
            // 1. Limpiamos los hijos existentes.
            // 2. Ponemos el placeholder "Cargando…" (igual que al crear el nodo en Cargar()).
            //    Con al menos un hijo el TreeView vuelve a mostrar la flecha de expansión.
            // 3. CargarDetallesTabla comprueba ese placeholder y lo elimina antes de
            //    agregar las columnas reales (mismo patrón que el handler Expanded).
            nodoTabla.Items.Clear();
            nodoTabla.Items.Add(new TreeViewItem { Header = "Cargando..." });

            // Expandir para que el usuario vea el resultado inmediatamente
            nodoTabla.IsExpanded = true;

            Task.Run(async () =>
            {
                try
                {
                    // Pequeña pausa para que la UI se refresque y muestre el nodo expandido
                    // antes de que la carga bloquee el Dispatcher con muchos Invokes.
                    await Task.Delay(50);

                    // Eliminar el placeholder igual que lo hace el handler Expanded nativo
                    Dispatcher.Invoke(() => nodoTabla.Items.Clear());

                    CargarDetallesTabla(
                        nodoTabla, schema, nombreTabla,
                        "TABLE",
                        connStr,
                        columnaIcon, columnaClaveIcon, claveIcon, tamañoIconos);
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                        AppendMessage($"Error recargando nodo de '{nombreTabla}': {ex.Message}"));
                }
            });
        }

        // ════════════════════════════════════════════════════════════════

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

            int offset = txtQuery.SelectionStart;
            int length = txtQuery.SelectionLength;

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
            // Limpiar búsqueda activa al explorar manualmente
            _resultadoBusqueda = null;
            _terminoBusqueda   = "";
            txtBuscar.Text     = "";

            _explorarCTS?.Cancel();
            _explorarCTS = new CancellationTokenSource();
            LanzarCargarEsquema();
        }

        private void btnExplorarConsultas_Click(object sender, RoutedEventArgs e)
        {
            // Cancelar si el otro sigue corriendo
            _explorarCTS?.Cancel();

            _explorarCTS = new CancellationTokenSource();

            List<string> tablasConsulta = ExtraerTablas(txtQuery.Text);
            CargarEsquema(string.Empty, tablasConsulta, _explorarCTS.Token);
        }

        // ── Selector de tipo (Tablas / Vistas / Ambas) ───────────────────────────────
        private void cbTipoObjeto_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbTipoObjeto.SelectedItem is ComboBoxItem item)
            {
                _filtroTipo = item.Tag?.ToString() ?? "BOTH";
                // Refrescar esquema con el nuevo filtro de tipo
                LanzarCargarEsquema();
            }
        }

        // ── Selector de schema ───────────────────────────────────────────────────────
        private void cbSchema_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cbSchema.SelectedItem is ComboBoxItem item)
            {
                _filtroSchema = item.Tag?.ToString() ?? "";
                // Refrescar el TreeView filtrando por schema (no recarga de la BD)
                AplicarFiltroSchemaEnUI();
            }
        }

        /// <summary>
        /// Lanza una recarga completa del esquema aplicando _filtroTipo.
        /// También actualiza el combo de schemas con los disponibles.
        /// </summary>
        private void LanzarCargarEsquema()
        {
            // Guard: puede llamarse durante InitializeComponent antes de que tvSchema esté listo
            if (tvSchema == null || conexionActual == null) return;

            _explorarCTS?.Cancel();
            _explorarCTS = new CancellationTokenSource();

            string[] tipos = _filtroTipo == "BOTH" ? new[] { "TABLE", "VIEW" }
                           : _filtroTipo == "TABLE" ? new[] { "TABLE" }
                                                    : new[] { "VIEW" };

            CargarEsquemaConTipos(tipos, _explorarCTS.Token);
        }

        /// <summary>
        /// Versión de CargarEsquema que acepta un array de tipos y, al terminar,
        /// puebla el ComboBox de schemas con los schemas distintos encontrados.
        /// </summary>
        private async void CargarEsquemaConTipos(string[] tipos, CancellationToken token)
        {
            if (conexionActual == null) { AppendMessage("No hay conexión seleccionada."); return; }

            string connStr = GetConnectionString();

            var tablaIconUri = new Uri("pack://application:,,,/Assets/tabla.png");
            var columnaIconUri = new Uri("pack://application:,,,/Assets/columna.png");
            var columnaClaveIconUri = new Uri("pack://application:,,,/Assets/columnaClave.png");
            var claveIconUri = new Uri("pack://application:,,,/Assets/clave.png");
            var vistaIconUri = new Uri("pack://application:,,,/Assets/vista.png");

            var tablaIcon = new System.Windows.Media.Imaging.BitmapImage(tablaIconUri);
            var columnaIcon = new System.Windows.Media.Imaging.BitmapImage(columnaIconUri);
            var columnaClaveIcon = new System.Windows.Media.Imaging.BitmapImage(columnaClaveIconUri);
            var claveIcon = new System.Windows.Media.Imaging.BitmapImage(claveIconUri);
            var vistaIcon = new System.Windows.Media.Imaging.BitmapImage(vistaIconUri);
            int tamañoIconos = 20;

            // Lista de schemas encontrados, para luego poblar cbSchema
            var schemasEncontrados = new List<string>();

            await Task.Run(() =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();

                    // Usamos el Cargar() existente que ya sabe dibujar los nodos.
                    // Si hay una búsqueda activa, limitamos al conjunto de tablas encontradas.
                    Cargar(tipos, string.Empty, _resultadoBusqueda, tvSchema, connStr,
                           tablaIcon, columnaIcon, columnaClaveIcon, claveIcon, vistaIcon,
                           tamañoIconos, token);

                    // Recopilar schemas de los nodos ya creados (corremos en UI thread vía Invoke)
                    Dispatcher.Invoke(() =>
                    {
                        foreach (TreeViewItem nodo in tvSchema.Items)
                        {
                            // El header del nodo es un StackPanel; el TextBlock tiene "schema.tabla" o "tabla"
                            string headerText = ObtenerHeaderText(nodo);
                            if (!string.IsNullOrEmpty(headerText) && headerText.Contains("."))
                            {
                                string schema = headerText.Substring(0, headerText.IndexOf('.'));
                                if (!schemasEncontrados.Contains(schema, StringComparer.OrdinalIgnoreCase))
                                    schemasEncontrados.Add(schema);
                            }
                        }
                    });
                }
                catch (TaskCanceledException) { Dispatcher.Invoke(() => AppendMessage("Exploración cancelada.")); }
                catch (OperationCanceledException) { Dispatcher.Invoke(() => AppendMessage("Exploración cancelada.")); }
                catch (Exception ex) { Dispatcher.Invoke(() => AppendMessage("Error al cargar esquema: " + ex.Message)); }
            });

            // Poblar cbSchema preservando la selección actual
            PoblarCbSchema(schemasEncontrados);
        }

        /// <summary>
        /// Extrae el texto visible (primer TextBlock) del header de un TreeViewItem de tabla.
        /// </summary>
        private string ObtenerHeaderText(TreeViewItem nodo)
        {
            if (nodo.Header is System.Windows.Controls.StackPanel sp)
            {
                foreach (var child in sp.Children)
                    if (child is System.Windows.Controls.TextBlock tb)
                        return tb.Text;
            }
            return nodo.Header?.ToString() ?? string.Empty;
        }

        /// <summary>
        /// Puebla el ComboBox de schemas con "(todos)" + la lista recibida.
        /// Intenta preservar la selección anterior; si no existe, selecciona "(todos)".
        /// Luego aplica el filtro visual sobre el TreeView.
        /// </summary>
        private void PoblarCbSchema(List<string> schemas)
        {
            string seleccionAnterior = _filtroSchema;

            cbSchema.SelectionChanged -= cbSchema_SelectionChanged;  // evitar recursión
            cbSchema.Items.Clear();

            var itemTodos = new ComboBoxItem { Content = "(todos)", Tag = "" };
            cbSchema.Items.Add(itemTodos);

            foreach (string s in schemas.OrderBy(x => x))
            {
                var ci = new ComboBoxItem { Content = s, Tag = s };
                cbSchema.Items.Add(ci);
            }

            // Restaurar selección
            bool restaurado = false;
            if (!string.IsNullOrEmpty(seleccionAnterior))
            {
                foreach (ComboBoxItem ci in cbSchema.Items)
                {
                    if (string.Equals(ci.Tag?.ToString(), seleccionAnterior, StringComparison.OrdinalIgnoreCase))
                    {
                        cbSchema.SelectedItem = ci;
                        restaurado = true;
                        break;
                    }
                }
            }
            if (!restaurado)
            {
                cbSchema.SelectedIndex = 0;
                _filtroSchema = ""; // el evento está desuscrito, actualizar manualmente
            }

            cbSchema.SelectionChanged += cbSchema_SelectionChanged;

            // Aplicar filtro visual con la selección resultante
            AplicarFiltroSchemaEnUI();
        }

        /// <summary>
        /// Muestra u oculta los nodos del TreeView según el schema seleccionado
        /// y la isla activa, SIN tocar la base de datos. Es una operación puramente de UI.
        /// </summary>
        private void AplicarFiltroSchemaEnUI()
        {
            // Guard: puede llamarse durante InitializeComponent antes de que tvSchema esté listo
            if (tvSchema == null) return;

            bool mostrarTodosSchemas = string.IsNullOrEmpty(_filtroSchema);

            foreach (TreeViewItem nodo in tvSchema.Items)
            {
                string headerText = ObtenerHeaderText(nodo);

                // ── Filtro de schema ──────────────────────────────────────────
                bool visiblePorSchema = mostrarTodosSchemas
                    || headerText.StartsWith(_filtroSchema + ".", StringComparison.OrdinalIgnoreCase);

                if (!visiblePorSchema)
                {
                    nodo.Visibility = Visibility.Collapsed;
                    continue;
                }

                // ── Filtro de isla (si hay una activa) ───────────────────────
                if (_islaActiva != null)
                {
                    string capTabla = headerText;
                    string capSchema = string.Empty;
                    int punto = headerText.IndexOf('.');
                    if (punto >= 0)
                    {
                        capSchema = headerText.Substring(0, punto);
                        capTabla  = headerText.Substring(punto + 1);
                    }
                    string idNodo = string.IsNullOrEmpty(capSchema) ? capTabla : $"{capSchema}.{capTabla}";
                    bool enIsla = _islaActiva.Tablas.Any(t =>
                        string.Equals(t, idNodo,    StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t, capTabla,  StringComparison.OrdinalIgnoreCase));
                    nodo.Visibility = enIsla ? Visibility.Visible : Visibility.Collapsed;
                }
                else
                {
                    nodo.Visibility = Visibility.Visible;
                }
            }

            ActualizarContadorExplorador();
        }

        /// <summary>
        /// Actualiza txtExplorar con el total de nodos cargados y cuántos
        /// son visibles según los filtros activos (schema e isla).
        /// </summary>
        private void ActualizarContadorExplorador()
        {
            if (tvSchema == null || txtExplorar == null) return;

            int total   = tvSchema.Items.Count;
            int visibles = 0;
            foreach (TreeViewItem nodo in tvSchema.Items)
                if (nodo.Visibility == Visibility.Visible)
                    visibles++;

            bool hayFiltroSchema  = !string.IsNullOrEmpty(_filtroSchema);
            bool hayFiltroIsla    = _islaActiva != null;
            bool hayBusqueda      = !string.IsNullOrEmpty(_terminoBusqueda);

            if (!hayFiltroSchema && !hayFiltroIsla && !hayBusqueda)
            {
                // Sin filtros: texto plano
                txtExplorar.Text = $"{total} tablas/vistas";
            }
            else
            {
                // Con filtros: indicar visibles sobre total + qué filtros están activos
                var partes = new System.Collections.Generic.List<string>();
                if (hayBusqueda)     partes.Add($"buscar: \"{_terminoBusqueda}\"");
                if (hayFiltroSchema) partes.Add($"esquema: {_filtroSchema}");
                if (hayFiltroIsla)   partes.Add($"isla: {_islaActiva.Nombre}");

                txtExplorar.Text =
                    $"{visibles} de {total}" +
                    $"\n({string.Join(" · ", partes)})";
            }
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

        // ── Colapsar / expandir el panel derecho completo ────────────────────────
        private bool   _derechoColapsado = false;
        private double _derechoAnchoExpandido = 0;

        private void btnToggleDerecho_Click(object sender, RoutedEventArgs e)
        {
            // Column 3 del Grid padre = grdDerecho
            var colDef = ((Grid)grdDerecho.Parent).ColumnDefinitions[3];

            if (!_derechoColapsado)
                _derechoAnchoExpandido = grdDerecho.ActualWidth;

            double from = colDef.ActualWidth;
            double to   = _derechoColapsado ? _derechoAnchoExpandido : 0;

            var anim = new GridLengthAnimation
            {
                From         = new GridLength(from, GridUnitType.Pixel),
                To           = new GridLength(to,   GridUnitType.Pixel),
                Duration     = new Duration(TimeSpan.FromMilliseconds(250)),
                FillBehavior = FillBehavior.Stop
            };

            anim.Completed += (s, _) =>
            {
                colDef.Width          = new GridLength(to, GridUnitType.Pixel);
                _derechoColapsado     = !_derechoColapsado;
                btnToggleDerecho.Content = _derechoColapsado ? "<<" : ">>";
            };

            colDef.BeginAnimation(ColumnDefinition.WidthProperty, anim);
        }

        // ── Colapsar / expandir Parámetros ───────────────────────────────────────
        private bool   _paramsColapsado        = false;
        private double _paramsAlturaExpandida  = 0;

        private void btnToggleParams_Click(object sender, RoutedEventArgs e)
        {
            var rowContenido  = grdDerecho.RowDefinitions[1]; // content Parámetros
            var rowSplitter   = grdDerecho.RowDefinitions[2]; // GridSplitter

            if (!_paramsColapsado)
                _paramsAlturaExpandida = rowContenido.ActualHeight;

            double to = _paramsColapsado ? _paramsAlturaExpandida : 0;

            AnimarFila(rowContenido, to, () =>
            {
                _paramsColapsado = !_paramsColapsado;
                btnToggleParams.Content = _paramsColapsado ? "▼" : "▲";
                rowSplitter.Height = (_paramsColapsado || _historialColapsado)
                    ? new GridLength(0)
                    : new GridLength(5);
            });
        }

        // ── Colapsar / expandir Historial ────────────────────────────────────────
        private bool   _historialColapsado       = false;
        private double _historialAlturaExpandida = 0;

        private void btnToggleHistorial_Click(object sender, RoutedEventArgs e)
        {
            var rowContenido = grdDerecho.RowDefinitions[4]; // content Historial
            var rowSplitter  = grdDerecho.RowDefinitions[2]; // GridSplitter

            if (!_historialColapsado)
                _historialAlturaExpandida = rowContenido.ActualHeight;

            double to = _historialColapsado ? _historialAlturaExpandida : 0;

            AnimarFila(rowContenido, to, () =>
            {
                _historialColapsado = !_historialColapsado;
                btnToggleHistorial.Content = _historialColapsado ? "▼" : "▲";
                rowSplitter.Height = (_paramsColapsado || _historialColapsado)
                    ? new GridLength(0)
                    : new GridLength(5);
            });
        }

        /// <summary>
        /// Anima el Height de una RowDefinition hacia <paramref name="to"/> píxeles
        /// y ejecuta <paramref name="onCompleted"/> al terminar.
        /// </summary>
        private void AnimarFila(RowDefinition fila, double to, Action onCompleted)
        {
            double from = fila.ActualHeight;
            var anim = new GridLengthAnimation
            {
                From         = new GridLength(from, GridUnitType.Pixel),
                To           = new GridLength(to,   GridUnitType.Pixel),
                Duration     = new Duration(TimeSpan.FromMilliseconds(200)),
                FillBehavior = FillBehavior.Stop
            };
            anim.Completed += (s, _) =>
            {
                fila.Height = new GridLength(to, GridUnitType.Pixel);
                onCompleted();
            };
            fila.BeginAnimation(RowDefinition.HeightProperty, anim);
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

        private async void btnBuscar_Click(object sender, RoutedEventArgs e)
        {
            string termino = txtBuscar.Text.Trim();

            // Siempre usamos tvSchema (tvSearch queda oculto)
            tvSearch.Visibility  = Visibility.Collapsed;
            tvSchema.Visibility  = Visibility.Visible;

            if (string.IsNullOrEmpty(termino))
            {
                // Sin texto → limpiar búsqueda y recargar normalmente
                _resultadoBusqueda = null;
                _terminoBusqueda   = "";
                LanzarCargarEsquema();
                return;
            }

            // ── Limpiar filtros de schema e isla antes de buscar ─────────
            _filtroSchema = "";
            _islaActiva   = null;

            cbSchema.SelectionChanged -= cbSchema_SelectionChanged;
            if (cbSchema.Items.Count > 0) cbSchema.SelectedIndex = 0;
            cbSchema.SelectionChanged += cbSchema_SelectionChanged;

            cbIslas.SelectionChanged -= cbIslas_SelectionChanged;
            cbIslas.SelectedIndex = 0;
            cbIslas.SelectionChanged += cbIslas_SelectionChanged;

            // ── Buscar en la base de datos ────────────────────────────────
            if (conexionActual == null)
            {
                AppendMessage("No hay conexión seleccionada.");
                return;
            }

            AppendMessage($"Buscando '{termino}' en tablas, columnas, índices y claves...");
            btnBuscar.IsEnabled = false;
            try
            {
                string connStr = GetConnectionString();
                var resultado  = await Task.Run(() =>
                    BuscarEnBaseDatos(connStr, conexionActual.Motor, termino));

                _resultadoBusqueda = resultado;
                _terminoBusqueda   = termino;

                AppendMessage($"Búsqueda completada: {resultado.Count} tabla(s)/vista(s) encontradas.");
            }
            catch (Exception ex)
            {
                AppendMessage("Error en búsqueda: " + ex.Message);
                _resultadoBusqueda = null;
                _terminoBusqueda   = "";
            }
            finally
            {
                btnBuscar.IsEnabled = true;
            }

            // Recargar tvSchema limitado a los resultados
            LanzarCargarEsquema();
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

        /// <summary>
        /// Busca en la base de datos activa todas las tablas/vistas que:
        ///   - tengan el término en su nombre,
        ///   - tengan alguna columna cuyo nombre contenga el término,
        ///   - tengan algún índice cuyo nombre contenga el término,
        ///   - tengan alguna clave/restricción cuyo nombre contenga el término.
        /// Devuelve una lista de "schema.tabla" (o solo "tabla" si no hay schema).
        /// </summary>
        private List<string> BuscarEnBaseDatos(string connStr, TipoMotor motor, string termino)
        {
            var resultados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string t = termino.Replace("'", "''"); // escapado básico

            using (var conn = new OdbcConnection(connStr))
            {
                conn.Open();

                string sql = null;

                switch (motor)
                {
                    case TipoMotor.MS_SQL:
                        sql = $@"
SELECT DISTINCT TABLE_SCHEMA, TABLE_NAME
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_NAME LIKE '%{t}%'
  AND TABLE_TYPE IN ('BASE TABLE','VIEW')
UNION
SELECT DISTINCT TABLE_SCHEMA, TABLE_NAME
FROM INFORMATION_SCHEMA.COLUMNS
WHERE COLUMN_NAME LIKE '%{t}%'
UNION
SELECT DISTINCT SCHEMA_NAME(o.schema_id), o.name
FROM sys.indexes i
INNER JOIN sys.objects o ON i.object_id = o.object_id
WHERE i.name LIKE '%{t}%'
  AND o.type IN ('U','V')
UNION
SELECT DISTINCT TABLE_SCHEMA, TABLE_NAME
FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
WHERE CONSTRAINT_NAME LIKE '%{t}%'";
                        break;

                    case TipoMotor.POSTGRES:
                        sql = $@"
SELECT DISTINCT table_schema, table_name
FROM information_schema.tables
WHERE table_name ILIKE '%{t}%'
  AND table_type IN ('BASE TABLE','VIEW')
UNION
SELECT DISTINCT table_schema, table_name
FROM information_schema.columns
WHERE column_name ILIKE '%{t}%'
UNION
SELECT DISTINCT schemaname, tablename
FROM pg_indexes
WHERE indexname ILIKE '%{t}%'
UNION
SELECT DISTINCT table_schema, table_name
FROM information_schema.table_constraints
WHERE constraint_name ILIKE '%{t}%'";
                        break;

                    case TipoMotor.DB2:
                        sql = $@"
SELECT DISTINCT TABSCHEMA, TABNAME
FROM SYSCAT.TABLES
WHERE TABNAME LIKE '%{t}%'
  AND TYPE IN ('T','V')
UNION
SELECT DISTINCT TABSCHEMA, TABNAME
FROM SYSCAT.COLUMNS
WHERE COLNAME LIKE '%{t}%'
UNION
SELECT DISTINCT TABSCHEMA, TABNAME
FROM SYSCAT.INDEXES
WHERE INDNAME LIKE '%{t}%'";
                        break;

                    case TipoMotor.SQLite:
                        // SQLite no tiene catálogo de columnas/índices sin PRAGMA por tabla;
                        // se busca solo por nombre de objeto.
                        sql = $@"
SELECT NULL AS TABLE_SCHEMA, name AS TABLE_NAME
FROM sqlite_master
WHERE (type='table' OR type='view')
  AND name LIKE '%{t}%'";
                        break;

                    default:
                        return new List<string>();
                }

                try
                {
                    using (var cmd = new OdbcCommand(sql, conn))
                    using (var rdr = cmd.ExecuteReader())
                    {
                        while (rdr.Read())
                        {
                            string schema = rdr.IsDBNull(0) ? null : rdr.GetString(0)?.Trim();
                            string tabla  = rdr.IsDBNull(1) ? null : rdr.GetString(1)?.Trim();
                            if (string.IsNullOrEmpty(tabla)) continue;

                            string clave = string.IsNullOrEmpty(schema)
                                ? tabla
                                : $"{schema}.{tabla}";
                            resultados.Add(clave);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Si el motor no soporta alguna vista del catálogo, lo ignoramos y devolvemos lo que haya
                    Dispatcher.Invoke(() => AppendMessage("Advertencia en búsqueda: " + ex.Message));
                }
            }

            return new List<string>(resultados);
        }

        // ════════════════════════════════════════════════════════════════
        // INTELLISENSE
        // ════════════════════════════════════════════════════════════════

        //// Palabras reservadas SQL que nunca deben abrir la ventana de completion.
        private static readonly HashSet<string> _palabrasReservadas = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT","FROM","WHERE","GROUP","ORDER","BY","HAVING","FETCH","FIRST","ROWS","ONLY",
            "INSERT","INTO","VALUES","UPDATE","SET","DELETE","ON","CASE","ADD","WHEN","THEN",
            "ELSE","END","AS","DISTINCT","UNION","CREATE","TABLE","DROP","ALTER","VIEW",
            "PROCEDURE","TRIGGER","ASC","DESC","SCHEMA","JOIN","LEFT","RIGHT","INNER","OUTER",
            "CROSS","FULL","LIMIT","OFFSET","AND","OR","NOT","NULL","IS","IN","BETWEEN","LIKE",
            "EXISTS","ALL","SUM","COUNT","CAST","CONVERT","COALESCE","NULLIF","ISNULL",
            "ROW_NUMBER","RANK","DENSE_RANK","LAG","LEAD","MAX","MIN"
        };

        /// <summary>
        /// Devuelve el token (palabra parcial) que el usuario está escribiendo justo antes del cursor.
        /// </summary>
        private string ObtenerTokenActual(int caretOffset)
        {
            if (caretOffset <= 0) return string.Empty;
            int inicio = caretOffset - 1;
            while (inicio > 0 &&
                   (char.IsLetterOrDigit(txtQuery.Document.GetCharAt(inicio - 1)) ||
                    txtQuery.Document.GetCharAt(inicio - 1) == '_'))
            {
                inicio--;
            }
            int len = caretOffset - inicio;
            return len > 0 ? txtQuery.Document.GetText(inicio, len) : string.Empty;
        }

        /// <summary>
        /// Maneja Ctrl+Space (disparar intellisense manualmente según contexto)
        /// y Ctrl+Shift+Home (seleccionar desde el cursor hasta el inicio del documento).
        /// </summary>
        private void TxtQueryArea_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // ── Ctrl+Shift+Home: seleccionar desde cursor hasta inicio ──────────
            if (e.Key == Key.Home &&
                (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control &&
                (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                var area = txtQuery.TextArea;
                int desde = area.Caret.Offset;
                // Crear selección desde la posición actual hasta el inicio del documento
                area.Selection = ICSharpCode.AvalonEdit.Editing.Selection.Create(area, desde, 0);
                area.Caret.Offset = 0;
                e.Handled = true;
                return;
            }

            // ── Ctrl+Space: disparar intellisense manual ─────────────────────────
            if (e.Key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;

                // Cerrar ventana previa si existe
                _completionWindow?.Close();
                _completionWindow = null;

                ActualizarAliasesYCargarColumnas(txtQuery.Text);
                int caret = txtQuery.CaretOffset;
                string token = ObtenerTokenActual(caret);

                // Verificar si hay un punto antes del token (contexto alias.columna)
                int tokenStart = caret - token.Length;
                if (tokenStart > 0 && txtQuery.Document.GetCharAt(tokenStart - 1) == '.')
                {
                    // Buscar el alias/tabla antes del punto
                    int pPos = tokenStart - 1;
                    int aInicio = pPos - 1;
                    while (aInicio > 0 &&
                           (char.IsLetterOrDigit(txtQuery.Document.GetCharAt(aInicio - 1)) ||
                            txtQuery.Document.GetCharAt(aInicio - 1) == '_'))
                    {
                        aInicio--;
                    }
                    string prefijo = txtQuery.Document.GetText(aInicio, pPos - aInicio).Trim();
                    if (!string.IsNullOrEmpty(prefijo))
                    {
                        AbrirIntellisense(prefijo, false);
                        return;
                    }
                }

                // Determinar contexto y abrir suggestions
                bool enContextoTabla = EstaEnContextoTabla(txtQuery.Text, caret);
                AbrirIntellisense(null, enContextoTabla);
                return;
            }
        }

        /// <summary>
        /// Si la ventana de completion está abierta y el usuario escribe un carácter
        /// que no forma parte de un identificador, cierra la ventana SIN autocompletar
        /// (si es espacio) o confirma la selección (si es otro separador como punto, coma, etc.).
        /// </summary>
        private void TxtQuery_TextEntering(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            if (e.Text.Length > 0 && _completionWindow != null)
            {
                char c = e.Text[0];
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    if (c == ' ')
                    {
                        // Espacio: cerrar la ventana sin insertar nada
                        _completionWindow.Close();
                        _completionWindow = null;
                    }
                    else
                    {
                        // Cualquier otro separador (punto, coma, paréntesis, etc.): confirmar inserción
                        _completionWindow.CompletionList.RequestInsertion(e);
                    }
                }
            }
        }

        /// <summary>
        /// Reacciona a cada carácter escrito en el editor:
        ///   - Actualiza el mapa de aliases/tablas y dispara carga en background.
        ///   - Si es un punto, abre suggestions de la tabla/alias que precede al punto.
        ///   - Si es una letra y no hay ventana abierta, y el token actual NO es una
        ///     palabra reservada, abre suggestions generales.
        /// </summary>
        private void TxtQuery_TextEntered(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            // Actualizamos alias y disparamos carga de columnas en background
            ActualizarAliasesYCargarColumnas(txtQuery.Text);

            if (e.Text == ".")
            {
                // Buscar hacia atrás la palabra antes del punto
                int offset = txtQuery.CaretOffset;
                int inicio = offset - 2; // -1 por el ".", -1 más para empezar antes del punto
                while (inicio > 0 &&
                       (char.IsLetterOrDigit(txtQuery.Document.GetCharAt(inicio - 1)) ||
                        txtQuery.Document.GetCharAt(inicio - 1) == '_'))
                {
                    inicio--;
                }

                if (inicio < offset - 1)
                {
                    string prefijo = txtQuery.Document.GetText(inicio, offset - inicio - 1).Trim();
                    if (!string.IsNullOrEmpty(prefijo))
                        AbrirIntellisense(prefijo);
                }
            }
            else if (e.Text.Length == 1 && char.IsLetter(e.Text[0]) && _completionWindow == null)
            {
                // Obtener el token completo que el usuario está escribiendo
                string tokenActual = ObtenerTokenActual(txtQuery.CaretOffset);

                // Determinamos si el cursor está en contexto FROM/JOIN para priorizar tablas
                bool enContextoTabla = EstaEnContextoTabla(txtQuery.Text, txtQuery.CaretOffset);
                // Sugerencias generales al empezar a escribir texto (incluye keywords filtradas)
                AbrirIntellisense(null, enContextoTabla);
            }
        }

        /// <summary>
        /// Detecta si la posición actual del cursor está inmediatamente después de
        /// FROM, JOIN (y variantes) para priorizar las sugerencias de tablas.
        /// </summary>
        private bool EstaEnContextoTabla(string sql, int caretOffset)
        {
            if (string.IsNullOrEmpty(sql) || caretOffset <= 0) return false;

            // Tomamos el texto antes del cursor y buscamos la última palabra clave de contexto tabla
            string anterior = sql.Substring(0, caretOffset).TrimEnd();

            // Palabras clave que introducen un nombre de tabla
            string[] palabrasContexto = { "FROM", "JOIN", "INTO", "UPDATE", "TABLE" };

            // Buscamos hacia atrás ignorando el token que el usuario está escribiendo ahora
            // Eliminamos el token parcial actual (hasta el último espacio/retorno)
            int ultimoEspacio = anterior.LastIndexOfAny(new char[] { ' ', '\t', '\r', '\n' });
            string sinTokenActual = ultimoEspacio >= 0 ? anterior.Substring(0, ultimoEspacio).TrimEnd() : string.Empty;

            if (string.IsNullOrEmpty(sinTokenActual)) return false;

            // Obtenemos la última "palabra" antes del token actual
            int penultimoEspacio = sinTokenActual.LastIndexOfAny(new char[] { ' ', '\t', '\r', '\n' });
            string ultimaPalabra = penultimoEspacio >= 0
                ? sinTokenActual.Substring(penultimoEspacio + 1)
                : sinTokenActual;

            return Array.Exists(palabrasContexto,
                p => p.Equals(ultimaPalabra, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Carga en background la lista completa de tablas de la base de datos activa.
        /// Se llama al conectar y al cambiar de conexión. Silencioso ante errores.
        /// </summary>
        private async void CargarTablasEnBackground(Conexion conexion)
        {
            if (conexion == null) return;
            if (_tablasEnCargaGlobal) return;

            _tablasEnCargaGlobal = true;
            try
            {
                var tablas = await IntelliSenseService.GetTablasAsync(conexion);
                // Actualizamos en el hilo de UI para evitar condiciones de carrera
                await Dispatcher.InvokeAsync(() =>
                {
                    _cacheTablas = tablas ?? new List<TablaInfo>();
                });
            }
            catch (Exception err)
            {
                Console.WriteLine("Error cargando tablas para intellisense: " + err.Message);
                // Silencioso
            }
            finally
            {
                _tablasEnCargaGlobal = false;
            }
        }

        /// <summary>
        /// Parsea el SQL para detectar tablas y aliases (FROM tabla [AS] alias / JOIN tabla [AS] alias).
        /// Por cada tabla nueva detectada dispara la carga asíncrona de sus columnas.
        /// </summary>
        private void ActualizarAliasesYCargarColumnas(string sql)
        {
            _mapaAliases.Clear();

            string pattern = @"(?i)\b(?:FROM|JOIN)\s+([a-zA-Z0-9_.]+)(?:\s+(?:AS\s+)?([a-zA-Z0-9_]+))?";
            var matches = Regex.Matches(sql, pattern);

            // Palabras reservadas SQL que pueden confundirse con alias
            string[] reservadas = { "WHERE", "SET", "ON", "AND", "OR", "INNER", "LEFT", "RIGHT",
                                     "OUTER", "CROSS", "FULL", "GROUP", "ORDER", "HAVING", "FETCH",
                                     "LIMIT", "OFFSET", "UNION", "EXCEPT", "INTERSECT" };

            foreach (Match m in matches)
            {
                string tabla = m.Groups[1].Value.Trim();
                string alias = m.Groups[2].Value.Trim();

                if (Array.Exists(reservadas, p => p.Equals(alias, StringComparison.OrdinalIgnoreCase)))
                    alias = string.Empty;

                if (!string.IsNullOrEmpty(tabla) && !_mapaAliases.ContainsKey(tabla))
                    _mapaAliases[tabla] = tabla;

                if (!string.IsNullOrEmpty(alias) && !_mapaAliases.ContainsKey(alias))
                    _mapaAliases[alias] = tabla;

                // Disparar carga de columnas si la tabla no está en caché
                CargarColumnasEnBackground(tabla);
            }
        }

        /// <summary>
        /// Carga columnas de una tabla en background sin bloquear la UI.
        /// Silencioso ante errores para no interrumpir la escritura.
        /// </summary>
        private async void CargarColumnasEnBackground(string tabla)
        {
            if (conexionActual == null) return;
            if (_cacheColumnas.ContainsKey(tabla)) return;
            if (_tablasEnCarga.Contains(tabla)) return;

            _tablasEnCarga.Add(tabla);
            try
            {
                var columnas = await IntelliSenseService.GetColumnasAsync(conexionActual, tabla);
                if (columnas != null && columnas.Count > 0)
                    _cacheColumnas[tabla] = columnas;
            }
            finally
            {
                _tablasEnCarga.Remove(tabla);
            }
        }

        /// <summary>
        /// Abre la CompletionWindow de AvalonEdit con las columnas relevantes.
        /// Si prefijo no es null, filtra por tabla/alias; si es null, muestra todo el caché.
        /// Si enContextoTabla es true, las tablas de la BD aparecen al inicio de la lista.
        /// El StartOffset de la ventana se ajusta para reemplazar el token que ya se escribió,
        /// evitando así la duplicación del primer carácter.
        /// </summary>
        private void AbrirIntellisense(string prefijo, bool enContextoTabla = false)
        {
            // Respetar la preferencia del usuario
            if (!_configApp.IntellisenseActivo) return;

            _completionWindow = new CompletionWindow(txtQuery.TextArea);

            // ── Ajustar StartOffset para incluir el token ya escrito ──────────────
            // Buscamos hacia atrás desde el cursor hasta el inicio del token actual
            // (letras, dígitos y guión bajo). Así la CompletionWindow sabe que debe
            // reemplazar esos caracteres en lugar de insertar delante de ellos.
            int caretOffset = txtQuery.CaretOffset;
            int tokenStart = caretOffset;
            while (tokenStart > 0 &&
                   (char.IsLetterOrDigit(txtQuery.Document.GetCharAt(tokenStart - 1)) ||
                    txtQuery.Document.GetCharAt(tokenStart - 1) == '_'))
            {
                tokenStart--;
            }
            _completionWindow.StartOffset = tokenStart;

            // Token actual para filtrar las keywords en tiempo real
            string tokenActual = tokenStart < caretOffset
                ? txtQuery.Document.GetText(tokenStart, caretOffset - tokenStart)
                : string.Empty;

            IList<ICompletionData> data = _completionWindow.CompletionList.CompletionData;

            if (!string.IsNullOrEmpty(prefijo))
            {
                // Resolver alias → tabla real
                string tablaReal;
                if (!_mapaAliases.TryGetValue(prefijo, out tablaReal))
                    tablaReal = prefijo;

                if (_cacheColumnas.ContainsKey(tablaReal))
                {
                    foreach (var col in _cacheColumnas[tablaReal])
                        data.Add(new SqlCompletionItem(col.Nombre, col.Tipo, tablaReal));
                }
            }
            else
            {
                if (enContextoTabla)
                {
                    // En contexto FROM/JOIN: las tablas de la BD van primero
                    foreach (var t in _cacheTablas)
                        data.Add(new SqlCompletionItem(t.Nombre, t.Tipo == "V" || t.Tipo == "view" || t.Tipo == "VIEW" ? "Vista" : "Tabla", string.Empty));

                    // Luego las columnas ya conocidas por las tablas del query actual
                    foreach (var kvp in _cacheColumnas)
                    {
                        foreach (var col in kvp.Value)
                            data.Add(new SqlCompletionItem(col.Nombre, col.Tipo, kvp.Key));
                    }
                }
                else
                {
                    // Contexto general: tablas conocidas del query actual + sus columnas
                    foreach (var kvp in _cacheColumnas)
                    {
                        data.Add(new SqlCompletionItem(kvp.Key, "Tabla", null));
                        foreach (var col in kvp.Value)
                            data.Add(new SqlCompletionItem(col.Nombre, col.Tipo, kvp.Key));
                    }

                    // Además incluimos las tablas de la BD por si el usuario quiere escribir una nueva
                    foreach (var t in _cacheTablas)
                    {
                        // Solo agregar si no está ya en el caché de columnas (evitar duplicados)
                        if (!_cacheColumnas.ContainsKey(t.Nombre))
                            data.Add(new SqlCompletionItem(t.Nombre, t.Tipo == "V" || t.Tipo == "view" || t.Tipo == "VIEW" ? "Vista" : "Tabla", string.Empty));
                    }
                }

                // ── Keywords SQL filtradas en tiempo real ─────────────────────────
                // Se muestran solo las que empiezan con lo que el usuario está escribiendo.
                // SqlKeywordCompletionItem siempre inserta en MAYÚSCULAS.
                foreach (string kw in _palabrasReservadas.OrderBy(k => k))
                {
                    if (string.IsNullOrEmpty(tokenActual) ||
                        kw.StartsWith(tokenActual, StringComparison.OrdinalIgnoreCase))
                    {
                        data.Add(new SqlKeywordCompletionItem(kw));
                    }
                }
            }

            if (data.Count > 0)
            {
                _completionWindow.Show();
                _completionWindow.Closed += (s, args) => _completionWindow = null;
            }
            else
            {
                _completionWindow = null;
            }
        }
    }
}

// ════════════════════════════════════════════════════════════════
// CLASES DE SOPORTE (fuera del namespace principal de la ventana)
// ════════════════════════════════════════════════════════════════

/// <summary>
/// Almacena el nombre y el tipo ("TABLE" o "VIEW") de un nodo del TreeView
/// del explorador de esquema, para que "Documentar todo" pueda distinguir vistas.
/// </summary>
public class NodoTablaTag
{
    public string Nombre { get; }
    public string Tipo { get; }

    public NodoTablaTag(string nombre, string tipo)
    {
        Nombre = nombre;
        Tipo = tipo;
    }

    // Compatibilidad con código que lee Tag.ToString() para obtener el nombre
    public override string ToString() => Nombre;
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

/// <summary>
/// Item de autocompletado para AvalonEdit.
/// Muestra el nombre de la columna en negrita y la tabla de origen en gris.
/// TAB o ENTER insertan solo el nombre de la columna/tabla.
/// </summary>
public class SqlCompletionItem : ICSharpCode.AvalonEdit.CodeCompletion.ICompletionData
{
    private readonly string _tipo;
    private readonly string _tabla;

    public SqlCompletionItem(string texto, string tipo, string tabla)
    {
        Text = texto;
        _tipo = tipo ?? string.Empty;
        _tabla = tabla ?? string.Empty;
    }

    public System.Windows.Media.ImageSource Image => null;
    public string Text { get; private set; }
    public double Priority => 0;

    // Tooltip lateral de la ventana de completion
    public object Description =>
        string.IsNullOrEmpty(_tabla)
            ? (object)_tipo
            : $"{_tipo}  —  {_tabla}";

    // Contenido visual del ítem en la lista desplegable
    public object Content
    {
        get
        {
            // Para ítems de tipo "Tabla" (sin tabla padre) mostramos solo el nombre
            if (string.IsNullOrEmpty(_tabla))
                return Text;

            var panel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal
            };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = Text,
                FontWeight = System.Windows.FontWeights.SemiBold
            });
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "  " + _tabla,
                Foreground = new System.Windows.Media.SolidColorBrush(
                                        System.Windows.Media.Color.FromRgb(130, 130, 130)),
                FontSize = 10,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new System.Windows.Thickness(4, 0, 0, 0)
            });
            return panel;
        }
    }

    public void Complete(
        ICSharpCode.AvalonEdit.Editing.TextArea textArea,
        ICSharpCode.AvalonEdit.Document.ISegment completionSegment,
        EventArgs insertionEventArgs)
    {
        // Inserta solo el nombre, sin el tipo ni la tabla
        textArea.Document.Replace(completionSegment, Text);
    }
}

/// <summary>
/// Item de autocompletado para palabras reservadas SQL.
/// Se muestra en azul negrita y siempre se inserta en MAYÚSCULAS,
/// independientemente de cómo el usuario haya empezado a escribir.
/// </summary>
public class SqlKeywordCompletionItem : ICSharpCode.AvalonEdit.CodeCompletion.ICompletionData
{
    public SqlKeywordCompletionItem(string keyword)
    {
        // Text en mayúsculas: la CompletionList lo usa para filtrar por prefijo
        Text = keyword.ToUpperInvariant();
    }

    public System.Windows.Media.ImageSource Image => null;
    public string Text { get; private set; }
    // Priority > 0 hace que las keywords aparezcan antes en la lista cuando hay coincidencia exacta
    public double Priority => 0.5;
    public object Description => "Palabra reservada SQL";

    public object Content
    {
        get
        {
            return new System.Windows.Controls.TextBlock
            {
                Text = Text,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(
                                 System.Windows.Media.Color.FromRgb(30, 120, 220)) // azul
            };
        }
    }

    public void Complete(
        ICSharpCode.AvalonEdit.Editing.TextArea textArea,
        ICSharpCode.AvalonEdit.Document.ISegment completionSegment,
        EventArgs insertionEventArgs)
    {
        // Regla estricta: insertar SIEMPRE en MAYÚSCULAS
        textArea.Document.Replace(completionSegment, Text.ToUpperInvariant());
    }
}