namespace LuxuryApp.Models.Saas
{
    public class Feature
    {
        public Guid Id { get; set; }
        public string Codigo { get; set; } = string.Empty; // "REPORTES_AVANZADOS"
        public string Nombre { get; set; } = string.Empty;

        public ICollection<PlanFeature> PlanFeatures { get; set; } = new List<PlanFeature>();
    }
}
