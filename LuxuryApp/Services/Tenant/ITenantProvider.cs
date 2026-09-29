namespace LuxuryApp.Services.Tenant
{
    public interface ITenantProvider
    {
        Guid GetTenantId();
        bool HasTenant();
    }
}