using System.ComponentModel;

namespace QueryAnalyzer
{
    public static class CategoriaOrtografia
    {
        public const string Ortografia  = "Ortografía";
        public const string FaltaTilde  = "Falta tilde";
        public const string Codificacion = "Codificación";
    }

    public class TablaOrtografia : INotifyPropertyChanged
    {
        private bool _incluir;

        public string Schema { get; set; }
        public string Nombre { get; set; }

        public string NombreCompleto => string.IsNullOrEmpty(Schema) ? Nombre : Schema + "." + Nombre;

        public bool Incluir
        {
            get { return _incluir; }
            set { if (_incluir == value) return; _incluir = value; Cambio("Incluir"); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Cambio(string n) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n)); }
    }

    public class ColumnaOrtografia : INotifyPropertyChanged
    {
        private bool _revisar;
        private string _estado = "";

        public string Schema { get; set; }
        public string Tabla  { get; set; }
        public string Nombre { get; set; }
        public string Tipo   { get; set; }
        public int?   Largo  { get; set; }

        /// <summary>Los tipos LOB (text, ntext, clob...) no admiten DISTINCT.</summary>
        public bool SinDistinct { get; set; }

        /// <summary>0 a 100: qué tan probable es que la columna tenga texto descriptivo. Solo sirve para preseleccionar.</summary>
        public int Puntaje { get; set; }

        public string Probabilidad => Puntaje >= 60 ? "Alta" : Puntaje >= 35 ? "Media" : "Baja";

        public string TablaCompleta => string.IsNullOrEmpty(Schema) ? Tabla : Schema + "." + Tabla;
        public string TipoLargo => Largo.HasValue && Largo.Value > 0 ? Tipo + "(" + Largo.Value + ")" : Tipo;

        /// <summary>Clave estable para recordar columnas ignoradas.</summary>
        public string Clave => TablaCompleta + "." + Nombre;

        public bool Revisar
        {
            get { return _revisar; }
            set { if (_revisar == value) return; _revisar = value; Cambio("Revisar"); }
        }

        public string Estado
        {
            get { return _estado; }
            set { if (_estado == value) return; _estado = value; Cambio("Estado"); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Cambio(string n) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n)); }
    }

    public class HallazgoOrtografico
    {
        public string Categoria     { get; set; }
        public string Schema        { get; set; }
        public string Tabla         { get; set; }
        public string Columna       { get; set; }
        public string Palabra       { get; set; }
        public string Sugerencias   { get; set; }

        /// <summary>Cantidad de valores distintos de la columna que contienen la palabra.</summary>
        public int    Valores       { get; set; }
        public string Ejemplo       { get; set; }

        public string TablaCompleta => string.IsNullOrEmpty(Schema) ? Tabla : Schema + "." + Tabla;
        public string ClaveColumna  => TablaCompleta + "." + Columna;
    }

    public class OpcionesOrtografia
    {
        public int  MaxValoresPorColumna { get; set; } = 50000;
        public bool TildeEsError         { get; set; } = true;
        public bool IgnorarSiglas        { get; set; } = true;
    }

    public class ResultadoColumnaOrtografia
    {
        public System.Collections.Generic.List<HallazgoOrtografico> Hallazgos { get; set; } =
            new System.Collections.Generic.List<HallazgoOrtografico>();
        public int  ValoresLeidos { get; set; }
        public bool Truncado      { get; set; }
    }
}
