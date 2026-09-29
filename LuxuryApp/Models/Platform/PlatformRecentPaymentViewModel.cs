using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Models.Platform
{
    public sealed class PlatformRecentPaymentViewModel
    {
        public string TenantName { get; init; } = string.Empty;
        public string PlanName { get; init; } = string.Empty;
        public decimal Amount { get; init; }
        public string Currency { get; init; } = string.Empty;
        public EstadoPagoProveedor Status { get; init; }
        public DateTime CreatedUtc { get; init; }
    }
}
