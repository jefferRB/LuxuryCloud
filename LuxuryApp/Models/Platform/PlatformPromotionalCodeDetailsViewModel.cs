namespace LuxuryApp.Models.Platform
{
    public sealed class PlatformPromotionalCodeDetailsViewModel
    {
        public PlatformPromotionalCodeListItemViewModel Code { get; init; } = new();
        public string? NotasInternas { get; init; }
        public IReadOnlyCollection<PlatformPromotionalCodeRedemptionItemViewModel> Redemptions { get; init; } = Array.Empty<PlatformPromotionalCodeRedemptionItemViewModel>();
    }
}
