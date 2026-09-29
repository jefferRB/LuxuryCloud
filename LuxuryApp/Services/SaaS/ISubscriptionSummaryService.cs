using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.SaaS
{
    /// <summary>
    /// Construye el resumen comercial/operativo de un tenant (plan, estado, funcionarios,
    /// addon WhatsApp y su consumo). Compartido por Billing (Suscripcion) y el modulo WhatsApp
    /// para evitar duplicar la logica de calculo de saldo y settings.
    /// </summary>
    public interface ISubscriptionSummaryService
    {
        Task<BillingSubscriptionSummaryViewModel?> BuildAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default);
    }
}
