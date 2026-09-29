using LuxuryApp.Models.WhatsApp;

namespace LuxuryApp.Services.WhatsApp
{
    public interface ITenantWhatsAppSettingsService
    {
        Task<TenantWhatsAppSettingsSnapshot> GetSettingsForTenantAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default);

        Task<TenantWhatsAppSettingsSnapshot> EnsureDefaultSettingsAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default);

        Task<bool> IsWhatsAppEnabledForTenantAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default);

        Task<TenantWhatsAppSendDecision> CanSendNotificationAsync(
            Guid tenantId,
            string notificationType,
            long? reservedMessageLogId = null,
            CancellationToken cancellationToken = default);

        Task<int> GetTodayUsageAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default);

        Task<bool> HasActiveWhatsAppAddonAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default);

        Task UpdateSettingsAsync(
            Guid tenantId,
            TenantWhatsAppSettingsUpdateDto dto,
            string? updatedByUserId,
            CancellationToken cancellationToken = default);
    }
}
