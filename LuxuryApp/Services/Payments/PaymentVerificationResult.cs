using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.Payments
{
    public class PaymentVerificationResult
    {
        public PaymentProviderType ProviderType { get; set; }
        public bool Exists { get; set; }
        public bool IsSuccess { get; set; }
        public string Reference { get; set; } = string.Empty;
        public string? ProviderOrderNumber { get; set; }
        public string StatusCode { get; set; } = string.Empty;
        public string StatusDescription { get; set; } = string.Empty;
        public string? ProviderTransactionId { get; set; }
        public string? ProviderCheckoutId { get; set; }
        public string? AuthorizationCode { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = string.Empty;
        public DateTime? ProviderProcessedAtUtc { get; set; }
        public string RawResponse { get; set; } = string.Empty;
    }
}
