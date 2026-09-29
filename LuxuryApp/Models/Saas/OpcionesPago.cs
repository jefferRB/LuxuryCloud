namespace LuxuryApp.Models.SaaS
{
    public class OpcionesPago
    {
        public PaymentProviderType ProveedorPredeterminado { get; set; } = PaymentProviderType.Tilopay;
        public string PublicBaseUrl { get; set; } = string.Empty;
        public bool EnableValidationPlans { get; set; }
        public bool ValidatePublicCallbackReachability { get; set; }
    }
}
