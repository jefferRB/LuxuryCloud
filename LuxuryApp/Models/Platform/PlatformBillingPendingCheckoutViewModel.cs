using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Models.Platform
{
    public sealed class PlatformBillingPendingCheckoutViewModel
    {
        public Guid PaymentId { get; init; }
        public string TenantName { get; init; } = string.Empty;
        public string? OwnerEmail { get; init; }
        public string PlanName { get; init; } = string.Empty;
        public string? PlanCode { get; init; }
        public string CheckoutKind { get; init; } = string.Empty;
        public decimal Amount { get; init; }
        public string Currency { get; init; } = string.Empty;
        public EstadoPagoProveedor Status { get; init; }
        public DateTime CreatedUtc { get; init; }
        public string? CorrelationToken { get; init; }
        public string? ProviderSubscriberId { get; init; }
        public string? ProviderTransactionId { get; init; }
        public string? ProviderResultMessage { get; init; }
    }
}
