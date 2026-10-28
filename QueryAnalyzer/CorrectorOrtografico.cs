using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using WeCantSpell.Hunspell;

namespace QueryAnalyzer
{
    public class ResultadoPalabra
    {
        public bool   Correcta     { get; set; }
        public string Categoria    { get; set; }
        public string Sugerencias  { get; set; }
    }

    /// <summary>
    /// Corrector en español (es_AR de LibreOffice, embebido en el .exe) sobre WeCantSpell.Hunspell.
    /// Cada palabra distinta se revisa una sola vez: el resultado queda en caché.
    /// </summary>
    public sealed class CorrectorOrtografico
    {
        private const string RecursoDic = "QueryAnalyzer.Diccionarios.es_AR.dic";
        private const string RecursoAff = "QueryAnalyzer.Diccionarios.es_AR.aff";

        private static readonly object _lockInstancia = new object();
        private static CorrectorOrtografico _instancia;

        private readonly WordList _lista;
        private readonly Dictionary<string, ResultadoPalabra> _cache =
            new Dictionary<string, ResultadoPalabra>(StringComparer.Ordinal);

        // Ã y Â seguidas de un símbolo: UTF-8 leído como Windows-1252 ("Ã³" en vez de "ó"). Ninguna palabra
        // en español lleva esas letras, y U+FFFD es el carácter de reemplazo que deja una conversión fallida.
        private static readonly Regex Mojibake = new Regex(
            "[ÃÂ][\u0080-¿‘’“”•–—˜™š›œžŸ" +
            "‚ƒ„…†‡ˆ‰Š‹Œ€]|�",
            RegexOptions.Compiled);

        // Correos y direcciones web no son prosa: se quitan antes de separar palabras
        private static readonly Regex EmailUrl = new Regex(
            @"\S+@\S+|https?://\S+|www\.\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private CorrectorOrtografico(WordList lista) { _lista = lista; }

        /// <summary>Carga el diccionario la primera vez (unos 250 ms y 7 MB). Lanza si el recurso no está en el .exe.</summary>
        public static CorrectorOrtografico Instancia
        {
            get
            {
                lock (_lockInstancia)
                {
                    if (_instancia != null) return _instancia;
                    var asm = typeof(CorrectorOrtografico).Assembly;
                    using (var dic = asm.GetManifestResourceStream(RecursoDic))
                    using (var aff = asm.GetManifestResourceStream(RecursoAff))
                    {
                        if (dic == null || aff == null)
                            throw new InvalidOperationException("El diccionario español no está incluido en la aplicación.");
                        _instancia = new CorrectorOrtografico(WordList.CreateFromStreams(dic, aff));
                    }
                    return _instancia;
                }
            }
        }

        public static bool TieneCodificacionRota(string texto, out string fragmento)
        {
            var m = Mojibake.Match(texto);
            fragmento = m.Success ? m.Value : null;
            return m.Success;
        }

        /// <summary>
        /// Palabras revisables del texto. Descarta lo que no es prosa: menos de 3 letras, códigos pegados a
        /// dígitos o guiones bajos, identificadores camelCase, correos y URLs.
        /// </summary>
        public static IEnumerable<string> Palabras(string texto)
        {
            if (string.IsNullOrEmpty(texto)) yield break;
            texto = EmailUrl.Replace(texto, " ");

            int n = texto.Length;
            int i = 0;
            while (i < n)
            {
                if (!char.IsLetter(texto[i])) { i++; continue; }
                int ini = i;
                while (i < n && char.IsLetter(texto[i])) i++;

                int largo = i - ini;
                if (largo < 3) continue;
                char antes   = ini > 0 ? texto[ini - 1] : ' ';
                char despues = i < n ? texto[i] : ' ';
                if (char.IsDigit(antes) || char.IsDigit(despues) || antes == '_' || despues == '_') continue;

                string token = texto.Substring(ini, largo);
                if (EsCamelCase(token)) continue;
                yield return token;
            }
        }

        public static bool EsSigla(string token)
        {
            return token.Length <= 4 && token.All(char.IsUpper);
        }

        private static bool EsCamelCase(string token)
        {
            for (int k = 1; k < token.Length; k++)
                if (char.IsUpper(token[k]) && char.IsLower(token[k - 1])) return true;
            return false;
        }

        public ResultadoPalabra Revisar(string token)
        {
            lock (_cache)
            {
                ResultadoPalabra r;
                if (_cache.TryGetValue(token, out r)) return r;
                r = Calcular(token);
                _cache[token] = r;
                return r;
            }
        }

        private ResultadoPalabra Calcular(string token)
        {
            // La capitalización no se juzga: "rafaela" o "PEREZ" no son errores de ortografía
            string minus = token.ToLowerInvariant();
            string capital = char.ToUpperInvariant(minus[0]) + minus.Substring(1);
            if (_lista.Check(token) || _lista.Check(minus) || _lista.Check(capital))
                return new ResultadoPalabra { Correcta = true };

            var sugeridas = _lista.Suggest(token).Take(5).ToList();
            string sinTilde = QuitarTildes(token);
            bool soloTilde = sugeridas.Any(s => !string.Equals(s, token, StringComparison.Ordinal) &&
                                                string.Equals(QuitarTildes(s), sinTilde, StringComparison.OrdinalIgnoreCase));
            return new ResultadoPalabra
            {
                Correcta    = false,
                Categoria   = soloTilde ? CategoriaOrtografia.FaltaTilde : CategoriaOrtografia.Ortografia,
                Sugerencias = string.Join(", ", sugeridas)
            };
        }

        // La ñ no se toca: "año" y "ano" son palabras distintas
        private static string QuitarTildes(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case 'á': sb.Append('a'); break;
                    case 'é': sb.Append('e'); break;
                    case 'í': sb.Append('i'); break;
                    case 'ó': sb.Append('o'); break;
                    case 'ú': case 'ü': sb.Append('u'); break;
                    case 'Á': sb.Append('A'); break;
                    case 'É': sb.Append('E'); break;
                    case 'Í': sb.Append('I'); break;
                    case 'Ó': sb.Append('O'); break;
                    case 'Ú': case 'Ü': sb.Append('U'); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
