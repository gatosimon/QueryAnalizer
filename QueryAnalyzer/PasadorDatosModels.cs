using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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

        /// <summary>
        /// Tamaño máximo (en bytes) de un valor binario/LOB por fila. Si una fila lo supera la
        /// transferencia se aborta (B queda intacta) salvo que <see cref="OmitirFilasLobGrandes"/> esté activo.
        /// </summary>
        public long UmbralLobBytes { get; set; } = 128L * 1024 * 1024;

        /// <summary>
        /// Si es true, las filas con un LOB sobre el umbral se omiten y se registran como incidencia
        /// (la tabla queda incompleta en B). Por defecto es false: se aborta.
        /// </summary>
        public bool OmitirFilasLobGrandes { get; set; } = false;

        /// <summary>Carpeta donde se escribe el script cuando <see cref="GenerarSoloScript"/> está activo.</summary>
        public string CarpetaScript { get; set; }

        /// <summary>Cada cuántas filas se actualiza la barra de progreso.</summary>
        public int FilasPorReporte { get; set; } = 200;

        /// <summary>
        /// Conteo de filas de A por tabla (clave = NombreCompleto), si ya se calculó en la
        /// prevalidación. Se usa para el total de la barra de progreso.
        /// </summary>
        public Dictionary<string, long> ConteosA { get; set; }
    }

    /// <summary>Una porción del script de transferencia (una tabla) escrita en disco.</summary>
    public class ParteScript
    {
        public string Tabla   { get; set; }
        public string Antes   { get; set; }   // p. ej. SET IDENTITY_INSERT ... ON
        public string Archivo { get; set; }   // INSERTs de la tabla, en disco
        public string Despues { get; set; }   // p. ej. SET IDENTITY_INSERT ... OFF
        public long   Filas   { get; set; }
    }

    public class ResultadoPasado
    {
        public bool   Exito               { get; set; }
        public string Error               { get; set; }

        /// <summary>Ruta del archivo de backup (modo "un archivo") o null.</summary>
        public string RutaBackup          { get; set; }

        /// <summary>Carpeta del backup (modo "uno por tabla") o null.</summary>
        public string CarpetaBackup       { get; set; }

        /// <summary>Carpeta temporal con las partes del script cuando se pidió "solo script".</summary>
        public string CarpetaScript       { get; set; }
        public string PrefijoScript       { get; set; }
        public string SufijoScript        { get; set; }
        public List<ParteScript> PartesScript { get; set; } = new List<ParteScript>();

        public long FilasTransferidas     { get; set; }

        /// <summary>Incidencias registradas durante toda la operación (compartida con la ventana).</summary>
        public RegistroIncidencias Incidencias { get; set; }

        public bool TieneScript => PartesScript != null && PartesScript.Count > 0;
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

    // ─────────────────────────────────────────────────────────────────────────
    // Incidencias: toda falla o anomalía se registra para que el usuario decida
    // ─────────────────────────────────────────────────────────────────────────

    public enum SeveridadIncidencia
    {
        Info    = 0,
        Aviso   = 1,   // la operación puede seguir sin riesgo para los datos
        Error   = 2,   // hay datos que no se pudieron leer/respaldar/cargar: requiere decisión
        Critica = 3    // el estado de B puede haber quedado inconsistente
    }

    public class IncidenciaPasado
    {
        public DateTime            Hora           { get; set; } = DateTime.Now;
        public string              Fase           { get; set; }   // Origen, Backup, Metadata, Transferencia, FK, Verificación
        public string              Tabla          { get; set; }
        public string              Columna        { get; set; }
        public string              Clave          { get; set; }   // fila afectada (primeros valores)
        public string              Tipo           { get; set; }
        public SeveridadIncidencia Severidad      { get; set; }
        public string              Mensaje        { get; set; }
        public string              AccionSugerida { get; set; }

        public string HoraTexto      => Hora.ToString("HH:mm:ss");
        public string SeveridadTexto => Severidad == SeveridadIncidencia.Critica ? "Crítica"
                                      : Severidad == SeveridadIncidencia.Error   ? "Error"
                                      : Severidad == SeveridadIncidencia.Aviso   ? "Aviso" : "Info";

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(SeveridadTexto).Append("] ").Append(Fase);
            if (!string.IsNullOrEmpty(Tabla))   sb.Append(" · ").Append(Tabla);
            if (!string.IsNullOrEmpty(Columna)) sb.Append('.').Append(Columna);
            sb.Append(": ").Append(Tipo).Append(" — ").Append(Mensaje);
            if (!string.IsNullOrEmpty(Clave))          sb.Append(" (").Append(Clave).Append(')');
            if (!string.IsNullOrEmpty(AccionSugerida)) sb.Append(" → ").Append(AccionSugerida);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Lista thread-safe de incidencias. Se puede escribir desde el hilo de trabajo y leer desde la UI.
    /// </summary>
    public class RegistroIncidencias
    {
        private readonly List<IncidenciaPasado> _lista = new List<IncidenciaPasado>();
        private readonly object _lock = new object();

        /// <summary>Se dispara (en el hilo que agrega) cada vez que se registra una incidencia.</summary>
        public event Action<IncidenciaPasado> Agregada;

        /// <summary>Tope de incidencias guardadas: evita inundar la UI y la memoria si falla cada fila.</summary>
        public const int Maximo = 5000;
        private int _descartadas;

        public int Count { get { lock (_lock) return _lista.Count; } }

        public IncidenciaPasado Agregar(
            string fase, string tabla, SeveridadIncidencia severidad, string tipo, string mensaje,
            string accionSugerida = null, string columna = null, string clave = null)
        {
            var inc = new IncidenciaPasado
            {
                Fase = fase, Tabla = tabla, Severidad = severidad, Tipo = tipo, Mensaje = mensaje,
                AccionSugerida = accionSugerida, Columna = columna, Clave = clave
            };
            lock (_lock)
            {
                if (_lista.Count >= Maximo)
                {
                    // Se conserva una sola marca de que hubo más; el CSV y la grilla quedan acotados
                    if (_descartadas++ > 0) return inc;
                    inc = new IncidenciaPasado
                    {
                        Fase = fase, Severidad = SeveridadIncidencia.Error, Tipo = "Demasiadas incidencias",
                        Mensaje = $"Se alcanzó el máximo de {Maximo} incidencias registradas; las siguientes no se guardan.",
                        AccionSugerida = "Corregí la causa de las primeras y repetí la operación."
                    };
                }
                _lista.Add(inc);
            }
            try { Agregada?.Invoke(inc); } catch { }
            return inc;
        }

        public List<IncidenciaPasado> Todas()
        {
            lock (_lock) return new List<IncidenciaPasado>(_lista);
        }

        /// <summary>Incidencias agregadas a partir de la posición <paramref name="indice"/>.</summary>
        public List<IncidenciaPasado> Desde(int indice)
        {
            lock (_lock) return _lista.Skip(Math.Max(0, indice)).ToList();
        }

        public bool Hay(SeveridadIncidencia minima, int desdeIndice = 0)
        {
            lock (_lock) return _lista.Skip(Math.Max(0, desdeIndice)).Any(i => i.Severidad >= minima);
        }

        /// <summary>Guarda las incidencias como CSV (separador ';', UTF-8 con BOM, para abrir en Excel).</summary>
        public void GuardarCsv(string ruta)
        {
            EscribirCsv(Todas(), ruta);
        }

        public static void EscribirCsv(IEnumerable<IncidenciaPasado> items, string ruta)
        {
            using (var w = new StreamWriter(ruta, false, new UTF8Encoding(true)))
            {
                w.WriteLine("Hora;Severidad;Fase;Tabla;Columna;Clave;Tipo;Mensaje;Accion sugerida");
                foreach (var i in items)
                    w.WriteLine(string.Join(";", new[]
                    {
                        Csv(i.Hora.ToString("yyyy-MM-dd HH:mm:ss")), Csv(i.SeveridadTexto), Csv(i.Fase),
                        Csv(i.Tabla), Csv(i.Columna), Csv(i.Clave), Csv(i.Tipo), Csv(i.Mensaje), Csv(i.AccionSugerida)
                    }));
            }
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.IndexOfAny(new[] { ';', '"' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
