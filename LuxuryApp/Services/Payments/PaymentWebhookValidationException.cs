namespace LuxuryApp.Services.Payments
{
    public class PaymentWebhookValidationException : InvalidOperationException
    {
        public PaymentWebhookValidationException(string message)
            : base(message)
        {
        }
    }
}
