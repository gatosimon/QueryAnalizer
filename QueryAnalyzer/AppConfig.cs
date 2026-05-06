using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace QueryAnalyzer
{
    /// <summary>
    /// Configuración general de la aplicación (persistida en config.xml).
    /// </summary>
    [Serializable]
    public class AppConfig
    {
        /// <summary>Tema oscuro activo (true) o claro (false).</summary>
        public bool TemaOscuro { get; set; } = false;

        /// <summary>Intellisense de tablas/columnas/keywords habilitado.</summary>
        public bool IntellisenseActivo { get; set; } = true;

        /// <summary>Al cambiar de conexión, carga la última consulta del historial de esa conexión.</summary>
        public bool CargarUltimaConsulta { get; set; } = true;

        /// <summary>
        /// Cuando está activo, "SELECT TOP 10" y "SELECT (todas las cols)" insertan
        /// el script, lo seleccionan automáticamente y lo ejecutan de inmediato,
        /// ignorando el resto del contenido del editor.
        /// </summary>
        public bool EjecutarSelectDirecto { get; set; } = false;

        /// <summary>
        /// Máximo de filas que se cargan en el grid de resultados por consulta.
        /// 0 = sin límite (puede causar OOM en tablas muy grandes).
        /// Default: 100 000.
        /// </summary>
        public int MaxFilasResultado { get; set; } = 100000;
    }

    /// <summary>
    /// Agrupa un conjunto nombrado de tablas/vistas para filtrar el explorador de esquema.
    /// Los conjuntos se guardan por nombre de conexión en config.xml.
    /// </summary>
    [Serializable]
    public class ConjuntoTablas
    {
        /// <summary>Nombre del conjunto (ej. "Facturación", "RRHH core").</summary>
        [XmlAttribute("Nombre")]
        public string Nombre { get; set; } = string.Empty;

        /// <summary>Lista de identificadores "schema.tabla" o "tabla" de las entidades del conjunto.</summary>
        [XmlElement("Tabla")]
        public List<string> Tablas { get; set; } = new List<string>();
    }

    /// <summary>
    /// Conjuntos de tablas de una conexión específica.
    /// </summary>
    [Serializable]
    public class ConjuntosConexion
    {
        /// <summary>Nombre de la conexión a la que pertenecen estos conjuntos.</summary>
        [XmlAttribute("Conexion")]
        public string NombreConexion { get; set; } = string.Empty;

        [XmlElement("Conjunto")]
        public List<ConjuntoTablas> Conjuntos { get; set; } = new List<ConjuntoTablas>();
    }

    /// <summary>
    /// Documento raíz del config.xml.
    /// Contiene la configuración de la app, la lista de conexiones y los conjuntos de tablas.
    /// </summary>
    [Serializable]
    [XmlRoot("QueryAnalyzerConfig")]
    public class QueryAnalyzerConfig
    {
        public AppConfig Configuracion { get; set; } = new AppConfig();

        [XmlArray("Conexiones")]
        [XmlArrayItem("Conexion")]
        public List<Conexion> Conexiones { get; set; } = new List<Conexion>();

        /// <summary>Conjuntos de tablas guardados por conexión.</summary>
        [XmlArray("ConjuntosTablas")]
        [XmlArrayItem("ConjuntosConexion")]
        public List<ConjuntosConexion> ConjuntosTablas { get; set; } = new List<ConjuntosConexion>();
    }
}
