namespace LuxuryApp.Models.Layout
{
    public sealed class NavigationMenuItemViewModel
    {
        public string Text { get; init; } = string.Empty;
        public string Controller { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string Icon { get; init; } = string.Empty;
        public bool Highlight { get; init; }
    }
}
