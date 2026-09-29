using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Models.Platform
{
    public sealed class PlatformTenantFichaViewModel
    {
        public Guid TenantId { get; init; }
        public string TenantName { get; init; } = string.Empty;
        // ── Contacto principal (regla determinista: admin > funcionario) ──
        public string? OwnerEmail { get; init; }
        public string? OwnerName { get; init; }
        public TenantOwnerSource OwnerSource { get; init; }
        public IReadOnlyList<string> OwnerWarnings { get; init; } = Array.Empty<string>();

        /// <summary>Resolucion completa: owner, admins adicionales, funcionarios y advertencias.</summary>
        public TenantOwnerResolution? Owner { get; init; }

        public DateTime FechaCreacion { get; init; }
        public bool Activo { get; init; }

        // ── Estado comercial EFECTIVO (misma fuente que la app del cliente) ──
        public bool CanAccessApp { get; init; }
        public string? EffectivePlanName { get; init; }
        public string? EffectivePlanCode { get; init; }
        public PlanCatalogKind EffectivePlanKind { get; init; }

        /// <summary>Limite de funcionarios efectivo. Null = ilimitado o sin plan resuelto.</summary>
        public int? EffectiveEmployeeLimit { get; init; }

        public int ActiveFuncionarios { get; init; }

        public bool ExceedsEmployeeLimit =>
            EffectiveEmployeeLimit.HasValue && ActiveFuncionarios > EffectiveEmployeeLimit.Value;

        /// <summary>El plan efectivo viene de un plan forzado por plataforma, no de un cobro.</summary>
        public bool IsForcedByPlatform { get; init; }

        public TenantAccessBillingSource AccessBillingSource { get; init; }
        public string? ProviderSubscriptionId { get; init; }
        public DateTime? NextBillingDateUtc { get; init; }

        /// <summary>Inconsistencias comerciales detectadas. Se muestran como alerta operativa.</summary>
        public IReadOnlyList<string> CommercialWarnings { get; init; } = Array.Empty<string>();

        public string? CommercialReason { get; init; }
        public TenantCommercialAccessMode CommercialAccessMode { get; init; }
        public string? CommercialNotes { get; init; }

        public PlatformTenantHealthViewModel Health { get; init; } = new();
        public PlatformTenantUsageViewModel Usage { get; init; } = new();
        public PlatformTenantBillingFichaViewModel Billing { get; init; } = new();
        public PlatformTenantWhatsAppFichaViewModel WhatsApp { get; init; } = new();
        public PlatformTenantReservationsFichaViewModel Reservations { get; init; } = new();

        public IReadOnlyList<PlatformTenantUserPreviewViewModel> Users { get; init; } = Array.Empty<PlatformTenantUserPreviewViewModel>();
        public int TotalUsersCount { get; init; }

        public IReadOnlyList<PlatformTenantAuditPreviewViewModel> AuditPreview { get; init; } = Array.Empty<PlatformTenantAuditPreviewViewModel>();
    }

    // ─── Billing ────────────────────────────────────────────────────────────────

    public sealed class PlatformTenantBillingFichaViewModel
    {
        public string? ActivePlanName { get; init; }
        public string? ActivePlanCode { get; init; }
        public EstadoSuscripcion? SuscripcionEstado { get; init; }
        public DateTime? SuscripcionFechaFin { get; init; }
        public DateTime? SuscripcionProximoCobro { get; init; }
        public bool IsTrial { get; init; }
        public DateTime? TrialFin { get; init; }
        public bool HasPendingCheckout { get; init; }
        public bool IsExpiringSoon { get; init; }
        public int PendingCheckoutsCount { get; init; }
        public IReadOnlyList<PlatformTenantPaymentRowViewModel> RecentPayments { get; init; } = Array.Empty<PlatformTenantPaymentRowViewModel>();
        public IReadOnlyList<string> ActiveAddonNames { get; init; } = Array.Empty<string>();
    }

    public sealed class PlatformTenantPaymentRowViewModel
    {
        public DateTime FechaUtc { get; init; }
        public string Estado { get; init; } = string.Empty;
        public decimal Monto { get; init; }
        public string Moneda { get; init; } = string.Empty;
        public string? PlanName { get; init; }
    }

    // ─── WhatsApp ────────────────────────────────────────────────────────────────

    public sealed class PlatformTenantWhatsAppFichaViewModel
    {
        public bool IsEnabled { get; init; }
        public bool AddonActive { get; init; }
        public string? AddonCode { get; init; }
        public DateTime? AddonFechaFin { get; init; }
        public int DailyMessageLimit { get; init; }
        public int TodayUsage { get; init; }
        public int MonthlyUsage30d { get; init; }
        public int? MonthlyMessageLimit { get; init; }
        public bool HasRecentError { get; init; }
        public string? LastErrorCode { get; init; }
        public string? LastErrorMessage { get; init; }
        public DateTime? LastErrorAtUtc { get; init; }
        public DateTime? LastMessageSentUtc { get; init; }
        public string? TimeZoneId { get; init; }
        public string? Notes { get; init; }
    }

    // ─── Reservas ────────────────────────────────────────────────────────────────

    public sealed class PlatformTenantReservationsFichaViewModel
    {
        public bool PublicBookingEnabled { get; init; }
        public string? PublicBookingSlug { get; init; }
        public int Total30d { get; init; }
        public int Pending { get; init; }
        public int Confirmed30d { get; init; }
        public int Rejected30d { get; init; }
        public double ConfirmationRate { get; init; }
        public DateTime? LastRequestUtc { get; init; }
    }

    // ─── Usuarios preview ────────────────────────────────────────────────────────

    public sealed class PlatformTenantUserPreviewViewModel
    {
        public string UserId { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string? Name { get; init; }
        public bool State { get; init; }
        public string? Roles { get; init; }
        public bool IsPlatformSuperAdmin { get; init; }

        /// <summary>Clasificacion funcional resuelta contra AspNetUserRoles (no texto libre).</summary>
        public TenantUserKind Kind { get; init; }

        /// <summary>Es el contacto principal del tenant segun la regla de owner.</summary>
        public bool IsOwner { get; init; }
    }

    // ─── Auditoría preview ───────────────────────────────────────────────────────

    public sealed class PlatformTenantAuditPreviewViewModel
    {
        public DateTime CreatedAtUtc { get; init; }
        public string ActorEmail { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string? TargetUserEmail { get; init; }
        public string? Reason { get; init; }
    }
}
