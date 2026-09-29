using LuxuryApp.Models.Common;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.SaaS;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.PublicPages
{
    public enum TenantPublicPageMetricType
    {
        PageView = 1,
        ReserveClick = 2,
        ServiceReserveClick = 3,
        WhatsAppClick = 4,
        MapsClick = 5,
        SocialClick = 6
    }

    public sealed class TenantPublicPageDailyMetric : ITenantEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [BindNever]
        public Guid TenantId { get; set; }

        public Tenant? Tenant { get; set; }

        public DateOnly Date { get; set; }

        public TenantPublicPageMetricType MetricType { get; set; }

        public string Slug { get; set; } = string.Empty;

        public int? ServicioId { get; set; }

        public Servicio? Servicio { get; set; }

        public long Count { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
