using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.Payments
{
    public class PaymentWebhookProcessingResult
    {
        public string EventId { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
        public bool IsDuplicate { get; set; }
        public bool IsProcessed { get; set; }
        public string Message { get; set; } = string.Empty;
        public EstadoPagoProveedor? EstadoPago { get; set; }
    }
}
