using System;
using System.Collections.Generic;
using System.Data.Odbc;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Windows;

namespace QueryAnalyzer
{
    public partial class DatosConexion : Window
    {
        private Dictionary<TipoMotor, string> motores;

        public DatosConexion()
        {
            InitializeComponent();
            InicializarMotores();
        }

        private void InicializarMotores()
        {
            motores = Enum.GetValues(typeof(TipoMotor))
                .Cast<TipoMotor>()
                .ToDictionary(m => m, m => m.ToString().Replace("_", " "));

            cmbMotor.ItemsSource = motores;
            cmbMotor.SelectedIndex = 0;
        }

        private void btnGuardar_Click(object sender, RoutedEventArgs e)
        {
            var conexion = new Conexion
            {
                Nombre = txtNombre.Text.Trim(),
                Motor = (TipoMotor)cmbMotor.SelectedValue,
                Servidor = txtServidor.Text.Trim(),
                BaseDatos = txtBaseDatos.Visibility == Visibility.Visible ? txtBaseDatos.Text.Trim() : cmbBaseDatos.Text,
                Usuario = txtUsuario.Text.Trim(),
                Contrasena = txtContrasena.Password
            };

            var conexiones = ConexionesManager.Cargar();
            conexiones[conexion.Nombre] = conexion;
            ConexionesManager.Guardar(conexiones);

            MainWindow.conexionActual = conexion;
            MessageBox.Show("Conexión guardada correctamente.", "Éxito", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }

        private void cmbMotor_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cmbMotor.SelectedValue.ToString() == TipoMotor.DB2.ToString())
            {
                cmbBaseDatos.Visibility = Visibility.Visible;
                cmbBaseDatos.ItemsSource = ConexionesManager.TablasDB2;
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
                    throw ex;
                }
            }
        }
    }
}
