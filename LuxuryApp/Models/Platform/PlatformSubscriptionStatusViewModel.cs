using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Models.Platform
{
    public sealed class PlatformSubscriptionStatusViewModel
    {
        public string TenantName { get; init; } = string.Empty;
        public string PlanName { get; init; } = string.Empty;
        public string? PlanCode { get; init; }
        public EstadoSuscripcion Status { get; init; }
        public DateTime? CurrentPeriodEndUtc { get; init; }
        public DateTime? NextBillingDateUtc { get; init; }
        public int? MaxFuncionarios { get; init; }
        public int? MonthlyMessageLimit { get; init; }
        public string? ProviderSubscriberId { get; init; }
        public string? ProviderTransactionId { get; init; }
    }
}
