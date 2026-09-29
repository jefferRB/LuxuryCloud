using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Common;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.WhatsApp
{
    public class WhatsAppMessageLog : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        public long Id { get; set; }

        public int? CitaId { get; set; }

        public Cita? Cita { get; set; }

        [MaxLength(20)]
        public string Direction { get; set; } = WhatsAppMessageDirections.Outbound;

        [MaxLength(40)]
        public string NotificationType { get; set; } = WhatsAppNotificationTypes.Confirmation;

        [MaxLength(40)]
        public string Provider { get; set; } = WhatsAppProviders.Meta;

        [MaxLength(128)]
        public string? MetaMessageId { get; set; }

        [MaxLength(128)]
        public string? ContextMessageId { get; set; }

        [MaxLength(32)]
        public string? RecipientPhoneE164 { get; set; }

        [MaxLength(32)]
        public string? SenderPhoneE164 { get; set; }

        [MaxLength(64)]
        public string? WaId { get; set; }

        [MaxLength(128)]
        public string? TemplateName { get; set; }

        public string? PayloadJson { get; set; }

        [MaxLength(30)]
        public string Status { get; set; } = WhatsAppMessageStatuses.Pending;

        [MaxLength(80)]
        public string? ErrorCode { get; set; }

        [MaxLength(1000)]
        public string? ErrorMessage { get; set; }

        public int AttemptCount { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime? SentAtUtc { get; set; }

        public DateTime? DeliveredAtUtc { get; set; }

        public DateTime? ReadAtUtc { get; set; }

        public DateTime? FailedAtUtc { get; set; }

        public DateTime? ProcessedAtUtc { get; set; }

        public DateTime? ProcessingStartedAtUtc { get; set; }

        public DateTime? LastAttemptAtUtc { get; set; }

        public DateTime? NextAttemptAtUtc { get; set; }
    }
}
