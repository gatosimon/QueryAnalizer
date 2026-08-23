using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace QueryAnalyzer
{
    // Los modos de borrado físico van al final: los ordinales de los tres primeros ya
    // están persistidos en el Tag de los ComboBoxItem y se parsean por nombre, pero no
    // vale la pena arriesgar el orden.
    //
    // BorradoSeguro es la contracara de BorradoEnCascada. Cascada condena al padre y
    // arrastra a los hijos; seguro borra una fila sólo si NADIE vivo la referencia, que
    // es lo que declaran las FK de la base (las 135 de SIEP son NO ACTION, sin una sola
    // excepción). Cascada emula un ON DELETE CASCADE que ninguna relación autoriza, y
    // por eso se llevó puestas 41 filas activas de PreguntasPorCuestionario.
    //
    // BorradoIterativo es la tercera vía, y la única que rompe la integridad a propósito:
    // suspende las constraints, borra TODAS las bajas sin mirar quién las referencia, barre en
    // loop lo que quedó totalmente desconectado, y recién ahí rehabilita. A diferencia de los
    // otros dos, este SÍ borra filas activas — una fila viva que sólo apuntaba a registros dados
    // de baja queda apuntando a la nada y se va. Es el resultado que arriba se describe como
    // accidente; acá es el criterio declarado del modo.
    public enum ModoConflicto { Bloquear, BajaEnCascada, Ignorar, BorradoEnCascada, BorradoSeguro, BorradoIterativo }

    // ── Condición de baja compuesta ───────────────────────────────────────

    public class CondicionBaja
    {
        public string Campo      { get; set; }
        // Operadores: "IS NOT NULL", "IS NULL", "IS NOT EMPTY", "IS EMPTY",
        //             "=", "<>", ">", ">=", "<", "<="
        public string Operador   { get; set; }
        public string Valor      { get; set; }   // para operadores de comparación; null si no aplica
        public string ValorSet   { get; set; }   // valor a SET en cascade baja (ej: "'SISTEMA'", "GETDATE()")
        public string Combinador { get; set; } = "AND";   // "AND" | "OR" — conecta con la siguiente condición

        public static readonly string[] OperadoresSinValor =
            { "IS NOT NULL", "IS NULL", "IS NOT EMPTY", "IS EMPTY" };

        // SQL de la expresión de esta condición (sin combinador)
        public string ToExprSql(string campoQuoted)
        {
            switch (Operador)
            {
                case "IS NOT NULL":  return $"{campoQuoted} IS NOT NULL";
                case "IS NULL":      return $"{campoQuoted} IS NULL";
                case "IS NOT EMPTY": return $"({campoQuoted} IS NOT NULL AND {campoQuoted} <> '')";
                case "IS EMPTY":     return $"({campoQuoted} IS NULL OR {campoQuoted} = '')";
                default:             return $"{campoQuoted} {Operador} {EscVal(Valor)}";
            }
        }

        private static string EscVal(string v)
        {
            if (string.IsNullOrEmpty(v)) return "NULL";
            if (int.TryParse(v, out _)) return v;
            if (v.Equals("true", System.StringComparison.OrdinalIgnoreCase) ||
                v.Equals("false", System.StringComparison.OrdinalIgnoreCase)) return v;
            return $"'{v.Replace("'", "''")}'";
        }
    }

    // ── Tabla ─────────────────────────────────────────────────────────────

    public class TablaConfigLimpiador
    {
        public string Schema  { get; set; }
        public string Nombre  { get; set; }
        public bool   Incluir { get; set; } = true;
        public List<CondicionBaja> CondicionesBaja { get; set; } = new List<CondicionBaja>();
        // Columnas de la clave primaria, en orden de la PK. Puede ser compuesta.
        public List<string> CamposPK { get; set; } = new List<string>();
        public bool   ReordenarIds { get; set; }

        public string NombreCompleto =>
            string.IsNullOrEmpty(Schema) ? Nombre : $"{Schema}.{Nombre}";

        public bool TieneCondiciones =>
            CondicionesBaja != null && CondicionesBaja.Count > 0;

        public bool TienePK  => CamposPK != null && CamposPK.Count > 0;

        // El reordenamiento de IDs sólo tiene sentido sobre una PK de una columna
        public bool PKSimple => CamposPK != null && CamposPK.Count == 1;

        public string ResumenPK => TienePK ? string.Join(", ", CamposPK) : "";

        public string ResumenCondiciones =>
            !TieneCondiciones
                ? "(sin condición)"
                : string.Join(" ", CondicionesBaja.Select((c, i) =>
                    (i > 0 ? c.Combinador + " " : "") +
                    c.Campo + " " + c.Operador +
                    (!string.IsNullOrEmpty(c.Valor) ? " " + c.Valor : "")));
    }

    // ── FK / Análisis ─────────────────────────────────────────────────────

    public class FKRelacionLimpiador
    {
        public string SchemaOrigen   { get; set; }
        public string TablaOrigen    { get; set; }
        public string ColumnaOrigen  { get; set; }
        public string SchemaDestino  { get; set; }
        public string TablaDestino   { get; set; }
        public string ColumnaDestino { get; set; }
        // Nombre del constraint: agrupa las columnas de una misma FK compuesta
        public string NombreFK       { get; set; }

        // El matcheo contra las tablas va SIEMPRE por nombre calificado: con dos tablas
        // homónimas en esquemas distintos, el nombre corto no alcanza para saber a cuál
        // apunta la FK, y resolverlo mal borra en la tabla equivocada.
        // El nombre corto se conserva sólo para los mensajes y comentarios del script.
        public string OrigenCompleto =>
            string.IsNullOrEmpty(SchemaOrigen) ? TablaOrigen : $"{SchemaOrigen}.{TablaOrigen}";

        public string DestinoCompleto =>
            string.IsNullOrEmpty(SchemaDestino) ? TablaDestino : $"{SchemaDestino}.{TablaDestino}";

        /// <summary>
        /// Clave para agrupar las columnas de una misma FK compuesta. Lleva el esquema de la
        /// hija porque los nombres de constraint son únicos por esquema, no por base: agrupar
        /// sólo por NombreFK mezcla dos FKs homónimas de esquemas distintos en una sola.
        /// </summary>
        public string ClaveFK =>
            $"{OrigenCompleto}|{(string.IsNullOrEmpty(NombreFK) ? $"{TablaOrigen}→{TablaDestino}" : NombreFK)}";
    }

    /// <summary>
    /// Una FK con filas colgando: la hija apunta a un padre que ya no existe.
    /// Es la unidad del informe de "relaciones truncadas" — una fila por constraint,
    /// no por tabla, porque lo que hay que revisar antes de borrar es la relación.
    /// </summary>
    public class RelacionTruncada
    {
        public string TablaHija     { get; set; }
        public string TablaPadre    { get; set; }
        public string NombreFK      { get; set; }
        /// <summary>"IdSector → Id" (o varias, separadas por coma, si la FK es compuesta).</summary>
        public string Columnas      { get; set; }
        public int    FilasRotas    { get; set; }
        /// <summary>Valores huérfanos de ejemplo, para poder mirarlos antes de borrar.</summary>
        public string Ejemplos      { get; set; }
        /// <summary>
        /// El padre está fuera del esquema en alcance. La relación rota es real y se informa,
        /// pero NO entra al barrido: el límite de esquema es duro y nada de afuera se toca.
        /// </summary>
        public bool   FueraDeAlcance { get; set; }
        /// <summary>Mensaje del motor si el conteo no se pudo evaluar.</summary>
        public string Error          { get; set; }

        public string Alcance => Error != null ? "Error"
                               : FueraDeAlcance ? "Fuera de alcance (no se borra)"
                               : "Se depura";
    }

    /// <summary>
    /// Filas dadas de baja que el modo BorradoSeguro NO va a eliminar porque algo las referencia.
    /// Una fila del informe por relación, no por tabla: lo que hay que decidir es qué hacer con
    /// cada vínculo, y el mismo padre puede quedar retenido por varias hijas a la vez.
    ///
    /// La distinción entre los dos motivos es la razón de ser del informe, porque determina qué se
    /// puede hacer al respecto:
    ///
    ///  • <see cref="CadenaIncompleta"/> — lo que retiene también está dado de baja, pero su tabla
    ///    no entró al alcance, así que nadie la borra y el padre queda trabado. Se resuelve solo
    ///    incluyendo esa tabla: como la captura va de hijos a padres, una corrida más cierra la
    ///    cadena entera. Es seguro, porque cada tabla sigue borrando únicamente sus propias bajas.
    ///
    ///  • Datos vivos — lo que retiene está activo. Acá no hay nada técnico que hacer: o la baja
    ///    del padre está mal puesta, o hay que dar de baja el vínculo desde la aplicación. Forzar
    ///    el borrado sería volver al arrastre en cascada, que es lo que rompió la base.
    /// </summary>
    public class RetencionLimpiador
    {
        /// <summary>La tabla cuyas filas en baja no se pueden borrar.</summary>
        public string TablaRetenida   { get; set; }
        /// <summary>La tabla que las referencia y por eso las retiene.</summary>
        public string TablaQueRetiene { get; set; }
        /// <summary>"IdPregunta → IdPregunta" (o varias, si la FK es compuesta).</summary>
        public string Columnas        { get; set; }
        /// <summary>Filas en baja de <see cref="TablaRetenida"/> que esta relación deja sin borrar.</summary>
        public int    FilasRetenidas  { get; set; }
        /// <summary>Lo que retiene también está en baja, pero su tabla quedó fuera del alcance.</summary>
        public bool   CadenaIncompleta { get; set; }
        /// <summary>
        /// Lo que retiene está en baja y su tabla SÍ está en el alcance, pero igual no se va a
        /// borrar porque ella misma quedó retenida. La traba se propaga hacia arriba: hasta que no
        /// se libere la hija, el padre no se puede tocar. Incluir tablas acá no sirve de nada.
        /// </summary>
        public bool   RetenidaEnCadena { get; set; }
        /// <summary>
        /// La tabla que retiene tiene campos de baja reconocibles, así que tildarla e incluirla
        /// alcanza. Sin esto el botón de incluir no puede hacer nada útil con ella.
        /// </summary>
        public bool   PuedeIncluirse  { get; set; }
        /// <summary>Consulta que devuelve las filas que están reteniendo, para poder mirarlas.</summary>
        public string SelectSql       { get; set; }
        /// <summary>Mensaje del motor si el conteo no se pudo evaluar.</summary>
        public string Error           { get; set; }

        public string Motivo => Error != null     ? "Error"
                              : CadenaIncompleta  ? "Cadena incompleta"
                              : RetenidaEnCadena  ? "Retenida en cadena"
                                                  : "Datos vivos";

        // El mensaje del motor va a la vista, no sólo al objeto: guardarlo y no mostrarlo dejaba una
        // grilla llena de "Error" sin una sola pista de qué había fallado.
        public string QueHacer => Error != null     ? Error
                                : CadenaIncompleta  ? (PuedeIncluirse
                                        ? "Incluir la tabla que retiene y volver a correr"
                                        : "Incluirla a mano: no se le detectan campos de baja")
                                : RetenidaEnCadena  ? $"Resolvé primero la retención de {TablaQueRetiene}"
                                : "Decisión de negocio: revisar la baja o dar de baja el vínculo";

        /// <summary>
        /// Si hay un botón que resuelva esto. Sólo la cadena incompleta lo tiene: las otras dos
        /// piden una decisión, y pintarlas como accionables haría creer que se arreglan solas.
        /// </summary>
        public bool EsAccionable => Error == null && CadenaIncompleta && PuedeIncluirse;
    }

    /// <summary>
    /// Una FK que quedó violada después de una corrida en modo BorradoIterativo. No es un error:
    /// es la contrapartida esperada del criterio de desconexión, que sólo borra la fila que no
    /// conecta con NADA. Una fila rota por un dato pero sana por otro sobrevive, y la relación
    /// rota queda.
    ///
    /// Se informa porque la constraint queda "untrusted": el motor la vuelve a exigir para lo que
    /// venga, pero no revalidó lo que ya estaba, y mientras siga así el optimizador la ignora al
    /// armar los planes. Enterarse recién meses después, por una consulta lenta, es peor que
    /// verlo acá antes de confirmar.
    /// </summary>
    public class FKVioladaLimpiador
    {
        public string NombreFK      { get; set; }
        public string TablaHija     { get; set; }
        public string TablaPadre    { get; set; }
        /// <summary>"IdPregunta → Id" (o varias, separadas por coma, si la FK es compuesta).</summary>
        public string Columnas      { get; set; }
        public int    FilasViolando { get; set; }
        /// <summary>Mensaje del motor si el conteo no se pudo evaluar.</summary>
        public string Error         { get; set; }

        public string Detalle => $"{TablaHija} → {TablaPadre} ({Columnas})";
    }

    /// <summary>Opciones del barrido. Van juntas porque se leen juntas desde la UI.</summary>
    public class OpcionesBarrido
    {
        public bool DepurarHuerfanos { get; set; }
        /// <summary>
        /// Trata 0 y cadena vacía como "sin referencia" en vez de "referencia rota". Las bases
        /// derivadas de Magic usan centinelas en lugar de NULL: sin esto el barrido se lleva
        /// filas perfectamente válidas.
        /// </summary>
        public bool CentinelasComoSinReferencia { get; set; } = true;
        /// <summary>
        /// Aborta el script si el borrado se lleva más del 90% de alguna tabla. Apagado por defecto:
        /// el umbral es porcentual, así que en una tabla de dos filas lo alcanza cualquier limpieza
        /// normal y frena de gusto. Vale la pena tildarlo cuando la base es grande y la condición de
        /// baja todavía no está probada, que es el escenario donde una tabla entera se puede ir.
        /// </summary>
        public bool FrenoSeguridad { get; set; }
    }

    /// <summary>Metadatos de una columna que el barrido y el reordenamiento necesitan del catálogo.</summary>
    public class ColumnaInfoLimpiador
    {
        public string Nombre     { get; set; }
        public string Tipo       { get; set; }
        public bool   EsIdentity { get; set; }
        /// <summary>Calculada o rowversion: no se puede insertar, bloquea el camino IDENTITY_INSERT.</summary>
        public bool   NoInsertable { get; set; }

        private static readonly HashSet<string> Numericos = new HashSet<string>(
            new[] { "int", "bigint", "smallint", "tinyint", "decimal", "numeric", "money",
                    "smallmoney", "float", "real", "double", "double precision", "integer",
                    "serial", "bigserial", "smallserial", "int4", "int8", "int2", "bit" },
            System.StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> Textos = new HashSet<string>(
            new[] { "char", "varchar", "nchar", "nvarchar", "text", "ntext", "character",
                    "character varying", "clob", "string" },
            System.StringComparer.OrdinalIgnoreCase);

        private string TipoBase => (Tipo ?? "").Split('(')[0].Trim();

        public bool EsNumerico => Numericos.Contains(TipoBase);
        public bool EsTexto    => Textos.Contains(TipoBase);

        /// <summary>Tipo al que castear ROW_NUMBER() para no perder el tipo original de la PK.</summary>
        public bool EsEnteroSimple =>
            new[] { "int", "bigint", "smallint", "tinyint", "integer", "int4", "int8", "int2" }
            .Contains(TipoBase, System.StringComparer.OrdinalIgnoreCase);
    }

    public class TablaAnalisisLimpiador
    {
        public string NombreCompleto  { get; set; }
        public int    RegistrosBaja   { get; set; }
        public int    RegistrosActivos { get; set; }
        public bool   TieneConflictos { get; set; }
        // Filas de esta tabla con al menos una FK rota. No es la suma de RelacionTruncada.FilasRotas:
        // una misma fila puede estar colgada de dos FKs distintas y ahí se contaría dos veces.
        public int    Huerfanos       { get; set; }
        public List<string> Conflictos { get; set; } = new List<string>();
        // Modo BorradoEnCascada: filas que se eliminan por arrastre. Es un piso, no el total —
        // se cuenta un solo nivel de FK, y una tabla puede quedar alcanzada también por un
        // padre que a su vez se elimina en cascada.
        public int    CascadaEstimada { get; set; }
        // Modo BorradoEnCascada: filas en baja que NO se eliminan porque las referencia una
        // tabla de otro esquema, fuera del límite. Se retienen a propósito: el esquema
        // elegido es un límite duro y nada de afuera se toca.
        public int    RetenidasPorExterno { get; set; }
        // "OK", "Conflicto", "Cascada", "Se omite", "Sin campo",
        // "Baja", "Cascada (fuera de selección)"
        public string Estado { get; set; }
    }

    public class AnalisisResultLimpiador
    {
        public List<TablaAnalisisLimpiador> Tablas      { get; set; } = new List<TablaAnalisisLimpiador>();
        public List<string>                 Advertencias { get; set; } = new List<string>();
        public List<RelacionTruncada>       Truncadas   { get; set; } = new List<RelacionTruncada>();
        /// <summary>Sólo se llena en modo BorradoSeguro: qué bajas quedan sin borrar y por qué.</summary>
        public List<RetencionLimpiador>     Retenciones { get; set; } = new List<RetencionLimpiador>();
        public bool HayConflictosBloquantes { get; set; }
    }

    // ── Helpers SQL para condiciones ──────────────────────────────────────

    public static class CondicionBajaHelper
    {
        /// <summary>
        /// Expresión SQL de la condición de baja. Con más de una condición el resultado va
        /// SIEMPRE entre paréntesis: si no, un grupo unido con OR se rompe al concatenarlo
        /// con AND (por ejemplo dentro del EXISTS de la cascada), porque AND liga más fuerte
        /// y el término del OR queda suelto y sin correlacionar.
        /// </summary>
        public static string ToCondicionSql(List<CondicionBaja> conds, System.Func<string, string> quoteCampo)
        {
            if (conds == null || conds.Count == 0) return "1=1";
            var sb = new StringBuilder();
            for (int i = 0; i < conds.Count; i++)
            {
                sb.Append(conds[i].ToExprSql(quoteCampo(conds[i].Campo)));
                if (i < conds.Count - 1)
                    sb.Append($" {conds[i].Combinador} ");
            }
            return conds.Count > 1 ? $"({sb})" : sb.ToString();
        }

        public static string ToNegacionSql(List<CondicionBaja> conds, System.Func<string, string> quoteCampo)
            => $"NOT ({ToCondicionSql(conds, quoteCampo)})";
    }
}
