using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Models.Platform
{
    public sealed class PlatformPromotionalCodesPageViewModel
    {
        public IReadOnlyCollection<Plan> AvailablePlans { get; init; } = Array.Empty<Plan>();
        public PlatformPromotionalCodeCreateViewModel CreateForm { get; init; } = new();
        public IReadOnlyCollection<PlatformPromotionalCodeListItemViewModel> Codes { get; init; } = Array.Empty<PlatformPromotionalCodeListItemViewModel>();
    }
}
