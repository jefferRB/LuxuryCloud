namespace LuxuryApp.Models.Platform
{
    public sealed class PlatformBillingEventViewModel
    {
        public string TenantName { get; init; } = string.Empty;
        public string? PlanName { get; init; }
        public string EventType { get; init; } = string.Empty;
        public string ProcessingStatus { get; init; } = string.Empty;
        public DateTime ReceivedUtc { get; init; }
        public decimal? Amount { get; init; }
        public string? Currency { get; init; }
        public string? CorrelationId { get; init; }
        public int? TilopayRecurringPlanId { get; init; }
        public string? ProviderTransactionId { get; init; }
        public string? ProviderSubscriberId { get; init; }
        public string? Error { get; init; }
    }
}
