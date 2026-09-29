using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.Payments
{
    public class PaymentCheckoutResult
    {
        public PaymentProviderType ProviderType { get; set; }
        public string RedirectUrl { get; set; } = string.Empty;
        public string? ProviderCheckoutId { get; set; }
        public string? ProviderReference { get; set; }
        public string? ProviderOrderNumber { get; set; }
        public string? CorrelationId { get; set; }
        public string? SuccessUrl { get; set; }
        public string? CancelUrl { get; set; }
        public string? WebhookUrl { get; set; }
        public string? RawResponse { get; set; }
    }
}
