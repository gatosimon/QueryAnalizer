using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace QueryAnalyzer
{
    /// <summary>
    /// Formato de texto plano con el que se comparte una conexión (botón Compartir) y
    /// lectura inversa (botón Importar). Un solo lugar define las etiquetas para que
    /// ambos lados no se desfasen.
    /// </summary>
    public static class ConexionCompartida
    {
        private const string EtNombre = "Conexión";
        private const string EtMotor = "Motor";
        private const string EtConnString = "Connection String";
        private const string EtServidor = "Servidor";
        private const string EtPuerto = "Puerto";
        private const string EtEsWeb = "Es Web";
        private const string EtUsuario = "Usuario";
        private const string EtContrasena = "Contraseña";
        private const string EtBaseDatos = "Base de datos";

        public static string Formatear(Conexion c)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{EtNombre}: {c.Nombre}");
            sb.AppendLine($"{EtMotor}: {c.Motor}");

            if (!string.IsNullOrWhiteSpace(c.ConnectionStringCustom))
            {
                sb.AppendLine($"{EtConnString}: {c.ConnectionStringCustom}");
            }
            else
            {
                sb.AppendLine($"{EtServidor}: {c.Servidor}");

                if (!string.IsNullOrWhiteSpace(c.Puerto))
                    sb.AppendLine($"{EtPuerto}: {c.Puerto}");

                if (c.EsWeb)
                    sb.AppendLine($"{EtEsWeb}: Sí");

                sb.AppendLine($"{EtUsuario}: {c.Usuario}");
                sb.AppendLine($"{EtContrasena}: {c.Contrasena}");

                if (!string.IsNullOrWhiteSpace(c.BaseDatos))
                    sb.AppendLine($"{EtBaseDatos}: {c.BaseDatos}");
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// Interpreta un texto generado por <see cref="Formatear"/>. Tolera mayúsculas, acentos
        /// de menos y prefijos que agrega el chat al copiar un mensaje ("[10:32, 7/10/2026] Fulano: ").
        /// Devuelve false si no hay motor ni servidor/connection string.
        /// </summary>
        public static bool TryParsear(string texto, out Conexion conexion)
        {
            conexion = null;
            if (string.IsNullOrWhiteSpace(texto)) return false;

            var c = new Conexion();
            bool hayMotor = false;

            foreach (string linea in texto.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string clave, valor;
                if (!SepararLinea(linea, out clave, out valor)) continue;

                switch (clave)
                {
                    case "conexion":
                    case "nombre":
                        c.Nombre = valor.Trim();
                        break;
                    case "motor":
                        TipoMotor motor;
                        if (!TryParsearMotor(valor, out motor)) return false;
                        c.Motor = motor;
                        hayMotor = true;
                        break;
                    case "connection string":
                        c.ConnectionStringCustom = valor.Trim();
                        break;
                    case "servidor":
                        c.Servidor = valor.Trim();
                        break;
                    case "puerto":
                        c.Puerto = valor.Trim();
                        break;
                    case "es web":
                        c.EsWeb = EsAfirmativo(valor);
                        break;
                    case "usuario":
                        c.Usuario = valor.Trim();
                        break;
                    case "contrasena":
                        // Sin Trim: la contraseña se respeta tal cual
                        c.Contrasena = valor;
                        break;
                    case "base de datos":
                        c.BaseDatos = valor.Trim();
                        break;
                }
            }

            bool hayDestino = !string.IsNullOrWhiteSpace(c.ConnectionStringCustom)
                              || !string.IsNullOrWhiteSpace(c.Servidor);
            if (!hayMotor || !hayDestino) return false;

            conexion = c;
            return true;
        }

        private static readonly HashSet<string> ClavesConocidas = new HashSet<string>
        {
            "conexion", "nombre", "motor", "connection string", "servidor", "puerto",
            "es web", "usuario", "contrasena", "base de datos"
        };

        /// <summary>
        /// Parte "Clave: valor". Si lo que está antes del primer ':' no es una clave conocida
        /// (prefijo de chat con hora y autor) se sigue buscando en el resto de la línea.
        /// </summary>
        private static bool SepararLinea(string linea, out string clave, out string valor)
        {
            clave = valor = null;
            string resto = linea;

            while (true)
            {
                int i = resto.IndexOf(':');
                if (i < 0) return false;

                string k = Normalizar(resto.Substring(0, i));
                string v = resto.Substring(i + 1);
                if (v.StartsWith(" ")) v = v.Substring(1);

                if (ClavesConocidas.Contains(k))
                {
                    clave = k;
                    valor = v;
                    return true;
                }
                resto = resto.Substring(i + 1);
            }
        }

        private static bool TryParsearMotor(string valor, out TipoMotor motor)
        {
            string v = Normalizar(valor).Replace(" ", "").Replace("_", "");
            switch (v)
            {
                case "mssql":
                case "sqlserver":
                    motor = TipoMotor.MS_SQL;
                    return true;
                case "db2":
                    motor = TipoMotor.DB2;
                    return true;
                case "postgres":
                case "postgresql":
                    motor = TipoMotor.POSTGRES;
                    return true;
                case "sqlite":
                    motor = TipoMotor.SQLite;
                    return true;
            }
            motor = default(TipoMotor);
            return false;
        }

        private static bool EsAfirmativo(string valor)
        {
            string v = Normalizar(valor);
            return v == "si" || v == "true" || v == "1" || v == "yes" || v == "s";
        }

        /// <summary>Minúsculas, sin acentos y sin espacios sobrantes.</summary>
        private static string Normalizar(string s)
        {
            string d = (s ?? "").Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char ch in d)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Trim();
        }
    }
}
