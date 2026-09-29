using LuxuryApp.Models.PublicPages;

namespace LuxuryApp.Services.PublicPages
{
    public interface ITenantPublicPageAnalyticsService
    {
        Task TryTrackCurrentTenantAsync(
            string slug,
            TenantPublicPageMetricType metricType,
            int? servicioId = null,
            CancellationToken cancellationToken = default);

        Task TryTrackAsync(
            Guid tenantId,
            string slug,
            TenantPublicPageMetricType metricType,
            int? servicioId = null,
            CancellationToken cancellationToken = default);

        Task<PublicPageAnalyticsSummaryViewModel> GetLast30DaysForCurrentTenantAsync(
            CancellationToken cancellationToken = default);
    }
}
