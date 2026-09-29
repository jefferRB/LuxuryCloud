using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Models.Platform
{
    public sealed class PlatformRecurringReconciliationPageViewModel
    {
        public IReadOnlyCollection<PlatformRecurringReconciliationItemViewModel> Items { get; init; } = Array.Empty<PlatformRecurringReconciliationItemViewModel>();
        public PlatformRecurringReconciliationItemViewModel? SelectedItem { get; init; }
        public PlatformRecurringApprovalFormViewModel ApprovalForm { get; init; } = new();
    }

    public sealed class PlatformRecurringReconciliationItemViewModel
    {
        public Guid PaymentId { get; init; }
        public Guid TenantId { get; init; }
        public string TenantName { get; init; } = string.Empty;
        public string? UserId { get; init; }
        public string? UserEmail { get; init; }
        public string PlanName { get; init; } = string.Empty;
        public string PlanCode { get; init; } = string.Empty;
        public int? TilopayRecurringPlanId { get; init; }
        public decimal ExpectedAmount { get; init; }
        public string Currency { get; init; } = "CRC";
        public string? CorrelationToken { get; init; }
        public DateTime CreatedUtc { get; init; }
        public EstadoPagoProveedor Status { get; init; }
        public string? ProviderResultMessage { get; init; }
        public string? ProviderTransactionId { get; init; }
        public string? ProviderSubscriberId { get; init; }
        public bool IsAddon { get; init; }
    }

    public sealed class PlatformRecurringApprovalFormViewModel
    {
        [Required]
        public Guid PaymentId { get; set; }

        [Required]
        [Display(Name = "TransactionId u orden Tilopay")]
        [StringLength(100)]
        public string ProviderTransactionId { get; set; } = string.Empty;

        [Display(Name = "SubscriberId Tilopay")]
        [StringLength(100)]
        public string? ProviderSubscriberId { get; set; }

        [Required]
        [Range(0.01d, 999999999.99d)]
        [Display(Name = "Monto aprobado")]
        public decimal ApprovedAmount { get; set; }

        [Required]
        [StringLength(10)]
        [Display(Name = "Moneda")]
        public string Currency { get; set; } = "CRC";

        [StringLength(250)]
        [Display(Name = "Observacion")]
        [Required]
        public string Observation { get; set; } = string.Empty;
    }
}
