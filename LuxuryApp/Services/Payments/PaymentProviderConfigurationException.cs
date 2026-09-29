namespace LuxuryApp.Services.Payments
{
    public class PaymentProviderConfigurationException : InvalidOperationException
    {
        public PaymentProviderConfigurationException(string message)
            : base(message)
        {
        }
    }
}
