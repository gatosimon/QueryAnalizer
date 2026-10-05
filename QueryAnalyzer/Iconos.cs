using System.Windows;
using System.Windows.Media;

namespace QueryAnalyzer
{
    /// <summary>Crea por codigo los iconos vectoriales de Icons.xaml (se tiÃ±en con el Foreground del control que los contiene).</summary>
    internal static class Iconos
    {
        public static System.Windows.Shapes.Path Crear(string clave)
        {
            return new System.Windows.Shapes.Path
            {
                Style = (Style)Application.Current.FindResource("Ic"),
                Data = (Geometry)Application.Current.FindResource(clave)
            };
        }
    }
}
