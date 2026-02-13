using System;
using System.Collections.Generic;
using System.Data.Odbc;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using Microsoft.Win32;
using System.Windows;
using System.Threading.Tasks;

namespace QueryAnalyzer
{
    public partial class DatosConexion : Window
    {
        private Dictionary<TipoMotor, string> motores;

        public Conexion ConexionActual = null;

        public DatosConexion()
        {
            InitializeComponent();
            InicializarMotores();
        }
        public DatosConexion(Conexion conexion)
        {
            InitializeComponent();
            InicializarMotores();
            ConexionActual = conexion;
            InicializarDatosConexion();
        }

        private void InicializarMotores()
        {
            motores = Enum.GetValues(typeof(TipoMotor))
                .Cast<TipoMotor>()
                .ToDictionary(m => m, m => m.ToString().Replace("_", " "));

            cmbMotor.ItemsSource = motores;
            cmbMotor.SelectedIndex = 0;
        }

        private void InicializarDatosConexion()
        {
            if (ConexionActual != null)
            {
                txtNombre.Text = ConexionActual.Nombre;
                cmbMotor.SelectedValue = ConexionActual.Motor;
                txtServidor.Text = ConexionActual.Servidor;
                try
                {
                    cmbBaseDatos.SelectedValue = ConexionActual.BaseDatos;
                }
                catch 
                { 
                }

                txtBaseDatos.Text = ConexionActual.BaseDatos;
                txtUsuario.Text = ConexionActual.Usuario;
                txtContrasena.Password = ConexionActual.Contrasena;
            }
        }

        private void btnGuardar_Click(object sender, RoutedEventArgs e)
        {
            if (ConexionActual == null)
            {
                ConexionActual = new Conexion();
            }

            ConexionActual.Nombre = txtNombre.Text.Trim();
            ConexionActual.Motor = (TipoMotor)cmbMotor.SelectedValue;
            ConexionActual.Servidor = txtServidor.Text.Trim();
            ConexionActual.BaseDatos = cmbBaseDatos.Visibility == Visibility.Visible ? cmbBaseDatos.Text.Trim() : txtBaseDatos.Text.Trim();
            ConexionActual.Usuario = txtUsuario.Text.Trim();
            ConexionActual.Contrasena = txtContrasena.Password;

            var conexiones = ConexionesManager.Cargar();
            conexiones[ConexionActual.Nombre] = ConexionActual;
            ConexionesManager.Guardar(conexiones);

            MainWindow.conexionActual = ConexionActual;
            MessageBox.Show("Conexión guardada correctamente.", "Éxito", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }

        private void cmbMotor_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var seleccionado = cmbMotor.SelectedValue != null
                ? cmbMotor.SelectedValue.ToString()
                : string.Empty;

            // 🔹 DB2: usa combo de bases
            if (seleccionado == TipoMotor.DB2.ToString())
            {
                cmbBaseDatos.Visibility = Visibility.Visible;
                cmbBaseDatos.ItemsSource = ConexionesManager.BasesDB2;
                cmbBaseDatos.SelectedIndex = 0;

                txtBaseDatos.Visibility = Visibility.Hidden;
                txtBaseDatos.IsEnabled = false;
                cmbBaseDatos.IsEnabled = true;
            }
            else
            {
                cmbBaseDatos.Visibility = Visibility.Hidden;
                txtBaseDatos.Visibility = Visibility.Visible;
                txtBaseDatos.IsEnabled = true;
                cmbBaseDatos.IsEnabled = false;
            }

            // 🔹 SQLite: muestra el botón de búsqueda
            if (seleccionado == TipoMotor.SQLite.ToString())
            {
                btnBuscarBase.IsEnabled = true;
                lblUsuario.Visibility = Visibility.Hidden;
                txtUsuario.Visibility = Visibility.Hidden;
                lblContrasena.Visibility = Visibility.Hidden;
                txtContrasena.Visibility = Visibility.Hidden;
                lblBaseDatos.Visibility = Visibility.Hidden;
                cmbBaseDatos.Visibility = Visibility.Hidden;
                txtBaseDatos.Visibility = Visibility.Hidden;
            }
            else
            {
                btnBuscarBase.IsEnabled = false;
                lblUsuario.Visibility = Visibility.Visible;
                txtUsuario.Visibility = Visibility.Visible;
                lblContrasena.Visibility = Visibility.Visible;
                txtContrasena.Visibility = Visibility.Visible;
                lblBaseDatos.Visibility = Visibility.Visible;
                cmbBaseDatos.Visibility = (seleccionado == TipoMotor.POSTGRES.ToString()) ? Visibility.Hidden : Visibility.Visible;
                txtBaseDatos.Visibility = Visibility.Visible;
            }
        }

        private void txtContrasena_LostFocus(object sender, RoutedEventArgs e)
        {
            if (cmbMotor.SelectedValue.ToString() == TipoMotor.MS_SQL.ToString())
            {
                string stringConnection = $@"Driver={{ODBC Driver 17 for SQL Server}};Server=SQL{txtServidor.Text.Trim()}\{txtServidor.Text.Trim()};Database=;Uid={txtUsuario.Text.Trim()};Pwd={txtContrasena.Password};TrustServerCertificate=yes;";
                try
                {
                    using (var c = new OdbcConnection(stringConnection))
                    {
                        c.Open();
                        cmbBaseDatos.Items.Clear();

                        OdbcCommand cmd = c.CreateCommand();
                        cmd.CommandText = "SELECT UPPER(name) FROM sys.databases WHERE state = 0 ORDER BY name ASC";
                        OdbcDataReader reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            cmbBaseDatos.Items.Add(reader.GetString(0));
                        }

                        c.Close();
                        cmbBaseDatos.Visibility = Visibility.Visible;
                        txtBaseDatos.Visibility = Visibility.Hidden;
                        txtBaseDatos.IsEnabled = false;
                        cmbBaseDatos.IsEnabled = true;
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Ocurrió un error al intentar obtener las bases de datos desde el servidor. Verifique el usuario y la contraseña.", "ATENCIÓN!!!");
                }
            }
        }

        private void btnBuscarBase_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Seleccionar archivo",
                Filter = "Todos los archivos (*.*)|*.db"
            };

            if (dlg.ShowDialog() == true)
            {
                // Ruta completa del archivo seleccionado
                string ruta = dlg.FileName;

                // Ejemplo: mostrar en un TextBox
                txtServidor.Text = ruta;
            }
        }

        private void btnProbar_Click(object sender, RoutedEventArgs e)
        {
            TipoMotor motor = (TipoMotor)cmbMotor.SelectedValue;

            string baseDatos = cmbBaseDatos.Text.Trim().Length > 0 ? cmbBaseDatos.Text.Trim() : txtBaseDatos.Text.Trim();
            string stringConnection = ConexionesManager.GetConnectionString(motor, txtServidor.Text, baseDatos, txtUsuario.Text, txtContrasena.Password);

            if (string.IsNullOrWhiteSpace(stringConnection))
            {
                MessageBox.Show("El string de conexión está vacío", "Atención!!!");
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    using (var c = new OdbcConnection(stringConnection))
                    {
                        c.Open();
                        MessageBox.Show("Conexión exitosa.");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Conexión fallida: " + ex.Message);
                }
            });
        }
    }
}
