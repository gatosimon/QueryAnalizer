using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace QueryAnalyzer
{
    public class OrtografiaDatos
    {
        public List<string> PalabrasIgnoradas { get; set; } = new List<string>();

        /// <summary>Formato: conexion|esquema.tabla.columna</summary>
        public List<string> ColumnasIgnoradas { get; set; } = new List<string>();
    }

    /// <summary>
    /// Palabras y columnas que el usuario decidió no volver a revisar. Se guardan en
    /// %APPDATA%\QueryAnalyzer\ortografia.xml (aparte de config.xml, que se serializa entero).
    /// </summary>
    public static class OrtografiaManager
    {
        private static readonly string ARCHIVO = Path.Combine(App.AppDataFolder, "ortografia.xml");
        private static readonly object _lock = new object();
        private static HashSet<string> _palabras;
        private static HashSet<string> _columnas;

        private static void Asegurar()
        {
            lock (_lock)
            {
                if (_palabras != null) return;
                _palabras = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _columnas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (!File.Exists(ARCHIVO)) return;
                    using (var fs = new FileStream(ARCHIVO, FileMode.Open, FileAccess.Read))
                    {
                        var datos = (OrtografiaDatos)new XmlSerializer(typeof(OrtografiaDatos)).Deserialize(fs);
                        if (datos == null) return;
                        foreach (var p in datos.PalabrasIgnoradas) _palabras.Add(p);
                        foreach (var c in datos.ColumnasIgnoradas) _columnas.Add(c);
                    }
                }
                catch { /* archivo ilegible: se arranca con listas vacías */ }
            }
        }

        public static bool EsPalabraIgnorada(string palabra)
        {
            Asegurar();
            lock (_lock) return _palabras.Contains(palabra);
        }

        public static void IgnorarPalabra(string palabra)
        {
            Asegurar();
            lock (_lock) { if (_palabras.Add(palabra)) Guardar(); }
        }

        public static bool EsColumnaIgnorada(string conexion, string columna)
        {
            Asegurar();
            lock (_lock) return _columnas.Contains(conexion + "|" + columna);
        }

        public static void IgnorarColumna(string conexion, string columna)
        {
            Asegurar();
            lock (_lock) { if (_columnas.Add(conexion + "|" + columna)) Guardar(); }
        }

        public static void QuitarColumnaIgnorada(string conexion, string columna)
        {
            Asegurar();
            lock (_lock) { if (_columnas.Remove(conexion + "|" + columna)) Guardar(); }
        }

        // Se llama con _lock tomado
        private static void Guardar()
        {
            try
            {
                var datos = new OrtografiaDatos
                {
                    PalabrasIgnoradas = new List<string>(_palabras),
                    ColumnasIgnoradas = new List<string>(_columnas)
                };
                using (var fs = new FileStream(ARCHIVO, FileMode.Create, FileAccess.Write))
                    new XmlSerializer(typeof(OrtografiaDatos)).Serialize(fs, datos);
            }
            catch { /* no interrumpir la app si falla el guardado */ }
        }
    }
}
