using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Models.Platform
{
    /// <summary>
    /// Estado local + del proveedor de una suscripción, para la consola de plataforma (SuperAdmin).
    /// Solo lectura: los campos del proveedor son el último snapshot local sincronizado por la
    /// reconciliación (ProviderStatusRaw/…), no una llamada en vivo a TiloPay.
    /// </summary>
    public sealed class ProviderSubscriptionLifecycleViewModel
    {
        public Guid TenantId { get; init; }
        public string? TenantName { get; init; }

        /// <summary>Existe una suscripción con id_suscriptor de TiloPay sobre la que operar.</summary>
        public bool HasManageableSubscription { get; init; }

        /// <summary>La integración admin de TiloPay está activa (habilita las acciones).</summary>
        public bool AdminEnabled { get; init; }

        public string? PlanCode { get; init; }
        public string? PlanName { get; init; }
        public EstadoSuscripcion? LocalStatus { get; init; }
        public string LocalStatusLabel { get; init; } = "—";
        public bool CanAccessApp { get; init; }
        public bool CancelAtPeriodEnd { get; init; }

        /// <summary>Caso D: renovación cancelada AÚN vigente ⇒ se puede reactivar el mismo suscriptor.</summary>
        public bool CanReactivateRenewal { get; init; }

        public string? ProviderSubscriptionIdSuffix { get; init; }
        public string? ProviderStatusRaw { get; init; }
        public string ProviderStateLabel { get; init; } = "Desconocido";
        public bool ProviderIsDeleted { get; init; }
        public bool ProviderIsPaused { get; init; }
        public bool ProviderIsActive { get; init; }

        public DateTime? ProviderPausedAtUtc { get; init; }
        public DateTime? ProviderCancelledAtUtc { get; init; }
        public DateTime? CancellationRequestedAtUtc { get; init; }
        public DateTime? CancellationEffectiveAtUtc { get; init; }
        public string? CancellationReason { get; init; }
        public DateTime? ProviderStatusLastSyncedUtc { get; init; }

        public DateTime? FechaFinUtc { get; init; }
        public string? EffectiveEndDisplay { get; init; }

        /// <summary>Última alerta de drift local↔proveedor (si la reconciliación la registró).</summary>
        public string? RecentMismatchReason { get; init; }
        public DateTime? RecentMismatchAtUtc { get; init; }
    }
}
