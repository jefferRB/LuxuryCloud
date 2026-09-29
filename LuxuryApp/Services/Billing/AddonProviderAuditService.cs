using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Security;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.Tilopay;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Billing
{
    /// <summary>Suscriptor de add-on que TiloPay puede seguir cobrando.</summary>
    public sealed record ChargeableAddonSubscriber(
        int RecurringPlanId,
        string SubscriberId,
        string? Status,
        DateTime? ExpiresAtUtc);

    public sealed record AddonProviderAuditResult
    {
        public required bool Executed { get; init; }

        /// <summary>Suscriptores de add-on del tenant que TiloPay PUEDE seguir cobrando.</summary>
        public IReadOnlyList<ChargeableAddonSubscriber> Chargeable { get; init; } = Array.Empty<ChargeableAddonSubscriber>();

        /// <summary>Alguna consulta falló o algún status es desconocido: NO se asume proveedor sano.</summary>
        public bool IsInconclusive { get; init; }

        public string? Detail { get; init; }

        public int ChargeableCount => Chargeable.Count;

        public bool HasDoubleActive => Chargeable.Count > 1;

        public static AddonProviderAuditResult NotExecuted(string detail) =>
            new() { Executed = false, Detail = detail };
    }

    /// <summary>
    /// Pregunta a TiloPay CUÁNTOS suscriptores de add-on de WhatsApp puede cobrarle a un tenant, y
    /// deja el resultado persistido (<see cref="ProviderAddonAuditSnapshot"/> + auditoría +
    /// incidente) para que BillingHealth/Mission Control no dependan de una llamada HTTP.
    ///
    /// Regresión que cubre: el webhook de un downgrade de add-on (WA800→WA400) se rechaza
    /// correctamente por monto y el estado LOCAL queda intacto y sano… pero el proveedor puede
    /// haber dejado WA400 y WA800 ACTIVOS a la vez. Si el health mirara solo lo local, el
    /// tablero mostraría riesgo 0 mientras el proveedor tiene doble cobro montado.
    ///
    /// Regla: rechazar un webhook protege el estado local, NO deshace lo que el proveedor ya hizo.
    /// Después de cada rechazo hay que preguntarle al proveedor. Solo lectura: nunca cancela nada.
    /// </summary>
    public interface IAddonProviderAuditService
    {
        bool IsEnabled { get; }

        /// <summary>
        /// Consulta getSuscriptorRepeat de TODOS los planes de add-on, cuenta los cobrables del
        /// tenant y persiste el snapshot. Con 2 o más deja auditoría CRÍTICA + incidente de add-on.
        /// Nunca lanza: cualquier fallo se reporta como no concluyente.
        /// </summary>
        Task<AddonProviderAuditResult> AuditAsync(
            Guid tenantId,
            string? customerEmail,
            string source,
            string auditAction,
            CancellationToken cancellationToken = default);
    }

    public sealed class AddonProviderAuditService : IAddonProviderAuditService
    {
        /// <summary>Código de resultado del incidente. Es la marca que BillingHealth y el repair buscan.</summary>
        public const string DoubleActiveResultCode = "PROVIDER_DOUBLE_ACTIVE";

        private readonly ApplicationDbContext _db;
        private readonly ITilopayRepeatAdminService _adminService;
        private readonly TilopayRepeatOptions _repeatOptions;
        private readonly ITenantExecutionContextAccessor _tenantExecutionContextAccessor;
        private readonly IBusinessDateTimeProvider _clock;
        private readonly ILogger<AddonProviderAuditService> _logger;

        public AddonProviderAuditService(
            ApplicationDbContext db,
            ITilopayRepeatAdminService adminService,
            IOptions<TilopayRepeatOptions> repeatOptions,
            ITenantExecutionContextAccessor tenantExecutionContextAccessor,
            IBusinessDateTimeProvider clock,
            ILogger<AddonProviderAuditService> logger)
        {
            _db = db;
            _adminService = adminService;
            _repeatOptions = repeatOptions.Value;
            _tenantExecutionContextAccessor = tenantExecutionContextAccessor;
            _clock = clock;
            _logger = logger;
        }

        public bool IsEnabled => _adminService.IsEnabled;

        public async Task<AddonProviderAuditResult> AuditAsync(
            Guid tenantId,
            string? customerEmail,
            string source,
            string auditAction,
            CancellationToken cancellationToken = default)
        {
            if (tenantId == Guid.Empty)
            {
                return AddonProviderAuditResult.NotExecuted("TenantId vacío.");
            }

            if (!_adminService.IsEnabled)
            {
                return AddonProviderAuditResult.NotExecuted(
                    "El API admin de TiloPay está deshabilitado: no se puede auditar el estado del proveedor.");
            }

            var addonPlanIds = _repeatOptions.GetAllPlans()
                .Where(registration => registration.Plan.IsAddon && registration.Plan.TilopayPlanId > 0)
                .Select(registration => registration.Plan.TilopayPlanId)
                .Distinct()
                .ToList();

            if (addonPlanIds.Count == 0)
            {
                return AddonProviderAuditResult.NotExecuted("No hay planes de add-on configurados en TilopayRepeat.");
            }

            var email = await ResolveEmailAsync(tenantId, customerEmail, cancellationToken);
            if (string.IsNullOrWhiteSpace(email))
            {
                return AddonProviderAuditResult.NotExecuted(
                    "No se pudo resolver el correo del tenant para auditar el proveedor.");
            }

            // ── HTTP primero, SIEMPRE fuera de cualquier transacción de BD ──
            var chargeable = new List<ChargeableAddonSubscriber>();
            var inconclusiveReasons = new List<string>();

            foreach (var planId in addonPlanIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var subscribers = await _adminService.GetSuscriptorRepeatAsync(planId, cancellationToken);

                    foreach (var subscriber in subscribers)
                    {
                        if (!string.Equals(
                                NormalizeEmail(subscriber.Email),
                                NormalizeEmail(email),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!ProviderSubscriberStatusRules.MayStillCharge(subscriber.Status))
                        {
                            continue;
                        }

                        // Un status que no sabemos clasificar cuenta como cobrable (lado seguro) pero
                        // marca la auditoría como no concluyente: no se declara sano lo que no se entiende.
                        if (ProviderSubscriberStatusRules.Classify(subscriber.Status) == ProviderSubscriberState.Unknown)
                        {
                            inconclusiveReasons.Add(
                                $"plan {planId}: status {ProviderSubscriberStatusRules.Sanitize(subscriber.Status)}");
                        }

                        chargeable.Add(new ChargeableAddonSubscriber(
                            planId,
                            subscriber.SubscriberId ?? string.Empty,
                            subscriber.Status,
                            subscriber.ExpiresAtUtc));
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    inconclusiveReasons.Add($"plan {planId}: {Trim(ex.Message, 80)}");
                    _logger.LogWarning(
                        ex,
                        "Auditoría de add-on en TiloPay falló para el plan {PlanId}. TenantId {TenantId}.",
                        planId,
                        tenantId);
                }
            }

            var isInconclusive = inconclusiveReasons.Count > 0;
            var detail = BuildDetail(chargeable, inconclusiveReasons);

            var result = new AddonProviderAuditResult
            {
                Executed = true,
                Chargeable = chargeable,
                IsInconclusive = isInconclusive,
                Detail = detail
            };

            await PersistAsync(tenantId, result, source, auditAction, cancellationToken);

            return result;
        }

        private async Task PersistAsync(
            Guid tenantId,
            AddonProviderAuditResult result,
            string source,
            string auditAction,
            CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _tenantExecutionContextAccessor.BeginScope(tenantId);
                var nowUtc = _clock.NowOffset().UtcDateTime;

                var addon = await _db.TenantSubscriptionAddons
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(a => a.TenantId == tenantId)
                    .Select(a => new { a.Id, a.AddonCode, a.ProviderSubscriptionId, a.TilopayRecurringPlanId })
                    .FirstOrDefaultAsync(cancellationToken);

                var snapshot = await _db.ProviderAddonAuditSnapshots
                    .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

                if (snapshot is null)
                {
                    snapshot = new ProviderAddonAuditSnapshot { Id = Guid.NewGuid(), TenantId = tenantId };
                    _db.ProviderAddonAuditSnapshots.Add(snapshot);
                }

                snapshot.CapturedAtUtc = nowUtc;
                snapshot.ActiveAddonSubscriberCount = result.ChargeableCount;
                snapshot.HasDoubleActive = result.HasDoubleActive;
                snapshot.IsInconclusive = result.IsInconclusive;
                snapshot.ActiveRecurringPlanIds = Trim(
                    string.Join(",", result.Chargeable.Select(c => c.RecurringPlanId).Distinct()),
                    200);
                snapshot.ActiveSubscriberIds = Trim(
                    string.Join(",", result.Chargeable.Select(c => c.SubscriberId)),
                    400);
                snapshot.LocalProviderSubscriptionId = Trim(addon?.ProviderSubscriptionId, 100);
                snapshot.Source = Trim(source, 40) ?? "manual";
                snapshot.Detail = Trim(result.Detail, 500);

                if (result.HasDoubleActive)
                {
                    _db.PlatformAuditLogs.Add(new PlatformAuditLog
                    {
                        Id = Guid.NewGuid(),
                        ActorUserId = "system",
                        ActorEmail = "system",
                        Action = auditAction,
                        EntityType = PlatformAuditEntityTypes.WhatsAppAddon,
                        EntityId = addon?.Id.ToString(),
                        TenantId = tenantId,
                        Reason = Trim(
                            $"CRÍTICO: TiloPay tiene {result.ChargeableCount} suscriptores de add-on COBRABLES para este tenant. Riesgo de doble cobro del add-on. {result.Detail}",
                            500)!,
                        CreatedAtUtc = nowUtc
                    });

                    await UpsertDoubleActiveIncidentAsync(tenantId, addon?.Id, addon?.AddonCode, result, nowUtc, cancellationToken);
                }
                else if (result.IsInconclusive)
                {
                    _db.PlatformAuditLogs.Add(new PlatformAuditLog
                    {
                        Id = Guid.NewGuid(),
                        ActorUserId = "system",
                        ActorEmail = "system",
                        Action = PlatformAuditActions.AddonProviderAuditInconclusive,
                        EntityType = PlatformAuditEntityTypes.WhatsAppAddon,
                        EntityId = addon?.Id.ToString(),
                        TenantId = tenantId,
                        Reason = Trim(
                            $"La auditoría del proveedor no fue concluyente: no se puede afirmar que el add-on esté sano en TiloPay. {result.Detail}",
                            500)!,
                        CreatedAtUtc = nowUtc
                    });
                }

                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "No se pudo persistir la auditoría del proveedor del add-on. TenantId {TenantId}.",
                    tenantId);
            }
        }

        /// <summary>
        /// Un incidente ABIERTO por tenant para el doble activo: se actualiza en vez de duplicar, y
        /// se queda abierto hasta que un humano repare el proveedor. Queda en
        /// <see cref="PaymentIncidentStatus.ManualReview"/> porque no es un impago: es dinero de más.
        /// </summary>
        private async Task UpsertDoubleActiveIncidentAsync(
            Guid tenantId,
            Guid? addonId,
            string? addonCode,
            AddonProviderAuditResult result,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var existing = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .Where(incident =>
                    incident.TenantId == tenantId &&
                    incident.Scope == PaymentIncidentScope.WhatsAppAddon &&
                    incident.ProviderResultCode == DoubleActiveResultCode &&
                    (incident.Status == PaymentIncidentStatus.Open ||
                     incident.Status == PaymentIncidentStatus.ManualReview))
                .OrderByDescending(incident => incident.UpdatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is not null)
            {
                existing.FailureCount += 1;
                existing.ProviderResultMessage = Trim(result.Detail, 300);
                existing.UpdatedAtUtc = nowUtc;
                return;
            }

            _db.SubscriptionPaymentIncidents.Add(new SubscriptionPaymentIncident
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Scope = PaymentIncidentScope.WhatsAppAddon,
                AddonId = addonId,
                SuscripcionId = Guid.Empty, // no aplica al add-on (columna escalar, sin FK)
                PlanCode = Trim(addonCode, 50),
                TilopayRecurringPlanId = result.Chargeable.FirstOrDefault()?.RecurringPlanId,
                ProviderSubscriptionId = Trim(result.Chargeable.FirstOrDefault()?.SubscriberId, 100),
                Status = PaymentIncidentStatus.ManualReview,
                FailureDetectedAtUtc = nowUtc,
                ProviderResultCode = DoubleActiveResultCode,
                ProviderResultMessage = Trim(result.Detail, 300),
                FailureCount = 1,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            });
        }

        private async Task<string?> ResolveEmailAsync(
            Guid tenantId,
            string? customerEmail,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(customerEmail))
            {
                return customerEmail;
            }

            return await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(payment => payment.TenantId == tenantId && payment.ClienteEmail != null)
                .OrderByDescending(payment => payment.FechaCreacionUtc)
                .Select(payment => payment.ClienteEmail)
                .FirstOrDefaultAsync(cancellationToken);
        }

        private static string BuildDetail(
            IReadOnlyList<ChargeableAddonSubscriber> chargeable,
            IReadOnlyList<string> inconclusiveReasons)
        {
            var subscribers = chargeable.Count == 0
                ? "sin suscriptores cobrables"
                : string.Join(
                    "; ",
                    chargeable.Select(c =>
                        $"plan {c.RecurringPlanId} sub {SensitiveDataMasker.MaskReference(c.SubscriberId)} status {ProviderSubscriberStatusRules.Sanitize(c.Status)}"));

            return inconclusiveReasons.Count == 0
                ? subscribers
                : $"{subscribers}. No concluyente: {string.Join("; ", inconclusiveReasons)}";
        }

        private static string NormalizeEmail(string? email) => email?.Trim().ToLowerInvariant() ?? string.Empty;

        private static string? Trim(string? value, int max) =>
            string.IsNullOrEmpty(value) ? value : (value.Length <= max ? value : value[..max]);
    }
}
