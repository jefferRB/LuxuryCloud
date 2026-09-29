using System.Security.Claims;
using LuxuryApp.Services.Identity;

namespace LuxuryApp.Services.Tenant
{
    public class TenantProvider : ITenantProvider
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ITenantExecutionContextAccessor _tenantExecutionContextAccessor;

        public TenantProvider(
            IHttpContextAccessor httpContextAccessor,
            ITenantExecutionContextAccessor tenantExecutionContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
            _tenantExecutionContextAccessor = tenantExecutionContextAccessor;
        }

        public Guid GetTenantId()
        {
            if (TryResolveTenantId(out var tenantId))
                return tenantId;

            throw new Exception("TenantId no encontrado en el contexto actual.");
        }

        public bool HasTenant() => TryResolveTenantId(out _);

        private bool TryResolveTenantId(out Guid tenantId)
        {
            if (_tenantExecutionContextAccessor.CurrentTenantId.HasValue)
            {
                tenantId = _tenantExecutionContextAccessor.CurrentTenantId.Value;
                return tenantId != Guid.Empty;
            }

            var httpContext = _httpContextAccessor.HttpContext;

            if (httpContext == null)
            {
                tenantId = Guid.Empty;
                return false;
            }

            if (httpContext.Items.TryGetValue("__resolved_tenant_id", out var cachedTenantId)
                && cachedTenantId is Guid parsedTenantId
                && parsedTenantId != Guid.Empty)
            {
                tenantId = parsedTenantId;
                return true;
            }

            var user = httpContext.User;

            if (user?.Identity == null || !user.Identity.IsAuthenticated)
            {
                tenantId = Guid.Empty;
                return false;
            }

            var tenantClaim = user.FindFirst(CustomClaimTypes.TenantId);

            if (tenantClaim == null)
            {
                tenantId = Guid.Empty;
                return false;
            }

            if (!Guid.TryParse(tenantClaim.Value, out tenantId) || tenantId == Guid.Empty)
            {
                tenantId = Guid.Empty;
                return false;
            }

            httpContext.Items["__resolved_tenant_id"] = tenantId;

            return true;
        }
    }
}
