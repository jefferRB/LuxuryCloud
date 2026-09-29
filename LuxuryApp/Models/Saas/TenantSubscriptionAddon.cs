using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.SaaS
{
    public class TenantSubscriptionAddon : ITenantEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        public Guid PlanId { get; set; }

        [MaxLength(50)]
        public string AddonCode { get; set; } = string.Empty;

        public EstadoSuscripcion Estado { get; set; } = EstadoSuscripcion.Pendiente;

        public int? TilopayRecurringPlanId { get; set; }

        [MaxLength(100)]
        public string? ProviderSubscriptionId { get; set; }

        [MaxLength(100)]
        public string? ProviderTransactionId { get; set; }

        public decimal? PrecioMensual { get; set; }

        [MaxLength(10)]
        public string? MonedaFacturacion { get; set; }

        public int MonthlyMessageLimit { get; set; }

        // ── Origen/fuente comercial del add-on (formalización de manuales/cortesía/canje) ──
        // BillingSource es la señal AUTORITATIVA: nunca inferir "manual" de ProviderTransactionId
        // 'MANUAL-...' ni de "TilopayRecurringPlanId is null". Ver [[WhatsAppAddonEntitlementRules]].

        /// <summary>Origen del add-on: pagado por TiloPay, acceso manual de plataforma, o legacy/test.</summary>
        public WhatsAppAddonBillingSource BillingSource { get; set; } = WhatsAppAddonBillingSource.ProviderRecurring;

        /// <summary>Tipo de acceso manual (solo aplica cuando BillingSource = ManualGrant).</summary>
        public ManualWhatsAppGrantType? ManualGrantType { get; set; }

        /// <summary>Motivo/observación del acceso manual (obligatorio al otorgar desde plataforma).</summary>
        [MaxLength(500)]
        public string? ManualGrantReason { get; set; }

        /// <summary>UserId del SuperAdmin/plataforma que otorgó el acceso manual.</summary>
        [MaxLength(450)]
        public string? GrantedByUserId { get; set; }

        public DateTime? GrantedAtUtc { get; set; }

        /// <summary>Vigencia del acceso manual (null = sin fecha; usar IsManualGrantIndefinite para "permanente").</summary>
        public DateTime? ManualGrantExpiresAtUtc { get; set; }

        /// <summary>El acceso manual es permanente (sin vencimiento). Para acuerdos fijos (canje, cortesía).</summary>
        public bool IsManualGrantIndefinite { get; set; }

        // ── Revocación del acceso manual (deja rastro; no borra la fila) ──
        public DateTime? RevokedAtUtc { get; set; }

        [MaxLength(450)]
        public string? RevokedByUserId { get; set; }

        [MaxLength(500)]
        public string? RevocationReason { get; set; }

        public DateTime FechaInicio { get; set; } = DateTime.UtcNow;

        public DateTime? FechaFin { get; set; }

        public DateTime? FechaProximoCobroUtc { get; set; }

        public DateTime? FechaFinGraciaUtc { get; set; }

        public DateTime? FechaCancelacionUtc { get; set; }

        // ── Cancelación de renovación programada del add-on (cascada base→add-on / cliente) ──
        // Igual que en el plan base: cancelar la RENOVACIÓN no corta el uso ya pagado; el add-on
        // sigue Activa hasta FechaFin y ahí expira solo (GetEffectiveStatus lo pasa a Suspendida).
        // Lo money-critical es dar de baja el suscriptor en TiloPay para que no vuelva a cobrar.

        /// <summary>El cliente/soporte canceló la renovación del add-on. El uso sigue hasta FechaFin.</summary>
        public bool CancelAtPeriodEnd { get; set; }

        /// <summary>Fecha efectiva en que la cancelación programada del add-on corta el uso (fin de período pagado).</summary>
        public DateTime? CancellationEffectiveAtUtc { get; set; }

        [MaxLength(450)]
        public string? CancellationRequestedByUserId { get; set; }

        [MaxLength(250)]
        public string? CancellationReason { get; set; }

        // ── Cancelación SALIENTE pendiente del suscriptor en TiloPay (Strategy B + cascada) ──
        // El suscriptor a cancelar puede ser el HUÉRFANO de un upgrade (WA400→WA800: se cancela el
        // viejo DESPUÉS de confirmar el nuevo) o el ACTUAL (cuando el cliente/plataforma cancela el
        // add-on o se canceló el plan base). Se guarda aparte de ProviderSubscriptionId para poder
        // cancelar el viejo sin perder el nuevo. El presupuesto de reintentos vive AQUÍ (nunca se
        // deduce contando auditorías): solo un intento REAL contra TiloPay mueve estos campos.

        /// <summary>Suscriptor de TiloPay que debe darse de baja. Null = nada pendiente.</summary>
        [MaxLength(100)]
        public string? PendingCancellationProviderSubscriptionId { get; set; }

        /// <summary>Plan recurrente de TiloPay del suscriptor a cancelar (para verificar por getSuscriptorRepeat).</summary>
        public int? PendingCancellationTilopayRecurringPlanId { get; set; }

        /// <summary>
        /// Estado del trabajo de baja PENDIENTE (la cola de reintentos), no el estado del add-on.
        /// OJO con <see cref="ProviderCancellationState.Cancelled"/>: por sí solo NO significa que el
        /// suscriptor ACTUAL esté dado de baja — puede referirse al suscriptor ANTERIOR de una
        /// transición de paquete. Para saber a quién se refiere hay que mirar
        /// <see cref="ProviderCancellationSubscriptionId"/>; nunca interpretar este campo solo.
        /// </summary>
        public ProviderCancellationState ProviderCancellation { get; set; } = ProviderCancellationState.NotRequired;

        /// <summary>
        /// A QUÉ suscriptor se refieren <see cref="ProviderCancellation"/> y
        /// <see cref="ProviderCancelledAtUtc"/>. Sin esto, tras un cambio de paquete la fila ACTIVA
        /// quedaba con ProviderCancellation=Cancelled (por la baja del suscriptor VIEJO) y la cascada
        /// del plan base concluía que el actual ya estaba cancelado, dejándolo cobrando para siempre.
        /// NULL en filas antiguas ⇒ se asume que NO corresponde al actual (lado seguro para el dinero).
        /// </summary>
        [MaxLength(100)]
        public string? ProviderCancellationSubscriptionId { get; set; }

        // ── Auditoría del suscriptor REEMPLAZADO en la última transición de paquete ──
        // WA400→WA800→WA400 encadenado: deja rastro de a quién se dio de baja sin ensuciar los
        // campos del suscriptor vigente. Solo auditoría; ninguna decisión de cobro los lee.

        /// <summary>Suscriptor de TiloPay que este add-on reemplazó en la última transición.</summary>
        [MaxLength(100)]
        public string? PreviousProviderSubscriptionId { get; set; }

        /// <summary>Cuándo se VERIFICÓ la baja del suscriptor reemplazado.</summary>
        public DateTime? PreviousProviderCancelledAtUtc { get; set; }

        /// <summary>Intentos REALES contra TiloPay desde el último reinicio de presupuesto. Los skips no cuentan.</summary>
        public int ProviderCancellationAttemptCount { get; set; }

        public DateTime? ProviderCancellationLastAttemptUtc { get; set; }

        /// <summary>Momento a partir del cual se permite el próximo intento real. NULL = elegible ya.</summary>
        public DateTime? ProviderCancellationNextRetryUtc { get; set; }

        /// <summary>Cuándo se VERIFICÓ contra TiloPay que el suscriptor a cancelar quedó inactivo.</summary>
        public DateTime? ProviderCancelledAtUtc { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        public Tenant? Tenant { get; set; }
        public Plan? Plan { get; set; }
    }
}
