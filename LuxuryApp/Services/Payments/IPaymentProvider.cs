using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.Payments
{
    public interface IPaymentProvider
    {
        PaymentProviderType ProviderType { get; }

        Task<PaymentCheckoutResult> CreateCheckoutAsync(
            PaymentCheckoutRequest request,
            CancellationToken cancellationToken = default);

        PaymentProviderWebhookData ParseWebhook(string payload);

        Task<PaymentVerificationResult> VerifyPaymentAsync(
            PaymentVerificationRequest request,
            CancellationToken cancellationToken = default);
    }
}
