using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.SaaS
{
    public interface ITenantCommercialAccessResolver
    {
        Task<TenantCommercialAccessResult> ResolveAsync(
            Guid tenantId,
            AppUsuario? user = null,
            CancellationToken cancellationToken = default);
    }
}
