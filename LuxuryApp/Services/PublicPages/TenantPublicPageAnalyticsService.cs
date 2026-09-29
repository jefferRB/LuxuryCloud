using LuxuryApp.Models.PublicPages;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Services.Tenant;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.PublicPages
{
    public sealed class TenantPublicPageAnalyticsService : ITenantPublicPageAnalyticsService
    {
        private const int SummaryDays = 30;

        private readonly ApplicationDbContext _context;
        private readonly ITenantProvider _tenantProvider;
        private readonly ILogger<TenantPublicPageAnalyticsService> _logger;

        public TenantPublicPageAnalyticsService(
            ApplicationDbContext context,
            ITenantProvider tenantProvider,
            ILogger<TenantPublicPageAnalyticsService> logger)
        {
            _context = context;
            _tenantProvider = tenantProvider;
            _logger = logger;
        }

        public async Task TryTrackCurrentTenantAsync(
            string slug,
            TenantPublicPageMetricType metricType,
            int? servicioId = null,
            CancellationToken cancellationToken = default)
        {
            if (!_tenantProvider.HasTenant())
            {
                return;
            }

            await TryTrackAsync(_tenantProvider.GetTenantId(), slug, metricType, servicioId, cancellationToken);
        }

        public async Task TryTrackAsync(
            Guid tenantId,
            string slug,
            TenantPublicPageMetricType metricType,
            int? servicioId = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await TrackAsync(tenantId, slug, metricType, servicioId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "No se pudo registrar la metrica publica {MetricType} para el slug {Slug}.",
                    metricType,
                    slug);
            }
        }

        public async Task<PublicPageAnalyticsSummaryViewModel> GetLast30DaysForCurrentTenantAsync(
            CancellationToken cancellationToken = default)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var start = today.AddDays(-(SummaryDays - 1));

            var metrics = await _context.TenantPublicPageDailyMetrics
                .AsNoTracking()
                .Where(metric => metric.Date >= start && metric.Date <= today)
                .ToListAsync(cancellationToken);

            var serviceClickMetrics = metrics
                .Where(metric =>
                    metric.MetricType == TenantPublicPageMetricType.ServiceReserveClick &&
                    metric.ServicioId.HasValue)
                .ToList();

            var serviceIds = serviceClickMetrics
                .Select(metric => metric.ServicioId!.Value)
                .Distinct()
                .ToArray();

            var serviceNames = serviceIds.Length == 0
                ? new Dictionary<int, string>()
                : await _context.Servicios
                    .AsNoTracking()
                    .Where(service => serviceIds.Contains(service.Id))
                    .ToDictionaryAsync(service => service.Id, service => service.Nombre, cancellationToken);

            var topServices = serviceClickMetrics
                .GroupBy(metric => metric.ServicioId!.Value)
                .Select(group => new PublicPageTopServiceMetricViewModel
                {
                    ServiceName = serviceNames.TryGetValue(group.Key, out var name)
                        ? name
                        : $"Servicio {group.Key}",
                    Clicks = group.Sum(metric => metric.Count)
                })
                .OrderByDescending(metric => metric.Clicks)
                .ThenBy(metric => metric.ServiceName)
                .Take(5)
                .ToList();

            return new PublicPageAnalyticsSummaryViewModel
            {
                Days = SummaryDays,
                PageViews = Sum(metrics, TenantPublicPageMetricType.PageView),
                ReserveClicks = Sum(metrics, TenantPublicPageMetricType.ReserveClick),
                ServiceReserveClicks = Sum(metrics, TenantPublicPageMetricType.ServiceReserveClick),
                WhatsAppClicks = Sum(metrics, TenantPublicPageMetricType.WhatsAppClick),
                MapsClicks = Sum(metrics, TenantPublicPageMetricType.MapsClick),
                TotalEvents = metrics.Sum(metric => metric.Count),
                TopServices = topServices
            };
        }

        private async Task TrackAsync(
            Guid tenantId,
            string slug,
            TenantPublicPageMetricType metricType,
            int? servicioId,
            CancellationToken cancellationToken)
        {
            var normalized = BookingSettingsService.NormalizeSlug(slug);
            if (tenantId == Guid.Empty || string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            var safeServicioId = servicioId.GetValueOrDefault() > 0 ? servicioId : null;
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var now = DateTime.UtcNow;

            var metric = await _context.TenantPublicPageDailyMetrics
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(item =>
                    item.TenantId == tenantId &&
                    item.Date == today &&
                    item.MetricType == metricType &&
                    item.Slug == normalized &&
                    item.ServicioId == safeServicioId,
                    cancellationToken);

            if (metric is null)
            {
                _context.TenantPublicPageDailyMetrics.Add(new TenantPublicPageDailyMetric
                {
                    TenantId = tenantId,
                    Date = today,
                    MetricType = metricType,
                    Slug = normalized,
                    ServicioId = safeServicioId,
                    Count = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            }
            else
            {
                metric.Count++;
                metric.UpdatedAtUtc = now;
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        private static long Sum(
            IReadOnlyList<TenantPublicPageDailyMetric> metrics,
            TenantPublicPageMetricType metricType) =>
            metrics
                .Where(metric => metric.MetricType == metricType)
                .Sum(metric => metric.Count);
    }
}
