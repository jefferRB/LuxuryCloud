namespace LuxuryApp.Models.SaaS
{
    public class OpcionesOnboardingTenant
    {
        public string RegistrationRole { get; set; } = "Administrador";
        public bool AddRegisteredRole { get; set; } = true;
        public string RegisteredRole { get; set; } = "Registrado";
        public bool CreateInitialSubscription { get; set; }
        public string? InitialPlanName { get; set; }
        public EstadoSuscripcion InitialSubscriptionState { get; set; } = EstadoSuscripcion.Trial;
        public int TrialDays { get; set; } = 14;
    }
}
