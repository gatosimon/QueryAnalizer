using System;

namespace QueryAnalyzer.Models
{
    /// <summary>
    /// Servidor predefinido que aparece en el desplegable de la ventana de conexiones.
    /// Se persiste en %APPDATA%\QueryAnalyzer\servidores_preset.xml.
    /// Hereda de Conexion para reutilizar toda la estructura de datos de acceso.
    /// </summary>
    [Serializable]
    public class ServidorPreset : Conexion
    {
        /// <summary>Etiqueta amigable que aparece en el combo (ej. "Producción DB2").</summary>
        public string NombreVisible { get; set; } = "";

        /// <summary>El ComboBox muestra el servidor.</summary>
        public override string ToString() => Servidor ?? "";
    }
}
