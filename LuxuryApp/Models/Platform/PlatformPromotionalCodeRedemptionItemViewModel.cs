namespace LuxuryApp.Models.Platform
{
    public sealed class PlatformPromotionalCodeRedemptionItemViewModel
    {
        public string TenantName { get; init; } = string.Empty;
        public string EmailConsumidor { get; init; } = string.Empty;
        public DateTime FechaConsumoUtc { get; init; }
        public DateTime? AccessEndsUtc { get; init; }
    }
}
