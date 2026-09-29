using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.SaaS
{
    /// <summary>
    /// Última FOTO del estado real de los suscriptores de add-on WhatsApp del tenant en TiloPay.
    ///
    /// Existe porque BillingHealth solo miraba el estado LOCAL y por eso mostró "riesgo 0" mientras
    /// TiloPay podía tener DOS add-ons activos del mismo tenant (p. ej. WA400 +
    /// WA800). El estado local no puede detectar eso: hay que preguntarle al proveedor.
    ///
    /// Se guarda como snapshot (una fila por tenant, se sobrescribe) para que BillingHealth y
    /// Mission Control lean el riesgo SIN pegarle a TiloPay en cada carga de pantalla. Lo escribe
    /// el sondeo post-rechazo del webhook y el worker de reconciliación.
    ///
    /// Tabla de PLATAFORMA (como <see cref="Models.Platform.PlatformAuditLog"/>): tiene TenantId
    /// pero NO es ITenantEntity, porque se consulta cross-tenant desde la consola de plataforma.
    /// Nunca guarda datos de tarjeta.
    /// </summary>
    public class ProviderAddonAuditSnapshot
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid TenantId { get; set; }

        public DateTime CapturedAtUtc { get; set; }

        /// <summary>Cuántos suscriptores de add-on del tenant PUEDEN seguir cobrando en TiloPay.</summary>
        public int ActiveAddonSubscriberCount { get; set; }

        /// <summary>&gt;= 2 suscriptores cobrables: doble cobro real o inminente.</summary>
        public bool HasDoubleActive { get; set; }

        /// <summary>La consulta al proveedor no fue concluyente (API caído/status desconocido).</summary>
        public bool IsInconclusive { get; set; }

        /// <summary>Planes recurrentes de TiloPay con suscriptor activo, separados por coma (p. ej. "7011,7012").</summary>
        [MaxLength(200)]
        public string? ActiveRecurringPlanIds { get; set; }

        /// <summary>Ids de suscriptor activos, separados por coma. Son ids del proveedor, no datos de tarjeta.</summary>
        [MaxLength(400)]
        public string? ActiveSubscriberIds { get; set; }

        /// <summary>Suscriptor que el estado LOCAL considera el vigente, para comparar contra el proveedor.</summary>
        [MaxLength(100)]
        public string? LocalProviderSubscriptionId { get; set; }

        /// <summary>Qué disparó el sondeo: "webhook-rejected", "reconciliation", "manual".</summary>
        [MaxLength(40)]
        public string Source { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Detail { get; set; }
    }
}
