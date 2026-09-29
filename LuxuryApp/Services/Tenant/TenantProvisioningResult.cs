using LuxuryApp.Models.Identity;

namespace LuxuryApp.Services.Tenant
{
    public class TenantProvisioningResult
    {
        public bool Succeeded { get; init; }
        public IReadOnlyCollection<string> Errors { get; init; } = Array.Empty<string>();
        public AppUsuario? User { get; init; }
        public Guid TenantId { get; init; }
        public bool RequiresPlanSelection { get; init; }
        public bool RequiresEmailConfirmation { get; init; }
        public bool InitialSubscriptionCreated { get; init; }
        public bool PromotionalAccessApplied { get; init; }

        public static TenantProvisioningResult Failure(params string[] errors) =>
            new()
            {
                Succeeded = false,
                Errors = errors
            };

        public static TenantProvisioningResult Success(
            AppUsuario user,
            Guid tenantId,
            bool initialSubscriptionCreated,
            bool promotionalAccessApplied,
            bool requiresEmailConfirmation,
            bool requiresPlanSelection) =>
            new()
            {
                Succeeded = true,
                User = user,
                TenantId = tenantId,
                InitialSubscriptionCreated = initialSubscriptionCreated,
                PromotionalAccessApplied = promotionalAccessApplied,
                RequiresEmailConfirmation = requiresEmailConfirmation,
                RequiresPlanSelection = requiresPlanSelection
            };
    }
}
