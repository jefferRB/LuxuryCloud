using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.SaaS
{
    /// <summary>Estado de un incidente de recuperación de pago recurrente.</summary>
    public enum PaymentIncidentStatus
    {
        /// <summary>Pago fallido con gracia en curso: acceso mantenido, esperando actualización/pago.</summary>
        Open = 0,

        /// <summary>Un pago posterior confirmado resolvió el incidente.</summary>
        Resolved = 1,

        /// <summary>La gracia venció sin pago (con o sin suspensión, según AutoSuspendAfterGrace).</summary>
        GraceExpired = 2,

        /// <summary>No se pudo correlacionar/decidir con seguridad (email/plan ambiguo): decide soporte.</summary>
        ManualReview = 3,

        /// <summary>Fallo no accionable (p.ej. cancelación de renovación ya dada de baja en el proveedor).</summary>
        Ignored = 4
    }

    /// <summary>A qué producto pertenece el incidente de recuperación de pago. Aditivo (default BasePlan).</summary>
    public enum PaymentIncidentScope
    {
        /// <summary>Plan base del SaaS. Comportamiento histórico (todo incidente previo es de base).</summary>
        BasePlan = 0,

        /// <summary>Add-on de WhatsApp: su ciclo de cobro es independiente y NUNCA contamina el base.</summary>
        WhatsAppAddon = 1
    }

    /// <summary>
    /// Incidente de recuperación de pago recurrente. Tabla separada de <see cref="Suscripcion"/> a
    /// propósito: agrupa el ciclo pago-fallido → gracia → notificación → resolución/suspensión sin
    /// sobrecargar la suscripción, y permite historial. Tenant-scoped (<see cref="ITenantEntity"/>):
    /// hereda el query filter + RLS. NUNCA almacena datos de tarjeta.
    /// </summary>
    public class SubscriptionPaymentIncident : ITenantEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        public Guid SuscripcionId { get; set; }

        /// <summary>Ámbito del incidente. Aditivo: los incidentes existentes son BasePlan (default 0).</summary>
        public PaymentIncidentScope Scope { get; set; } = PaymentIncidentScope.BasePlan;

        /// <summary>Para incidentes de add-on: la fila de <see cref="TenantSubscriptionAddon"/>. Null en base.</summary>
        public Guid? AddonId { get; set; }

        [MaxLength(50)]
        public string? PlanCode { get; set; }

        public int? TilopayRecurringPlanId { get; set; }

        [MaxLength(100)]
        public string? ProviderSubscriptionId { get; set; }

        /// <summary>Correo del cliente usado en la correlación (email+plan). Es el correo del admin del tenant, no dato de tarjeta.</summary>
        [MaxLength(320)]
        public string? ClienteEmail { get; set; }

        [Required]
        public PaymentIncidentStatus Status { get; set; } = PaymentIncidentStatus.Open;

        public DateTime FailureDetectedAtUtc { get; set; }

        public DateTime? GraceEndsAtUtc { get; set; }

        public DateTime? ResolvedAtUtc { get; set; }

        /// <summary>Clave estable para deduplicar reintentos del mismo ciclo (hash tenant+plan+resultcode+ventana).</summary>
        [MaxLength(128)]
        public string? ProviderEventKey { get; set; }

        [MaxLength(40)]
        public string? ProviderResultCode { get; set; }

        [MaxLength(300)]
        public string? ProviderResultMessage { get; set; }

        public int FailureCount { get; set; }

        public int NotificationCount { get; set; }

        public DateTime? LastNotificationAtUtc { get; set; }

        public DateTime? LastReminderAtUtc { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        [ForeignKey("TenantId")]
        public Tenant? Tenant { get; set; }
    }
}
