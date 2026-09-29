namespace LuxuryApp.Services.Tenant
{
    public interface ITenantExecutionContextAccessor
    {
        Guid? CurrentTenantId { get; }

        IDisposable BeginScope(Guid tenantId);

        IDisposable ClearScope();
    }
}
