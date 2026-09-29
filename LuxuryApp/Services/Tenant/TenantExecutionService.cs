using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Tenant
{
    public sealed class TenantExecutionService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TenantExecutionService> _logger;

        public TenantExecutionService(
            IServiceScopeFactory scopeFactory,
            ILogger<TenantExecutionService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public async Task RunForEachActiveTenantAsync(
            Func<IServiceProvider, Guid, CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);

            using var discoveryScope = _scopeFactory.CreateScope();
            var discoveryContext = discoveryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var tenantIds = await discoveryContext.Tenants
                .AsNoTracking()
                .Where(t => t.Activo)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            foreach (var tenantId in tenantIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var tenantScope = _scopeFactory.CreateScope();
                var tenantExecutionAccessor = tenantScope.ServiceProvider.GetRequiredService<ITenantExecutionContextAccessor>();

                using var scope = tenantExecutionAccessor.BeginScope(tenantId);
                using var logScope = _logger.BeginScope(new Dictionary<string, object>
                {
                    ["TenantId"] = tenantId
                });

                await operation(tenantScope.ServiceProvider, tenantId, cancellationToken);
            }
        }

        public async Task RunForTenantAsync(
            Guid tenantId,
            Func<IServiceProvider, Guid, CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);

            if (tenantId == Guid.Empty)
            {
                throw new ArgumentException("El tenant scope requiere un TenantId valido.", nameof(tenantId));
            }

            using var tenantScope = _scopeFactory.CreateScope();
            var tenantExecutionAccessor = tenantScope.ServiceProvider.GetRequiredService<ITenantExecutionContextAccessor>();

            using var scope = tenantExecutionAccessor.BeginScope(tenantId);
            using var logScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["TenantId"] = tenantId
            });

            await operation(tenantScope.ServiceProvider, tenantId, cancellationToken);
        }
    }
}
