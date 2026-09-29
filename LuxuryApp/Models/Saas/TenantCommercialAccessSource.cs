namespace LuxuryApp.Models.SaaS
{
    public enum TenantCommercialAccessSource
    {
        None = 0,
        PlatformSuperAdmin = 1,
        TenantExempt = 2,
        TenantInternal = 3,
        PromotionalGrant = 4,
        SubscriptionActive = 5,
        SubscriptionTrial = 6
    }
}
