using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using System.IO;
using System.Windows;

namespace QueryAnalyzer
{
    public static class PhraseManager
    {
        private static readonly Random _rnd = new Random();

        // Catálogo cargado desde frases.xml
        private static Dictionary<string, List<string>> _frasesData;
        private static bool _isInitialized = false;

        // Cola persistente: secuencia global barajada de todas las frases.
        // Cada entrada es "categoria|indiceEnCategoria".
        private static List<string> _colaGlobal = null;
        private static int _posicionGlobal = 0;

        private static readonly string _appDataFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QueryAnalyzer");

        private static readonly string _estadoPath =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "QueryAnalyzer", "frases_estado.xml");

        // ── Inicialización del catálogo ──────────────────────────────────────────

        /// <summary>
        /// Carga el catálogo desde el XML de frases. Idempotente.
        /// </summary>
        public static void Initialize(string xmlPath)
        {
            if (_isInitialized) return;

            _frasesData = new Dictionary<string, List<string>>();

            if (!File.Exists(xmlPath))
            {
                _isInitialized = true;
                return;
            }

            XDocument doc = XDocument.Load(xmlPath);
            foreach (var cat in doc.Descendants("Categoria"))
            {
                string nombre = cat.Attribute("nombre")?.Value;
                if (string.IsNullOrEmpty(nombre)) continue;

                var frases = cat.Descendants("Frase")
                                .Select(f => f.Value.Trim())
                                .Where(s => !string.IsNullOrEmpty(s))
                                .ToList();

                if (frases.Count > 0 && !_frasesData.ContainsKey(nombre))
                    _frasesData[nombre] = frases;
            }

            _isInitialized = true;
        }

        // ── API pública ──────────────────────────────────────────────────────────

        /// <summary>
        /// Devuelve la siguiente frase de la cola global barajada.
        /// Las frases no se repiten hasta que se hayan mostrado todas.
        /// El estado se persiste en AppData para que la no-repetición
        /// funcione incluso entre reinicios de la aplicación.
        /// </summary>
        public static string ObtenerFraseCualquiera()
        {
            string rutaXml = Path.Combine(_appDataFolder, "frases.xml");

            if (File.Exists(rutaXml))
            {
                Initialize(rutaXml);
            }
            else
            {
                GarantizarArchivoEnPath("frases.xml", _appDataFolder);
                Initialize(rutaXml);
            }

            if (_frasesData == null || _frasesData.Count == 0)
                return "La vida es bella!";

            // Cargar o construir la cola persistente
            EnsurarColaGlobal();

            // Si ya recorrimos toda la cola, crear un nuevo ciclo barajado distinto
            if (_posicionGlobal >= _colaGlobal.Count)
            {
                RebuildCola(evitarPrimero: _colaGlobal.LastOrDefault());
                _posicionGlobal = 0;
            }

            string entrada = _colaGlobal[_posicionGlobal];
            _posicionGlobal++;
            PersistirEstado();

            return ResolverEntrada(entrada);
        }

        /// <summary>
        /// Devuelve una frase de una categoría específica sin repetir dentro de la sesión.
        /// </summary>
        public static string ObtenerFrase(string categoria)
        {
            ValidarInicializacion();

            if (!_frasesData.ContainsKey(categoria))
                return "Categoría no encontrada.";

            // Para uso por categoría, se sigue usando la selección aleatoria simple.
            var lista = _frasesData[categoria];
            return lista[_rnd.Next(lista.Count)];
        }

        public static List<string> GetCategorias()
        {
            ValidarInicializacion();
            return _frasesData.Keys.ToList();
        }

        // ── Cola global persistente ──────────────────────────────────────────────

        /// <summary>
        /// Carga la cola desde disco o la construye si no existe / está corrupta.
        /// </summary>
        private static void EnsurarColaGlobal()
        {
            if (_colaGlobal != null) return;  // ya cargada en esta sesión

            if (File.Exists(_estadoPath))
            {
                try
                {
                    XDocument doc = XDocument.Load(_estadoPath);
                    _posicionGlobal = int.Parse(doc.Root.Element("Posicion")?.Value ?? "0");
                    _colaGlobal = doc.Root
                                     .Element("Cola")?
                                     .Elements("I")
                                     .Select(e => e.Value)
                                     .ToList()
                                  ?? new List<string>();

                    // Validar que las entradas del estado sigan siendo coherentes
                    // con el catálogo actual (las frases pueden haber cambiado).
                    if (!EsEstadoValido())
                    {
                        _colaGlobal = null;
                        _posicionGlobal = 0;
                    }
                }
                catch
                {
                    _colaGlobal = null;
                    _posicionGlobal = 0;
                }
            }

            // Sin estado guardado o estado inválido: construir cola nueva
            if (_colaGlobal == null)
            {
                RebuildCola(evitarPrimero: null);
                _posicionGlobal = 0;
                PersistirEstado();
            }
        }

        /// <summary>
        /// Construye una cola barajada con todas las frases del catálogo.
        /// Si <paramref name="evitarPrimero"/> no es null, asegura que la primera
        /// entrada del nuevo ciclo sea distinta a la última del ciclo anterior,
        /// para evitar la misma frase dos veces seguidas al girar el ciclo.
        /// </summary>
        private static void RebuildCola(string evitarPrimero)
        {
            // Aplanar catálogo: "categoria|indice"
            var todas = new List<string>();
            foreach (var kvp in _frasesData)
                for (int i = 0; i < kvp.Value.Count; i++)
                    todas.Add($"{kvp.Key}|{i}");

            // Fisher-Yates shuffle
            for (int j = todas.Count - 1; j > 0; j--)
            {
                int k = _rnd.Next(j + 1);
                var tmp = todas[j]; todas[j] = todas[k]; todas[k] = tmp;
            }

            // Evitar repetición en la unión de ciclos
            if (evitarPrimero != null && todas.Count > 1 && todas[0] == evitarPrimero)
            {
                int swap = _rnd.Next(1, todas.Count);
                var tmp = todas[0]; todas[0] = todas[swap]; todas[swap] = tmp;
            }

            _colaGlobal = todas;
        }

        /// <summary>
        /// Verifica que todas las entradas de la cola guardada sigan existiendo
        /// en el catálogo actual.
        /// </summary>
        private static bool EsEstadoValido()
        {
            if (_colaGlobal == null || _colaGlobal.Count == 0) return false;
            if (_posicionGlobal < 0) return false;

            // Calcular el total de frases del catálogo actual
            int totalActual = _frasesData.Values.Sum(l => l.Count);
            if (_colaGlobal.Count != totalActual) return false;

            // Chequear que cada entrada apunte a una categoría/índice existente
            foreach (var entrada in _colaGlobal)
            {
                var partes = entrada.Split('|');
                if (partes.Length != 2) return false;
                if (!_frasesData.TryGetValue(partes[0], out var lista)) return false;
                if (!int.TryParse(partes[1], out int idx) || idx < 0 || idx >= lista.Count) return false;
            }
            return true;
        }

        /// <summary>
        /// Guarda la cola y la posición actual en el archivo de estado.
        /// </summary>
        private static void PersistirEstado()
        {
            try
            {
                if (!Directory.Exists(_appDataFolder))
                    Directory.CreateDirectory(_appDataFolder);

                var doc = new XDocument(
                    new XElement("Estado",
                        new XElement("Posicion", _posicionGlobal),
                        new XElement("Cola",
                            _colaGlobal.Select(e => new XElement("I", e)))));

                doc.Save(_estadoPath);
            }
            catch
            {
                // No fatal: en el peor caso la posición se pierde al reiniciar.
            }
        }

        /// <summary>
        /// Convierte "categoria|indice" → texto de la frase.
        /// </summary>
        private static string ResolverEntrada(string entrada)
        {
            var partes = entrada.Split('|');
            if (partes.Length == 2
                && _frasesData.TryGetValue(partes[0], out var lista)
                && int.TryParse(partes[1], out int idx)
                && idx >= 0 && idx < lista.Count)
            {
                return lista[idx];
            }
            return "La vida es bella!";
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static void ValidarInicializacion()
        {
            if (!_isInitialized)
                throw new InvalidOperationException(
                    "PhraseManager no ha sido inicializado. Llamá a PhraseManager.Initialize(path) primero.");
        }

        public static void GarantizarArchivoEnPath(string nombreArchivo, string pathDestino)
        {
            try
            {
                string fullPath = Path.Combine(pathDestino, nombreArchivo);
                if (File.Exists(fullPath)) return;

                if (!Directory.Exists(pathDestino))
                    Directory.CreateDirectory(pathDestino);

                var uri = new Uri($"/Assets/{nombreArchivo}", UriKind.Relative);
                var resourceStream = Application.GetResourceStream(uri);
                if (resourceStream != null)
                {
                    using (var fileStream = File.Create(fullPath))
                        resourceStream.Stream.CopyTo(fileStream);
                }
            }
            catch
            {
                // No fatal
            }
        }
    }
}
