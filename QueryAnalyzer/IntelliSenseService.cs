using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Odbc;
using System.Threading.Tasks;

namespace QueryAnalyzer
{
    public static class IntelliSenseService
    {
        /// <summary>
        /// Obtiene columnas de una tabla desde la base de datos activa.
        /// Silencioso ante errores: el intellisense no debe interrumpir la escritura.
        /// </summary>
        public static async Task<List<ColumnInfo>> GetColumnasAsync(Conexion conexion, string tabla)
        {
            return await Task.Run(() => GetColumnas(conexion, tabla));
        }

        private static List<ColumnInfo> GetColumnas(Conexion conexion, string tabla)
        {
            var resultado = new List<ColumnInfo>();
            if (conexion == null || string.IsNullOrWhiteSpace(tabla)) return resultado;

            // Separamos esquema de nombre en caso de notación esquema.tabla
            string nombreTabla = tabla.Contains(".")
                ? tabla.Substring(tabla.LastIndexOf('.') + 1)
                : tabla;

            try
            {
                string connStr = ConexionesManager.GetConnectionString(conexion);
                using (var conn = new OdbcConnection(connStr))
                {
                    conn.Open();
                    string query = BuildQuery(conexion.Motor, nombreTabla);
                    if (string.IsNullOrEmpty(query)) return resultado;

                    using (var cmd = new OdbcCommand(query, conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string nombre = reader[0].ToString().Trim();
                            string tipo = reader.FieldCount > 1 ? reader[1].ToString().Trim() : string.Empty;

                            // Para SQLite: PRAGMA table_info devuelve cid,name,type,notnull,dflt_value,pk
                            // Por eso si el motor es SQLite leemos índice 1 (name) y 2 (type)
                            if (conexion.Motor == TipoMotor.SQLite)
                            {
                                nombre = reader[1].ToString().Trim();
                                tipo = reader[2].ToString().Trim();
                            }

                            if (!string.IsNullOrEmpty(nombre))
                                resultado.Add(new ColumnInfo { Nombre = nombre, Tipo = tipo });
                        }
                    }
                }
            }
            catch (Exception err)
            {
                Console.WriteLine(err.Message);
                // Silencioso
            }

            return resultado;
        }

        private static string BuildQuery(TipoMotor motor, string tabla)
        {
            string t = tabla.Replace("'", "''");
            switch (motor)
            {
                case TipoMotor.MS_SQL:
                    return 
                        $@"SELECT COLUMN_NAME, DATA_TYPE 
                        FROM INFORMATION_SCHEMA.COLUMNS 
                        WHERE TABLE_NAME = '{t}' 
                        ORDER BY ORDINAL_POSITION";

                case TipoMotor.DB2:
                    // DB2 v7 usa SYSIBM.SYSCOLUMNS. COLTYPE es CHAR(8) → tipo truncado, normal para v7
                    return 
                        $@"SELECT NAME, COLTYPE 
                        FROM SYSIBM.SYSCOLUMNS 
                        WHERE TBNAME = '{t.ToUpper()}' 
                        ORDER BY COLNO";

                case TipoMotor.POSTGRES:
                    return 
                        $@"SELECT column_name, data_type 
                        FROM information_schema.columns 
                        WHERE table_name = '{t.ToLower()}' 
                        ORDER BY ordinal_position";

                case TipoMotor.SQLite:
                    // PRAGMA devuelve: cid | name | type | notnull | dflt_value | pk
                    return $"PRAGMA table_info({tabla})";

                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Obtiene la lista de tablas (y vistas) de la base de datos activa.
        /// Silencioso ante errores: el intellisense no debe interrumpir la escritura.
        /// </summary>
        public static async Task<List<TablaInfo>> GetTablasAsync(Conexion conexion)
        {
            return await Task.Run(() => GetTablas(conexion));
        }

        private static List<TablaInfo> GetTablas(Conexion conexion)
        {
            var resultado = new List<TablaInfo>();
            if (conexion == null) return resultado;

            try
            {
                string connStr = ConexionesManager.GetConnectionString(conexion);
                using (var conn = new OdbcConnection(connStr))
                {
                    conn.Open();
                    string query = BuildQueryTablas(conexion.Motor);
                    if (string.IsNullOrEmpty(query))
                    {
                        // Fallback: usar GetSchema de ODBC (funciona en todos los motores)
                        DataTable schema = conn.GetSchema("Tables");
                        foreach (DataRow row in schema.Rows)
                        {
                            string tipo = row["TABLE_TYPE"]?.ToString() ?? string.Empty;
                            string nombre = row["TABLE_NAME"]?.ToString()?.Trim() ?? string.Empty;
                            string esquema = row["TABLE_SCHEM"]?.ToString()?.Trim() ?? string.Empty;
                            if (!string.IsNullOrEmpty(nombre))
                                resultado.Add(new TablaInfo { Nombre = nombre, Esquema = esquema, Tipo = tipo });
                        }
                        return resultado;
                    }

                    using (var cmd = new OdbcCommand(query, conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string nombre = reader[0].ToString().Trim();
                            string esquema = reader.FieldCount > 1 ? reader[1].ToString().Trim() : string.Empty;
                            string tipo = reader.FieldCount > 2 ? reader[2].ToString().Trim() : "TABLE";
                            if (!string.IsNullOrEmpty(nombre))
                                resultado.Add(new TablaInfo { Nombre = nombre, Esquema = esquema, Tipo = tipo });
                        }
                    }
                }
            }
            catch (Exception err)
            {
                Console.WriteLine(err.Message);
                // Silencioso
            }

            return resultado;
        }

        private static string BuildQueryTablas(TipoMotor motor)
        {
            switch (motor)
            {
                case TipoMotor.MS_SQL:
                    // information_schema funciona en SQL Server 2000+
                    return "SELECT TABLE_NAME, TABLE_SCHEMA, TABLE_TYPE " +
                           "FROM INFORMATION_SCHEMA.TABLES " +
                           "ORDER BY TABLE_SCHEMA, TABLE_NAME";

                case TipoMotor.DB2:
                    // SYSIBM.SYSTABLES existe en DB2 v5 en adelante
                    return "SELECT NAME, CREATOR, TYPE " +
                           "FROM SYSIBM.SYSTABLES " +
                           "WHERE TYPE IN ('T','V') " +
                           "ORDER BY CREATOR, NAME";

                case TipoMotor.POSTGRES:
                    return "SELECT table_name, table_schema, table_type " +
                           "FROM information_schema.tables " +
                           "WHERE table_schema NOT IN ('pg_catalog','information_schema') " +
                           "ORDER BY table_schema, table_name";

                case TipoMotor.SQLite:
                    // sqlite_master contiene tablas y vistas
                    return "SELECT name, '' AS schema, type " +
                           "FROM sqlite_master " +
                           "WHERE type IN ('table','view') " +
                           "ORDER BY name";

                default:
                    // Dejar vacío para que el llamador use GetSchema como fallback
                    return string.Empty;
            }
        }
    }

    public class ColumnInfo
    {
        public string Nombre { get; set; }
        public string Tipo { get; set; }
    }

    public class TablaInfo
    {
        public string Nombre { get; set; }
        public string Esquema { get; set; }
        /// <summary>TABLE, VIEW, T, V según el motor.</summary>
        public string Tipo { get; set; }

        /// <summary>Nombre completo con esquema (si lo hay).</summary>
        public string NombreCompleto =>
            string.IsNullOrEmpty(Esquema) ? Nombre : $"{Esquema}.{Nombre}";
    }
}