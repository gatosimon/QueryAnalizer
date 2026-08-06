using System;
using System.Collections.Generic;

namespace QueryAnalyzer
{
    public class TablaTransfer
    {
        public string Schema { get; set; }
        public string Nombre { get; set; }

        public string NombreCompleto =>
            string.IsNullOrEmpty(Schema) ? Nombre : $"{Schema}.{Nombre}";

        public override string ToString() => NombreCompleto;
    }

    // Arista del grafo de FK: Origen tiene FK → Destino (Destino debe insertarse primero)
    public class FkEdge
    {
        public string TablaOrigen   { get; set; }   // tabla que tiene la FK
        public string TablaDestino  { get; set; }   // tabla referenciada
        public string ColumnaOrigen { get; set; }
        public string ColumnaDestino{ get; set; }
    }

    public class OpcionesPasado
    {
        public bool GenerarSoloScript { get; set; } = false;
        public bool HacerBackupDeB    { get; set; } = true;
        public bool Transaccional     { get; set; } = true;
    }

    public class ResultadoPasado
    {
        public bool   Exito               { get; set; }
        public string BackupScript        { get; set; }
        public string ScriptTransferencia { get; set; }
        public List<string> Advertencias  { get; set; } = new List<string>();
        public string Error               { get; set; }

        // Clave = NombreCompleto de la tabla; valor = script INSERT solo de esa tabla (sin DELETE)
        public Dictionary<string, string> ScriptsPorTabla { get; set; } = new Dictionary<string, string>();
    }

    public class TablaMetadata
    {
        // Columnas identity/autoincrement de cualquier tipo
        public HashSet<string> ColumnasIdentity       { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Columnas que necesitan OVERRIDING SYSTEM VALUE (PG/DB2: GENERATED ALWAYS AS IDENTITY)
        public HashSet<string> ColumnasIdentityAlways { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Columnas que NO se pueden insertar: computed, timestamp, rowversion, generated expr
        public HashSet<string> ColumnasExcluidas      { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool TieneIdentity       => ColumnasIdentity.Count > 0;
        public bool TieneIdentityAlways => ColumnasIdentityAlways.Count > 0;
    }
}
