using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.SaaS
{
    public class Suscripcion : ITenantEntity
    {
        public Guid Id { get; set; }

        [Required]
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        [Required]
        public Guid PlanId { get; set; }

        public PaymentProviderType Proveedor { get; set; } = PaymentProviderType.None;

        [MaxLength(100)]
        public string? ProviderCustomerId { get; set; }

        [MaxLength(100)]
        public string? ProviderSubscriptionId { get; set; }

        [MaxLength(100)]
        public string? ProviderTransactionId { get; set; }

        [MaxLength(100)]
        public string? ProviderPaymentLinkId { get; set; }

        [MaxLength(100)]
        public string? ProviderReference { get; set; }

        [MaxLength(100)]
        public string? UltimoEventoProveedorId { get; set; }

        [MaxLength(50)]
        public string? CodigoPlan { get; set; }

        public int? TilopayRecurringPlanId { get; set; }

        [Required]
        public EstadoSuscripcion Estado { get; set; }

        public DateTime FechaInicio { get; set; } = DateTime.Now;

        public DateTime? FechaFin { get; set; }

        public DateTime? FechaTrialFin { get; set; }

        public DateTime? FechaProximoCobroUtc { get; set; }

        public DateTime? FechaFinGraciaUtc { get; set; }

        // ── Vencimiento REAL en el proveedor (fuente de verdad de cuándo cobra TiloPay) ──
        // Separado de FechaFin/FechaProximoCobroUtc a propósito: esas son la vigencia que calcula
        // LuxuryCloud; estas son lo que dice el proveedor. Se guardan aparte para poder auditar la
        // diferencia y calcular la fecha EFECTIVA sin perder ninguna de las dos.

        /// <summary>Expire del suscriptor Active en TiloPay, ya en UTC (fin del día Costa Rica). Null si no se ha sincronizado.</summary>
        public DateTime? ProviderExpiresAtUtc { get; set; }

        /// <summary>Cuándo se sincronizó por última vez <see cref="ProviderExpiresAtUtc"/> contra getSuscriptorRepeat.</summary>
        public DateTime? ProviderExpiryLastSyncedUtc { get; set; }

        /// <summary>El expire crudo de TiloPay ("2026-09-15"), para diagnóstico/auditoría.</summary>
        [MaxLength(20)]
        public string? ProviderExpiryRaw { get; set; }

        public DateTime? FechaCancelacionUtc { get; set; }

        // ── Ciclo de vida: cancelación programada (cancel-at-period-end), pausa y estado provider ──
        // Separado de Estado/FechaFin a propósito: CancelAtPeriodEnd mantiene el acceso ya pagado
        // vivo hasta la fecha efectiva, y estas columnas guardan la evidencia verificada contra
        // TiloPay (nunca solo por un HTTP 200) para poder auditar y reconciliar el cierre del período.

        /// <summary>Cuándo el cliente/soporte pidió cancelar la renovación (no corta acceso).</summary>
        public DateTime? CancellationRequestedAtUtc { get; set; }

        /// <summary>Cuándo se VERIFICÓ contra TiloPay que el suscriptor quedó inactivo (Delete/Eliminado).</summary>
        public DateTime? ProviderCancelledAtUtc { get; set; }

        /// <summary>Fecha efectiva en que la cancelación programada corta el acceso (fin de período ya pagado).</summary>
        public DateTime? CancellationEffectiveAtUtc { get; set; }

        /// <summary>Motivo de la cancelación. Dedicada (no se pisa como MotivoEstado en cada cambio).</summary>
        [MaxLength(250)]
        public string? CancellationReason { get; set; }

        /// <summary>UserId de quien solicitó la cancelación (cliente admin o SuperAdmin de plataforma).</summary>
        [MaxLength(450)]
        public string? CancellationRequestedByUserId { get; set; }

        /// <summary>Cuándo se VERIFICÓ contra TiloPay que el suscriptor quedó Pausado (status 3). Null = no pausado.</summary>
        public DateTime? ProviderPausedAtUtc { get; set; }

        /// <summary>Última vez que se sincronizó el status crudo del suscriptor contra getSuscriptorRepeat.</summary>
        public DateTime? ProviderStatusLastSyncedUtc { get; set; }

        /// <summary>Último status crudo leído del proveedor (para detectar drift local↔proveedor y auditar).</summary>
        [MaxLength(40)]
        public string? ProviderStatusRaw { get; set; }

        // ── Resumen de recuperación de pago (el detalle vive en SubscriptionPaymentIncidents) ──
        // La gracia sigue usando FechaFinGraciaUtc (arriba); estos campos son para display/consulta rápida.

        /// <summary>Cuándo se detectó el último pago recurrente fallido (null = sin incidente abierto).</summary>
        public DateTime? LastPaymentFailedAtUtc { get; set; }

        /// <summary>Estado de recuperación para mostrar en UI (p.ej. "GraceActive", "GraceExpired"). Null = sano.</summary>
        [MaxLength(40)]
        public string? PaymentRecoveryStatus { get; set; }

        /// <summary>Cuándo se envió la última notificación de recuperación de pago.</summary>
        public DateTime? LastPaymentRecoveryNotificationAtUtc { get; set; }

        public decimal? PrecioMensual { get; set; }

        [MaxLength(10)]
        public string? MonedaFacturacion { get; set; }

        public int? MaxFuncionarios { get; set; }

        public bool CancelAtPeriodEnd { get; set; }

        public DateTime? FechaUltimoPagoUtc { get; set; }

        public DateTime? FechaUltimaActualizacionUtc { get; set; }

        [MaxLength(250)]
        public string? MotivoEstado { get; set; }

        [ForeignKey("TenantId")]
        public Tenant? Tenant { get; set; }

        [ForeignKey("PlanId")]
        public Plan? Plan { get; set; }

        public ICollection<HistorialSuscripcion> Historiales { get; set; } = new List<HistorialSuscripcion>();
    }
}
