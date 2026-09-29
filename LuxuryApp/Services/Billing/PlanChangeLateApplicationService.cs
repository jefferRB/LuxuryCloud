using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Security;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.Tilopay;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Billing
{
    public enum LatePlanChangeApplicationStatus
    {
        /// <summary>No hay nada que aplicar: sin intent pendiente, sin pago confirmado, o ya aplicado.</summary>
        NotApplicable,

        /// <summary>Cambio aplicado y suscripción apuntando al suscriptor nuevo.</summary>
        Applied,

        /// <summary>El plan destino todavía no muestra un suscriptor activo: se reintenta luego.</summary>
        LeftPendingNoActiveSubscriber,

        /// <summary>Ambiguo o contradictorio: no se aplica nada y se alerta.</summary>
        ManualReview,

        /// <summary>No se pudo consultar a TiloPay: se reintenta luego.</summary>
        ProviderUnavailable
    }

    public sealed record LatePlanChangeApplicationResult
    {
        public required LatePlanChangeApplicationStatus Status { get; init; }
        public required string Message { get; init; }
        public Guid? IntentId { get; init; }

        public static LatePlanChangeApplicationResult NotApplicable(string message) =>
            new() { Status = LatePlanChangeApplicationStatus.NotApplicable, Message = message };
    }

    public interface IPlanChangeLateApplicationService
    {
        /// <summary>
        /// Aplica un cambio de plan cuyo pago YA está confirmado pero cuyo id_suscriptor nuevo se
        /// conoció tarde. Idempotente: si el intent ya está aplicado o el pago no está confirmado,
        /// no hace nada. Nunca crea pagos ni checkouts.
        /// </summary>
        Task<LatePlanChangeApplicationResult> ApplyPendingPlanChangeAfterSubscriberResolvedAsync(
            Guid paymentId,
            string source,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Cierra el hueco de ORDEN entre "el pago se confirmó" y "conocemos el suscriptor nuevo".
    ///
    /// TiloPay no manda el id_suscriptor en el webhook: se resuelve después, por getSuscriptorRepeat.
    /// La aprobación del pago corre dentro de una transacción financiera y, sin suscriptor nuevo,
    /// se niega a aplicar el plan (aplicarlo dejaría la suscripción apuntando al suscriptor VIEJO:
    /// doble cobro invisible). Ese guard es correcto y se mantiene. Lo que faltaba era volver a
    /// intentar la aplicación cuando el suscriptor aparece, unos segundos más tarde.
    ///
    /// Regresión que cubre (downgrade LC_M_03 → LC_M_02): el pago quedó Confirmado, el suscriptor
    /// nuevo se resolvió acto seguido... y nadie aplicó el cambio. El cliente quedó pagando
    /// LC_M_02 mientras la app mostraba LC_M_03, con DOS suscriptores activos en el proveedor.
    ///
    /// No reimplementa nada: reutiliza ActivarSuscripcionRecurrenteAsync, ApplyAppliedAsync y
    /// TryCancelOldSubscriberForUpgradeAsync. El único aporte es correr esas piezas en el momento
    /// correcto y con verificación previa contra el proveedor.
    /// </summary>
    public sealed class PlanChangeLateApplicationService : IPlanChangeLateApplicationService
    {
        private readonly ApplicationDbContext _db;
        private readonly SuscripcionService _suscripcionService;
        private readonly IPlanChangeService _planChangeService;
        private readonly ITenantExecutionContextAccessor _tenantExecutionContextAccessor;
        private readonly IBusinessDateTimeProvider _clock;
        private readonly ITilopayRepeatAdminService _adminService;
        private readonly IProviderSubscriptionManager? _providerSubscriptionManager;
        private readonly ILogger<PlanChangeLateApplicationService> _logger;

        public PlanChangeLateApplicationService(
            ApplicationDbContext db,
            SuscripcionService suscripcionService,
            IPlanChangeService planChangeService,
            ITenantExecutionContextAccessor tenantExecutionContextAccessor,
            IBusinessDateTimeProvider clock,
            ITilopayRepeatAdminService adminService,
            ILogger<PlanChangeLateApplicationService> logger,
            IProviderSubscriptionManager? providerSubscriptionManager = null)
        {
            _db = db;
            _suscripcionService = suscripcionService;
            _planChangeService = planChangeService;
            _tenantExecutionContextAccessor = tenantExecutionContextAccessor;
            _clock = clock;
            _adminService = adminService;
            _providerSubscriptionManager = providerSubscriptionManager;
            _logger = logger;
        }

        public async Task<LatePlanChangeApplicationResult> ApplyPendingPlanChangeAfterSubscriberResolvedAsync(
            Guid paymentId,
            string source,
            CancellationToken cancellationToken = default)
        {
            if (!_adminService.IsEnabled)
            {
                return LatePlanChangeApplicationResult.NotApplicable("La integración admin de TiloPay está deshabilitada.");
            }

            // Lectura cross-tenant mínima para saber a qué tenant pertenece el trabajo.
            var payment = await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

            if (payment is null || payment.TenantId == Guid.Empty)
            {
                return LatePlanChangeApplicationResult.NotApplicable("El pago indicado no existe.");
            }

            // Guardas de dinero: solo un pago CONFIRMADO con suscriptor conocido puede aplicar.
            if (payment.Estado != EstadoPagoProveedor.Confirmado)
            {
                return LatePlanChangeApplicationResult.NotApplicable("El pago todavía no está confirmado.");
            }

            if (string.IsNullOrWhiteSpace(payment.ProviderSubscriberId))
            {
                return LatePlanChangeApplicationResult.NotApplicable("El pago aún no tiene id_suscriptor resuelto.");
            }

            using var tenantScope = _tenantExecutionContextAccessor.BeginScope(payment.TenantId);

            var intent = await _db.PlanChangeIntents
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    i => i.PagoSuscripcionId == paymentId &&
                         i.TenantId == payment.TenantId &&
                         i.Estado == PlanChangeIntentState.Pending,
                    cancellationToken);

            if (intent is null)
            {
                // Idempotencia: si ya se aplicó (o nunca fue un cambio), no hay nada que hacer.
                return LatePlanChangeApplicationResult.NotApplicable(
                    "No hay un cambio de plan pendiente asociado a ese pago.");
            }

            if (payment.TilopayRecurringPlanId != intent.ToTilopayRecurringPlanId)
            {
                // El pago no corresponde al destino del intent: no es este cambio.
                return LatePlanChangeApplicationResult.NotApplicable(
                    "El pago no corresponde al plan destino del cambio pendiente.");
            }

            var newSubscriberId = payment.ProviderSubscriberId!.Trim();

            // ── Verificación contra el proveedor ANTES de tocar la suscripción ──
            // La regla de seguridad no se relaja: sin un suscriptor ACTIVO y sin ambigüedad en el
            // plan destino, no se aplica nada.
            var verification = await VerifyNewSubscriberAsync(intent, payment, newSubscriberId, cancellationToken);
            if (verification is not null)
            {
                return verification;
            }

            var targetPlan = await _db.Planes
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == intent.ToPlanId, cancellationToken);

            if (targetPlan is null)
            {
                return await ManualReviewAsync(
                    intent,
                    payment.TenantId,
                    $"El plan destino {intent.ToPlanCode} ya no existe en el catálogo local.",
                    cancellationToken);
            }

            var oldSubscriberId = intent.FromProviderSubscriptionId;

            // ── Aplicación: las MISMAS piezas del camino normal, solo que ahora ──
            await _suscripcionService.ActivarSuscripcionRecurrenteAsync(
                payment.TenantId,
                targetPlan,
                intent.ToTilopayRecurringPlanId,
                newSubscriberId,
                payment.ProviderTransactionId,
                payment.ProviderReference ?? payment.ReferenciaInterna,
                motivo: $"Cambio de plan aplicado al resolverse el id_suscriptor nuevo ({source}).",
                cancellationToken: cancellationToken);

            // El ciclo arranca cuando el cliente PAGÓ, no cuando nosotros nos enteramos: si la
            // reparación corre horas después, cobrarle desde hoy le robaría esas horas.
            AlignBillingPeriodToPayment(payment, targetPlan, payment.TenantId);

            await _planChangeService.ApplyAppliedAsync(
                payment.TenantId,
                intent.ToTilopayRecurringPlanId,
                newSubscriberId,
                cancellationToken);

            _db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = "system",
                ActorEmail = "system",
                Action = string.Equals(source, "reconciliation", StringComparison.OrdinalIgnoreCase)
                    ? PlatformAuditActions.PlanChangeConfirmedPaymentWithLateSubscriberRepaired
                    : PlatformAuditActions.PlanChangeAppliedAfterLateSubscriberResolution,
                EntityType = PlatformAuditEntityTypes.Subscription,
                EntityId = intent.Id.ToString(),
                TenantId = payment.TenantId,
                Reason = Trim(
                    $"Cambio {intent.FromPlanCode} → {intent.ToPlanCode} aplicado tras resolverse el id_suscriptor nuevo ({source}). " +
                    $"NuevoSuffix {SensitiveDataMasker.MaskReference(newSubscriberId)} verificado ACTIVO en el plan {intent.ToTilopayRecurringPlanId}. " +
                    $"ViejoSuffix {SensitiveDataMasker.MaskReference(oldSubscriberId)} queda por cancelar. " +
                    $"Ciclo desde la confirmación del pago ({payment.FechaConfirmacionUtc:yyyy-MM-dd HH:mm} UTC).",
                    500),
                CreatedAtUtc = GetUtcNow()
            });

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Cambio de plan aplicado tras resolución tardía del suscriptor. TenantId {TenantId}. IntentId {IntentId}. From {From} To {To}. Source {Source}.",
                payment.TenantId,
                intent.Id,
                intent.FromPlanCode,
                intent.ToPlanCode,
                source);

            // Cancelar el viejo: mismo camino idempotente y verificado del flujo normal. Si falla,
            // el intent queda PendingManualCancellation y el worker de reintento lo persigue.
            await TryCancelOldSubscriberAsync(payment.TenantId, intent.Id, cancellationToken);

            return new LatePlanChangeApplicationResult
            {
                Status = LatePlanChangeApplicationStatus.Applied,
                Message = $"Cambio a {intent.ToPlanCode} aplicado con el suscriptor nuevo.",
                IntentId = intent.Id
            };
        }

        /// <summary>
        /// Devuelve null si el suscriptor nuevo es válido; si no, el resultado que corresponde.
        /// Tabla: 1 activo que coincide con el pago ⇒ aplicar; 0 activos ⇒ esperar; varios activos,
        /// status desconocido, o activo que NO coincide con el pago ⇒ revisión manual.
        /// </summary>
        private async Task<LatePlanChangeApplicationResult?> VerifyNewSubscriberAsync(
            PlanChangeIntent intent,
            PagoSuscripcion payment,
            string newSubscriberId,
            CancellationToken cancellationToken)
        {
            TargetSubscriberAssessment assessment;
            try
            {
                assessment = await _adminService.AssessTargetSubscribersAsync(
                    intent.ToTilopayRecurringPlanId,
                    payment.ClienteEmail,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "No se pudo verificar el suscriptor nuevo contra TiloPay. TenantId {TenantId}. IntentId {IntentId}.",
                    payment.TenantId,
                    intent.Id);

                return new LatePlanChangeApplicationResult
                {
                    Status = LatePlanChangeApplicationStatus.ProviderUnavailable,
                    Message = "No se pudo verificar el suscriptor nuevo contra TiloPay; se reintentará.",
                    IntentId = intent.Id
                };
            }

            switch (assessment.Verdict)
            {
                case TargetSubscriberVerdict.ProviderError:
                    return new LatePlanChangeApplicationResult
                    {
                        Status = LatePlanChangeApplicationStatus.ProviderUnavailable,
                        Message = $"Verificación no concluyente: {assessment.Detail}",
                        IntentId = intent.Id
                    };

                case TargetSubscriberVerdict.Free:
                    // El pago está confirmado pero el plan destino no muestra a nadie cobrando aún
                    // (consistencia eventual). Se espera: aplicar sin suscriptor activo dejaría la
                    // suscripción apuntando a un id que no cobra.
                    _logger.LogWarning(
                        "Pago confirmado pero el plan destino aún no muestra suscriptor activo. TenantId {TenantId}. IntentId {IntentId}. Plan {Plan}.",
                        payment.TenantId,
                        intent.Id,
                        intent.ToTilopayRecurringPlanId);

                    return new LatePlanChangeApplicationResult
                    {
                        Status = LatePlanChangeApplicationStatus.LeftPendingNoActiveSubscriber,
                        Message = "El plan destino todavía no tiene un suscriptor activo; se reintentará.",
                        IntentId = intent.Id
                    };

                case TargetSubscriberVerdict.MultipleActive:
                    return await ManualReviewAsync(
                        intent,
                        payment.TenantId,
                        $"El plan destino {intent.ToTilopayRecurringPlanId} tiene {assessment.Active.Count} suscriptores ACTIVOS del mismo correo: no se puede decidir cuál es el nuevo sin arriesgar un doble cobro.",
                        cancellationToken);

                case TargetSubscriberVerdict.UnknownStatus:
                    return await ManualReviewAsync(
                        intent,
                        payment.TenantId,
                        $"El plan destino {intent.ToTilopayRecurringPlanId} tiene suscriptores con status que no sabemos clasificar: no se aplica el cambio a ciegas.",
                        cancellationToken);
            }

            // SingleActive: debe ser exactamente el que quedó en el pago confirmado.
            var active = assessment.Active[0];
            if (!string.Equals(active.SubscriberId?.Trim(), newSubscriberId, StringComparison.OrdinalIgnoreCase))
            {
                return await ManualReviewAsync(
                    intent,
                    payment.TenantId,
                    $"El suscriptor ACTIVO del plan destino (suffix {SensitiveDataMasker.MaskReference(active.SubscriberId)}) no coincide con el del pago confirmado (suffix {SensitiveDataMasker.MaskReference(newSubscriberId)}). Algo cambió en TiloPay: decide soporte.",
                    cancellationToken);
            }

            return null; // Verificado: es el suscriptor nuevo y está cobrando el plan destino.
        }

        /// <summary>
        /// Pone el ciclo a partir de la confirmación del pago. ActivarSuscripcionRecurrenteAsync usa
        /// "ahora" (correcto en el camino normal, donde ahora ≈ el pago); en una aplicación tardía
        /// pueden haber pasado horas o un pase de reconciliación entero.
        /// </summary>
        private void AlignBillingPeriodToPayment(PagoSuscripcion payment, Plan targetPlan, Guid tenantId)
        {
            if (payment.FechaConfirmacionUtc is not { } confirmedAtUtc)
            {
                return;
            }

            var subscription = _db.Suscripciones.Local.FirstOrDefault(s => s.TenantId == tenantId);
            if (subscription is null)
            {
                return;
            }

            var periodEndUtc = targetPlan.BillingCycle == BillingCycle.Annual
                ? confirmedAtUtc.AddYears(1)
                : confirmedAtUtc.AddMonths(1);

            subscription.FechaInicio = confirmedAtUtc;
            subscription.FechaFin = periodEndUtc;
            subscription.FechaProximoCobroUtc = periodEndUtc;
        }

        private async Task TryCancelOldSubscriberAsync(Guid tenantId, Guid intentId, CancellationToken cancellationToken)
        {
            if (_providerSubscriptionManager is null || !_providerSubscriptionManager.IsEnabled)
            {
                return;
            }

            try
            {
                await _providerSubscriptionManager.TryCancelOldSubscriberForUpgradeAsync(
                    tenantId,
                    intentId,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                // Best-effort: el cambio YA se aplicó. Si la baja del viejo falla, queda pendiente
                // y el worker de reintento la persigue con backoff.
                _logger.LogError(
                    ex,
                    "Cancelación del suscriptor viejo tras aplicación tardía no se completó. TenantId {TenantId}. IntentId {IntentId}.",
                    tenantId,
                    intentId);
            }
        }

        private async Task<LatePlanChangeApplicationResult> ManualReviewAsync(
            PlanChangeIntent intent,
            Guid tenantId,
            string reason,
            CancellationToken cancellationToken)
        {
            const int cooldownHours = 6;
            var intentKey = intent.Id.ToString();
            var cooldownCutoffUtc = GetUtcNow().AddHours(-cooldownHours);

            var alreadyAlerted = await _db.PlatformAuditLogs.AnyAsync(
                log =>
                    log.Action == PlatformAuditActions.PlanChangeLateSubscriberRequiresManualReview &&
                    log.EntityId == intentKey &&
                    log.CreatedAtUtc >= cooldownCutoffUtc,
                cancellationToken);

            if (!alreadyAlerted)
            {
                _db.PlatformAuditLogs.Add(new PlatformAuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorUserId = "system",
                    ActorEmail = "system",
                    Action = PlatformAuditActions.PlanChangeLateSubscriberRequiresManualReview,
                    EntityType = PlatformAuditEntityTypes.Subscription,
                    EntityId = intentKey,
                    TenantId = tenantId,
                    Reason = Trim(
                        $"Cambio {intent.FromPlanCode} → {intent.ToPlanCode} NO aplicado pese al pago confirmado: {reason}",
                        500),
                    CreatedAtUtc = GetUtcNow()
                });

                await _db.SaveChangesAsync(cancellationToken);
            }

            _logger.LogError(
                "Cambio de plan con pago confirmado requiere revisión manual. TenantId {TenantId}. IntentId {IntentId}. Reason {Reason}.",
                tenantId,
                intent.Id,
                reason);

            return new LatePlanChangeApplicationResult
            {
                Status = LatePlanChangeApplicationStatus.ManualReview,
                Message = reason,
                IntentId = intent.Id
            };
        }

        private DateTime GetUtcNow() => _clock.NowOffset().UtcDateTime;

        private static string Trim(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength];
    }
}
