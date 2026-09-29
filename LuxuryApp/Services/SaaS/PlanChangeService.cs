using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Billing;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.SaaS
{
    /// <summary>Datos para iniciar un cambio de plan (origen + destino).</summary>
    public sealed record PlanChangeRequest
    {
        public required Guid TenantId { get; init; }

        public Guid? FromPlanId { get; init; }
        public string? FromPlanCode { get; init; }
        public int? FromWorkerCount { get; init; }
        public int? FromTilopayRecurringPlanId { get; init; }
        public string? FromProviderSubscriptionId { get; init; }

        public required Guid ToPlanId { get; init; }
        public required string ToPlanCode { get; init; }
        public required int ToWorkerCount { get; init; }
        public required BillingCycle ToBillingCycle { get; init; }
        public required int ToTilopayRecurringPlanId { get; init; }
    }

    public sealed record PlanChangeStartResult
    {
        public PlanChangeIntent? Intent { get; init; }
        public string? Error { get; init; }
        public bool Succeeded => Intent is not null && string.IsNullOrEmpty(Error);

        public static PlanChangeStartResult Ok(PlanChangeIntent intent) => new() { Intent = intent };
        public static PlanChangeStartResult Fail(string error) => new() { Error = error };
    }

    public interface IPlanChangeService
    {
        Task<PlanChangeIntent?> GetOpenIntentAsync(Guid tenantId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Crea (o reutiliza) el intento de cambio de plan. Anti doble-cambio: si ya hay un
        /// intento Pending para OTRO plan destino, lo rechaza; si es el mismo destino, lo reutiliza.
        /// Un Pending SIN pago asociado hacia otro destino no representa dinero y se supersede en
        /// vez de trabar al tenant.
        /// </summary>
        Task<PlanChangeStartResult> CreateOrReuseAsync(PlanChangeRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Cierra el intento Pending que quedó huérfano porque el checkout nunca llegó a abrirse.
        /// Solo toca Pending SIN pago asociado: si hay un PagoSuscripcion enlazado puede haber
        /// dinero en juego y el caso es de la reconciliación, no de aquí. Nunca toca Applied.
        /// </summary>
        Task ExpirePendingAfterBlockedCheckoutAsync(
            Guid tenantId,
            Guid intentId,
            string reason,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Marca aplicado el intento del tenant que apunta al plan recurrente recién activado.
        /// Idempotente. Si la suscripción anterior del proveedor difiere, la marca para
        /// cancelación manual y emite una alerta de plataforma. Sin intento abierto => no-op
        /// (fue una alta nueva, no un cambio).
        /// </summary>
        Task ApplyAppliedAsync(
            Guid tenantId,
            int appliedRecurringPlanId,
            string? newProviderSubscriptionId,
            CancellationToken cancellationToken = default);
    }

    public sealed class PlanChangeService : IPlanChangeService
    {
        private readonly ApplicationDbContext _db;
        private readonly ILogger<PlanChangeService> _logger;

        public PlanChangeService(ApplicationDbContext db, ILogger<PlanChangeService> logger)
        {
            _db = db;
            _logger = logger;
        }

        public Task<PlanChangeIntent?> GetOpenIntentAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            _db.PlanChangeIntents
                .IgnoreQueryFilters()
                .Where(intent => intent.TenantId == tenantId && intent.Estado == PlanChangeIntentState.Pending)
                .OrderByDescending(intent => intent.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

        public async Task<PlanChangeStartResult> CreateOrReuseAsync(
            PlanChangeRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var open = await GetOpenIntentAsync(request.TenantId, cancellationToken);
            if (open is not null)
            {
                if (open.ToTilopayRecurringPlanId == request.ToTilopayRecurringPlanId)
                {
                    // Mismo destino: se reutiliza, pero refrescando el ORIGEN. Un intent viejo puede
                    // traer un From* de cuando el tenant estaba en otro plan, y ese dato es el que
                    // luego decide QUÉ suscriptor se cancela: reutilizarlo tal cual podría dar de
                    // baja al suscriptor equivocado.
                    RefreshOrigin(open, request);
                    await _db.SaveChangesAsync(cancellationToken);
                    return PlanChangeStartResult.Ok(open);
                }

                // Otro destino. La pregunta no es "¿tiene pago?" sino "¿hay DINERO detrás?": un
                // checkout abierto y nunca pagado deja un PagoSuscripcion Pendiente vacío, y eso no
                // puede trabar al cliente para siempre (el índice único permite un Pending por
                // tenant). Con cualquier señal de dinero, en cambio, no se toca nada.
                var openPayment = await LoadPaymentAsync(open, cancellationToken);
                var hasProviderEvent = await HasProviderEventAsync(open.PagoSuscripcionId, cancellationToken);

                if (PlanChangeCheckoutAbandonmentRules.HasMoneySignals(openPayment, hasProviderEvent))
                {
                    return PlanChangeStartResult.Fail(
                        $"Ya tenés un cambio de plan en proceso (hacia {open.ToPlanCode}). Completá ese pago o cancelalo antes de iniciar otro.");
                }

                var nowUtc = DateTime.UtcNow;
                open.Estado = PlanChangeIntentState.Superseded;
                open.UpdatedAtUtc = nowUtc;
                open.Notes = Trim(
                    $"Reemplazado por un cambio nuevo hacia {request.ToPlanCode}; el checkout anterior nunca se pagó.",
                    300);

                // El checkout viejo también se cierra: dejarlo Pendiente permitiría que un webhook
                // tardío lo confirmara y aplicara un cambio que el cliente ya descartó.
                if (openPayment is not null && openPayment.Estado == EstadoPagoProveedor.Pendiente)
                {
                    openPayment.Estado = EstadoPagoProveedor.Expirado;
                    openPayment.ProviderResultCode = "EXPIRED_PLAN_CHANGE_CHECKOUT";
                    openPayment.ProviderResultMessage = Trim(
                        $"Checkout de cambio hacia {open.ToPlanCode} reemplazado por uno nuevo hacia {request.ToPlanCode} sin haberse pagado.",
                        300);
                    openPayment.FechaActualizacionUtc = nowUtc;
                }

                _db.PlatformAuditLogs.Add(new PlatformAuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorUserId = "system",
                    ActorEmail = "system",
                    Action = PlatformAuditActions.PlanChangePendingCheckoutSuperseded,
                    EntityType = PlatformAuditEntityTypes.Subscription,
                    EntityId = open.Id.ToString(),
                    TenantId = request.TenantId,
                    Reason = Trim(
                        $"Intento Pending hacia {open.ToPlanCode} reemplazado por un cambio nuevo hacia {request.ToPlanCode}. Sin dinero de por medio: el checkout anterior nunca se pagó (sin transacción, sin suscriptor, sin confirmación).",
                        500),
                    CreatedAtUtc = nowUtc
                });

                await _db.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "PlanChangeIntent Pending sin dinero reemplazado por un cambio nuevo. TenantId {TenantId}. IntentId {IntentId}. De {From} a {To}.",
                    request.TenantId,
                    open.Id,
                    open.ToPlanCode,
                    request.ToPlanCode);
            }

            var intent = new PlanChangeIntent
            {
                Id = Guid.NewGuid(),
                TenantId = request.TenantId,
                FromPlanId = request.FromPlanId,
                FromPlanCode = request.FromPlanCode,
                FromWorkerCount = request.FromWorkerCount,
                FromTilopayRecurringPlanId = request.FromTilopayRecurringPlanId,
                FromProviderSubscriptionId = request.FromProviderSubscriptionId,
                ToPlanId = request.ToPlanId,
                ToPlanCode = request.ToPlanCode,
                ToWorkerCount = request.ToWorkerCount,
                ToBillingCycle = request.ToBillingCycle,
                ToTilopayRecurringPlanId = request.ToTilopayRecurringPlanId,
                Estado = PlanChangeIntentState.Pending,
                OldProviderCancellation = ProviderCancellationState.NotRequired,
                CreatedAtUtc = DateTime.UtcNow
            };

            _db.PlanChangeIntents.Add(intent);

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // El índice único filtrado (un Pending por tenant) bloqueó una carrera de doble click.
                _db.Entry(intent).State = EntityState.Detached;
                var existing = await GetOpenIntentAsync(request.TenantId, cancellationToken);
                if (existing is not null && existing.ToTilopayRecurringPlanId == request.ToTilopayRecurringPlanId)
                {
                    return PlanChangeStartResult.Ok(existing);
                }

                return PlanChangeStartResult.Fail(
                    "Ya hay un cambio de plan en proceso para tu cuenta. Esperá a que termine o cancelalo antes de iniciar otro.");
            }

            return PlanChangeStartResult.Ok(intent);
        }

        public async Task ExpirePendingAfterBlockedCheckoutAsync(
            Guid tenantId,
            Guid intentId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            var intent = await _db.PlanChangeIntents
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    current =>
                        current.Id == intentId &&
                        current.TenantId == tenantId &&
                        current.Estado == PlanChangeIntentState.Pending &&
                        current.PagoSuscripcionId == null,
                    cancellationToken);

            if (intent is null)
            {
                // No existe, ya no está Pending, o tiene un pago enlazado (posible dinero real).
                return;
            }

            intent.Estado = PlanChangeIntentState.Cancelled;
            intent.UpdatedAtUtc = DateTime.UtcNow;
            intent.Notes = Trim($"Cerrado sin checkout: {reason}", 300);

            _db.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = "system",
                ActorEmail = "system",
                Action = PlatformAuditActions.PlanChangePendingIntentExpiredAfterBlockedCheckout,
                EntityType = PlatformAuditEntityTypes.Subscription,
                EntityId = intent.Id.ToString(),
                TenantId = tenantId,
                Reason = Trim(
                    $"Intento hacia {intent.ToPlanCode} cerrado porque el checkout no se pudo abrir y no quedó pago asociado. Así no bloquea el próximo intento. Motivo: {reason}",
                    500),
                CreatedAtUtc = DateTime.UtcNow
            });

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "PlanChangeIntent Pending cerrado tras checkout bloqueado. TenantId {TenantId}. IntentId {IntentId}. To {ToPlanCode}.",
                tenantId,
                intent.Id,
                intent.ToPlanCode);
        }

        /// <summary>
        /// Refresca el ORIGEN del intent con el estado actual del tenant. El destino no se toca:
        /// es lo que el usuario eligió y lo que ya se está reutilizando.
        /// </summary>
        private static void RefreshOrigin(PlanChangeIntent intent, PlanChangeRequest request)
        {
            if (intent.FromPlanId == request.FromPlanId &&
                intent.FromPlanCode == request.FromPlanCode &&
                intent.FromWorkerCount == request.FromWorkerCount &&
                intent.FromTilopayRecurringPlanId == request.FromTilopayRecurringPlanId &&
                intent.FromProviderSubscriptionId == request.FromProviderSubscriptionId)
            {
                return;
            }

            intent.FromPlanId = request.FromPlanId;
            intent.FromPlanCode = request.FromPlanCode;
            intent.FromWorkerCount = request.FromWorkerCount;
            intent.FromTilopayRecurringPlanId = request.FromTilopayRecurringPlanId;
            intent.FromProviderSubscriptionId = request.FromProviderSubscriptionId;
            intent.UpdatedAtUtc = DateTime.UtcNow;
        }

        private static string Trim(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength];

        /// <summary>Pago del checkout del intento, si llegó a crearse. Tracked: puede haber que cerrarlo.</summary>
        private async Task<PagoSuscripcion?> LoadPaymentAsync(PlanChangeIntent intent, CancellationToken cancellationToken)
        {
            if (intent.PagoSuscripcionId is not { } paymentId)
            {
                return null;
            }

            return await _db.PagosSuscripcion
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(p => p.Id == paymentId && p.TenantId == intent.TenantId, cancellationToken);
        }

        /// <summary>True si TiloPay ya tocó este intento con algún webhook: entonces no es un abandono limpio.</summary>
        private async Task<bool> HasProviderEventAsync(Guid? paymentId, CancellationToken cancellationToken)
        {
            if (paymentId is not { } id)
            {
                return false;
            }

            return await _db.EventosPago
                .IgnoreQueryFilters()
                .AnyAsync(e => e.PagoSuscripcionId == id, cancellationToken);
        }

        public async Task ApplyAppliedAsync(
            Guid tenantId,
            int appliedRecurringPlanId,
            string? newProviderSubscriptionId,
            CancellationToken cancellationToken = default)
        {
            var intent = await _db.PlanChangeIntents
                .IgnoreQueryFilters()
                .Where(current =>
                    current.TenantId == tenantId &&
                    current.Estado == PlanChangeIntentState.Pending &&
                    current.ToTilopayRecurringPlanId == appliedRecurringPlanId)
                .OrderByDescending(current => current.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (intent is null)
            {
                // No había cambio en curso para este plan: fue una alta/renovación normal.
                return;
            }

            var nowUtc = DateTime.UtcNow;
            intent.Estado = PlanChangeIntentState.Applied;
            intent.NewProviderSubscriptionId = newProviderSubscriptionId;
            intent.AppliedAtUtc = nowUtc;
            intent.UpdatedAtUtc = nowUtc;

            var oldSubscriberId = intent.FromProviderSubscriptionId?.Trim();
            var requiresCancellation =
                !string.IsNullOrWhiteSpace(oldSubscriberId) &&
                !string.Equals(oldSubscriberId, newProviderSubscriptionId?.Trim(), StringComparison.OrdinalIgnoreCase);

            if (requiresCancellation)
            {
                intent.OldProviderCancellation = ProviderCancellationState.PendingManualCancellation;
                intent.Notes = $"Cancelar manualmente en TiloPay la suscripción anterior {oldSubscriberId} (plan {intent.FromPlanCode}).";

                _db.PlatformAuditLogs.Add(new PlatformAuditLog
                {
                    Id = Guid.NewGuid(),
                    ActorUserId = "system",
                    ActorEmail = "system",
                    Action = PlatformAuditActions.PlanUpgradeRequiresProviderCancellation,
                    EntityType = PlatformAuditEntityTypes.Subscription,
                    EntityId = intent.Id.ToString(),
                    TenantId = tenantId,
                    Reason = $"Upgrade de {intent.FromPlanCode} a {intent.ToPlanCode}. Cancelar la suscripción TiloPay anterior {oldSubscriberId} para evitar doble cobro.",
                    CreatedAtUtc = nowUtc
                });

                _logger.LogWarning(
                    "Upgrade aplicado: requiere cancelar manualmente la suscripción anterior en TiloPay. TenantId {TenantId}. From {FromCode} ({FromSubscriber}). To {ToCode} ({ToSubscriber}).",
                    tenantId,
                    intent.FromPlanCode,
                    oldSubscriberId,
                    intent.ToPlanCode,
                    newProviderSubscriptionId);
            }
            else
            {
                intent.OldProviderCancellation = ProviderCancellationState.NotRequired;
            }

            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
