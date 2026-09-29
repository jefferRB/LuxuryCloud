namespace LuxuryApp.Models.Platform
{
    public sealed class PlatformPromotionalCodeListItemViewModel
    {
        public Guid Id { get; init; }
        public string Codigo { get; init; } = string.Empty;
        public bool Activo { get; init; }
        public string PlanName { get; init; } = string.Empty;
        public int DiasGratis { get; init; }
        public int? MaxUsos { get; init; }
        public int UsosActuales { get; init; }
        public DateTime? FechaExpiracionUtc { get; init; }
        public bool SoloPrimerRegistro { get; init; }
        public string? EmailObjetivo { get; init; }
    }
}
