namespace LuxuryApp.Models.Legal
{
    public sealed class ContractAcceptanceStatus
    {
        public ContractDocument? ActiveDocument { get; init; }
        public bool HasActiveDocument => ActiveDocument is not null;
        public bool HasAcceptedCurrentVersion { get; init; }
        public DateTime? AcceptedAtUtc { get; init; }
        public bool RequiresAcceptance => HasActiveDocument && !HasAcceptedCurrentVersion;
        public bool BlocksApplicationAccess => !HasActiveDocument || !HasAcceptedCurrentVersion;
    }
}
