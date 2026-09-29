using LuxuryApp.Models.Identity;

namespace LuxuryApp.Services.SaaS
{
    public interface IPromotionalCodeService
    {
        Task<PromotionalCodeRedemptionResult> RedeemAsync(
            string code,
            Guid tenantId,
            AppUsuario user,
            CancellationToken cancellationToken = default);
    }
}
