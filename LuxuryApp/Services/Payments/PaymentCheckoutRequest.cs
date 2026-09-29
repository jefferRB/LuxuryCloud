using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.Payments
{
    public class PaymentCheckoutRequest
    {
        public Guid TenantId { get; set; }
        public Guid PlanId { get; set; }
        public PaymentProviderType ProviderType { get; set; }
        public string Reference { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "CRC";
        public string Description { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public string CustomerEmail { get; set; } = string.Empty;
        public string SuccessUrl { get; set; } = string.Empty;
        public string CancelUrl { get; set; } = string.Empty;
        public string WebhookUrl { get; set; } = string.Empty;
    }
}
