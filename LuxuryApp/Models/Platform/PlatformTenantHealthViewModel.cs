namespace LuxuryApp.Models.Platform
{
    public enum TenantHealthState
    {
        Saludable = 0,
        Atencion = 1,
        Riesgo = 2,
        SinAcceso = 3
    }

    public sealed class PlatformTenantHealthViewModel
    {
        public TenantHealthState State { get; init; }
        public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

        public string BadgeClass => State switch
        {
            TenantHealthState.Saludable => "platform-badge-success",
            TenantHealthState.Atencion  => "platform-badge-warning",
            TenantHealthState.Riesgo    => "platform-badge-danger",
            _                           => "platform-badge-dark"
        };

        public string Label => State switch
        {
            TenantHealthState.Saludable => "Saludable",
            TenantHealthState.Atencion  => "Atención",
            TenantHealthState.Riesgo    => "Riesgo",
            _                           => "Sin acceso"
        };

        public string Icon => State switch
        {
            TenantHealthState.Saludable => "bi-check-circle-fill",
            TenantHealthState.Atencion  => "bi-exclamation-triangle-fill",
            TenantHealthState.Riesgo    => "bi-x-octagon-fill",
            _                           => "bi-slash-circle-fill"
        };
    }
}
