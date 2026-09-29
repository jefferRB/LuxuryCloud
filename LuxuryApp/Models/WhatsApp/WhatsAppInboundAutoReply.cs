using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.WhatsApp
{
    /// <summary>
    /// Un mensaje entrante al numero central de LuxuryCloud y el resultado de su respuesta
    /// automatica neutral.
    ///
    /// <para>
    /// <b>Por que no vive en <c>WhatsAppMessageLogs</c>:</b> esa tabla es <c>ITenantEntity</c>, con
    /// filtro global y BLOCK PREDICATE de RLS en INSERT/UPDATE, asi que escribir una fila exige un
    /// tenant resuelto. La respuesta automatica es deliberadamente lo contrario: no averigua a que
    /// negocio pertenece quien escribe, no consulta datos de ningun tenant y no revela nada. Por eso
    /// esta bitacora es cross-tenant (NO implementa <c>ITenantEntity</c>) y queda fuera del RLS,
    /// igual que <c>PlatformAuditLog</c> o <c>PlatformWorkerHeartbeats</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Para que sirve:</b> es la clave de idempotencia. Meta reenvia webhooks; el id del mensaje
    /// entrante es unico, asi que insertar la fila ANTES de responder convierte el reenvio en un
    /// choque de indice unico y nadie recibe dos veces la misma respuesta.
    /// </para>
    /// </summary>
    public sealed class WhatsAppInboundAutoReply
    {
        public long Id { get; set; }

        /// <summary>Id del mensaje entrante (<c>messages[].id</c>). Clave de idempotencia.</summary>
        [Required]
        [MaxLength(128)]
        public string InboundMessageId { get; set; } = string.Empty;

        /// <summary>Numero de quien escribio, normalizado. Necesario para responderle y para soporte.</summary>
        [MaxLength(32)]
        public string? SenderPhoneE164 { get; set; }

        /// <summary>Tipo de mensaje reportado por Meta (text, image, button…). No se guarda el contenido.</summary>
        [MaxLength(40)]
        public string? MessageType { get; set; }

        [MaxLength(30)]
        public string Status { get; set; } = WhatsAppMessageStatuses.Pending;

        [MaxLength(128)]
        public string? ReplyMetaMessageId { get; set; }

        [MaxLength(80)]
        public string? ErrorCode { get; set; }

        [MaxLength(1000)]
        public string? ErrorMessage { get; set; }

        public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime? ProcessedAtUtc { get; set; }
    }
}
