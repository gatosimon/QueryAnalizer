using System.Collections.Generic;

namespace QueryAnalyzer
{
    public enum DiffEstado { Igual, SoloEnA, SoloEnB, Diferente }

    public class ColumnaComp
    {
        public string Nombre     { get; set; }
        public string TipoDato   { get; set; }
        public int?   Longitud   { get; set; }
        public int?   Precision  { get; set; }
        public int?   Escala     { get; set; }
        public bool   Nullable   { get; set; }
        public string Default    { get; set; }
        public bool   EsPK       { get; set; }

        public string ResumenTipo()
        {
            string t = TipoDato ?? "";
            if (Longitud.HasValue)  return $"{t}({Longitud})";
            if (Precision.HasValue && Escala.HasValue) return $"{t}({Precision},{Escala})";
            if (Precision.HasValue) return $"{t}({Precision})";
            return t;
        }
    }

    public class TablaComp
    {
        public string             Schema   { get; set; }
        public string             Nombre   { get; set; }
        public List<ColumnaComp> Columnas { get; set; } = new List<ColumnaComp>();
        public string             NombreCompleto => string.IsNullOrEmpty(Schema) ? Nombre : $"{Schema}.{Nombre}";
    }

    public class VistaComp
    {
        public string Schema     { get; set; }
        public string Nombre     { get; set; }
        public string Definicion { get; set; }
        public string NombreCompleto => string.IsNullOrEmpty(Schema) ? Nombre : $"{Schema}.{Nombre}";
    }

    public class IndiceComp
    {
        public string Schema   { get; set; }
        public string Tabla    { get; set; }
        public string Nombre   { get; set; }
        public bool   EsUnico  { get; set; }
        public bool   EsPK     { get; set; }
        public string Columnas { get; set; }
        public string Clave    => $"{Schema}.{Tabla}.{Nombre}";
    }

    public class DiffColumna
    {
        public ColumnaComp LadoA     { get; set; }
        public ColumnaComp LadoB     { get; set; }
        public DiffEstado  Estado    { get; set; }
        public string      Nombre    => (LadoA ?? LadoB).Nombre;
    }

    public class DiffTabla
    {
        public TablaComp       LadoA    { get; set; }
        public TablaComp       LadoB    { get; set; }
        public DiffEstado      Estado   { get; set; }
        public List<DiffColumna> Columnas { get; set; } = new List<DiffColumna>();
        public string          Nombre   => (LadoA ?? LadoB).NombreCompleto;
    }

    public class DiffVista
    {
        public VistaComp  LadoA  { get; set; }
        public VistaComp  LadoB  { get; set; }
        public DiffEstado Estado { get; set; }
        public string     Nombre => (LadoA ?? LadoB).NombreCompleto;
    }

    public class DiffIndice
    {
        public IndiceComp LadoA  { get; set; }
        public IndiceComp LadoB  { get; set; }
        public DiffEstado Estado { get; set; }
        public string     Clave  => (LadoA ?? LadoB).Clave;
        public string     Nombre => (LadoA ?? LadoB).Nombre;
    }

    public class DiffDatos
    {
        public string       Tabla       { get; set; }
        public long         ConteoA     { get; set; }
        public long         ConteoB     { get; set; }
        public DiffEstado   Estado      { get; set; }
        public List<string> FilasSoloA  { get; set; } = new List<string>();
        public List<string> FilasSoloB  { get; set; } = new List<string>();
    }

    public class ResultadoComp
    {
        public List<DiffTabla>  Tablas  { get; set; } = new List<DiffTabla>();
        public List<DiffVista>  Vistas  { get; set; } = new List<DiffVista>();
        public List<DiffIndice> Indices { get; set; } = new List<DiffIndice>();
        public List<DiffDatos>  Datos   { get; set; } = new List<DiffDatos>();
    }

    public class OpcionesComp
    {
        public bool CompararTablas   { get; set; } = true;
        public bool CompararVistas   { get; set; } = true;
        public bool CompararIndices  { get; set; } = true;
        public bool CompararDatos    { get; set; } = false;
        public bool MostrarIguales   { get; set; } = false;
    }

    public class InfoLado
    {
        public Conexion  Conexion  { get; set; }
        public string    ConnStr   { get; set; }
        public string[]  Schemas   { get; set; }
    }
}
