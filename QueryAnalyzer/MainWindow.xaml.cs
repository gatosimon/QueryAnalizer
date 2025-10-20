using Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Odbc;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QueryAnalyzer
{
    public partial class MainWindow : Window
    {
        private const string HISTORY_FILE = "query_history.txt";
        public Dictionary<string, OdbcType> OdbcTypes { get; set; }
        public List<QueryParameter> Parametros { get; set; }

        static public Conexion conexionActual = null;

        public MainWindow()
        {
            InitializeComponent();

            // 🔹 AÑADIDO: esto asegura que los bindings de DataContext funcionen correctamente.
            DataContext = this;

            LoadHistory();
            txtQuery.KeyDown += TxtQuery_KeyDown;
            txtQuery.Text = "SELECT * FROM SYSIBM.SYSCOLUMNS FETCH FIRST 10 ROWS ONLY; -- ejemplo para DB2";

            CargarTipos();

            // Configura ItemsSource correctamente
            Parametros = new List<QueryParameter>();
            gridParams.ItemsSource = Parametros;
            InicializarConexiones();
            BloquearUI(true);
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
            string connStr = GetConnectionString();
            string sql = txtQuery.Text;

            if (string.IsNullOrWhiteSpace(connStr))
            {
                AppendMessage("Connection string is empty.");
                return;
            }
            if (string.IsNullOrWhiteSpace(sql))
            {
                AppendMessage("Query is empty.");
                return;
            }

            AppendMessage($"Executing... ({DateTime.Now})");

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
                txtRowCount.Text = dt.Rows.Count.ToString();
                AppendMessage($"Execution successful. {dt.Rows.Count} rows returned.");
                AddToHistory(sql);
            }
            catch (Exception ex)
            {
                AppendMessage("Error: " + ex.Message);
            }
        }

        private async Task<DataTable> ExecuteQueryAsync(string connStr, string sql, List<QueryParameter> parametros)
        {
            return await Task.Run(() =>
            {
                var dt = new DataTable();

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

                return dt;
            });
        }

        private async void BtnExecuteScalar_Click(object sender, RoutedEventArgs e)
        {
            string connStr = GetConnectionString();
            string sql = txtQuery.Text;

            AppendMessage("Executing scalar...");

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

                AppendMessage("Scalar result: " + (result?.ToString() ?? "(null)"));
            }
            catch (Exception ex)
            {
                AppendMessage("Error: " + ex.Message);
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            dgResults.ItemsSource = null;
            txtRowCount.Text = "0";
            AppendMessage("Results cleared.");
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
                        stringConnection = $"Host={conexionActual.Servidor};Port=5432;Database={conexionActual.BaseDatos};Username={conexionActual.Usuario};Password={conexionActual.Contrasena};";
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
            txtMessages.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\n");
            txtMessages.ScrollToEnd();
        }

        private void AddToHistory(string sql)
        {
            try
            {
                lstHistory.Items.Insert(0, sql);
                File.AppendAllText(HISTORY_FILE, sql + Environment.NewLine + "---" + Environment.NewLine);
            }
            catch { }
        }

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

        private void LstHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (lstHistory.SelectedItem != null)
                txtQuery.Text = lstHistory.SelectedItem.ToString();
        }

        private void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            string conn = GetConnectionString();
            if (string.IsNullOrWhiteSpace(conn))
            {
                AppendMessage("Connection string empty.");
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    using (var c = new OdbcConnection(conn))
                    {
                        c.Open();
                        Dispatcher.Invoke(() => AppendMessage("Connection successful."));
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => AppendMessage("Connection failed: " + ex.Message));
                }
            });
        }

        private void BtnSaveConn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                File.WriteAllText("last_connection.txt", cbDriver.Text ?? string.Empty);
                AppendMessage("Connection string saved.");
            }
            catch (Exception ex)
            {
                AppendMessage("Error saving connection: " + ex.Message);
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
    }
}
