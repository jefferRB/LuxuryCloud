namespace LuxuryApp.Models.SaaS
{
    public sealed class BillingCheckoutContinuationViewModel
    {
        public Guid PlanId { get; init; }
        public string PlanName { get; init; } = string.Empty;
        public string? PlanCode { get; init; }
        public string? AccountEmail { get; init; }
        public bool IsAddon { get; init; }
    }
}
