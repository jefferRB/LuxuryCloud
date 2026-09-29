namespace LuxuryApp.Services.WhatsApp
{
    public interface ITenantWhatsAppFeatureService
    {
        Task<bool> IsWhatsAppEnabledForCurrentTenantAsync(CancellationToken cancellationToken = default);

        Task<bool> HasWhatsAppAddonAsync(CancellationToken cancellationToken = default);
    }
}
