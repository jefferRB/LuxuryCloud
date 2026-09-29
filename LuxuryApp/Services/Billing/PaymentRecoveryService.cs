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
    public sealed record PaymentRecoveryActionResult
    {
        public required bool Succeeded { get; init; }
        public string? Message { get; init; }

        public static PaymentRecoveryActionResult Ok(string message) => new() { Succeeded = true, Message = message };
        public static PaymentRecoveryActionResult Fail(string message) => new() { Succeeded = false, Message = message };
    }

    /// <summary>Vista (solo lectura, sanitizada) de un incidente para la consola de plataforma.</summary>
    public sealed record PaymentRecoveryConsoleItem
    {
        public required Guid IncidentId { get; init; }
        public required Guid TenantId { get; init; }

        /// <summary>Ámbito del incidente: plan base o add-on de WhatsApp. Determina qué plan usa la update URL.</summary>
        public PaymentIncidentScope Scope { get; init; }

        /// <summary>Plan recurrente de TiloPay del incidente (base o add-on): el "id" para recurrentUrl.</summary>
        public int? TilopayRecurringPlanId { get; init; }

        public string? TenantName { get; init; }
        public string? ClienteEmail { get; init; }
        public string? PlanCode { get; init; }

        /// <summary>id_suscriptor enmascarado (nunca completo).</summary>
        public string? ProviderSubscriberSuffix { get; init; }

        public PaymentIncidentStatus Status { get; init; }
        public int FailureCount { get; init; }
        public DateTime FailureDetectedAtUtc { get; init; }
        public DateTime? GraceEndsAtUtc { get; init; }
        public int NotificationCount { get; init; }
        public DateTime? LastNotificationAtUtc { get; init; }
        public DateTime? LastReminderAtUtc { get; init; }
        public string? ProviderResultCode { get; init; }
        public string? ProviderResultMessage { get; init; }

        /// <summary>true si el acceso quedó suspendido por impago (distinto de "gracia vencida sin suspensión").</summary>
        public bool SuspendedForNonPayment { get; init; }
    }

    /// <summary>
    /// Incidente que la reconciliación cerró como "renovado por el proveedor" SIN evidencia de pago
    /// (sin pago confirmado posterior al fallo y sin expire avanzado). Candidato a reapertura manual.
    /// </summary>
    public sealed record UnverifiedRecoveryResolutionItem
    {
        public required Guid IncidentId { get; init; }
        public required Guid TenantId { get; init; }
        public string? TenantName { get; init; }
        public string? PlanCode { get; init; }
        public DateTime FailureDetectedAtUtc { get; init; }
        public DateTime? GraceEndsAtUtc { get; init; }
        public DateTime? ResolvedAtUtc { get; init; }
        public string? ProviderStatusRaw { get; init; }
        public string? ProviderExpiryRaw { get; init; }
    }

    public interface IPaymentRecoveryService
    {
        /// <summary>
        /// Registra un pago recurrente fallido: abre/actualiza el incidente y el período de gracia.
        /// Idempotente (un incidente Open por tenant/plan; incrementa FailureCount). No accionable si
        /// la renovación ya está cancelada+dada de baja, si el fallo es de un plan viejo, o si ya hubo
        /// un pago confirmado más reciente (success-gana). Best-effort, local, sin HTTP.
        /// </summary>
        Task RegisterFailedPaymentAsync(
            Guid tenantId,
            int? failedRecurringPlanId,
            string? providerSubscriberId,
            string? resultCode,
            string? resultMessage,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resuelve el incidente abierto tras un pago exitoso del plan actual y limpia el estado de
        /// recuperación. Un éxito de OTRO plan no toca el incidente actual. Idempotente.
        /// </summary>
        Task ResolveOnSuccessAsync(
            Guid tenantId,
            int? paidRecurringPlanId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Igual que <see cref="RegisterFailedPaymentAsync"/> pero para el ADD-ON de WhatsApp: abre/
        /// actualiza un incidente con Scope=WhatsAppAddon ligado a la fila del add-on. NUNCA toca el
        /// plan base ni sus incidentes. La gracia/estado del add-on ya los maneja SuscripcionService;
        /// acá solo se agrega el incidente (historial + visibilidad en Mission Control). Sin emails.
        /// </summary>
        Task RegisterFailedAddonPaymentAsync(
            Guid tenantId,
            int? failedRecurringPlanId,
            string? providerSubscriberId,
            string? resultCode,
            string? resultMessage,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resuelve el incidente ABIERTO del add-on tras un pago exitoso del add-on actual. Un éxito de
        /// otro paquete no toca el incidente actual. Idempotente. NUNCA toca el plan base.
        /// </summary>
        Task ResolveAddonOnSuccessAsync(
            Guid tenantId,
            int? paidRecurringPlanId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Pase LOCAL (sin HTTP) que cierra los incidentes cuya gracia venció: los marca GraceExpired
        /// y, SOLO si <c>AutoSuspendAfterGrace=true</c>, suspende el acceso; si no, deja rastro dry-run
        /// sin cortar. Nunca suspende antes de la fecha efectiva ni con renovación cancelada vigente.
        /// Idempotente. Devuelve cuántos incidentes procesó.
        /// </summary>
        Task<int> RunGraceExpirationPassAsync(CancellationToken cancellationToken = default);

        /// <summary>Lista (cross-tenant, solo lectura, sanitizada) los incidentes vivos para la consola de plataforma.</summary>
        Task<IReadOnlyList<PaymentRecoveryConsoleItem>> ListConsoleIncidentsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// SuperAdmin cierra manualmente un incidente (Resolved) y limpia el estado de recuperación de
        /// la suscripción. NO cambia el acceso (Estado): reactivar un acceso suspendido es una acción de
        /// ciclo de vida aparte. Idempotente. Local, sin HTTP.
        /// </summary>
        Task<PaymentRecoveryActionResult> ResolveManuallyAsync(Guid incidentId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default);

        /// <summary>
        /// SuperAdmin marca un incidente como Ignorado (no accionable) con motivo y limpia el banner de
        /// recuperación de la suscripción. NO cambia el acceso. Idempotente. Local, sin HTTP.
        /// </summary>
        Task<PaymentRecoveryActionResult> IgnoreAsync(Guid incidentId, string actorUserId, string actorEmail, string? reason, CancellationToken cancellationToken = default);

        /// <summary>
        /// Lista (solo lectura) incidentes base cerrados automáticamente por "renovación del proveedor"
        /// que NO tienen evidencia de pago: ni PagoSuscripcion confirmado posterior al fallo ni expire
        /// del proveedor avanzado. Son los cierres del bug previo (Active + mismo expire).
        /// </summary>
        Task<IReadOnlyList<UnverifiedRecoveryResolutionItem>> ListUnverifiedResolutionsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// SuperAdmin reabre un cierre sin evidencia: re-verifica TODAS las guardas, restaura la gracia
        /// ORIGINAL del incidente (nunca la extiende), vuelve a Morosa/GraceActive y audita antes/después.
        /// Si la gracia original ya venció, aplica en el acto el pase normal de gracia (respeta
        /// AutoSuspendAfterGrace). No toca TiloPay, no crea pagos, no mueve FechaFin. Idempotente.
        /// </summary>
        Task<PaymentRecoveryActionResult> ReopenUnverifiedResolutionAsync(Guid incidentId, string actorUserId, string actorEmail, string? reason, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Gestiona los incidentes de recuperación de pago (<see cref="SubscriptionPaymentIncident"/>).
    /// Se engancha en el webhook (fallo/éxito) de forma best-effort y POST-commit: abre su propio
    /// <c>BeginScope(tenantId)</c> para que el INSERT del incidente (ITenantEntity) pase el RLS, y
    /// guarda en una transacción corta aparte. La gracia se apoya en <c>FechaFinGraciaUtc</c> +
    /// estado Morosa que YA maneja SuscripcionService; acá se agrega solo el tracking/incidente.
    /// </summary>
    public sealed class PaymentRecoveryService : IPaymentRecoveryService
    {
        private readonly ApplicationDbContext _db;
        private readonly ITenantExecutionContextAccessor _tenantExecutionContextAccessor;
        private readonly IBusinessDateTimeProvider _clock;
        private readonly BillingPaymentRecoveryOptions _options;
        private readonly ITenantCommercialAccessCache? _accessCache;
        private readonly ILogger<PaymentRecoveryService> _logger;
        private readonly ITenantOwnerResolver _ownerResolver;

        public PaymentRecoveryService(
            ApplicationDbContext db,
            ITenantExecutionContextAccessor tenantExecutionContextAccessor,
            IBusinessDateTimeProvider clock,
            IOptions<BillingPaymentRecoveryOptions> options,
            ILogger<PaymentRecoveryService> logger,
            ITenantOwnerResolver ownerResolver,
            ITenantCommercialAccessCache? accessCache = null)
        {
            _db = db;
            _tenantExecutionContextAccessor = tenantExecutionContextAccessor;
            _clock = clock;
            _options = options.Value;
            _accessCache = accessCache;
            _logger = logger;
            _ownerResolver = ownerResolver;
        }

        public async Task RegisterFailedPaymentAsync(
            Guid tenantId,
            int? failedRecurringPlanId,
            string? providerSubscriberId,
            string? resultCode,
            string? resultMessage,
            CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled || tenantId == Guid.Empty)
            {
                return;
            }

            using var scope = _tenantExecutionContextAccessor.BeginScope(tenantId);
            var nowUtc = GetUtcNow();

            var subscription = await LoadSubscriptionAsync(tenantId, cancellationToken);
            if (subscription is null)
            {
                return;
            }

            // El fallo es de un plan VIEJO (ya cambiado/cancelado): no marca el plan actual.
            if (failedRecurringPlanId is { } failedPlan &&
                subscription.TilopayRecurringPlanId is { } currentPlan &&
                failedPlan != currentPlan)
            {
                _logger.LogInformation(
                    "Pago fallido de un plan distinto al actual: no se abre incidente. TenantId {TenantId}. FailedPlan {FailedPlan}. CurrentPlan {CurrentPlan}.",
                    tenantId, failedPlan, currentPlan);
                return;
            }

            // Renovación cancelada y suscriptor ya dado de baja en el proveedor: el fallo no es accionable.
            if (subscription.CancelAtPeriodEnd &&
                ProviderSubscriberStatusRules.IsProviderSubscriberInactive(subscription.ProviderStatusRaw))
            {
                _logger.LogInformation(
                    "Pago fallido no accionable (renovación cancelada + suscriptor inactivo). TenantId {TenantId}.",
                    tenantId);
                return;
            }

            var planId = subscription.TilopayRecurringPlanId;

            // Success-gana: si el pago confirmado más reciente es posterior al fallido más reciente,
            // el éxito ya ganó (webhooks desordenados): NO se abre/reabre incidente.
            if (await ConfirmedPaymentWinsAsync(tenantId, planId, cancellationToken))
            {
                _logger.LogInformation(
                    "Pago fallido ignorado: hay un pago confirmado más reciente (success-gana). TenantId {TenantId}.",
                    tenantId);
                return;
            }

            // Incidente VIVO del ciclo (Open o GraceExpired): mientras no haya un pago confirmado (el
            // success-gana de arriba lo descarta) un nuevo rechazo es un REINTENTO del mismo ciclo, no
            // un ciclo nuevo. Incluir GraceExpired evita que el reintento del día 6 abra otra gracia.
            var existing = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .Where(i =>
                    i.TenantId == tenantId &&
                    i.Scope == PaymentIncidentScope.BasePlan &&
                    (i.Status == PaymentIncidentStatus.Open || i.Status == PaymentIncidentStatus.GraceExpired) &&
                    i.TilopayRecurringPlanId == planId)
                .OrderByDescending(i => i.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            var graceDays = Math.Clamp(_options.GraceDays, 1, 60);

            if (existing is not null)
            {
                // Reintento del mismo ciclo: NO se resetea la gracia; solo se incrementa el conteo.
                existing.FailureCount += 1;
                existing.ProviderResultCode = Trim(resultCode, 40) ?? existing.ProviderResultCode;
                existing.ProviderResultMessage = Trim(resultMessage, 300) ?? existing.ProviderResultMessage;
                existing.ProviderSubscriptionId = providerSubscriberId ?? existing.ProviderSubscriptionId;
                existing.UpdatedAtUtc = nowUtc;
                subscription.LastPaymentFailedAtUtc = nowUtc;
                subscription.FechaUltimaActualizacionUtc = nowUtc;

                // El incidente es la fuente de verdad de la ventana: el registro del fallo en
                // SuscripcionService puede haber recalculado FechaFinGraciaUtc; se re-afirma la
                // ORIGINAL para que ningún reintento extienda la gracia.
                if (existing.GraceEndsAtUtc is { } originalGraceEndsUtc)
                {
                    subscription.FechaFinGraciaUtc = originalGraceEndsUtc;
                }

                await _db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "Pago fallido recurrente adicional. TenantId {TenantId}. FailureCount {FailureCount}.",
                    tenantId, existing.FailureCount);
                return;
            }

            var graceEndsAtUtc = nowUtc.AddDays(graceDays);
            var clienteEmail = await ResolveTenantEmailAsync(tenantId, cancellationToken);

            var incident = new SubscriptionPaymentIncident
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SuscripcionId = subscription.Id,
                PlanCode = subscription.CodigoPlan ?? subscription.Plan?.Codigo,
                TilopayRecurringPlanId = planId,
                ProviderSubscriptionId = providerSubscriberId ?? subscription.ProviderSubscriptionId,
                ClienteEmail = Trim(clienteEmail, 320),
                Status = PaymentIncidentStatus.Open,
                FailureDetectedAtUtc = nowUtc,
                GraceEndsAtUtc = graceEndsAtUtc,
                ProviderEventKey = BuildEventKey(tenantId, planId, resultCode, nowUtc),
                ProviderResultCode = Trim(resultCode, 40),
                ProviderResultMessage = Trim(resultMessage, 300),
                FailureCount = 1,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            };
            _db.SubscriptionPaymentIncidents.Add(incident);

            // Resumen en la suscripción + alinear la gracia con GraceDays de recuperación.
            subscription.LastPaymentFailedAtUtc = nowUtc;
            subscription.PaymentRecoveryStatus = "GraceActive";
            subscription.FechaFinGraciaUtc = graceEndsAtUtc;
            subscription.FechaUltimaActualizacionUtc = nowUtc;

            _db.PlatformAuditLogs.Add(BuildAudit(
                PlatformAuditActions.SubscriptionPaymentFailedGraceStarted,
                tenantId,
                incident.Id.ToString(),
                $"Pago recurrente fallido: incidente abierto. Gracia hasta {graceEndsAtUtc:yyyy-MM-dd HH:mm} UTC. " +
                $"Plan {incident.PlanCode}. SuscriptorSuffix {SensitiveDataMasker.MaskReference(incident.ProviderSubscriptionId)}. Code {Trim(resultCode, 40)}.",
                nowUtc));

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Incidente de pago recurrente abierto. TenantId {TenantId}. IncidentId {IncidentId}. GraceEnds {GraceEnds}.",
                tenantId, incident.Id, graceEndsAtUtc);
        }

        public async Task ResolveOnSuccessAsync(
            Guid tenantId,
            int? paidRecurringPlanId,
            CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled || tenantId == Guid.Empty)
            {
                return;
            }

            using var scope = _tenantExecutionContextAccessor.BeginScope(tenantId);
            var nowUtc = GetUtcNow();

            var subscription = await LoadSubscriptionAsync(tenantId, cancellationToken);
            if (subscription is null)
            {
                return;
            }

            // Un éxito de OTRO plan no resuelve el incidente del plan actual.
            if (paidRecurringPlanId is { } paidPlan &&
                subscription.TilopayRecurringPlanId is { } currentPlan &&
                paidPlan != currentPlan)
            {
                return;
            }

            // Vivos = Open o GraceExpired: un pago confirmado cierra el ciclo aunque la gracia ya
            // hubiera vencido (si no, el incidente quedaba GraceExpired para siempre).
            var liveIncidents = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .Where(i =>
                    i.TenantId == tenantId &&
                    i.Scope == PaymentIncidentScope.BasePlan &&
                    (i.Status == PaymentIncidentStatus.Open || i.Status == PaymentIncidentStatus.GraceExpired))
                .OrderByDescending(i => i.CreatedAtUtc)
                .ToListAsync(cancellationToken);
            var open = liveIncidents.FirstOrDefault();

            var clearedRecoveryFields = subscription.LastPaymentFailedAtUtc is not null ||
                                        subscription.PaymentRecoveryStatus is not null;

            if (open is null && !clearedRecoveryFields)
            {
                return; // Nada que resolver.
            }

            foreach (var incident in liveIncidents)
            {
                incident.Status = PaymentIncidentStatus.Resolved;
                incident.ResolvedAtUtc = nowUtc;
                incident.UpdatedAtUtc = nowUtc;
            }

            subscription.LastPaymentFailedAtUtc = null;
            subscription.PaymentRecoveryStatus = null;
            subscription.FechaFinGraciaUtc = null;
            subscription.FechaUltimaActualizacionUtc = nowUtc;

            _db.PlatformAuditLogs.Add(BuildAudit(
                PlatformAuditActions.SubscriptionPaymentRecoveryResolved,
                tenantId,
                (open?.Id ?? subscription.Id).ToString(),
                "Pago recurrente confirmado: incidente de recuperación resuelto y gracia limpiada.",
                nowUtc));

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Incidente de recuperación resuelto por pago confirmado. TenantId {TenantId}. IncidentId {IncidentId}.",
                tenantId, open?.Id);
        }

        public async Task RegisterFailedAddonPaymentAsync(
            Guid tenantId,
            int? failedRecurringPlanId,
            string? providerSubscriberId,
            string? resultCode,
            string? resultMessage,
            CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled || tenantId == Guid.Empty)
            {
                return;
            }

            using var scope = _tenantExecutionContextAccessor.BeginScope(tenantId);
            var nowUtc = GetUtcNow();

            var addon = await _db.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .Where(a => a.TenantId == tenantId)
                .OrderByDescending(a => a.UpdatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (addon is null)
            {
                return;
            }

            // Fallo de un add-on VIEJO (ya cambiado): no abre incidente del actual.
            if (failedRecurringPlanId is { } failedPlan &&
                addon.TilopayRecurringPlanId is { } currentPlan &&
                failedPlan != currentPlan)
            {
                return;
            }

            var planId = addon.TilopayRecurringPlanId;

            var existing = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .Where(i =>
                    i.TenantId == tenantId &&
                    i.Status == PaymentIncidentStatus.Open &&
                    i.Scope == PaymentIncidentScope.WhatsAppAddon &&
                    i.AddonId == addon.Id)
                .OrderByDescending(i => i.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is not null)
            {
                existing.FailureCount += 1;
                existing.ProviderResultCode = Trim(resultCode, 40) ?? existing.ProviderResultCode;
                existing.ProviderResultMessage = Trim(resultMessage, 300) ?? existing.ProviderResultMessage;
                existing.ProviderSubscriptionId = providerSubscriberId ?? existing.ProviderSubscriptionId;
                existing.UpdatedAtUtc = nowUtc;
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }

            var clienteEmail = await ResolveTenantEmailAsync(tenantId, cancellationToken);
            var graceEndsAtUtc = addon.FechaFinGraciaUtc ?? nowUtc.AddDays(Math.Clamp(_options.GraceDays, 1, 60));

            var incident = new SubscriptionPaymentIncident
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Scope = PaymentIncidentScope.WhatsAppAddon,
                AddonId = addon.Id,
                SuscripcionId = Guid.Empty, // no aplica al add-on (columna escalar, sin FK)
                PlanCode = addon.AddonCode,
                TilopayRecurringPlanId = planId,
                ProviderSubscriptionId = providerSubscriberId ?? addon.ProviderSubscriptionId,
                ClienteEmail = Trim(clienteEmail, 320),
                Status = PaymentIncidentStatus.Open,
                FailureDetectedAtUtc = nowUtc,
                GraceEndsAtUtc = graceEndsAtUtc,
                ProviderEventKey = BuildEventKey(tenantId, planId, resultCode, nowUtc),
                ProviderResultCode = Trim(resultCode, 40),
                ProviderResultMessage = Trim(resultMessage, 300),
                FailureCount = 1,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            };
            _db.SubscriptionPaymentIncidents.Add(incident);

            _db.PlatformAuditLogs.Add(BuildAudit(
                PlatformAuditActions.AddonPaymentFailedGraceStarted,
                tenantId,
                incident.Id.ToString(),
                $"Pago recurrente del ADD-ON fallido: incidente abierto (no afecta el plan base). " +
                $"Add-on {addon.AddonCode}. Gracia hasta {graceEndsAtUtc:yyyy-MM-dd HH:mm} UTC. Code {Trim(resultCode, 40)}.",
                nowUtc));

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Incidente de pago del add-on abierto. TenantId {TenantId}. IncidentId {IncidentId}. AddonId {AddonId}.",
                tenantId, incident.Id, addon.Id);
        }

        public async Task ResolveAddonOnSuccessAsync(
            Guid tenantId,
            int? paidRecurringPlanId,
            CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled || tenantId == Guid.Empty)
            {
                return;
            }

            using var scope = _tenantExecutionContextAccessor.BeginScope(tenantId);
            var nowUtc = GetUtcNow();

            var addon = await _db.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .Where(a => a.TenantId == tenantId)
                .OrderByDescending(a => a.UpdatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (addon is null)
            {
                return;
            }

            // Un éxito de OTRO paquete no resuelve el incidente del add-on actual.
            if (paidRecurringPlanId is { } paidPlan &&
                addon.TilopayRecurringPlanId is { } currentPlan &&
                paidPlan != currentPlan)
            {
                return;
            }

            var open = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .Where(i =>
                    i.TenantId == tenantId &&
                    i.Status == PaymentIncidentStatus.Open &&
                    i.Scope == PaymentIncidentScope.WhatsAppAddon &&
                    i.AddonId == addon.Id)
                .OrderByDescending(i => i.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (open is null)
            {
                return;
            }

            open.Status = PaymentIncidentStatus.Resolved;
            open.ResolvedAtUtc = nowUtc;
            open.UpdatedAtUtc = nowUtc;

            _db.PlatformAuditLogs.Add(BuildAudit(
                PlatformAuditActions.AddonPaymentRecoveryResolved,
                tenantId,
                open.Id.ToString(),
                "Pago del add-on confirmado: incidente de recuperación del add-on resuelto.",
                nowUtc));

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Incidente de recuperación del add-on resuelto por pago confirmado. TenantId {TenantId}. IncidentId {IncidentId}.",
                tenantId, open.Id);
        }

        public async Task<int> RunGraceExpirationPassAsync(CancellationToken cancellationToken = default)
        {
            if (!_options.Enabled)
            {
                return 0;
            }

            var nowUtc = GetUtcNow();
            // SOLO incidentes de PLAN BASE: la expiración de gracia suspende (con AutoSuspend) la
            // suscripción base. Los incidentes de add-on NO pasan por acá — el corte del add-on lo hace
            // su propio estado efectivo al vencer su gracia, sin tocar el plan base.
            // Con AutoSuspend también se revisan los GraceExpired AÚN NO suspendidos: los que quedaron en
            // dry-run (flag apagado, o período pagado todavía vigente al vencer la gracia). Así, al
            // encender el flag o al terminar el período, la suspensión se aplica una sola vez.
            var autoSuspend = _options.AutoSuspendAfterGrace;
            var candidates = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(i =>
                    i.Scope == PaymentIncidentScope.BasePlan &&
                    i.GraceEndsAtUtc != null &&
                    i.GraceEndsAtUtc <= nowUtc &&
                    (i.Status == PaymentIncidentStatus.Open ||
                     (autoSuspend &&
                      i.Status == PaymentIncidentStatus.GraceExpired &&
                      !_db.Suscripciones.IgnoreQueryFilters().Any(s =>
                          s.TenantId == i.TenantId &&
                          (s.PaymentRecoveryStatus == "Suspended" || s.Estado == EstadoSuscripcion.Suspendida)))))
                .Select(i => new { i.Id, i.TenantId })
                .ToListAsync(cancellationToken);

            var openChecked = candidates.Count;
            int graceExpiredMarked = 0, suspended = 0, dryRuns = 0, ignored = 0, processed = 0;

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidate.TenantId == Guid.Empty)
                {
                    continue;
                }

                try
                {
                    _db.ChangeTracker.Clear();
                    switch (await ExpireOneGraceAsync(candidate.Id, candidate.TenantId, nowUtc, cancellationToken))
                    {
                        case GraceExpirationOutcome.Ignored:
                            ignored++; processed++; break;
                        case GraceExpirationOutcome.DryRunMarked:
                            dryRuns++; graceExpiredMarked++; processed++; break;
                        case GraceExpirationOutcome.Suspended:
                            suspended++; graceExpiredMarked++; processed++; break;
                        case GraceExpirationOutcome.Skipped:
                        default:
                            break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _db.ChangeTracker.Clear();
                    _logger.LogError(ex, "No se pudo procesar la expiración de gracia del incidente {IncidentId}; se continúa.", candidate.Id);
                }
            }

            if (openChecked > 0)
            {
                _logger.LogInformation(
                    "Pase de expiración de gracia. OpenChecked {OpenChecked}. GraceExpiredMarked {GraceExpiredMarked}. Suspended {Suspended}. DryRuns {DryRuns}. Ignored {Ignored}. AutoSuspend {AutoSuspend}.",
                    openChecked, graceExpiredMarked, suspended, dryRuns, ignored, _options.AutoSuspendAfterGrace);
            }

            return processed;
        }

        /// <summary>Qué le pasó a un incidente en el pase de expiración de gracia.</summary>
        private enum GraceExpirationOutcome
        {
            /// <summary>No aplicaba (ya no Open, o la gracia aún no venció entre la lectura y el tracked).</summary>
            Skipped,

            /// <summary>Renovación cancelada: fallo no accionable, incidente marcado Ignored.</summary>
            Ignored,

            /// <summary>Marcado GraceExpired conservando el acceso (AutoSuspend=false, o período pagado aún vigente).</summary>
            DryRunMarked,

            /// <summary>Marcado GraceExpired y acceso suspendido por impago (AutoSuspend=true y período pagado vencido).</summary>
            Suspended
        }

        public async Task<IReadOnlyList<PaymentRecoveryConsoleItem>> ListConsoleIncidentsAsync(CancellationToken cancellationToken = default)
        {
            // Incidentes vivos que le importan a soporte: en curso, con gracia vencida o en revisión.
            var incidents = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(i =>
                    i.Status == PaymentIncidentStatus.Open ||
                    i.Status == PaymentIncidentStatus.GraceExpired ||
                    i.Status == PaymentIncidentStatus.ManualReview)
                .OrderBy(i => i.GraceEndsAtUtc)
                .ThenByDescending(i => i.FailureDetectedAtUtc)
                .Take(200)
                .Select(i => new
                {
                    i.Id,
                    i.TenantId,
                    i.Scope,
                    i.TilopayRecurringPlanId,
                    i.ClienteEmail,
                    i.PlanCode,
                    i.ProviderSubscriptionId,
                    i.Status,
                    i.FailureCount,
                    i.FailureDetectedAtUtc,
                    i.GraceEndsAtUtc,
                    i.NotificationCount,
                    i.LastNotificationAtUtc,
                    i.LastReminderAtUtc,
                    i.ProviderResultCode,
                    i.ProviderResultMessage
                })
                .ToListAsync(cancellationToken);

            if (incidents.Count == 0)
            {
                return Array.Empty<PaymentRecoveryConsoleItem>();
            }

            var tenantIds = incidents.Select(i => i.TenantId).Distinct().ToList();
            var tenantNames = await _db.Tenants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(t => tenantIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);
            var suspendedTenants = (await _db.Suscripciones
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(s => tenantIds.Contains(s.TenantId) && s.PaymentRecoveryStatus == "Suspended")
                    .Select(s => s.TenantId)
                    .ToListAsync(cancellationToken))
                .ToHashSet();

            return incidents.Select(i => new PaymentRecoveryConsoleItem
            {
                IncidentId = i.Id,
                TenantId = i.TenantId,
                Scope = i.Scope,
                TilopayRecurringPlanId = i.TilopayRecurringPlanId,
                TenantName = tenantNames.TryGetValue(i.TenantId, out var name) ? name : null,
                ClienteEmail = i.ClienteEmail,
                PlanCode = i.PlanCode,
                ProviderSubscriberSuffix = SensitiveDataMasker.MaskReference(i.ProviderSubscriptionId),
                Status = i.Status,
                FailureCount = i.FailureCount,
                FailureDetectedAtUtc = i.FailureDetectedAtUtc,
                GraceEndsAtUtc = i.GraceEndsAtUtc,
                NotificationCount = i.NotificationCount,
                LastNotificationAtUtc = i.LastNotificationAtUtc,
                LastReminderAtUtc = i.LastReminderAtUtc,
                ProviderResultCode = i.ProviderResultCode,
                ProviderResultMessage = i.ProviderResultMessage,
                SuspendedForNonPayment = suspendedTenants.Contains(i.TenantId)
            }).ToList();
        }

        public Task<PaymentRecoveryActionResult> ResolveManuallyAsync(Guid incidentId, string actorUserId, string actorEmail, CancellationToken cancellationToken = default) =>
            CloseIncidentAsync(
                incidentId, actorUserId, actorEmail,
                targetStatus: PaymentIncidentStatus.Resolved,
                auditAction: PlatformAuditActions.PaymentRecoveryManuallyResolved,
                reason: null,
                successMessage: "Incidente cerrado manualmente. El estado de recuperación de la suscripción quedó limpio.",
                cancellationToken);

        public Task<PaymentRecoveryActionResult> IgnoreAsync(Guid incidentId, string actorUserId, string actorEmail, string? reason, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return Task.FromResult(PaymentRecoveryActionResult.Fail("Indicá un motivo para ignorar el incidente."));
            }

            return CloseIncidentAsync(
                incidentId, actorUserId, actorEmail,
                targetStatus: PaymentIncidentStatus.Ignored,
                auditAction: PlatformAuditActions.PaymentRecoveryIgnored,
                reason: reason,
                successMessage: "Incidente marcado como ignorado.",
                cancellationToken);
        }

        public async Task<IReadOnlyList<UnverifiedRecoveryResolutionItem>> ListUnverifiedResolutionsAsync(CancellationToken cancellationToken = default)
        {
            var sinceUtc = GetUtcNow().AddDays(-UnverifiedResolutionLookbackDays);

            // Punto de partida acotado: las auditorías de la sanación automática (son pocas). Solo los
            // tenants que la tuvieron pueden tener un cierre sin evidencia.
            var healAudits = await _db.PlatformAuditLogs
                .AsNoTracking()
                .Where(l =>
                    l.Action == PlatformAuditActions.PaymentRecoveryResolvedByProviderRenewal &&
                    l.TenantId != null &&
                    l.CreatedAtUtc >= sinceUtc)
                .Select(l => new { l.TenantId, l.CreatedAtUtc })
                .Take(500)
                .ToListAsync(cancellationToken);

            if (healAudits.Count == 0)
            {
                return Array.Empty<UnverifiedRecoveryResolutionItem>();
            }

            var healedTenantIds = healAudits.Select(a => a.TenantId!.Value).Distinct().ToList();
            var resolvedIncidents = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(i =>
                    healedTenantIds.Contains(i.TenantId) &&
                    i.Scope == PaymentIncidentScope.BasePlan &&
                    i.Status == PaymentIncidentStatus.Resolved &&
                    i.ResolvedAtUtc != null &&
                    i.ResolvedAtUtc >= sinceUtc)
                .OrderByDescending(i => i.ResolvedAtUtc)
                .ToListAsync(cancellationToken);

            // El cierre debe coincidir (±1 min) con una auditoría de sanación del MISMO tenant.
            var candidates = resolvedIncidents
                .Where(i => healAudits.Any(a =>
                    a.TenantId == i.TenantId &&
                    a.CreatedAtUtc >= i.ResolvedAtUtc!.Value.AddMinutes(-1) &&
                    a.CreatedAtUtc <= i.ResolvedAtUtc!.Value.AddMinutes(1)))
                .ToList();

            var items = new List<UnverifiedRecoveryResolutionItem>();
            foreach (var incident in candidates)
            {
                var subscription = await _db.Suscripciones
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(s => s.TenantId == incident.TenantId)
                    .OrderByDescending(s => s.FechaUltimaActualizacionUtc ?? s.FechaInicio)
                    .FirstOrDefaultAsync(cancellationToken);

                if (await DescribeReopenBlockerAsync(incident, subscription, cancellationToken) is not null)
                {
                    continue;
                }

                items.Add(new UnverifiedRecoveryResolutionItem
                {
                    IncidentId = incident.Id,
                    TenantId = incident.TenantId,
                    PlanCode = incident.PlanCode,
                    FailureDetectedAtUtc = incident.FailureDetectedAtUtc,
                    GraceEndsAtUtc = incident.GraceEndsAtUtc,
                    ResolvedAtUtc = incident.ResolvedAtUtc,
                    ProviderStatusRaw = subscription!.ProviderStatusRaw,
                    ProviderExpiryRaw = subscription.ProviderExpiryRaw
                });
            }

            if (items.Count == 0)
            {
                return items;
            }

            var tenantIds = items.Select(i => i.TenantId).Distinct().ToList();
            var tenantNames = await _db.Tenants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(t => tenantIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

            return items
                .Select(i => i with { TenantName = tenantNames.TryGetValue(i.TenantId, out var name) ? name : null })
                .ToList();
        }

        public async Task<PaymentRecoveryActionResult> ReopenUnverifiedResolutionAsync(
            Guid incidentId,
            string actorUserId,
            string actorEmail,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            if (incidentId == Guid.Empty)
            {
                return PaymentRecoveryActionResult.Fail("Incidente no especificado.");
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                return PaymentRecoveryActionResult.Fail("Indicá un motivo para reabrir el incidente.");
            }

            var owner = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(i => i.Id == incidentId)
                .Select(i => (Guid?)i.TenantId)
                .FirstOrDefaultAsync(cancellationToken);

            if (owner is not { } tenantId || tenantId == Guid.Empty)
            {
                return PaymentRecoveryActionResult.Fail("No se encontró el incidente.");
            }

            GraceExpirationOutcome? graceOutcome = null;
            var graceAlreadyEnded = false;
            DateTime nowUtc;

            using (_tenantExecutionContextAccessor.BeginScope(tenantId))
            {
                nowUtc = GetUtcNow();

                var incident = await _db.SubscriptionPaymentIncidents
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(i => i.Id == incidentId && i.TenantId == tenantId, cancellationToken);
                if (incident is null)
                {
                    return PaymentRecoveryActionResult.Fail("No se encontró el incidente.");
                }

                // Idempotente: ya reabierto (o ya vencido tras reabrir) ⇒ nada que hacer.
                if (incident.Status is PaymentIncidentStatus.Open or PaymentIncidentStatus.GraceExpired)
                {
                    return PaymentRecoveryActionResult.Ok("El incidente ya está abierto.");
                }

                var subscription = await LoadSubscriptionAsync(tenantId, cancellationToken);

                // Guardas re-evaluadas bajo tracked: nunca se reabre algo con evidencia de pago.
                if (!await HasProviderRenewalResolutionAuditAsync(incident, cancellationToken))
                {
                    return PaymentRecoveryActionResult.Fail("Este incidente no fue cerrado por la sanación automática del proveedor; no aplica.");
                }

                if (await DescribeReopenBlockerAsync(incident, subscription, cancellationToken) is { } blocker)
                {
                    return PaymentRecoveryActionResult.Fail(blocker);
                }

                var sub = subscription!;
                var before = $"Estado {sub.Estado}, Recovery {sub.PaymentRecoveryStatus ?? "-"}, Gracia {sub.FechaFinGraciaUtc:yyyy-MM-dd HH:mm}, UltimoPago {sub.FechaUltimoPagoUtc:yyyy-MM-dd HH:mm}";

                var lastFailureUtc = await _db.PagosSuscripcion
                    .IgnoreQueryFilters()
                    .Where(p =>
                        p.TenantId == tenantId &&
                        p.TilopayRecurringPlanId == incident.TilopayRecurringPlanId &&
                        p.Estado == EstadoPagoProveedor.Fallido &&
                        (p.FechaActualizacionUtc ?? p.FechaCreacionUtc) >= incident.FailureDetectedAtUtc)
                    .MaxAsync(p => (DateTime?)(p.FechaActualizacionUtc ?? p.FechaCreacionUtc), cancellationToken);

                // La sanación había escrito la hora de la sanación como "último pago" (falso): se
                // restaura el último cobro CONFIRMADO real del plan, si existe.
                var lastConfirmedUtc = await _db.PagosSuscripcion
                    .IgnoreQueryFilters()
                    .Where(p =>
                        p.TenantId == tenantId &&
                        p.TilopayRecurringPlanId == incident.TilopayRecurringPlanId &&
                        p.Estado == EstadoPagoProveedor.Confirmado)
                    .MaxAsync(p => p.FechaConfirmacionUtc, cancellationToken);

                incident.Status = PaymentIncidentStatus.Open;
                incident.ResolvedAtUtc = null;
                incident.UpdatedAtUtc = nowUtc;

                sub.Estado = EstadoSuscripcion.Morosa;
                sub.PaymentRecoveryStatus = "GraceActive";
                sub.FechaFinGraciaUtc = incident.GraceEndsAtUtc; // la ORIGINAL: no se extiende
                sub.LastPaymentFailedAtUtc = lastFailureUtc ?? incident.FailureDetectedAtUtc;
                if (lastConfirmedUtc is not null)
                {
                    sub.FechaUltimoPagoUtc = lastConfirmedUtc;
                }
                sub.MotivoEstado = "Recuperación reabierta: el cierre automático no tenía evidencia de pago.";
                sub.FechaUltimaActualizacionUtc = nowUtc;

                _db.PlatformAuditLogs.Add(new PlatformAuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorUserId = string.IsNullOrWhiteSpace(actorUserId) ? "system" : actorUserId,
                    ActorEmail = string.IsNullOrWhiteSpace(actorEmail) ? "system" : actorEmail,
                    Action = PlatformAuditActions.PaymentRecoveryUnverifiedResolutionReopened,
                    EntityType = PlatformAuditEntityTypes.Subscription,
                    EntityId = incident.Id.ToString(),
                    TenantId = tenantId,
                    Reason = Trim(
                        $"Reabierto (cierre sin evidencia de pago). Antes: {before}. Después: Estado Morosa, Recovery GraceActive, " +
                        $"Gracia {incident.GraceEndsAtUtc:yyyy-MM-dd HH:mm} UTC (original). Motivo: {Trim(reason, 200)}",
                        500)!,
                    CreatedAtUtc = nowUtc
                });

                await _db.SaveChangesAsync(cancellationToken);
                _accessCache?.Invalidate(tenantId);
                graceAlreadyEnded = incident.GraceEndsAtUtc is { } graceEnd && graceEnd <= nowUtc;
            }

            // Gracia original ya vencida ⇒ se aplica YA la política vigente (dry-run o suspensión)
            // con el mismo código del worker (abre su propio scope), sin ventana de estado incoherente.
            if (graceAlreadyEnded)
            {
                _db.ChangeTracker.Clear();
                graceOutcome = await ExpireOneGraceAsync(incidentId, tenantId, nowUtc, cancellationToken);
            }

            _logger.LogWarning(
                "Incidente de recuperación reabierto (cierre sin evidencia). IncidentId {IncidentId}. TenantId {TenantId}. Actor {Actor}. GraceOutcome {GraceOutcome}.",
                incidentId, tenantId, actorEmail, graceOutcome);

            return PaymentRecoveryActionResult.Ok(graceOutcome switch
            {
                GraceExpirationOutcome.Suspended => "Incidente reabierto. La gracia original ya había vencido: se aplicó la suspensión por impago (AutoSuspendAfterGrace=true).",
                GraceExpirationOutcome.DryRunMarked => "Incidente reabierto. La gracia original ya había vencido: quedó en 'gracia vencida' sin suspensión (AutoSuspendAfterGrace=false).",
                _ => "Incidente reabierto con su gracia original."
            });
        }

        private const int UnverifiedResolutionLookbackDays = 120;

        private Task<bool> HasProviderRenewalResolutionAuditAsync(SubscriptionPaymentIncident incident, CancellationToken cancellationToken)
        {
            if (incident.ResolvedAtUtc is not { } resolvedAtUtc)
            {
                return Task.FromResult(false);
            }

            var fromUtc = resolvedAtUtc.AddMinutes(-1);
            var toUtc = resolvedAtUtc.AddMinutes(1);
            return _db.PlatformAuditLogs.AnyAsync(
                l =>
                    l.TenantId == incident.TenantId &&
                    l.Action == PlatformAuditActions.PaymentRecoveryResolvedByProviderRenewal &&
                    l.CreatedAtUtc >= fromUtc &&
                    l.CreatedAtUtc <= toUtc,
                cancellationToken);
        }

        /// <summary>
        /// Por qué NO se puede reabrir un cierre (null = se puede). Única definición de las guardas,
        /// compartida por el listado y la acción: incidente base Resolved; suscripción recurrente del
        /// mismo plan, sin cancelación y en Activa/Morosa; SIN pago confirmado posterior al fallo; SIN
        /// expire del proveedor avanzado; y sin otro incidente base vivo o más reciente.
        /// </summary>
        private async Task<string?> DescribeReopenBlockerAsync(
            SubscriptionPaymentIncident incident,
            Suscripcion? subscription,
            CancellationToken cancellationToken)
        {
            if (incident.Scope != PaymentIncidentScope.BasePlan || incident.Status != PaymentIncidentStatus.Resolved)
            {
                return "Solo se reabren incidentes del plan base cerrados como resueltos.";
            }

            if (subscription is null ||
                subscription.TilopayRecurringPlanId != incident.TilopayRecurringPlanId)
            {
                return "La suscripción actual ya no corresponde al plan del incidente (cambio de plan posterior).";
            }

            if (subscription.CancelAtPeriodEnd || subscription.Estado is not (EstadoSuscripcion.Activa or EstadoSuscripcion.Morosa))
            {
                return $"La suscripción está en un estado que no se reabre automáticamente ({subscription.Estado}{(subscription.CancelAtPeriodEnd ? ", renovación cancelada" : string.Empty)}).";
            }

            var paidAfterFailure = await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .AnyAsync(p =>
                    p.TenantId == incident.TenantId &&
                    p.TilopayRecurringPlanId == incident.TilopayRecurringPlanId &&
                    p.Estado == EstadoPagoProveedor.Confirmado &&
                    p.FechaConfirmacionUtc > incident.FailureDetectedAtUtc,
                    cancellationToken);
            if (paidAfterFailure)
            {
                return "Hay un pago confirmado posterior al fallo: la resolución fue correcta.";
            }

            if (PaymentRecoveryRenewalEvidence.ProviderShowsNewPaidPeriod(
                    subscription.ProviderStatusRaw,
                    subscription.ProviderExpiresAtUtc,
                    incident.FailureDetectedAtUtc))
            {
                return "El proveedor muestra un período nuevo (expire avanzado): la resolución fue correcta.";
            }

            var hasNewerOrLiveIncident = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .AnyAsync(i =>
                    i.TenantId == incident.TenantId &&
                    i.Id != incident.Id &&
                    i.Scope == PaymentIncidentScope.BasePlan &&
                    (i.CreatedAtUtc > incident.CreatedAtUtc ||
                     i.Status == PaymentIncidentStatus.Open ||
                     i.Status == PaymentIncidentStatus.GraceExpired ||
                     i.Status == PaymentIncidentStatus.ManualReview),
                    cancellationToken);
            if (hasNewerOrLiveIncident)
            {
                return "Existe otro incidente más reciente o vivo para el tenant: revisar manualmente.";
            }

            return null;
        }

        /// <summary>
        /// Cierre manual de un incidente (Resolved/Ignored) por SuperAdmin. Resuelve el tenant a partir
        /// del incidente y escribe bajo BeginScope(tenantId) para el RLS. NO cambia el Estado/acceso de
        /// la suscripción (reactivar un suspendido es una acción de ciclo de vida aparte). Idempotente.
        /// </summary>
        private async Task<PaymentRecoveryActionResult> CloseIncidentAsync(
            Guid incidentId,
            string actorUserId,
            string actorEmail,
            PaymentIncidentStatus targetStatus,
            string auditAction,
            string? reason,
            string successMessage,
            CancellationToken cancellationToken)
        {
            if (incidentId == Guid.Empty)
            {
                return PaymentRecoveryActionResult.Fail("Incidente no especificado.");
            }

            var tenantId = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(i => i.Id == incidentId)
                .Select(i => (Guid?)i.TenantId)
                .FirstOrDefaultAsync(cancellationToken);

            if (tenantId is not { } owner || owner == Guid.Empty)
            {
                return PaymentRecoveryActionResult.Fail("No se encontró el incidente.");
            }

            using var scope = _tenantExecutionContextAccessor.BeginScope(owner);
            var nowUtc = GetUtcNow();

            var incident = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.Id == incidentId && i.TenantId == owner, cancellationToken);
            if (incident is null)
            {
                return PaymentRecoveryActionResult.Fail("No se encontró el incidente.");
            }

            if (incident.Status == targetStatus)
            {
                return PaymentRecoveryActionResult.Ok(successMessage); // idempotente
            }

            incident.Status = targetStatus;
            incident.UpdatedAtUtc = nowUtc;
            if (targetStatus == PaymentIncidentStatus.Resolved)
            {
                incident.ResolvedAtUtc = nowUtc;
            }

            // Solo se limpia el estado de recuperación de la suscripción si NO quedan otros incidentes
            // vivos (otro incidente abierto/vencido/revisión sigue siendo válido y no debe borrarse).
            var hasOtherLiveIncidents = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .AnyAsync(i =>
                    i.TenantId == owner &&
                    i.Id != incidentId &&
                    (i.Status == PaymentIncidentStatus.Open ||
                     i.Status == PaymentIncidentStatus.GraceExpired ||
                     i.Status == PaymentIncidentStatus.ManualReview),
                    cancellationToken);

            var subscription = await LoadSubscriptionAsync(owner, cancellationToken);
            if (subscription is not null)
            {
                subscription.FechaUltimaActualizacionUtc = nowUtc;

                if (!hasOtherLiveIncidents)
                {
                    // Una suspensión REAL (acceso cortado) no se limpia ni se reactiva desde acá:
                    // reactivar un suspendido es una acción de ciclo de vida explícita.
                    var reallySuspended =
                        subscription.Estado == EstadoSuscripcion.Suspendida ||
                        string.Equals(subscription.PaymentRecoveryStatus, "Suspended", StringComparison.Ordinal);

                    subscription.LastPaymentFailedAtUtc = null;
                    subscription.LastPaymentRecoveryNotificationAtUtc = null;

                    if (!reallySuspended)
                    {
                        subscription.PaymentRecoveryStatus = null;
                        subscription.FechaFinGraciaUtc = null;

                        // Si la morosidad la causó recovery y la suscripción sigue vigente (sin otros
                        // bloqueos), al RESOLVER se vuelve a Activa para que la UI quede consistente.
                        if (targetStatus == PaymentIncidentStatus.Resolved &&
                            subscription.Estado == EstadoSuscripcion.Morosa &&
                            !subscription.CancelAtPeriodEnd &&
                            subscription.ProviderPausedAtUtc is null &&
                            SubscriptionEffectiveDates.GetEffectiveEndUtc(subscription.FechaFin, subscription.ProviderExpiresAtUtc) is { } end &&
                            end > nowUtc)
                        {
                            subscription.Estado = EstadoSuscripcion.Activa;
                            subscription.MotivoEstado = "Incidente de pago resuelto manualmente por soporte.";
                            _accessCache?.Invalidate(owner);
                        }
                    }
                }
            }

            var detail = reason is null
                ? $"Incidente {targetStatus} manualmente por soporte."
                : $"Incidente marcado {targetStatus}. Motivo: {Trim(reason, 250)}";
            _db.PlatformAuditLogs.Add(BuildAudit(auditAction, owner, incidentId.ToString(), detail, nowUtc));

            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Incidente de recuperación {IncidentId} cerrado manualmente ({Status}) por {Actor}.",
                incidentId, targetStatus, actorEmail);
            return PaymentRecoveryActionResult.Ok(successMessage);
        }

        private async Task<GraceExpirationOutcome> ExpireOneGraceAsync(Guid incidentId, Guid tenantId, DateTime nowUtc, CancellationToken cancellationToken)
        {
            using var scope = _tenantExecutionContextAccessor.BeginScope(tenantId);

            var incident = await _db.SubscriptionPaymentIncidents
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.Id == incidentId && i.TenantId == tenantId, cancellationToken);

            // Re-verificación bajo tracked: pudo resolverse entre la lectura y ahora.
            var alreadyGraceExpired = incident?.Status == PaymentIncidentStatus.GraceExpired;
            if (incident is null ||
                !(incident.Status == PaymentIncidentStatus.Open ||
                  (alreadyGraceExpired && _options.AutoSuspendAfterGrace)) ||
                incident.GraceEndsAtUtc is null ||
                incident.GraceEndsAtUtc > nowUtc)
            {
                return GraceExpirationOutcome.Skipped;
            }

            var subscription = await LoadSubscriptionAsync(tenantId, cancellationToken);

            // Renovación cancelada: el fallo no es accionable (la cancelación manda). Se marca Ignored.
            if (subscription?.CancelAtPeriodEnd == true)
            {
                incident.Status = PaymentIncidentStatus.Ignored;
                incident.UpdatedAtUtc = nowUtc;
                await _db.SaveChangesAsync(cancellationToken);
                return GraceExpirationOutcome.Ignored;
            }

            // Solo se suspende con AutoSuspend=true Y cuando el período pagado (local/proveedor) ya
            // venció: nunca se corta acceso ya pagado.
            var effectiveEndUtc = subscription is null
                ? null
                : SubscriptionEffectiveDates.GetEffectiveEndUtc(subscription.FechaFin, subscription.ProviderExpiresAtUtc);
            var paidPeriodActive = effectiveEndUtc is { } end && end > nowUtc;

            if (alreadyGraceExpired)
            {
                // Segunda etapa de un GraceExpired en dry-run: solo aplica la suspensión pendiente.
                // Ya suspendido (o período pagado aún vigente) ⇒ nada que hacer (idempotente).
                if (paidPeriodActive ||
                    subscription is null ||
                    subscription.Estado is not (EstadoSuscripcion.Activa or EstadoSuscripcion.Morosa))
                {
                    return GraceExpirationOutcome.Skipped;
                }
            }
            else
            {
                // Marcar GraceExpired SIEMPRE que la gracia haya vencido: es un ESTADO (no corta acceso
                // por sí solo). El corte de acceso se decide aparte y NUNCA quita un período ya pagado.
                incident.Status = PaymentIncidentStatus.GraceExpired;
                incident.UpdatedAtUtc = nowUtc;

                _db.PlatformAuditLogs.Add(BuildAudit(
                    PlatformAuditActions.SubscriptionPaymentGraceExpired,
                    tenantId, incident.Id.ToString(),
                    $"Gracia de pago vencida ({incident.GraceEndsAtUtc:yyyy-MM-dd HH:mm} UTC). AutoSuspend {_options.AutoSuspendAfterGrace}.",
                    nowUtc));
            }

            if (_options.AutoSuspendAfterGrace && !paidPeriodActive)
            {
                if (subscription is not null &&
                    subscription.Estado is EstadoSuscripcion.Activa or EstadoSuscripcion.Morosa)
                {
                    subscription.Estado = EstadoSuscripcion.Suspendida;
                    subscription.PaymentRecoveryStatus = "Suspended";
                    subscription.MotivoEstado = "Suspendida por impago tras vencer el período de gracia.";
                    subscription.FechaUltimaActualizacionUtc = nowUtc;
                    _accessCache?.Invalidate(tenantId);
                }

                incident.UpdatedAtUtc = nowUtc;

                _db.PlatformAuditLogs.Add(BuildAudit(
                    PlatformAuditActions.SubscriptionSuspendedForNonPayment,
                    tenantId, incident.Id.ToString(),
                    "Acceso suspendido por impago (AutoSuspendAfterGrace=true).",
                    nowUtc));

                await _db.SaveChangesAsync(cancellationToken);
                return GraceExpirationOutcome.Suspended;
            }

            // Dry-run: se conserva el acceso (AutoSuspend=false, o período pagado todavía vigente).
            if (subscription is not null)
            {
                subscription.PaymentRecoveryStatus = "GraceExpired";
                subscription.FechaUltimaActualizacionUtc = nowUtc;
            }

            _db.PlatformAuditLogs.Add(BuildAudit(
                PlatformAuditActions.SubscriptionPaymentGraceExpiredDryRun,
                tenantId, incident.Id.ToString(),
                paidPeriodActive
                    ? "Gracia vencida; acceso conservado porque el período pagado sigue vigente."
                    : "Gracia vencida SIN suspensión (AutoSuspendAfterGrace=false): solo alerta, acceso conservado.",
                nowUtc));

            await _db.SaveChangesAsync(cancellationToken);
            return GraceExpirationOutcome.DryRunMarked;
        }

        // ── Internos ─────────────────────────────────────────────────────────────

        private Task<Suscripcion?> LoadSubscriptionAsync(Guid tenantId, CancellationToken cancellationToken) =>
            _db.Suscripciones
                .IgnoreQueryFilters()
                .Include(s => s.Plan)
                .Where(s => s.TenantId == tenantId)
                .OrderByDescending(s => s.FechaUltimaActualizacionUtc ?? s.FechaInicio)
                .FirstOrDefaultAsync(cancellationToken);

        private async Task<bool> ConfirmedPaymentWinsAsync(Guid tenantId, int? planId, CancellationToken cancellationToken)
        {
            var latestConfirmed = await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .Where(p =>
                    p.TenantId == tenantId &&
                    p.Proveedor == PaymentProviderType.Tilopay &&
                    p.Estado == EstadoPagoProveedor.Confirmado &&
                    (planId == null || p.TilopayRecurringPlanId == planId))
                .MaxAsync(p => (DateTime?)p.FechaConfirmacionUtc, cancellationToken);

            if (latestConfirmed is null)
            {
                return false;
            }

            var latestFailed = await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .Where(p =>
                    p.TenantId == tenantId &&
                    p.Proveedor == PaymentProviderType.Tilopay &&
                    (p.Estado == EstadoPagoProveedor.Fallido || p.Estado == EstadoPagoProveedor.Cancelado) &&
                    (planId == null || p.TilopayRecurringPlanId == planId))
                .MaxAsync(p => (DateTime?)p.FechaActualizacionUtc, cancellationToken);

            // El éxito gana si es igual o posterior al último fallo (o si no hay fallo registrado).
            return latestFailed is null || latestConfirmed >= latestFailed;
        }

        /// <summary>Contacto del tenant por regla de owner (admin &gt; funcionario), no alfabético.</summary>
        private Task<string?> ResolveTenantEmailAsync(Guid tenantId, CancellationToken cancellationToken) =>
            _ownerResolver.ResolveOwnerEmailAsync(tenantId, cancellationToken);

        private PlatformAuditLog BuildAudit(string action, Guid tenantId, string entityId, string reason, DateTime nowUtc) =>
            new()
            {
                Id = Guid.NewGuid(),
                ActorUserId = "system",
                ActorEmail = "system",
                Action = action,
                EntityType = PlatformAuditEntityTypes.Subscription,
                EntityId = entityId,
                TenantId = tenantId,
                Reason = reason.Length <= 500 ? reason : reason[..500],
                CreatedAtUtc = nowUtc
            };

        private static string BuildEventKey(Guid tenantId, int? planId, string? resultCode, DateTime nowUtc) =>
            $"{tenantId:N}:{planId?.ToString() ?? "-"}:{resultCode ?? "-"}:{nowUtc:yyyyMMdd}";

        private DateTime GetUtcNow() => _clock.NowOffset().UtcDateTime;

        private static string? Trim(string? value, int max) =>
            string.IsNullOrEmpty(value) ? value : (value.Length <= max ? value : value[..max]);
    }
}
