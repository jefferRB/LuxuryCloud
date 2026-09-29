namespace LuxuryApp.Models.Layout
{
    public sealed class PrivateNavigationViewModel
    {
        public bool IsAuthenticated { get; init; }
        public bool CanAccessCommercialModules { get; init; }
        public string AccountDisplayName { get; init; } = string.Empty;
        public string HomeController { get; init; } = "Home";
        public string HomeAction { get; init; } = "Index";
        public string AccessBadgeText { get; init; } = string.Empty;
        public string AccessBadgeTone { get; init; } = "muted";
        public IReadOnlyCollection<NavigationMenuItemViewModel> PrimaryItems { get; init; } = Array.Empty<NavigationMenuItemViewModel>();
        public IReadOnlyCollection<NavigationMenuItemViewModel> SecondaryItems { get; init; } = Array.Empty<NavigationMenuItemViewModel>();
    }
}
