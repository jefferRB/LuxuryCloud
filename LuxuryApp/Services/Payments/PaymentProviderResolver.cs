using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.Payments
{
    public class PaymentProviderResolver
    {
        private readonly IReadOnlyDictionary<PaymentProviderType, IPaymentProvider> _providers;

        public PaymentProviderResolver(IEnumerable<IPaymentProvider> providers)
        {
            _providers = providers.ToDictionary(p => p.ProviderType);
        }

        public IPaymentProvider Get(PaymentProviderType providerType)
        {
            if (_providers.TryGetValue(providerType, out var provider))
            {
                return provider;
            }

            throw new InvalidOperationException($"No hay proveedor de pago registrado para '{providerType}'.");
        }
    }
}
