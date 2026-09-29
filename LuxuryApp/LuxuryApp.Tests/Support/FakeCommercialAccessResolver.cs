using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.SaaS;

namespace LuxuryApp.Tests.Support
{
    /// <summary>Resolver de acceso comercial de prueba: por defecto concede acceso.</summary>
    internal sealed class FakeCommercialAccessResolver : ITenantCommercialAccessResolver
    {
        public bool CanAccessApp { get; set; } = true;

        public Task<TenantCommercialAccessResult> ResolveAsync(
            Guid tenantId,
            AppUsuario? user = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TenantCommercialAccessResult
            {
                CanAccessApp = CanAccessApp,
                TenantId = tenantId
            });
    }
}
