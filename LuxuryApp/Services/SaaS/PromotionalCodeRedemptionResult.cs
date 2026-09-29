using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.SaaS
{
    public sealed class PromotionalCodeRedemptionResult
    {
        public bool Succeeded { get; init; }
        public string? Error { get; init; }
        public TenantCommercialAccessGrant? AccessGrant { get; init; }
        public PromotionalCode? PromotionalCode { get; init; }

        public static PromotionalCodeRedemptionResult Failure(string error) => new()
        {
            Error = error
        };

        public static PromotionalCodeRedemptionResult Success(
            PromotionalCode promotionalCode,
            TenantCommercialAccessGrant accessGrant) => new()
            {
                Succeeded = true,
                PromotionalCode = promotionalCode,
                AccessGrant = accessGrant
            };
    }
}
