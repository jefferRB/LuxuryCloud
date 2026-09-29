using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Security;
using LuxuryApp.Services.Tilopay;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Billing
{
    public sealed record PaymentMethodUpdateResult
    {
        public required bool Succeeded { get; init; }

        /// <summary>URL de actualización (recurrentUrl). SOLO en éxito; nunca se persiste ni se loguea.</summary>
        public string? Url { get; init; }

        /// <summary>Mensaje seguro para la UI (sin secretos ni la URL).</summary>
        public string? Message { get; init; }

        /// <summary>True si el camino correcto es un checkout NUEVO (no recurrentUrl).</summary>
        public bool RequiresNewCheckout { get; init; }

        /// <summary>Contrato de body que funcionó ("id_plan" / "id_plan+aliases"). Para diagnóstico.</summary>
        public string? Contract { get; init; }

        /// <summary>
        /// True si la URL se generó SOLO con el contrato de fallback (id_plan+aliases): el enlace es
        /// sospechoso y puede fallar en TiloPay ("suscripción no existe"). La UI de plataforma avisa.
        /// </summary>
        public bool UsedFallbackContract { get; init; }

        public static PaymentMethodUpdateResult Ok(string url, string? contract = null, bool usedFallback = false) =>
            new() { Succeeded = true, Url = url, Contract = contract, UsedFallbackContract = usedFallback };
        public static PaymentMethodUpdateResult Fail(string message, bool requiresNewCheckout = false) =>
            new() { Succeeded = false, Message = message, RequiresNewCheckout = requiresNewCheckout };
    }

    public interface IPaymentMethodUpdateService
    {
        bool IsEnabled { get; }

        /// <summary>
        /// Genera ON-DEMAND la URL de TiloPay para actualizar tarjeta / reintentar el cobro
        /// (recurrentUrl) de la suscripción recurrente del tenant. Valida ownership, estado y que la
        /// URL sea HTTPS del dominio de TiloPay (anti open-redirect). NUNCA almacena la URL.
        /// </summary>
        Task<PaymentMethodUpdateResult> GenerateUpdateUrlAsync(
            Guid tenantId,
            string? email,
            string actorUserId,
            string actorEmail,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Igual que <see cref="GenerateUpdateUrlAsync"/> pero resolviendo el correo del tenant
        /// server-side (para uso de plataforma/soporte, que no debe pasar el email por el formulario).
        /// </summary>
        Task<PaymentMethodUpdateResult> GenerateUpdateUrlForTenantAsync(
            Guid tenantId,
            string actorUserId,
            string actorEmail,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Genera la url_renew para el plan CORRECTO según el SCOPE del incidente: add-on ⇒ plan
        /// recurrente del add-on; base ⇒ plan recurrente base. Nunca mezcla el suscriptor base con el
        /// del add-on. Lo usa la consola de recuperación de plataforma (por incidente).
        /// </summary>
        Task<PaymentMethodUpdateResult> GenerateUpdateUrlForIncidentAsync(
            Guid incidentId,
            string actorUserId,
            string actorEmail,
            CancellationToken cancellationToken = default);
    }

    public sealed class PaymentMethodUpdateService : IPaymentMethodUpdateService
    {
        private readonly ApplicationDbContext _db;
        private readonly ITilopayRepeatAdminService _adminService;
        private readonly IBusinessDateTimeProvider _clock;
        private readonly ILogger<PaymentMethodUpdateService> _logger;
        private readonly Tenant.ITenantOwnerResolver _ownerResolver;

        public PaymentMethodUpdateService(
            ApplicationDbContext db,
            ITilopayRepeatAdminService adminService,
            IBusinessDateTimeProvider clock,
            ILogger<PaymentMethodUpdateService> logger,
            Tenant.ITenantOwnerResolver ownerResolver)
        {
            _db = db;
            _adminService = adminService;
            _clock = clock;
            _logger = logger;
            _ownerResolver = ownerResolver;
        }

        public bool IsEnabled => _adminService.IsEnabled;

        public async Task<PaymentMethodUpdateResult> GenerateUpdateUrlForTenantAsync(
            Guid tenantId,
            string actorUserId,
            string actorEmail,
            CancellationToken cancellationToken = default)
        {
            // Correo del admin del tenant resuelto server-side (no se confía en el formulario).
            // El orden alfabético anterior podía elegir una cuenta de FUNCIONARIO por encima del
            // administrador; ahora manda la regla de owner (admin > funcionario).
            var email = await _ownerResolver.ResolveOwnerEmailAsync(tenantId, cancellationToken);

            return await GenerateUpdateUrlAsync(tenantId, email, actorUserId, actorEmail, cancellationToken);
        }

        public async Task<PaymentMethodUpdateResult> GenerateUpdateUrlAsync(
            Guid tenantId,
            string? email,
            string actorUserId,
            string actorEmail,
            CancellationToken cancellationToken = default)
        {
            if (!_adminService.IsEnabled)
            {
                return PaymentMethodUpdateResult.Fail("La actualización de tarjeta en línea no está disponible en este momento. Contactá soporte.");
            }

            if (tenantId == Guid.Empty || string.IsNullOrWhiteSpace(email))
            {
                return PaymentMethodUpdateResult.Fail("No pudimos generar el enlace de actualización. Contactá soporte.");
            }

            var subscription = await _db.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => s.TenantId == tenantId && s.TilopayRecurringPlanId != null)
                .OrderByDescending(s => s.FechaUltimaActualizacionUtc ?? s.FechaInicio)
                .Select(s => new
                {
                    s.TilopayRecurringPlanId,
                    s.ProviderSubscriptionId,
                    s.Estado,
                    s.PaymentRecoveryStatus,
                    s.CancelAtPeriodEnd,
                    s.CancellationEffectiveAtUtc,
                    s.FechaFin,
                    s.ProviderExpiresAtUtc
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (subscription?.TilopayRecurringPlanId is not { } recurringPlanId)
            {
                return PaymentMethodUpdateResult.Fail(
                    "No encontramos una suscripción recurrente para actualizar. Elegí un plan para continuar.",
                    requiresNewCheckout: true);
            }

            // Renovación cancelada: hay que reactivar primero (si vigente) o suscribirse de nuevo (si venció).
            if (subscription.CancelAtPeriodEnd)
            {
                var effectiveEndUtc = subscription.CancellationEffectiveAtUtc
                    ?? SubscriptionEffectiveDates.GetEffectiveEndUtc(subscription.FechaFin, subscription.ProviderExpiresAtUtc);

                if (effectiveEndUtc is { } end && end > GetUtcNow())
                {
                    return PaymentMethodUpdateResult.Fail("Tu renovación está cancelada. Reactivá tu renovación antes de actualizar el método de pago.");
                }

                return PaymentMethodUpdateResult.Fail(
                    "Tu suscripción finalizó. Iniciá una nueva suscripción para continuar.",
                    requiresNewCheckout: true);
            }

            if (subscription.Estado == EstadoSuscripcion.Cancelada)
            {
                return PaymentMethodUpdateResult.Fail(
                    "Tu suscripción está cancelada. Iniciá una nueva suscripción para continuar.",
                    requiresNewCheckout: true);
            }

            // url_renew de TiloPay COBRA/renueva al suscriptor: NO es "actualizar tarjeta" sin cobro.
            // Solo se permite desde el cliente cuando hay un pago por regularizar (recovery). En una
            // cuenta activa/vigente se bloquea con mensaje controlado (evita el cobro sorpresa).
            var inRecovery = subscription.Estado == EstadoSuscripcion.Morosa ||
                             subscription.PaymentRecoveryStatus is "GraceActive" or "GraceExpired" or "Suspended";
            if (!inRecovery)
            {
                return PaymentMethodUpdateResult.Fail(
                    "Para cambiar la tarjeta sin adelantar un cobro, contactá soporte. Este enlace regulariza un pago pendiente y solo está disponible cuando tenés un cobro por resolver.");
            }

            return await GenerateForPlanAsync(tenantId, recurringPlanId, email, "base", actorUserId, actorEmail, cancellationToken);
        }

        public async Task<PaymentMethodUpdateResult> GenerateUpdateUrlForIncidentAsync(
            Guid incidentId,
            string actorUserId,
            string actorEmail,
            CancellationToken cancellationToken = default)
        {
            if (!_adminService.IsEnabled)
            {
                return PaymentMethodUpdateResult.Fail("La actualización de tarjeta en línea no está disponible en este momento. Contactá soporte.");
            }

            var incident = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(i => i.Id == incidentId)
                .Select(i => new { i.TenantId, i.Scope, i.AddonId })
                .FirstOrDefaultAsync(cancellationToken);

            if (incident is null || incident.TenantId == Guid.Empty)
            {
                return PaymentMethodUpdateResult.Fail("No encontramos el incidente indicado.");
            }

            var email = await ResolveTenantEmailAsync(incident.TenantId, cancellationToken);
            if (string.IsNullOrWhiteSpace(email))
            {
                return PaymentMethodUpdateResult.Fail("No pudimos generar el enlace de actualización. Contactá soporte.");
            }

            // El scope del incidente decide QUÉ plan recurrente usa la update URL. NUNCA se mezcla el
            // suscriptor base con el del add-on: para add-on se usa el TilopayRecurringPlanId del add-on,
            // para base el de la suscripción base. recurrentUrl solo necesita (id=plan, email), sin id_suscriptor.
            if (incident.Scope == PaymentIncidentScope.WhatsAppAddon)
            {
                var addonPlanId = await _db.TenantSubscriptionAddons
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(a => a.TenantId == incident.TenantId &&
                                (incident.AddonId == null || a.Id == incident.AddonId) &&
                                a.TilopayRecurringPlanId != null)
                    .OrderByDescending(a => a.UpdatedAtUtc)
                    .Select(a => a.TilopayRecurringPlanId)
                    .FirstOrDefaultAsync(cancellationToken);

                if (addonPlanId is not { } addonRecurringPlanId)
                {
                    return PaymentMethodUpdateResult.Fail("El add-on del incidente no tiene un plan recurrente para actualizar el método de pago.");
                }

                return await GenerateForPlanAsync(incident.TenantId, addonRecurringPlanId, email, "add-on", actorUserId, actorEmail, cancellationToken);
            }

            var basePlanId = await _db.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => s.TenantId == incident.TenantId && s.TilopayRecurringPlanId != null)
                .OrderByDescending(s => s.FechaUltimaActualizacionUtc ?? s.FechaInicio)
                .Select(s => s.TilopayRecurringPlanId)
                .FirstOrDefaultAsync(cancellationToken);

            if (basePlanId is not { } baseRecurringPlanId)
            {
                return PaymentMethodUpdateResult.Fail(
                    "No encontramos una suscripción recurrente base para actualizar.",
                    requiresNewCheckout: true);
            }

            return await GenerateForPlanAsync(incident.TenantId, baseRecurringPlanId, email, "base", actorUserId, actorEmail, cancellationToken);
        }

        /// <summary>
        /// Núcleo: genera + valida + audita la recurrentUrl para un plan recurrente concreto (base o
        /// add-on) y un email. Usa url_renew (contrato oficial "id"). Si no hay url_renew utilizable o
        /// no es un dominio TiloPay, audita el fallo y devuelve un mensaje controlado (nunca url_register
        /// como sustituto silencioso). NUNCA almacena ni loguea la URL completa.
        /// </summary>
        private async Task<PaymentMethodUpdateResult> GenerateForPlanAsync(
            Guid tenantId,
            int recurringPlanId,
            string email,
            string scopeLabel,
            string actorUserId,
            string actorEmail,
            CancellationToken cancellationToken)
        {
            var nowUtc = GetUtcNow();
            var result = await _adminService.GetRecurrentUrlAsync(recurringPlanId, email, cancellationToken);

            if (!result.Succeeded || string.IsNullOrWhiteSpace(result.Url) || !IsSafeTilopayUrl(result.Url))
            {
                _db.PlatformAuditLogs.Add(BuildAudit(
                    PlatformAuditActions.PaymentMethodUpdateUrlFailed,
                    tenantId, actorUserId, actorEmail,
                    $"No se pudo generar/validar la url_renew ({scopeLabel}). Plan {recurringPlanId}. UrlValida {(!string.IsNullOrWhiteSpace(result.Url) && IsSafeTilopayUrl(result.Url))}.",
                    nowUtc));
                await _db.SaveChangesAsync(cancellationToken);

                _logger.LogWarning(
                    "url_renew no disponible/insegura para actualizar tarjeta ({Scope}). TenantId {TenantId}. PlanId {PlanId}.",
                    scopeLabel, tenantId, recurringPlanId);
                return PaymentMethodUpdateResult.Fail("No pudimos generar el enlace de actualización. Contactá soporte.");
            }

            // Compat: si por config de diagnóstico se usara un contrato de fallback, se audita distinto
            // para no ocultarlo. Con el contrato oficial "id" esto es siempre false.
            var diag = result.RecurrentDiagnostics;
            var usedFallback = diag?.UsedFallbackContract ?? string.Equals(result.Contract, "id_plan+aliases", StringComparison.Ordinal);

            var diagDetail = diag is null
                ? string.Empty
                : $" Status {diag.HttpStatus}. Type {diag.ProviderType ?? "-"}. Message {diag.ProviderMessage ?? "-"}. " +
                  $"HasRenew {diag.HasUrlRenew}. HasRegister {diag.HasUrlRegister}. Field {diag.SelectedField ?? "-"}. UrlHostPath {diag.UrlHostPathMasked ?? "-"}.";

            _db.PlatformAuditLogs.Add(BuildAudit(
                usedFallback
                    ? PlatformAuditActions.PaymentMethodUpdateUrlGeneratedWithFallback
                    : PlatformAuditActions.PaymentMethodUpdateUrlGenerated,
                tenantId, actorUserId, actorEmail,
                $"url_renew generada para actualizar método de pago ({scopeLabel}). Plan {recurringPlanId}. Contract {result.Contract ?? "n/a"}. " +
                $"ProviderContractFallback {usedFallback}.{diagDetail} EmailMasked {SensitiveDataMasker.MaskEmail(email)}.",
                nowUtc));
            await _db.SaveChangesAsync(cancellationToken);

            return PaymentMethodUpdateResult.Ok(result.Url!, result.Contract, usedFallback);
        }

        /// <summary>Contacto del tenant por regla de owner (admin &gt; funcionario), no alfabético.</summary>
        private Task<string?> ResolveTenantEmailAsync(Guid tenantId, CancellationToken cancellationToken) =>
            _ownerResolver.ResolveOwnerEmailAsync(tenantId, cancellationToken);

        /// <summary>Anti open-redirect: solo se acepta redirigir a HTTPS del dominio de TiloPay.</summary>
        private static bool IsSafeTilopayUrl(string? url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            var host = uri.Host;
            return host.Equals("tilopay.com", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".tilopay.com", StringComparison.OrdinalIgnoreCase);
        }

        private PlatformAuditLog BuildAudit(string action, Guid tenantId, string actorUserId, string actorEmail, string reason, DateTime nowUtc) =>
            new()
            {
                Id = Guid.NewGuid(),
                ActorUserId = string.IsNullOrWhiteSpace(actorUserId) ? "system" : actorUserId,
                ActorEmail = string.IsNullOrWhiteSpace(actorEmail) ? "system" : actorEmail,
                Action = action,
                EntityType = PlatformAuditEntityTypes.Subscription,
                TenantId = tenantId,
                Reason = reason.Length <= 500 ? reason : reason[..500],
                CreatedAtUtc = nowUtc
            };

        private DateTime GetUtcNow() => _clock.NowOffset().UtcDateTime;
    }
}
