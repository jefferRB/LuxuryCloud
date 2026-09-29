namespace LuxuryApp.Models.Legal
{
    public sealed class ContractPageViewModel
    {
        public bool HasActiveDocument { get; init; }
        public Guid ContractDocumentId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string VersionNumber { get; init; } = string.Empty;
        public DateTime? EffectiveFromUtc { get; init; }
        public string ContentHtml { get; init; } = string.Empty;
    }
}
