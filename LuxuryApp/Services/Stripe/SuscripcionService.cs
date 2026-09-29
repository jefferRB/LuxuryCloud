using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.BusinessTime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.SaaS
{
    public class SuscripcionService
    {
        private readonly ApplicationDbContext _db;
        private readonly IMemoryCache _cache;
        private readonly ITenantCommercialAccessCache _accessCache;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly TilopayRepeatOptions _tilopayRepeatOptions;
        private readonly Billing.BillingPaymentRecoveryOptions? _recoveryOptions;
        private readonly ILogger<SuscripcionService> _logger;

        public SuscripcionService(
            ApplicationDbContext db,
            IMemoryCache cache,
            ITenantCommercialAccessCache accessCache,
            IBusinessDateTimeProvider businessDateTimeProvider,
            IOptions<TilopayRepeatOptions> tilopayRepeatOptions,
            ILogger<SuscripcionService> logger,
            IOptions<Billing.BillingPaymentRecoveryOptions>? recoveryOptions = null)
        {
            _db = db;
            _cache = cache;
            _accessCache = accessCache;
            _businessDateTimeProvider = businessDateTimeProvider;
            _tilopayRepeatOptions = tilopayRepeatOptions.Value;
            _recoveryOptions = recoveryOptions?.Value;
            _logger = logger;
        }

        public async Task ActivarSuscripcionAsync(
            Guid tenantId,
            Guid planId,
            PaymentProviderType provider,
            string? providerCustomerId,
            string? providerSubscriptionId,
            string? providerPaymentLinkId,
            string? providerTransactionId,
            string? providerReference,
            DateTime? trialEnd = null,
            string? motivo = null,
            CancellationToken cancellationToken = default)
        {
            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["TenantId"] = tenantId,
                ["PlanId"] = planId,
                ["Provider"] = provider,
                ["ProviderReference"] = providerReference
            });

            var suscripcion = await _db.Suscripciones
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

            var nowUtc = GetUtcNow();
            var nuevoEstado = trialEnd.HasValue ? EstadoSuscripcion.Trial : EstadoSuscripcion.Activa;
            var (periodStartUtc, periodEndUtc) = ResolveNextBillingPeriod(
                nowUtc,
                suscripcion?.FechaFin,
                shouldExtendExistingPeriod: provider == PaymentProviderType.Tilopay && !trialEnd.HasValue);

            if (suscripcion is null)
            {
                suscripcion = new Suscripcion
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    PlanId = planId,
                    Proveedor = provider,
                    ProviderCustomerId = providerCustomerId,
                    ProviderSubscriptionId = providerSubscriptionId,
                    ProviderPaymentLinkId = providerPaymentLinkId,
                    ProviderTransactionId = providerTransactionId,
                    ProviderReference = providerReference,
                    Estado = nuevoEstado,
                    FechaInicio = periodStartUtc,
                    FechaFin = provider == PaymentProviderType.Tilopay && !trialEnd.HasValue
                        ? periodEndUtc
                        : null,
                    FechaTrialFin = trialEnd,
                    FechaProximoCobroUtc = provider == PaymentProviderType.Tilopay && !trialEnd.HasValue
                        ? periodEndUtc
                        : null,
                    FechaFinGraciaUtc = null,
                    FechaCancelacionUtc = null,
                    FechaUltimaActualizacionUtc = nowUtc,
                    MotivoEstado = motivo,
                    CancelAtPeriodEnd = false
                };

                _db.Suscripciones.Add(suscripcion);
            }
            else
            {
                var planAnterior = suscripcion.PlanId;
                var estadoAnterior = suscripcion.Estado;

                suscripcion.PlanId = planId;
                suscripcion.Proveedor = provider;
                suscripcion.ProviderCustomerId = providerCustomerId ?? suscripcion.ProviderCustomerId;
                suscripcion.ProviderSubscriptionId = providerSubscriptionId ?? suscripcion.ProviderSubscriptionId;
                suscripcion.ProviderPaymentLinkId = providerPaymentLinkId ?? suscripcion.ProviderPaymentLinkId;
                suscripcion.ProviderTransactionId = providerTransactionId ?? suscripcion.ProviderTransactionId;
                suscripcion.ProviderReference = providerReference ?? suscripcion.ProviderReference;
                suscripcion.Estado = nuevoEstado;
                suscripcion.FechaInicio = periodStartUtc;
                suscripcion.FechaFin = provider == PaymentProviderType.Tilopay && !trialEnd.HasValue
                    ? periodEndUtc
                    : null;
                suscripcion.FechaTrialFin = trialEnd;
                suscripcion.FechaProximoCobroUtc = provider == PaymentProviderType.Tilopay && !trialEnd.HasValue
                    ? periodEndUtc
                    : null;
                suscripcion.FechaFinGraciaUtc = null;
                suscripcion.FechaCancelacionUtc = null;
                suscripcion.CancelAtPeriodEnd = false;
                suscripcion.FechaUltimaActualizacionUtc = nowUtc;
                suscripcion.MotivoEstado = motivo;

                if (planAnterior != planId || estadoAnterior != nuevoEstado)
                {
                    _db.HistorialSuscripciones.Add(new HistorialSuscripcion
                    {
                        Id = Guid.NewGuid(),
                        SuscripcionId = suscripcion.Id,
                        PlanIdAnterior = planAnterior,
                        PlanIdNuevo = planId,
                        FechaCambio = nowUtc,
                        Proveedor = provider,
                        Motivo = motivo ?? "Actualizacion de suscripcion"
                    });
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            InvalidateSubscriptionCache(tenantId);

            _logger.LogInformation(
                "Suscripcion activada o actualizada correctamente. Estado {Estado}.",
                suscripcion.Estado);
        }

        public async Task RegistrarPagoConfirmadoAsync(
            Guid tenantId,
            Guid planId,
            Guid? pagoSuscripcionId,
            PaymentProviderType provider,
            string providerReference,
            string? providerTransactionId,
            string? providerAuthorizationCode,
            decimal monto,
            string moneda,
            string? motivo = null,
            CancellationToken cancellationToken = default)
        {
            var suscripcion = await EnsureSubscriptionForPaymentAsync(
                tenantId,
                planId,
                provider,
                providerReference,
                providerTransactionId,
                cancellationToken);

            var nowUtc = GetUtcNow();
            var facturaExiste = await _db.Facturas
                .IgnoreQueryFilters()
                .AnyAsync(
                    factura => factura.Proveedor == provider &&
                    (
                        (!string.IsNullOrWhiteSpace(providerTransactionId) &&
                         factura.ProviderTransactionId == providerTransactionId) ||
                        factura.ProviderReference == providerReference
                    ),
                    cancellationToken);

            if (!facturaExiste)
            {
                _db.Facturas.Add(new Factura
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    SuscripcionId = suscripcion.Id,
                    PagoSuscripcionId = pagoSuscripcionId,
                    Proveedor = provider,
                    ProviderInvoiceId = providerReference,
                    ProviderTransactionId = providerTransactionId,
                    ProviderReference = providerReference,
                    Monto = monto,
                    Moneda = moneda,
                    Estado = "Pagado",
                    Fecha = nowUtc
                });
            }

            suscripcion.Estado = EstadoSuscripcion.Activa;
            suscripcion.ProviderTransactionId = providerTransactionId ?? suscripcion.ProviderTransactionId;
            suscripcion.ProviderReference = providerReference;
            suscripcion.FechaUltimoPagoUtc = nowUtc;
            suscripcion.FechaUltimaActualizacionUtc = nowUtc;
            suscripcion.MotivoEstado = motivo ?? "Pago confirmado";
            suscripcion.CancelAtPeriodEnd = false;
            suscripcion.FechaFinGraciaUtc = null;
            suscripcion.FechaCancelacionUtc = null;

            if (provider == PaymentProviderType.Tilopay &&
                suscripcion.Estado != EstadoSuscripcion.Trial)
            {
                var (periodStartUtc, periodEndUtc) = ResolveNextBillingPeriod(
                    nowUtc,
                    suscripcion.FechaFin,
                    shouldExtendExistingPeriod: true);

                suscripcion.FechaInicio = periodStartUtc;
                suscripcion.FechaFin = periodEndUtc;
                suscripcion.FechaProximoCobroUtc = periodEndUtc;
            }

            await _db.SaveChangesAsync(cancellationToken);
            InvalidateSubscriptionCache(tenantId);

            _logger.LogInformation("Pago confirmado registrado correctamente.");
        }

        public async Task RegistrarPagoFallidoAsync(
            Guid tenantId,
            Guid planId,
            Guid? pagoSuscripcionId,
            PaymentProviderType provider,
            string providerReference,
            string? providerTransactionId,
            decimal monto,
            string moneda,
            string? motivo = null,
            CancellationToken cancellationToken = default)
        {
            var suscripcion = await _db.Suscripciones
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

            var nowUtc = GetUtcNow();

            if (suscripcion is null)
            {
                suscripcion = new Suscripcion
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    PlanId = planId,
                    Proveedor = provider,
                    ProviderReference = providerReference,
                    ProviderTransactionId = providerTransactionId,
                    Estado = EstadoSuscripcion.Fallida,
                    FechaInicio = nowUtc,
                    FechaUltimaActualizacionUtc = nowUtc,
                    MotivoEstado = motivo ?? "Pago fallido"
                };

                _db.Suscripciones.Add(suscripcion);
            }
            else
            {
                suscripcion.PlanId = planId;
                suscripcion.Proveedor = provider;
                suscripcion.ProviderReference = providerReference;
                suscripcion.ProviderTransactionId = providerTransactionId ?? suscripcion.ProviderTransactionId;
                // Reintento rechazado del MISMO ciclo (ya Morosa con gracia): se conserva el estado y la
                // gracia original. Antes, Morosa caía a Fallida (acceso cortado en pleno período de
                // gracia) y la gracia se recalculaba desde cada reintento (se extendía sin fin).
                var retryWithinGrace = suscripcion.Estado == EstadoSuscripcion.Morosa &&
                                       suscripcion.FechaFinGraciaUtc.HasValue;

                suscripcion.Estado = suscripcion.Estado is EstadoSuscripcion.Activa or EstadoSuscripcion.Trial or EstadoSuscripcion.Morosa
                    ? EstadoSuscripcion.Morosa
                    : EstadoSuscripcion.Fallida;
                if (!retryWithinGrace)
                {
                    suscripcion.FechaFinGraciaUtc = ResolveGracePeriodEndsUtc(suscripcion.FechaFin, nowUtc);
                }
                suscripcion.FechaUltimaActualizacionUtc = nowUtc;
                suscripcion.MotivoEstado = motivo ?? "Pago fallido";
            }

            var facturaExiste = await _db.Facturas
                .IgnoreQueryFilters()
                .AnyAsync(
                    factura => factura.Proveedor == provider &&
                    (
                        (!string.IsNullOrWhiteSpace(providerTransactionId) &&
                         factura.ProviderTransactionId == providerTransactionId) ||
                        factura.ProviderReference == providerReference
                    ),
                    cancellationToken);

            if (!facturaExiste)
            {
                _db.Facturas.Add(new Factura
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    SuscripcionId = suscripcion.Id,
                    PagoSuscripcionId = pagoSuscripcionId,
                    Proveedor = provider,
                    ProviderInvoiceId = providerReference,
                    ProviderTransactionId = providerTransactionId,
                    ProviderReference = providerReference,
                    Monto = monto,
                    Moneda = moneda,
                    Estado = "Fallido",
                    Fecha = nowUtc
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
            InvalidateSubscriptionCache(tenantId);

            _logger.LogWarning("Pago fallido registrado. Estado de suscripcion actualizado a {Estado}.", suscripcion.Estado);
        }

        public async Task CancelarSuscripcionAsync(
            string providerSubscriptionId,
            bool cancelAtPeriodEnd,
            CancellationToken cancellationToken = default)
        {
            var suscripcion = await _db.Suscripciones
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    s => s.ProviderSubscriptionId == providerSubscriptionId,
                    cancellationToken);

            if (suscripcion is null)
            {
                return;
            }

            suscripcion.CancelAtPeriodEnd = cancelAtPeriodEnd;
            suscripcion.FechaUltimaActualizacionUtc = GetUtcNow();
            suscripcion.MotivoEstado = cancelAtPeriodEnd
                ? "Cancelacion programada"
                : "Cancelacion inmediata";

            if (!cancelAtPeriodEnd)
            {
                suscripcion.Estado = EstadoSuscripcion.Cancelada;
                suscripcion.FechaCancelacionUtc = GetUtcNow();
                suscripcion.FechaFin = suscripcion.FechaCancelacionUtc;
            }

            await _db.SaveChangesAsync(cancellationToken);
            InvalidateSubscriptionCache(suscripcion.TenantId);
        }

        public async Task ActualizarEstadoDesdeStripeAsync(
            string providerSubscriptionId,
            EstadoSuscripcion nuevoEstado,
            bool cancelAtPeriodEnd,
            CancellationToken cancellationToken = default)
        {
            var suscripcion = await _db.Suscripciones
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(
                    s => s.ProviderSubscriptionId == providerSubscriptionId,
                    cancellationToken);

            if (suscripcion is null)
            {
                return;
            }

            suscripcion.Estado = nuevoEstado;
            suscripcion.CancelAtPeriodEnd = cancelAtPeriodEnd;
            suscripcion.FechaUltimaActualizacionUtc = GetUtcNow();
            suscripcion.MotivoEstado = "Actualizacion de estado desde proveedor externo";

            if (nuevoEstado == EstadoSuscripcion.Cancelada)
            {
                suscripcion.FechaCancelacionUtc = GetUtcNow();
                suscripcion.FechaFin = suscripcion.FechaCancelacionUtc;
            }

            await _db.SaveChangesAsync(cancellationToken);
            InvalidateSubscriptionCache(suscripcion.TenantId);
        }

        public async Task ActivarSuscripcionRecurrenteAsync(
            Guid tenantId,
            Plan plan,
            int tilopayRecurringPlanId,
            string? providerSubscriberId,
            string? providerTransactionId,
            string? providerReference,
            string? motivo = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(plan);

            var suscripcion = await _db.Suscripciones
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

            var nowUtc = GetUtcNow();
            var previousPlanId = suscripcion?.PlanId;
            var previousState = suscripcion?.Estado;
            // Solo se extiende el periodo vigente cuando es el MISMO plan (renovacion); un cambio de
            // plan (p.ej. upgrade de funcionarios o mensual<->anual) reinicia el periodo segun el
            // ciclo del plan nuevo para no arrastrar vigencia de un ciclo distinto.
            // Se exige que coincidan AMBOS: la fila Plan y el plan recurrente del proveedor. Si el
            // suscriptor recurrente cambio, es un plan distinto aunque PlanId ya hubiera sido
            // mutado por un webhook previo -> nunca encadenar el ciclo del plan anterior.
            var isSamePlan = suscripcion is not null &&
                             suscripcion.PlanId == plan.Id &&
                             (!suscripcion.TilopayRecurringPlanId.HasValue ||
                              suscripcion.TilopayRecurringPlanId.Value == tilopayRecurringPlanId);
            var (periodStartUtc, periodEndUtc) = ResolveNextBillingPeriod(
                nowUtc,
                suscripcion?.FechaFin,
                shouldExtendExistingPeriod: isSamePlan,
                billingCycle: plan.BillingCycle);

            if (suscripcion is null)
            {
                suscripcion = new Suscripcion
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId
                };

                _db.Suscripciones.Add(suscripcion);
            }

            suscripcion.PlanId = plan.Id;
            suscripcion.CodigoPlan = plan.Codigo ?? plan.Nombre;
            suscripcion.Proveedor = PaymentProviderType.Tilopay;
            suscripcion.TilopayRecurringPlanId = tilopayRecurringPlanId;
            suscripcion.ProviderSubscriptionId = providerSubscriberId ?? suscripcion.ProviderSubscriptionId;
            suscripcion.ProviderTransactionId = providerTransactionId ?? suscripcion.ProviderTransactionId;
            suscripcion.ProviderReference = providerReference ?? suscripcion.ProviderReference;
            suscripcion.Estado = EstadoSuscripcion.Activa;
            suscripcion.FechaInicio = periodStartUtc;
            suscripcion.FechaFin = periodEndUtc;
            suscripcion.FechaTrialFin = null;
            suscripcion.FechaProximoCobroUtc = periodEndUtc;
            suscripcion.FechaFinGraciaUtc = null;
            suscripcion.FechaCancelacionUtc = null;
            // Guardar SIEMPRE el equivalente mensual en Suscripcion.PrecioMensual (no el total
            // anual) para que reportes/portal no se ensucien con un cobro anual de golpe.
            suscripcion.PrecioMensual = ResolveMonthlyEquivalent(plan);
            suscripcion.MonedaFacturacion = string.IsNullOrWhiteSpace(plan.Moneda) ? "CRC" : plan.Moneda;
            suscripcion.MaxFuncionarios = plan.MaxFuncionarios;
            suscripcion.FechaUltimoPagoUtc = nowUtc;
            suscripcion.FechaUltimaActualizacionUtc = nowUtc;
            suscripcion.MotivoEstado = motivo ?? "Suscripcion recurrente activada por Tilopay Repeat.";
            suscripcion.CancelAtPeriodEnd = false;

            if (previousPlanId.HasValue &&
                previousState.HasValue &&
                (previousPlanId.Value != plan.Id || previousState.Value != EstadoSuscripcion.Activa))
            {
                _db.HistorialSuscripciones.Add(new HistorialSuscripcion
                {
                    Id = Guid.NewGuid(),
                    SuscripcionId = suscripcion.Id,
                    PlanIdAnterior = previousPlanId,
                    PlanIdNuevo = plan.Id,
                    FechaCambio = nowUtc,
                    Proveedor = PaymentProviderType.Tilopay,
                    Motivo = motivo ?? "Activacion recurrente Tilopay Repeat."
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
            InvalidateSubscriptionCache(tenantId);
        }

        public async Task ActivarAddonWhatsAppRecurrenteAsync(
            Guid tenantId,
            Plan plan,
            int tilopayRecurringPlanId,
            string? providerSubscriberId,
            string? providerTransactionId,
            string? motivo = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(plan);

            var addon = await _db.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.TenantId == tenantId, cancellationToken);

            var nowUtc = GetUtcNow();
            var isSameAddonPlan = addon is not null &&
                                  (addon.PlanId == plan.Id ||
                                   addon.TilopayRecurringPlanId == tilopayRecurringPlanId ||
                                   string.Equals(
                                       addon.AddonCode,
                                       plan.Codigo ?? plan.Nombre,
                                       StringComparison.OrdinalIgnoreCase));

            // Strategy B: capturar el suscriptor recurrente ANTERIOR antes de sobrescribirlo. Si es un
            // CAMBIO de paquete (WA400→WA800→WA1200 o bajada), el anterior sigue vivo en TiloPay y hay
            // que cancelarlo DESPUÉS de confirmar el nuevo (post-commit). Sin capturarlo aquí se
            // perdería su id al pisar ProviderSubscriptionId y quedaría cobrando para siempre.
            var previousSubscriberId = addon?.ProviderSubscriptionId;
            var previousRecurringPlanId = addon?.TilopayRecurringPlanId;

            var (periodStartUtc, periodEndUtc) = ResolveNextBillingPeriod(
                nowUtc,
                addon?.FechaFin,
                shouldExtendExistingPeriod: isSameAddonPlan && addon is not null && IsWhatsAppAddonActive(addon));

            if (addon is null)
            {
                addon = new TenantSubscriptionAddon
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    CreatedAtUtc = nowUtc
                };

                _db.TenantSubscriptionAddons.Add(addon);
            }

            addon.PlanId = plan.Id;
            addon.AddonCode = plan.Codigo ?? plan.Nombre;
            addon.Estado = EstadoSuscripcion.Activa;
            // Un pago recurrente confirmado marca el add-on como pagado por TiloPay. Si el tenant venía
            // de un acceso manual y ahora PAGA de verdad, pasa a ProviderRecurring y se limpia el rastro
            // de grant manual (la dirección inversa —provider→manual automático— jamás ocurre).
            addon.BillingSource = WhatsAppAddonBillingSource.ProviderRecurring;
            addon.ManualGrantType = null;
            addon.ManualGrantReason = null;
            addon.ManualGrantExpiresAtUtc = null;
            addon.IsManualGrantIndefinite = false;
            addon.RevokedAtUtc = null;
            addon.RevokedByUserId = null;
            addon.RevocationReason = null;
            addon.TilopayRecurringPlanId = tilopayRecurringPlanId;
            addon.ProviderSubscriptionId = providerSubscriberId ?? addon.ProviderSubscriptionId;
            addon.ProviderTransactionId = providerTransactionId ?? addon.ProviderTransactionId;
            addon.PrecioMensual = plan.PrecioMensual;
            addon.MonedaFacturacion = string.IsNullOrWhiteSpace(plan.Moneda) ? "CRC" : plan.Moneda;
            addon.MonthlyMessageLimit = plan.LimiteMensajesMensual ?? 0;
            addon.FechaInicio = periodStartUtc;
            addon.FechaFin = periodEndUtc;
            addon.FechaProximoCobroUtc = periodEndUtc;
            addon.FechaFinGraciaUtc = null;
            addon.FechaCancelacionUtc = null;
            // Una activación pagada revierte cualquier cancelación de renovación previa del add-on.
            addon.CancelAtPeriodEnd = false;
            addon.CancellationEffectiveAtUtc = null;
            addon.CancellationRequestedByUserId = null;
            addon.CancellationReason = null;
            addon.UpdatedAtUtc = nowUtc;

            // Strategy B: si cambió el suscriptor recurrente (paquete distinto), dejar el ANTERIOR
            // pendiente de cancelación en TiloPay. Se cancela DESPUÉS (post-commit del webhook o
            // reconciliación), nunca antes de confirmar el nuevo. Guarda: si el proveedor reutilizó el
            // mismo id (viejo == nuevo), no hay nada que cancelar.
            var newSubscriberId = addon.ProviderSubscriptionId;

            // Un pago recurrente confirmado significa que el suscriptor VIGENTE está cobrando: se
            // limpia cualquier estado de cancelación previo. Es obligatorio incluso al renovar el
            // mismo paquete, porque tras una transición la fila arrastraba ProviderCancellation
            // =Cancelled (que era del suscriptor VIEJO) y eso hacía que la cascada del plan base
            // creyera que el actual ya estaba dado de baja.
            addon.ProviderCancellation = ProviderCancellationState.NotRequired;
            addon.ProviderCancellationSubscriptionId = null;
            addon.ProviderCancelledAtUtc = null;

            if (!isSameAddonPlan &&
                !string.IsNullOrWhiteSpace(previousSubscriberId) &&
                !string.Equals(previousSubscriberId, newSubscriberId, StringComparison.OrdinalIgnoreCase))
            {
                addon.PreviousProviderSubscriptionId = previousSubscriberId;
                addon.PendingCancellationProviderSubscriptionId = previousSubscriberId;
                addon.PendingCancellationTilopayRecurringPlanId = previousRecurringPlanId;
                addon.ProviderCancellation = ProviderCancellationState.PendingManualCancellation;
                addon.ProviderCancellationAttemptCount = 0;
                addon.ProviderCancellationLastAttemptUtc = null;
                addon.ProviderCancellationNextRetryUtc = null;
            }
            else if (!isSameAddonPlan &&
                     !string.IsNullOrWhiteSpace(previousSubscriberId) &&
                     previousRecurringPlanId.HasValue &&
                     previousRecurringPlanId.Value != tilopayRecurringPlanId)
            {
                // CAMBIO de paquete SIN id_suscriptor en el webhook: el add-on ya apunta al plan
                // nuevo pero conserva el suscriptor del viejo. No se puede stashear todavía (no
                // sabemos cuál es el nuevo), así que se APARCA el plan recurrente anterior para que
                // la resolución tardía (ISubscriberResolutionService) pueda adoptar el nuevo y
                // verificar la baja del viejo contra getSuscriptorRepeat. Sin este dato la baja
                // nunca sería verificable y el suscriptor viejo seguiría cobrando.
                addon.PendingCancellationProviderSubscriptionId = null;
                addon.PendingCancellationTilopayRecurringPlanId = previousRecurringPlanId;
                addon.ProviderCancellationAttemptCount = 0;
                addon.ProviderCancellationLastAttemptUtc = null;
                addon.ProviderCancellationNextRetryUtc = null;
            }

            // Opción A (entitlement ≠ configuración): comprar/renovar el paquete recurrente crea o
            // actualiza SOLO el add-on comercial (cuota mensual = TenantSubscriptionAddon.MonthlyMessageLimit).
            // NO se crea ni se habilita TenantWhatsAppSettings: la integración técnica de WhatsApp
            // (IsEnabled, throttle diario, horarios) se configura EXPLÍCITAMENTE desde "Configurar
            // WhatsApp" (/WhatsApp → UpdateSettingsAsync). Así, comprar el paquete nunca dispara envíos
            // automáticos ni pisa la configuración existente del cliente al renovar.
            await _db.SaveChangesAsync(cancellationToken);
        }

        /// <summary>
        /// Otorga un ACCESO MANUAL de WhatsApp (cortesía/canje/interno/prueba) desde plataforma. Es un
        /// entitlement COMERCIAL (BillingSource=ManualGrant): no cobra por TiloPay, no llama a TiloPay,
        /// y —por Opción A— NO toca TenantWhatsAppSettings (la configuración técnica se maneja aparte).
        /// Protege los add-ons TiloPay pagados: si el tenant tiene uno ACTIVO no se reemplaza salvo
        /// override explícito, y aún así TiloPay NO se cancela automáticamente (eso va por billing).
        /// </summary>
        public async Task<ManualWhatsAppGrantResult> GrantManualWhatsAppAddonAsync(
            Guid tenantId,
            string addonCode,
            ManualWhatsAppGrantType grantType,
            string reason,
            bool isIndefinite,
            DateTime? expiresAtUtc,
            string grantedByUserId,
            bool allowOverrideProviderRecurring,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(addonCode) ||
                !PlanCodes.WhatsAppAddons.Contains(addonCode, StringComparer.OrdinalIgnoreCase))
            {
                return new ManualWhatsAppGrantResult(ManualWhatsAppGrantOutcome.Invalid, $"Código de paquete WhatsApp no válido: {addonCode}.");
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                return new ManualWhatsAppGrantResult(ManualWhatsAppGrantOutcome.Invalid, "El motivo/nota del acceso manual es obligatorio.");
            }

            if (!isIndefinite && expiresAtUtc is null)
            {
                return new ManualWhatsAppGrantResult(ManualWhatsAppGrantOutcome.Invalid, "Indica una fecha de vencimiento o marca el acceso como indefinido.");
            }

            var nowUtc = GetUtcNow();
            var plan = await _db.Planes
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Codigo == addonCode && p.Activo, cancellationToken)
                ?? throw new InvalidOperationException($"Plan {addonCode} no encontrado o inactivo en la BD.");

            var addon = await _db.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(a => a.TenantId == tenantId, cancellationToken);

            var overridingProvider = false;
            if (addon is not null)
            {
                var current = ResolveWhatsAppEntitlement(addon);
                if (current.Source == WhatsAppAddonBillingSource.ProviderRecurring && current.IsEffective)
                {
                    if (!allowOverrideProviderRecurring)
                    {
                        return new ManualWhatsAppGrantResult(
                            ManualWhatsAppGrantOutcome.BlockedProviderRecurringActive,
                            "Este tenant tiene un add-on WhatsApp pagado por TiloPay activo. Confirma el override explícitamente. TiloPay NO se cancela automáticamente: si corresponde, cancélalo desde el flujo de billing.");
                    }

                    overridingProvider = true;
                }
            }

            var providerSubSuffix = MaskSuffix(addon?.ProviderSubscriptionId);

            if (addon is null)
            {
                addon = new TenantSubscriptionAddon
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    CreatedAtUtc = nowUtc
                };
                _db.TenantSubscriptionAddons.Add(addon);
            }

            var expires = isIndefinite ? (DateTime?)null : expiresAtUtc;

            addon.PlanId = plan.Id;
            addon.AddonCode = plan.Codigo!;
            addon.Estado = EstadoSuscripcion.Activa;
            addon.BillingSource = WhatsAppAddonBillingSource.ManualGrant;
            addon.ManualGrantType = grantType;
            addon.ManualGrantReason = Truncate(reason, 500);
            addon.GrantedByUserId = grantedByUserId;
            addon.GrantedAtUtc = nowUtc;
            addon.IsManualGrantIndefinite = isIndefinite;
            addon.ManualGrantExpiresAtUtc = expires;
            addon.RevokedAtUtc = null;
            addon.RevokedByUserId = null;
            addon.RevocationReason = null;
            addon.PrecioMensual = null;
            addon.MonedaFacturacion = "CRC";
            addon.MonthlyMessageLimit = plan.LimiteMensajesMensual ?? 0;
            addon.FechaInicio = nowUtc;
            // Vigencia de display sincronizada con el grant (indefinido → null). El acceso manual no cobra.
            addon.FechaFin = expires;
            addon.FechaProximoCobroUtc = null;
            addon.FechaFinGraciaUtc = null;
            addon.FechaCancelacionUtc = null;
            addon.CancelAtPeriodEnd = false;
            addon.CancellationEffectiveAtUtc = null;
            addon.CancellationRequestedByUserId = null;
            addon.CancellationReason = null;

            if (overridingProvider)
            {
                // Override explícito de un add-on TiloPay activo: NO tocamos TiloPay ni sus ids (se
                // conservan como referencia histórica para que soporte pueda cancelar por billing).
                // BillingSource=ManualGrant hace que el clasificador ya NO lo cuente como riesgo de dinero.
                _logger.LogWarning(
                    "Override manual de add-on TiloPay activo. TenantId {TenantId}. GrantedBy {UserId}. El suscriptor TiloPay {SubSuffix} pudo quedar activo; cancelar por billing si corresponde.",
                    tenantId, grantedByUserId, providerSubSuffix);
            }
            else
            {
                // Alta/cambio manual limpio: MANUAL-... queda SOLO como referencia legacy; la lógica
                // nunca depende de ese string (manda BillingSource).
                addon.TilopayRecurringPlanId = null;
                addon.ProviderSubscriptionId = null;
                addon.ProviderTransactionId = $"MANUAL-{addonCode}-{nowUtc:yyyyMMddHHmmss}";
            }

            addon.UpdatedAtUtc = nowUtc;

            await _db.SaveChangesAsync(cancellationToken);

            var message = overridingProvider
                ? $"Acceso manual {addonCode} otorgado por OVERRIDE. Atención: el suscriptor TiloPay {providerSubSuffix} pudo quedar activo; cancélalo desde billing si corresponde (no se canceló automáticamente)."
                : $"Acceso manual {addonCode} otorgado.";
            return new ManualWhatsAppGrantResult(ManualWhatsAppGrantOutcome.Granted, message);
        }

        /// <summary>
        /// Revoca el add-on WhatsApp del tenant. Solo para accesos MANUALES/legacy: un add-on TiloPay
        /// ACTIVO no se revoca por aquí (se cancela desde el flujo de billing). Deja rastro
        /// (RevokedAtUtc/By/Reason), no borra la fila y no toca TiloPay ni TenantWhatsAppSettings.
        /// </summary>
        public async Task<ManualWhatsAppGrantResult> RevokeWhatsAppAddonAsync(
            Guid tenantId,
            string revokedByUserId,
            string? reason,
            CancellationToken cancellationToken = default)
        {
            var addon = await _db.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(a => a.TenantId == tenantId, cancellationToken);

            if (addon is null)
            {
                return new ManualWhatsAppGrantResult(ManualWhatsAppGrantOutcome.NoChange, "El tenant no tiene un add-on WhatsApp que revocar.");
            }

            var entitlement = ResolveWhatsAppEntitlement(addon);
            if (entitlement.Source == WhatsAppAddonBillingSource.ProviderRecurring && entitlement.IsEffective)
            {
                return new ManualWhatsAppGrantResult(
                    ManualWhatsAppGrantOutcome.BlockedProviderRecurringActive,
                    "Este add-on es pagado por TiloPay. Cancélalo desde el flujo de billing, no desde el acceso manual.");
            }

            var nowUtc = GetUtcNow();
            addon.Estado = EstadoSuscripcion.Cancelada;
            addon.RevokedAtUtc = nowUtc;
            addon.RevokedByUserId = revokedByUserId;
            addon.RevocationReason = Truncate(string.IsNullOrWhiteSpace(reason) ? "Revocado desde plataforma." : reason, 500);
            addon.IsManualGrantIndefinite = false;
            addon.ManualGrantExpiresAtUtc = nowUtc;
            addon.FechaCancelacionUtc = nowUtc;
            addon.FechaFin = nowUtc;
            addon.UpdatedAtUtc = nowUtc;

            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Add-on WhatsApp revocado. TenantId {TenantId}. RevokedBy {UserId}. Source {Source}.",
                tenantId, revokedByUserId, entitlement.Source);

            return new ManualWhatsAppGrantResult(ManualWhatsAppGrantOutcome.Revoked, "Acceso WhatsApp revocado.");
        }

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value[..max];

        private static string MaskSuffix(string? reference) =>
            string.IsNullOrWhiteSpace(reference)
                ? "-"
                : "…" + reference[^Math.Min(4, reference.Length)..];

        public async Task RegistrarPagoFallidoAddonAsync(
            Guid tenantId,
            string addonCode,
            string? providerSubscriberId,
            string? providerTransactionId,
            string? motivo = null,
            CancellationToken cancellationToken = default)
        {
            var addon = await _db.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current =>
                    current.TenantId == tenantId &&
                    (current.ProviderSubscriptionId == providerSubscriberId ||
                     current.AddonCode == addonCode),
                    cancellationToken);

            if (addon is null)
            {
                return;
            }

            var nowUtc = GetUtcNow();
            addon.ProviderTransactionId = providerTransactionId ?? addon.ProviderTransactionId;
            addon.Estado = addon.Estado == EstadoSuscripcion.Activa || addon.Estado == EstadoSuscripcion.Trial
                ? EstadoSuscripcion.Morosa
                : EstadoSuscripcion.Fallida;
            addon.FechaFinGraciaUtc = ResolveGracePeriodEndsUtc(addon.FechaFin, nowUtc);
            addon.UpdatedAtUtc = nowUtc;

            if (!string.IsNullOrWhiteSpace(motivo))
            {
                _logger.LogWarning(
                    "Pago recurrente WhatsApp fallido. TenantId {TenantId}. AddonCode {AddonCode}. Motivo {Motivo}.",
                    tenantId,
                    addonCode,
                    motivo);
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        public async Task MarcarSuscripcionCanceladaRecurrenteAsync(
            Guid tenantId,
            string? providerSubscriberId,
            bool isAddon,
            string? motivo = null,
            CancellationToken cancellationToken = default)
        {
            var nowUtc = GetUtcNow();

            if (isAddon)
            {
                var addonQuery = _db.TenantSubscriptionAddons
                    .IgnoreQueryFilters()
                    .Where(current => current.TenantId == tenantId);

                if (!string.IsNullOrWhiteSpace(providerSubscriberId))
                {
                    addonQuery = addonQuery.Where(current => current.ProviderSubscriptionId == providerSubscriberId);
                }

                var addon = await addonQuery
                    .OrderByDescending(current => current.UpdatedAtUtc)
                    .ThenByDescending(current => current.CreatedAtUtc)
                    .FirstOrDefaultAsync(cancellationToken);

                if (addon is null)
                {
                    return;
                }

                addon.Estado = EstadoSuscripcion.Cancelada;
                addon.FechaCancelacionUtc = nowUtc;
                addon.FechaFin = nowUtc;
                addon.UpdatedAtUtc = nowUtc;
                await _db.SaveChangesAsync(cancellationToken);
                return;
            }

            var suscripcion = await _db.Suscripciones
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.TenantId == tenantId, cancellationToken);

            if (suscripcion is null)
            {
                return;
            }

            suscripcion.Estado = EstadoSuscripcion.Cancelada;
            suscripcion.ProviderSubscriptionId = providerSubscriberId ?? suscripcion.ProviderSubscriptionId;
            suscripcion.FechaCancelacionUtc = nowUtc;
            suscripcion.FechaFin = nowUtc;
            suscripcion.FechaUltimaActualizacionUtc = nowUtc;
            suscripcion.MotivoEstado = motivo ?? "Cancelacion recibida desde Tilopay Repeat.";

            await _db.SaveChangesAsync(cancellationToken);
            InvalidateSubscriptionCache(tenantId);
        }

        public EstadoSuscripcion GetEffectiveStatus(Suscripcion suscripcion)
        {
            ArgumentNullException.ThrowIfNull(suscripcion);

            // Fin de período EFECTIVO: el más tardío entre lo que calculó LuxuryCloud y lo que
            // TiloPay realmente va a cobrar. Solo puede EXTENDER la vigencia (el máximo nunca
            // acorta), así que un suscriptor con expire posterior en el proveedor no se marca moroso
            // antes de tiempo, y uno con expire anterior tampoco pierde acceso ya pagado.
            var effectiveEndUtc = SubscriptionEffectiveDates.GetEffectiveEndUtc(
                suscripcion.FechaFin,
                suscripcion.ProviderExpiresAtUtc);

            // Renovación cancelada: el acceso NO puede exceder la fecha efectiva de cancelación,
            // aunque el Estado local siga Activa. El cierre del Estado lo hace un worker que puede
            // correr después; el control de acceso NO debe esperarlo (fail-closed al vencer).
            if (suscripcion.CancelAtPeriodEnd && suscripcion.CancellationEffectiveAtUtc is { } cancelEndUtc)
            {
                effectiveEndUtc = effectiveEndUtc is { } end && end < cancelEndUtc ? end : cancelEndUtc;
            }

            var effectiveStatus = GetEffectiveStatusInternal(
                suscripcion.Estado,
                effectiveEndUtc,
                suscripcion.FechaTrialFin,
                suscripcion.FechaFinGraciaUtc,
                GetUtcNow());

            // Gate de recuperación de pago: una suscripción Morosa (impago en gracia) cuya gracia
            // venció NO se suspende automáticamente salvo que AutoSuspendAfterGrace lo habilite. El
            // gate solo aplica con la recuperación Enabled (en producción); si el módulo no está
            // configurado (p.ej. tests que no lo usan), se conserva el comportamiento anterior. NO
            // aplica a cancelaciones de renovación (ese corte manda). El worker registra el dry-run.
            if (effectiveStatus == EstadoSuscripcion.Suspendida &&
                suscripcion.Estado == EstadoSuscripcion.Morosa &&
                !suscripcion.CancelAtPeriodEnd &&
                _recoveryOptions is { Enabled: true, AutoSuspendAfterGrace: false })
            {
                return EstadoSuscripcion.Morosa;
            }

            return effectiveStatus;
        }

        public EstadoSuscripcion GetEffectiveStatus(TenantSubscriptionAddon addon)
        {
            ArgumentNullException.ThrowIfNull(addon);

            return GetEffectiveStatusInternal(
                addon.Estado,
                addon.FechaFin,
                trialEndsUtc: null,
                addon.FechaFinGraciaUtc,
                GetUtcNow());
        }

        public bool CanAccessApp(Suscripcion suscripcion)
        {
            var effectiveStatus = GetEffectiveStatus(suscripcion);
            return effectiveStatus == EstadoSuscripcion.Activa ||
                   effectiveStatus == EstadoSuscripcion.Trial ||
                   effectiveStatus == EstadoSuscripcion.Morosa;
        }

        /// <summary>
        /// Clasifica el add-on por su fuente comercial (ver <see cref="WhatsAppAddonEntitlementRules"/>):
        /// si da acceso efectivo, si es riesgo de dinero, si es manual (vigente/vencido) o legacy.
        /// ÚNICA fuente de verdad para send-gate, summary, health y plataforma.
        /// </summary>
        public WhatsAppAddonEntitlement ResolveWhatsAppEntitlement(TenantSubscriptionAddon addon) =>
            WhatsAppAddonEntitlementRules.Classify(addon, GetUtcNow());

        public bool IsWhatsAppAddonActive(TenantSubscriptionAddon addon) =>
            ResolveWhatsAppEntitlement(addon).IsEffective;

        public async Task<int> GetWhatsAppUsageInCurrentPeriodAsync(
            Guid tenantId,
            DateTime? periodStartUtc,
            DateTime? periodEndUtc,
            CancellationToken cancellationToken = default)
        {
            if (!periodStartUtc.HasValue || !periodEndUtc.HasValue)
            {
                return 0;
            }

            return await _db.WhatsAppMessageLogs
                .AsNoTracking()
                .Where(message =>
                    message.TenantId == tenantId &&
                    message.Direction == "Outbound" &&
                    (message.NotificationType == "Confirmation" ||
                     message.NotificationType == "Reminder3Hours") &&
                    (message.Status == "Sent" ||
                     message.Status == "Delivered" ||
                     message.Status == "Read") &&
                    (message.SentAtUtc ?? message.DeliveredAtUtc ?? message.ReadAtUtc ?? message.CreatedAtUtc) >= periodStartUtc.Value &&
                    (message.SentAtUtc ?? message.DeliveredAtUtc ?? message.ReadAtUtc ?? message.CreatedAtUtc) < periodEndUtc.Value)
                .CountAsync(cancellationToken);
        }

        public int ResolveWhatsAppDailyMessageLimit(
            TenantSubscriptionAddon? addon,
            int configuredDailyLimit = TenantWhatsAppSettings.DefaultDailyMessageLimit) =>
            ResolveWhatsAppDailyMessageLimit(
                addon?.AddonCode ?? addon?.Plan?.Codigo,
                configuredDailyLimit);

        public int ResolveWhatsAppDailyMessageLimit(
            string? addonCode,
            int configuredDailyLimit = TenantWhatsAppSettings.DefaultDailyMessageLimit)
        {
            var configuredLimit = _tilopayRepeatOptions.FindByCode(addonCode)?.DailyMessageLimit;
            if (configuredLimit is > 0)
            {
                return configuredLimit.Value;
            }

            return addonCode switch
            {
                PlanCodes.WhatsApp400 => 15,
                PlanCodes.WhatsApp800 => 30,
                PlanCodes.WhatsApp1200 => 45,
                _ => configuredDailyLimit > 0 ? configuredDailyLimit : TenantWhatsAppSettings.DefaultDailyMessageLimit
            };
        }

        // Compatibilidad razonable con el codigo legado de Stripe.
        public Task ActivarSuscripcionAsync(
            Guid tenantId,
            Guid planId,
            string subscriptionId,
            string customerId,
            DateTime? trialEnd = null) =>
            ActivarSuscripcionAsync(
                tenantId,
                planId,
                PaymentProviderType.Stripe,
                customerId,
                subscriptionId,
                providerPaymentLinkId: null,
                providerTransactionId: null,
                providerReference: subscriptionId,
                trialEnd: trialEnd,
                motivo: "Activacion desde Stripe");

        public async Task RegistrarPagoAsync(
            string subscriptionId,
            string invoiceId,
            decimal monto,
            string moneda)
        {
            var suscripcion = await _db.Suscripciones
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.ProviderSubscriptionId == subscriptionId);

            if (suscripcion is null)
            {
                return;
            }

            await RegistrarPagoConfirmadoAsync(
                suscripcion.TenantId,
                suscripcion.PlanId,
                pagoSuscripcionId: null,
                provider: PaymentProviderType.Stripe,
                providerReference: invoiceId,
                providerTransactionId: invoiceId,
                providerAuthorizationCode: null,
                monto: monto,
                moneda: moneda,
                motivo: "Pago confirmado desde Stripe");
        }

        public async Task MarcarPagoFallidoAsync(
            string subscriptionId,
            string invoiceId,
            decimal monto,
            string moneda)
        {
            var suscripcion = await _db.Suscripciones
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.ProviderSubscriptionId == subscriptionId);

            if (suscripcion is null)
            {
                return;
            }

            await RegistrarPagoFallidoAsync(
                suscripcion.TenantId,
                suscripcion.PlanId,
                pagoSuscripcionId: null,
                provider: PaymentProviderType.Stripe,
                providerReference: invoiceId,
                providerTransactionId: invoiceId,
                monto: monto,
                moneda: moneda,
                motivo: "Pago fallido desde Stripe");
        }

        private async Task<Suscripcion> EnsureSubscriptionForPaymentAsync(
            Guid tenantId,
            Guid planId,
            PaymentProviderType provider,
            string providerReference,
            string? providerTransactionId,
            CancellationToken cancellationToken)
        {
            var suscripcion = await _db.Suscripciones
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

            if (suscripcion is not null)
            {
                return suscripcion;
            }

            suscripcion = new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                Proveedor = provider,
                ProviderReference = providerReference,
                ProviderTransactionId = providerTransactionId,
                Estado = EstadoSuscripcion.Pendiente,
                FechaInicio = GetUtcNow(),
                FechaUltimaActualizacionUtc = GetUtcNow(),
                MotivoEstado = "Suscripcion creada desde confirmacion de pago"
            };

            _db.Suscripciones.Add(suscripcion);
            await _db.SaveChangesAsync(cancellationToken);

            return suscripcion;
        }

        private void InvalidateSubscriptionCache(Guid tenantId)
        {
            _cache.Remove($"suscripcion_{tenantId}");
            _accessCache.Invalidate(tenantId);
        }

        private DateTime GetUtcNow() => _businessDateTimeProvider.NowOffset().UtcDateTime;

        /// <summary>
        /// Equivalente mensual de un plan para guardar en Suscripcion.PrecioMensual.
        /// Mensual => PrecioMensual; Anual => MonthlyEquivalentAmount configurado o total/12.
        /// </summary>
        private static decimal ResolveMonthlyEquivalent(Plan plan)
        {
            if (plan.BillingCycle != BillingCycle.Annual)
            {
                return plan.PrecioMensual;
            }

            if (plan.MonthlyEquivalentAmount is > 0)
            {
                return plan.MonthlyEquivalentAmount.Value;
            }

            return plan.PrecioMensual > 0
                ? decimal.Round(plan.PrecioMensual / 12m, 2)
                : plan.PrecioMensual;
        }

        private static (DateTime PeriodStartUtc, DateTime PeriodEndUtc) ResolveNextBillingPeriod(
            DateTime nowUtc,
            DateTime? currentPeriodEndUtc,
            bool shouldExtendExistingPeriod,
            BillingCycle billingCycle = BillingCycle.Monthly)
        {
            var periodStartUtc = shouldExtendExistingPeriod &&
                                 currentPeriodEndUtc.HasValue &&
                                 currentPeriodEndUtc.Value > nowUtc
                ? currentPeriodEndUtc.Value
                : nowUtc;

            var periodEndUtc = billingCycle == BillingCycle.Annual
                ? periodStartUtc.AddYears(1)
                : periodStartUtc.AddMonths(1);

            return (periodStartUtc, periodEndUtc);
        }

        private DateTime ResolveGracePeriodEndsUtc(DateTime? currentPeriodEndUtc, DateTime nowUtc)
        {
            var graceStartUtc = currentPeriodEndUtc.HasValue && currentPeriodEndUtc.Value > nowUtc
                ? currentPeriodEndUtc.Value
                : nowUtc;

            var graceDays = _tilopayRepeatOptions.GracePeriodDays <= 0
                ? 3
                : _tilopayRepeatOptions.GracePeriodDays;

            return graceStartUtc.AddDays(graceDays);
        }

        private static EstadoSuscripcion GetEffectiveStatusInternal(
            EstadoSuscripcion status,
            DateTime? currentPeriodEndUtc,
            DateTime? trialEndsUtc,
            DateTime? graceEndsUtc,
            DateTime nowUtc)
        {
            if (status == EstadoSuscripcion.Trial)
            {
                return trialEndsUtc.HasValue && trialEndsUtc.Value < nowUtc
                    ? EstadoSuscripcion.Suspendida
                    : EstadoSuscripcion.Trial;
            }

            if (status == EstadoSuscripcion.Activa)
            {
                if (!currentPeriodEndUtc.HasValue || currentPeriodEndUtc.Value >= nowUtc)
                {
                    return EstadoSuscripcion.Activa;
                }

                return graceEndsUtc.HasValue && graceEndsUtc.Value >= nowUtc
                    ? EstadoSuscripcion.Morosa
                    : EstadoSuscripcion.Suspendida;
            }

            if (status == EstadoSuscripcion.Morosa)
            {
                return graceEndsUtc.HasValue && graceEndsUtc.Value >= nowUtc
                    ? EstadoSuscripcion.Morosa
                    : EstadoSuscripcion.Suspendida;
            }

            if (status == EstadoSuscripcion.Vencida)
            {
                return EstadoSuscripcion.Suspendida;
            }

            return status;
        }
    }
}
