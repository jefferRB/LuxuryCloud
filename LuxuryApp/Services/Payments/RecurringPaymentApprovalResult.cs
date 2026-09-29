using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.Payments
{
    public sealed class RecurringPaymentApprovalResult
    {
        public Guid PaymentId { get; init; }
        public Guid TenantId { get; init; }
        public Guid PlanId { get; init; }
        public string PlanCode { get; init; } = string.Empty;
        public bool IsAddon { get; init; }
        public EstadoPagoProveedor PaymentStatus { get; init; }
        public EstadoSuscripcion SubscriptionStatus { get; init; }
        public DateTime? CurrentPeriodEndUtc { get; init; }
        public DateTime? NextBillingDateUtc { get; init; }
        public string ProviderTransactionId { get; init; } = string.Empty;
        public string? ProviderSubscriberId { get; init; }
    }
}
