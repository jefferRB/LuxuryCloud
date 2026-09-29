using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Models.Saas
{
    public class PlanFeature
    {
        public Guid PlanId { get; set; }
        public Plan Plan { get; set; } = null!;
        public Guid FeatureId { get; set; }
        public Feature Feature { get; set; } = null!;
        public int? Limite { get; set; }
    }
}
