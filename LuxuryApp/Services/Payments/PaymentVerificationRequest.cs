using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.Payments
{
    public class PaymentVerificationRequest
    {
        public PaymentProviderType ProviderType { get; set; }
        public string Reference { get; set; } = string.Empty;
        public string? ProviderOrderNumber { get; set; }
        public string? MerchantId { get; set; }
    }
}
