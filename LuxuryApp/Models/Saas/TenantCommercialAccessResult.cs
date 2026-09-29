namespace LuxuryApp.Models.SaaS
{
    public sealed class TenantCommercialAccessResult
    {
        public bool CanAccessApp { get; init; }
        public bool RequiresBilling { get; init; }
        public bool IsPlatformSuperAdmin { get; init; }
        public Guid TenantId { get; init; }
        public Guid? EffectivePlanId { get; init; }
        public string? EffectivePlanName { get; init; }
        public TenantCommercialAccessMode CommercialAccessMode { get; init; }
        public TenantCommercialAccessSource AccessSource { get; init; }
        public string Reason { get; init; } = string.Empty;
        public DateTime? AccessEndsUtc { get; init; }
        public bool HasCommercialHistory { get; init; }
        public EstadoSuscripcion? SubscriptionStatus { get; init; }
        public DateTime? CurrentPeriodEndUtc { get; init; }
        public DateTime? NextBillingDateUtc { get; init; }
        public DateTime? GracePeriodEndsUtc { get; init; }
        public bool IsInGracePeriod { get; init; }

        // ─────────────────────────────────────────────────────────────────────────────
        // Estado comercial EFECTIVO (aditivo, sin migracion). Antes de esto el limite de
        // funcionarios se calculaba en dos lugares distintos: el enforcement usaba
        // EffectivePlanId (respetaba el plan forzado) y el display de la cuenta leia la fila
        // de Suscripciones (mostraba el limite viejo, p. ej. 7 con un plan forzado de 3).
        // Estos campos hacen de este resolver la UNICA fuente de verdad para ambos.
        // ─────────────────────────────────────────────────────────────────────────────

        /// <summary>Codigo del plan efectivo (LC_M_05, WA400 nunca aplica aqui, BASIC en legacy...).</summary>
        public string? EffectivePlanCode { get; init; }

        /// <summary>Clasificacion del plan efectivo. Un add-on jamas deberia aparecer como plan base.</summary>
        public PlanCatalogKind EffectivePlanKind { get; init; }

        /// <summary>
        /// Limite de funcionarios efectivo. Null = ilimitado (o sin plan resuelto: ver
        /// <see cref="HasEffectivePlan"/> para distinguir los dos casos).
        /// </summary>
        public int? EffectiveEmployeeLimit { get; init; }

        /// <summary>True cuando el limite proviene de un plan forzado por plataforma, no del cobro.</summary>
        public bool IsForcedByPlatform { get; init; }

        /// <summary>Plan forzado configurado en el tenant (puede existir aunque no sea el efectivo).</summary>
        public Guid? ForcedPlanId { get; init; }

        /// <summary>Quien paga el acceso base: proveedor recurrente, manual/exento, legacy o nadie.</summary>
        public TenantAccessBillingSource BillingSource { get; init; }

        /// <summary>Suscriptor del proveedor del plan BASE (null en accesos manuales/exentos).</summary>
        public string? ProviderSubscriptionId { get; init; }

        /// <summary>Inconsistencias detectadas al resolver. Se muestran; no se corrigen en silencio.</summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        public bool HasEffectivePlan => EffectivePlanId.HasValue;
        public bool HasWarnings => Warnings.Count > 0;
    }
}
