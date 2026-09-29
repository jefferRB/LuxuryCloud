using Microsoft.Extensions.Caching.Memory;

namespace LuxuryApp.Services.SaaS
{
    public interface ITenantCommercialAccessCache
    {
        string BuildTenantKey(Guid tenantId);
        void Invalidate(Guid tenantId);
    }

    public sealed class TenantCommercialAccessCache : ITenantCommercialAccessCache
    {
        private readonly IMemoryCache _cache;

        public TenantCommercialAccessCache(IMemoryCache cache)
        {
            _cache = cache;
        }

        public string BuildTenantKey(Guid tenantId) => $"tenant_commercial_access_{tenantId}";

        public void Invalidate(Guid tenantId)
        {
            _cache.Remove(BuildTenantKey(tenantId));
        }
    }
}
