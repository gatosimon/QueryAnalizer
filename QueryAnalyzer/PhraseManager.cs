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
        private static Dictionary<string, List<string>> _frasesData;
        private static Dictionary<string, List<int>> _indicesDisponibles;
        private static bool _isInitialized = false;

        /// <summary>
        /// Carga los datos del XML. Debe llamarse una sola vez al iniciar la app.
        /// </summary>
        public static void Initialize(string xmlPath)
        {
            if (_isInitialized) return;

            _frasesData = new Dictionary<string, List<string>>();
            _indicesDisponibles = new Dictionary<string, List<int>>();

            if (!File.Exists(xmlPath))
                return; //throw new FileNotFoundException("No se encontró el archivo XML de frases.");

            XDocument doc = XDocument.Load(xmlPath);
            var categorias = doc.Descendants("Categoria");

            foreach (var cat in categorias)
            {
                string nombreCat = cat.Attribute("nombre").Value;
                var frases = cat.Descendants("Frase")
                                .Select(f => f.Value.Trim())
                                .Where(s => !string.IsNullOrEmpty(s))
                                .ToList();

                if (frases.Count > 0 && !_frasesData.ContainsKey(nombreCat))
                {
                    _frasesData.Add(nombreCat, frases);
                    _indicesDisponibles.Add(nombreCat, Enumerable.Range(0, frases.Count).ToList());
                }
            }

            _isInitialized = true;
        }

        /// <summary>
        /// Obtiene una frase de una categoría específica sin repetir.
        /// </summary>
        public static string ObtenerFrase(string categoria)
        {
            ValidarInicializacion();

            if (!_frasesData.ContainsKey(categoria))
                return "Categoría no encontrada.";

            var disponibles = _indicesDisponibles[categoria];

            if (disponibles.Count == 0)
            {
                disponibles.AddRange(Enumerable.Range(0, _frasesData[categoria].Count));
            }

            int n = _rnd.Next(disponibles.Count);
            int indiceReal = disponibles[n];
            disponibles.RemoveAt(n);

            return _frasesData[categoria][indiceReal];
        }

        /// <summary>
        /// Obtiene una frase al azar de cualquier categoría sin repetir.
        /// </summary>
        public static string ObtenerFraseCualquiera()
        {
            // Obtenés la ruta del XML (asumiendo que está en la carpeta del .exe)
            string rutaXml = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QueryAnalyzer", "frases.xml");
            if (File.Exists(rutaXml))
            {
                // Inicialización estática
                PhraseManager.Initialize(rutaXml);


                ValidarInicializacion();

                var categoriasConFrases = _indicesDisponibles
                    .Where(kvp => kvp.Value.Count > 0)
                    .Select(kvp => kvp.Key)
                    .ToList();

                // Si todas las categorías se vaciaron, reiniciamos todos los índices
                if (categoriasConFrases.Count == 0)
                {
                    foreach (var cat in _frasesData.Keys)
                    {
                        _indicesDisponibles[cat] = Enumerable.Range(0, _frasesData[cat].Count).ToList();
                    }
                    categoriasConFrases = _frasesData.Keys.ToList();
                }

                string categoriaAzar = categoriasConFrases[_rnd.Next(categoriasConFrases.Count)];
                return ObtenerFrase(categoriaAzar);
            }
            else
            {
                GarantizarArchivoEnPath("frases.xml", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QueryAnalyzer"));
                return "La vida es bella!";
            }
        }

        public static List<string> GetCategorias()
        {
            ValidarInicializacion();
            return _frasesData.Keys.ToList();
        }

        private static void ValidarInicializacion()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("PhraseManager no ha sido inicializado. Llamá a PhraseManager.Initialize(path) primero.");
        }

        public static void GarantizarArchivoEnPath(string nombreArchivo, string pathDestino)
        {
            try
            {
                // 1. Combinar la ruta completa
                string fullPath = Path.Combine(pathDestino, nombreArchivo);

                // 2. Si el archivo ya existe, no hacemos nada
                if (File.Exists(fullPath)) return;

                // 3. Si no existe, crear el directorio si es necesario
                string directorio = Path.GetDirectoryName(fullPath);
                if (!Directory.Exists(directorio))
                {
                    Directory.CreateDirectory(directorio);
                }

                // 4. Leer el archivo desde los Assets (Resources de WPF)
                // La URI suele ser: "pack://application:,,,/NombreDeTuEnsamblado;component/Assets/tuarchivo.ext"
                var uri = new Uri($"/Assets/{nombreArchivo}", UriKind.Relative);
                var resourceStream = Application.GetResourceStream(uri);

                if (resourceStream != null)
                {
                    using (var fileStream = File.Create(fullPath))
                    {
                        resourceStream.Stream.CopyTo(fileStream);
                    }
                }
            }
            catch (Exception ex)
            {
            }
        }
    }
}