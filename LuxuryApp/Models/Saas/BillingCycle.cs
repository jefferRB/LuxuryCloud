namespace LuxuryApp.Models.SaaS
{
    /// <summary>
    /// Ciclo de facturacion de un plan de suscripcion. Default Monthly para preservar
    /// el comportamiento de los planes legacy (BASIC/PRO/BUSINESS) que no declaran ciclo.
    /// </summary>
    public enum BillingCycle
    {
        Monthly = 0,
        Annual = 1
    }
}
