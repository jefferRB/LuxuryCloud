namespace LuxuryApp.Services.Payments
{
    public sealed class RecurringPaymentApprovalRequest
    {
        public Guid PaymentId { get; init; }
        public string ProviderTransactionId { get; init; } = string.Empty;
        public decimal ApprovedAmount { get; init; }
        public string Currency { get; init; } = "CRC";
        public string? ProviderSubscriberId { get; init; }
        public string? ProviderReference { get; init; }
        public string? ProviderAuthorizationCode { get; init; }
        public DateTime? NextBillingDateUtc { get; init; }
        public string Source { get; init; } = "manual";
        public string? Observation { get; init; }
        public string? CorrelationId { get; init; }
        public string? ActorUserId { get; init; }
        public string? ActorEmail { get; init; }
        public string? RawPayload { get; init; }
        public string? EventType { get; init; }
        public string? ProviderResultCode { get; init; }
        public string? ProviderResultMessage { get; init; }
        public bool CreateAuditEvent { get; init; } = true;
    }
}
