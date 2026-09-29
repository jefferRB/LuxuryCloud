using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.SaaS
{
    /// <summary>
    /// Intento de cambio de plan (típicamente "aumentar funcionarios") de un tenant.
    /// Sirve para (a) impedir múltiples cambios abiertos a la vez, (b) saber que existe una
    /// suscripción recurrente ANTERIOR en el proveedor que debe cancelarse manualmente
    /// (TiloPay no expone API de cancelación), y (c) alertar al admin. Tenant-scoped (RLS).
    /// </summary>
    public class PlanChangeIntent : ITenantEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [BindNever]
        public Guid TenantId { get; set; }

        // Origen (plan actual al iniciar el cambio).
        public Guid? FromPlanId { get; set; }

        [MaxLength(50)]
        public string? FromPlanCode { get; set; }

        public int? FromWorkerCount { get; set; }

        public int? FromTilopayRecurringPlanId { get; set; }

        [MaxLength(100)]
        public string? FromProviderSubscriptionId { get; set; }

        // Destino (plan nuevo deseado).
        public Guid ToPlanId { get; set; }

        [MaxLength(50)]
        public string ToPlanCode { get; set; } = string.Empty;

        public int ToWorkerCount { get; set; }

        public BillingCycle ToBillingCycle { get; set; }

        public int ToTilopayRecurringPlanId { get; set; }

        public PlanChangeIntentState Estado { get; set; } = PlanChangeIntentState.Pending;

        public ProviderCancellationState OldProviderCancellation { get; set; } = ProviderCancellationState.NotRequired;

        public Guid? PagoSuscripcionId { get; set; }

        [MaxLength(100)]
        public string? NewProviderSubscriptionId { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAtUtc { get; set; }

        public DateTime? AppliedAtUtc { get; set; }

        [MaxLength(300)]
        public string? Notes { get; set; }

        // ── Presupuesto de reintentos de la cancelación del suscriptor viejo ──
        // El estado del reintento vive AQUÍ, en el intent, y no se deduce contando filas de
        // auditoría por tenant. Contar auditorías demostró ser incorrecto en producción: un
        // tenant quedaba bloqueado 24h por intentos de OTRO intent, o por "intentos" que en
        // realidad nunca llamaron a TiloPay (AutoCancel apagado, IDs sin reparar). Solo los
        // intentos REALES contra el proveedor mueven estos campos.

        /// <summary>
        /// Intentos REALES contra TiloPay desde el último reinicio de presupuesto. Nunca lo
        /// mueven los skips. Es lo que escala el backoff (inmediato → 5m → 15m → 30m → 1h → 6h → diario).
        /// </summary>
        public int OldCancellationAttemptCount { get; set; }

        public DateTime? OldCancellationLastAttemptUtc { get; set; }

        /// <summary>Momento a partir del cual se permite el próximo intento real. NULL = elegible ya.</summary>
        public DateTime? OldCancellationNextRetryUtc { get; set; }

        /// <summary>
        /// Marca del último reinicio del presupuesto (reparación de estado inconsistente o retry
        /// forzado por soporte). Los intentos anteriores a esta marca no cuentan contra el tope
        /// diario: se hicieron sobre datos que aún estaban rotos, así que no prueban nada.
        /// </summary>
        public DateTime? OldCancellationAttemptsResetAtUtc { get; set; }
    }

    public enum PlanChangeIntentState
    {
        Pending = 0,
        Applied = 1,
        Failed = 2,
        Cancelled = 3,
        Superseded = 4,

        /// <summary>
        /// El cliente abrió el checkout y nunca pagó. Distinto de Cancelled (alguien lo cerró) y de
        /// Superseded (otro cambio lo reemplazó): aquí simplemente no pasó nada. Valor aditivo — la
        /// columna es int y el índice único filtra por Estado = 0, así que expirar libera el cupo
        /// del tenant sin migración.
        /// </summary>
        Expired = 5
    }

    public enum ProviderCancellationState
    {
        NotRequired = 0,
        PendingManualCancellation = 1,
        Cancelled = 2
    }
}
