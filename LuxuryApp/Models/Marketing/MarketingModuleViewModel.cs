namespace LuxuryApp.Models.Marketing
{
    public sealed class MarketingModuleViewModel
    {
        public string Id { get; init; } = string.Empty;
        public string Eyebrow { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public string Solution { get; init; } = string.Empty;
        public string Icon { get; init; } = string.Empty;
        public string MockupTitle { get; init; } = string.Empty;
        public string PrimaryMetric { get; init; } = string.Empty;
        public string PrimaryLabel { get; init; } = string.Empty;
        public string SecondaryMetric { get; init; } = string.Empty;
        public string SecondaryLabel { get; init; } = string.Empty;
        public string TertiaryMetric { get; init; } = string.Empty;
        public string TertiaryLabel { get; init; } = string.Empty;
        public IReadOnlyCollection<string> Tags { get; init; } = Array.Empty<string>();
        public bool ReverseLayout { get; init; }
    }
}
