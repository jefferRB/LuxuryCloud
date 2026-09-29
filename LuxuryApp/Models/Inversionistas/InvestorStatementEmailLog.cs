using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Inversionistas
{
    /// <summary>
    /// Bitácora de envíos del estado de cuenta. Mismo patrón que
    /// <c>TenantMonthlyReportEmailLog</c>: un índice único filtrado garantiza que un envío real
    /// exitoso no se repita para el mismo estado y destinatario (las pruebas sí pueden repetirse).
    /// </summary>
    public class InvestorStatementEmailLog : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        public int StatementId { get; set; }

        public InvestorStatement? Statement { get; set; }

        [MaxLength(256)]
        public string RecipientEmail { get; set; } = string.Empty;

        [MaxLength(300)]
        public string Subject { get; set; } = string.Empty;

        public InvestorStatementEmailStatus Status { get; set; } = InvestorStatementEmailStatus.Pending;

        public bool IsTest { get; set; }

        /// <summary>Número de reenvío manual (0 = envío original). Permite reenviar sin romper la idempotencia.</summary>
        public int ResendSequence { get; set; }

        [MaxLength(450)]
        public string? TriggeredByUserId { get; set; }

        [MaxLength(200)]
        public string? ProviderMessageId { get; set; }

        [MaxLength(500)]
        public string? ErrorMessage { get; set; }

        /// <summary>Hash del snapshot enviado: prueba de que el correo salió del estado congelado.</summary>
        [MaxLength(64)]
        public string? ContentHash { get; set; }

        public DateTime? SentAtUtc { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
