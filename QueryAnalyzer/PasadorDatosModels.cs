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

        /// <summary>
        /// Si no es null, los identificadores se escriben entre comillas del motor (corchetes en SQL Server,
        /// comillas dobles en el resto). Solo lo usan los nombres de destino resueltos desde el catálogo de B
        /// (transferencia entre motores), donde se conoce el nombre exacto.
        /// </summary>
        public TipoMotor? ComillasMotor { get; set; }

        /// <summary>
        /// Tabla equivalente en B cuando los motores de A y B difieren (nombre/esquema reales de B).
        /// Null = B usa el mismo nombre que A (transferencia entre bases del mismo motor).
        /// </summary>
        public TablaTransfer Destino { get; set; }

        /// <summary>Columnas y metadata de la tabla en B, ya leídas del catálogo (solo en el objeto de destino).</summary>
        public TablaMetadata Meta { get; set; }

        /// <summary>La tabla tal como se llama en B.</summary>
        public TablaTransfer B => Destino ?? this;

        public string NombreCompleto
        {
            get
            {
                if (ComillasMotor.HasValue)
                {
                    string n = ScriptHelper.QuoteId(Nombre, ComillasMotor.Value);
                    return string.IsNullOrEmpty(Schema) ? n : ScriptHelper.QuoteId(Schema, ComillasMotor.Value) + "." + n;
                }
                return string.IsNullOrEmpty(Schema) ? Nombre : $"{Schema}.{Nombre}";
            }
        }

        public override string ToString() => NombreCompleto;
    }

    /// <summary>Familia de un tipo de dato, independiente del motor (para convertir valores entre motores).</summary>
    public enum CategoriaTipo
    {
        Otro, Texto, TextoUnicode, Entero, Decimal, Real, Bool,
        Fecha, FechaHora, FechaHoraTz, Hora, Binario, Guid, Xml
    }

    /// <summary>Una columna de la tabla destino (B), leída del catálogo del motor de B.</summary>
    public class ColumnaDestino
    {
        public string        Nombre           { get; set; }
        public string        Tipo             { get; set; }   // en minúsculas, tal como lo informa el catálogo
        public CategoriaTipo Categoria        { get; set; }
        public bool          Nulable          { get; set; }
        public bool          TieneDefault     { get; set; }
        public bool          EsIdentity       { get; set; }
        public bool          EsIdentityAlways { get; set; }
        public bool          Excluida         { get; set; }   // calculada / rowversion / generada: no se inserta
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
        /// Con B en SQL Server, carga con SqlBulkCopy en vez de un INSERT por fila (mucho más rápido). Si no se puede
        /// abrir la conexión directa a SQL Server, se usa el INSERT fila por fila y queda una incidencia de aviso.
        /// </summary>
        public bool UsarCargaMasiva   { get; set; } = true;

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

        /// <summary>Columnas de la tabla en B con su tipo (solo se completa en transferencias entre motores distintos).</summary>
        public List<ColumnaDestino> Columnas { get; } = new List<ColumnaDestino>();

        private Dictionary<string, ColumnaDestino> _porNombre;

        /// <summary>Busca una columna de B por nombre sin distinguir mayúsculas. Null si no existe.</summary>
        public ColumnaDestino Buscar(string nombre)
        {
            if (_porNombre == null || _porNombre.Count != Columnas.Count)
            {
                _porNombre = new Dictionary<string, ColumnaDestino>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in Columnas) _porNombre[c.Nombre] = c;
            }
            ColumnaDestino r;
            return nombre != null && _porNombre.TryGetValue(nombre, out r) ? r : null;
        }

        /// <summary>Completa los conjuntos identity / excluidas a partir de <see cref="Columnas"/>.</summary>
        public void DerivarConjuntos()
        {
            foreach (var c in Columnas)
            {
                if (c.EsIdentity) ColumnasIdentity.Add(c.Nombre);
                if (c.EsIdentityAlways) ColumnasIdentityAlways.Add(c.Nombre);
                if (c.Excluida) ColumnasExcluidas.Add(c.Nombre);
            }
        }
    }

    /// <summary>
    /// Cómo se copian las columnas de una tabla de A a B cuando los motores difieren: qué columnas de A se
    /// escriben, con qué nombre exacto (entre comillas) y con qué tipo de destino.
    /// </summary>
    internal sealed class MapaInsert
    {
        public int[]             Indices;      // posición de cada columna copiada en la fila leída de A
        public string[]          Nombres;      // nombre en A
        public string[]          TiposA;       // nombre de tipo informado por el driver de A
        public ColumnaDestino[]  Destino;
        public string            ColsSql;      // lista de columnas de B, entre comillas
        public object[]          Buf;          // valores de la fila ya filtrados, alineados con Destino
        public bool              CopiaIdentity;
        public bool              CopiaIdentityAlways;

        public static MapaInsert Crear(LectorTabla lector, TablaMetadata meta, TipoMotor motorB)
        {
            var idx = new List<int>(); var nom = new List<string>(); var tip = new List<string>();
            var dst = new List<ColumnaDestino>();
            for (int k = 0; k < lector.Columnas.Length; k++)
            {
                var cd = meta.Buscar(lector.Columnas[k]);
                if (cd == null || cd.Excluida) continue;   // sin equivalente en B: se omite (ya quedó como incidencia)
                idx.Add(k); nom.Add(lector.Columnas[k]); tip.Add(lector.TiposA[k]); dst.Add(cd);
            }
            if (idx.Count == 0)
                throw new InvalidOperationException(
                    "Ninguna columna de A tiene equivalente en B (los nombres no coinciden). Revisá que sean las mismas tablas.");

            var m = new MapaInsert
            {
                Indices = idx.ToArray(), Nombres = nom.ToArray(), TiposA = tip.ToArray(), Destino = dst.ToArray(),
                ColsSql = string.Join(", ", dst.Select(c => ScriptHelper.QuoteId(c.Nombre, motorB))),
                Buf = new object[idx.Count]
            };
            m.CopiaIdentity       = dst.Any(c => c.EsIdentity);
            m.CopiaIdentityAlways = dst.Any(c => c.EsIdentityAlways);
            return m;
        }

        /// <summary>Copia a <see cref="Buf"/> solo los valores de las columnas que se escriben en B.</summary>
        public object[] Filtrar(object[] fila)
        {
            for (int j = 0; j < Indices.Length; j++) Buf[j] = fila[Indices[j]];
            return Buf;
        }
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
