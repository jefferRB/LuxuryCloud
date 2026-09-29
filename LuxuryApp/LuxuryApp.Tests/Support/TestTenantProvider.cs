using LuxuryApp.Services.Tenant;

namespace LuxuryApp.Tests.Support
{
    internal sealed class TestTenantProvider : ITenantProvider
    {
        public Guid TenantId { get; set; } = Guid.Empty;

        public Guid GetTenantId()
        {
            if (TenantId == Guid.Empty)
            {
                throw new InvalidOperationException("Tenant no configurado en el test.");
            }

            return TenantId;
        }

        public bool HasTenant() => TenantId != Guid.Empty;
    }
}
