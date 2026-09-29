using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.WhatsApp;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.SaaS
{
    /// <summary>
    /// Implementacion extraida desde BillingController.BuildCurrentSubscriptionSummaryAsync.
    /// Mantiene exactamente el mismo calculo (queries tenant-safe con IgnoreQueryFilters +
    /// filtro explicito por TenantId) para no alterar el comportamiento existente.
    ///
    /// EXCEPCION deliberada: el limite de funcionarios ya NO se lee de la fila de Suscripciones.
    /// Antes se calculaba aqui como <c>subscription?.MaxFuncionarios ?? subscription?.Plan?.MaxFuncionarios</c>,
    /// lo que ignoraba <c>Tenant.ForcedPlanId</c>: un tenant exento con plan forzado LC_M_03 veia
    /// el limite de su suscripcion vieja (7) mientras el enforcement de FuncionariosController
    /// usaba el plan forzado (3). Ahora ambos leen del mismo resolver.
    /// </summary>
    public sealed class SubscriptionSummaryService : ISubscriptionSummaryService
    {
        private readonly ApplicationDbContext _context;
        private readonly SuscripcionService _suscripcionService;
        private readonly ITenantWhatsAppSettingsService _tenantWhatsAppSettingsService;
        private readonly ITenantCommercialAccessResolver _accessResolver;

        public SubscriptionSummaryService(
            ApplicationDbContext context,
            SuscripcionService suscripcionService,
            ITenantWhatsAppSettingsService tenantWhatsAppSettingsService,
            ITenantCommercialAccessResolver accessResolver)
        {
            _context = context;
            _suscripcionService = suscripcionService;
            _tenantWhatsAppSettingsService = tenantWhatsAppSettingsService;
            _accessResolver = accessResolver;
        }

        public async Task<BillingSubscriptionSummaryViewModel?> BuildAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            var subscription = await _context.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(s => s.Plan)
                .Where(s => s.TenantId == tenantId)
                .OrderByDescending(s => s.FechaUltimaActualizacionUtc ?? s.FechaInicio)
                .ThenByDescending(s => s.FechaInicio)
                .FirstOrDefaultAsync(cancellationToken);

            var addon = await _context.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(a => a.Plan)
                .Where(a => a.TenantId == tenantId)
                .OrderByDescending(a => a.UpdatedAtUtc)
                .ThenByDescending(a => a.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            // Estado comercial efectivo: unica fuente del plan/limite mostrados. Para un tenant
            // exento/interno esto resuelve el PLAN FORZADO; para un tenant pagado, su suscripcion.
            var access = await _accessResolver.ResolveAsync(tenantId, cancellationToken: cancellationToken);

            // Un tenant exento con plan forzado y sin fila de Suscripciones igual debe
            // ver su plan y su limite: antes esta rama devolvia null y la cuenta mostraba "0 / ∞".
            if (subscription is null && addon is null && !access.IsForcedByPlatform)
            {
                return null;
            }

            var activeFuncionarios = await _context.Funcionarios
                .AsNoTracking()
                .CountAsync(funcionario => funcionario.Activo, cancellationToken);

            var subscriptionStatus = subscription is null
                ? (EstadoSuscripcion?)null
                : _suscripcionService.GetEffectiveStatus(subscription);

            var addonStatus = addon is null
                ? (EstadoSuscripcion?)null
                : _suscripcionService.GetEffectiveStatus(addon);
            // Entitlement por fuente: distingue pagado / manual (vigente o vencido) / legacy. Un legacy
            // con Estado activo NO es acceso efectivo, así que se usa IsEffective (no GetEffectiveStatus).
            var addonEntitlement = addon is null
                ? null
                : _suscripcionService.ResolveWhatsAppEntitlement(addon);
            var hasActiveWhatsAppAddon = addonEntitlement?.IsEffective == true;
            // La tarjeta del add-on también se muestra para un acceso manual VENCIDO (para avisar).
            var showWhatsAppAddonCard = hasActiveWhatsAppAddon || addonEntitlement?.IsManualGrantExpired == true;
            var isManualGrant = addonEntitlement?.IsManualGrant == true;
            var whatsAppSettings = hasActiveWhatsAppAddon
                ? await _tenantWhatsAppSettingsService.GetSettingsForTenantAsync(tenantId, cancellationToken)
                : null;

            var whatsAppUsage = addon is null
                ? 0
                : await _suscripcionService.GetWhatsAppUsageInCurrentPeriodAsync(
                    tenantId,
                    addon.FechaInicio,
                    addon.FechaFin,
                    cancellationToken);
            var todaysWhatsAppUsage = hasActiveWhatsAppAddon
                ? await _tenantWhatsAppSettingsService.GetTodayUsageAsync(tenantId, cancellationToken)
                : 0;

            return new BillingSubscriptionSummaryViewModel
            {
                // Plan mostrado = plan EFECTIVO. Con acceso forzado por plataforma manda el plan
                // forzado; en el camino pagado normal coincide con la suscripcion (mismo valor que antes).
                PlanName = access.IsForcedByPlatform
                    ? access.EffectivePlanName
                    : subscription?.Plan?.Nombre,
                PlanCode = access.IsForcedByPlatform
                    ? access.EffectivePlanCode
                    : subscription?.CodigoPlan ?? subscription?.Plan?.Codigo,
                Status = subscriptionStatus,
                StatusLabel = ResolveStatusLabel(subscriptionStatus),
                StatusTone = ResolveStatusTone(subscriptionStatus),
                IsPlatformGrantedAccess = access.IsForcedByPlatform,
                PlatformAccessReason = access.IsForcedByPlatform ? access.Reason : null,
                CanAccessApp = access.IsForcedByPlatform
                    ? access.CanAccessApp
                    : subscription is not null && _suscripcionService.CanAccessApp(subscription),
                IsInGracePeriod = subscriptionStatus == EstadoSuscripcion.Morosa,
                CancelAtPeriodEnd = subscription?.CancelAtPeriodEnd ?? false,
                IsRecurringTilopay = subscription is not null &&
                                     subscription.Proveedor == PaymentProviderType.Tilopay &&
                                     subscription.TilopayRecurringPlanId != null &&
                                     subscription.ProviderSubscriptionId != null,
                ProviderStatusRaw = subscription?.ProviderStatusRaw,
                // Pausa del proveedor (status 3 / "Pause By Commerce"): la clasificación vive en
                // ProviderSubscriberStatusRules para no duplicar la lista de valores del proveedor.
                IsRenewalPaused = Tilopay.ProviderSubscriberStatusRules.IsProviderSubscriberPaused(
                    subscription?.ProviderStatusRaw),
                // Pausa del proveedor en plena recuperación sin pausa de soporte: consecuencia de los
                // reintentos rechazados. La gracia local sigue mandando (no se muestra "pausada por soporte").
                IsProviderPauseFromRecovery = subscription is not null &&
                    subscription.PaymentRecoveryStatus is not null &&
                    subscription.ProviderPausedAtUtc is null &&
                    Tilopay.ProviderSubscriberStatusRules.IsProviderSubscriberPaused(subscription.ProviderStatusRaw),
                // Recuperación de pago: el backend ya mantiene este estado en la suscripción.
                PaymentRecoveryStatus = subscription?.PaymentRecoveryStatus,
                // Fail-safe UI: si la ventana de gracia ya venció por reloj, la UI no muestra "en gracia"
                // aunque el worker todavía no haya marcado GraceExpired.
                PaymentGraceWindowEnded = subscription?.FechaFinGraciaUtc is { } graceEnd && graceEnd <= DateTime.UtcNow,
                // Fechas EFECTIVAS (UTC, para lógica): si TiloPay va a cobrar más tarde de lo que
                // calculamos (p.ej. reactivó un suscriptor y extendió el expire), el cliente no debe
                // ver una fecha más temprana que lo haría dudar de un cobro que no va a ocurrir aún.
                CurrentPeriodEndUtc = Billing.SubscriptionEffectiveDates.GetEffectiveEndUtc(
                    subscription?.FechaFin, subscription?.ProviderExpiresAtUtc),
                NextBillingDateUtc = Billing.SubscriptionEffectiveDates.GetEffectiveEndUtc(
                    subscription?.FechaProximoCobroUtc, subscription?.ProviderExpiresAtUtc),
                GracePeriodEndsUtc = subscription?.FechaFinGraciaUtc,

                // Fechas de DISPLAY (fecha de calendario Tica): cuando el proveedor es la fuente,
                // muestran su expire crudo (15/09/2026), no el fin de día UTC (16/09/2026).
                CurrentPeriodEndDisplay = Billing.SubscriptionDisplayDates.FormatEffective(
                    subscription?.FechaFin, subscription?.ProviderExpiresAtUtc, subscription?.ProviderExpiryRaw),
                NextBillingDateDisplay = Billing.SubscriptionDisplayDates.FormatEffective(
                    subscription?.FechaProximoCobroUtc, subscription?.ProviderExpiresAtUtc, subscription?.ProviderExpiryRaw),
                // La gracia no tiene fuente en el proveedor: es un instante UTC local que se muestra como
                // fecha de calendario en Costa Rica (antes tomaba la fecha UTC: una gracia que vence a
                // las 02:00 UTC se mostraba un día más tarde de lo real).
                GracePeriodEndsDisplay = Billing.SubscriptionDisplayDates.Format(
                    Billing.SubscriptionDisplayDates.ToCostaRicaDate(subscription?.FechaFinGraciaUtc)),
                // Limite EFECTIVO desde el resolver unico (respeta plan forzado). Ya no se lee de
                // la fila de Suscripciones, que es lo que producia "limite 7" con plan forzado de 3.
                MaxFuncionarios = access.EffectiveEmployeeLimit,
                ActiveFuncionarios = activeFuncionarios,
                WhatsAppAddonName = showWhatsAppAddonCard ? addon?.Plan?.Nombre ?? addon?.AddonCode : null,
                WhatsAppAddonCode = showWhatsAppAddonCard ? addon?.AddonCode ?? addon?.Plan?.Codigo : null,
                WhatsAppAddonStatus = hasActiveWhatsAppAddon ? addonStatus : null,
                WhatsAppAddonStatusLabel = hasActiveWhatsAppAddon ? ResolveStatusLabel(addonStatus) : null,
                // ── Acceso manual/cortesía/canje (distinto de un paquete pagado por TiloPay) ──
                WhatsAppAddonIsManualGrant = isManualGrant,
                WhatsAppManualGrantIndefinite = addonEntitlement?.IsIndefinite == true,
                WhatsAppManualGrantExpired = addonEntitlement?.IsManualGrantExpired == true,
                WhatsAppManualGrantExpiresDisplay = isManualGrant && addonEntitlement?.IsIndefinite == false && addonEntitlement.ExpiresAtUtc is { } manualExp
                    ? Billing.SubscriptionDisplayDates.Format(DateOnly.FromDateTime(manualExp))
                    : null,
                WhatsAppMonthlyLimit = !hasActiveWhatsAppAddon || addon is null
                    ? null
                    : addon.MonthlyMessageLimit > 0
                        ? addon.MonthlyMessageLimit
                        : addon.Plan?.LimiteMensajesMensual,
                WhatsAppMessagesUsed = hasActiveWhatsAppAddon ? whatsAppUsage : 0,
                WhatsAppMessagesRemaining = !hasActiveWhatsAppAddon || addon is null
                    ? null
                    : Math.Max(
                        (addon.MonthlyMessageLimit > 0
                            ? addon.MonthlyMessageLimit
                            : addon.Plan?.LimiteMensajesMensual ?? 0) - whatsAppUsage,
                        0),
                WhatsAppTodayUsage = hasActiveWhatsAppAddon ? todaysWhatsAppUsage : 0,
                WhatsAppDailyLimit = hasActiveWhatsAppAddon ? whatsAppSettings?.DailyMessageLimit : null,
                // Opción A: la automatización solo cuenta como "habilitada" si hay una configuración
                // PERSISTIDA (Exists) y habilitada. El snapshot sintético para un add-on sin configurar
                // trae IsEnabled=true (defaults de la pantalla de configuración), pero eso NO es un
                // envío autorizado: exigir Exists evita mostrar "activo" cuando en realidad falta configurar.
                WhatsAppAutomationEnabled = hasActiveWhatsAppAddon && (whatsAppSettings?.Exists ?? false) && (whatsAppSettings?.IsEnabled ?? false),
                SendAppointmentConfirmations = hasActiveWhatsAppAddon && (whatsAppSettings?.Exists ?? false) && (whatsAppSettings?.SendConfirmationOnCreate ?? false),
                SendAppointmentReminders = hasActiveWhatsAppAddon && (whatsAppSettings?.Exists ?? false) && (whatsAppSettings?.SendReminderThreeHoursBefore ?? false),
                // El paquete está activo pero la integración técnica aún no se configuró (sin fila persistida).
                WhatsAppAddonNeedsConfiguration = hasActiveWhatsAppAddon && !(whatsAppSettings?.Exists ?? false),
                ConfirmationHoursBefore = whatsAppSettings?.ConfirmationHoursBefore ?? Models.WhatsApp.TenantWhatsAppSettings.DefaultConfirmationHoursBefore,
                SendConfirmationImmediatelyIfInsideWindow = whatsAppSettings?.SendConfirmationImmediatelyIfInsideWindow ?? true,
                ReminderHoursBefore = whatsAppSettings?.ReminderHoursBefore ?? Models.WhatsApp.TenantWhatsAppSettings.DefaultReminderHoursBefore,
                SendReminderImmediatelyIfInsideWindow = whatsAppSettings?.SendReminderImmediatelyIfInsideWindow ?? true,
                ConfirmationIsBatch = whatsAppSettings is not null &&
                    !string.Equals(whatsAppSettings.ConfirmationScheduleMode, Models.WhatsApp.WhatsAppConfirmationScheduleModes.RelativeBeforeAppointment, StringComparison.Ordinal),
                ConfirmationBatchTime = whatsAppSettings?.ConfirmationBatchTime,
                ConfirmationBatchTarget = whatsAppSettings?.ConfirmationBatchTarget ?? Models.WhatsApp.WhatsAppConfirmationBatchTargets.TomorrowAllDay,
                ConfirmationMorningStart = whatsAppSettings?.ConfirmationMorningStart,
                ConfirmationMorningEnd = whatsAppSettings?.ConfirmationMorningEnd,
                ReminderIsBatch = whatsAppSettings is not null &&
                    string.Equals(whatsAppSettings.ReminderScheduleMode, Models.WhatsApp.WhatsAppReminderScheduleModes.DailyBatchSameDay, StringComparison.Ordinal),
                ReminderBatchTime = whatsAppSettings?.ReminderBatchTime,
                ReminderBatchTarget = whatsAppSettings?.ReminderBatchTarget ?? Models.WhatsApp.WhatsAppReminderBatchTargets.SameDayRemaining,
                QuietHoursEnabled = whatsAppSettings?.QuietHoursEnabled ?? false,
                QuietHoursStart = whatsAppSettings?.QuietHoursStart,
                QuietHoursEnd = whatsAppSettings?.QuietHoursEnd,
                WhatsAppNextBillingDateUtc = hasActiveWhatsAppAddon ? addon?.FechaProximoCobroUtc : null,
                WhatsAppNextBillingDateDisplay = hasActiveWhatsAppAddon && addon?.FechaProximoCobroUtc is { } addonNext
                    ? Billing.SubscriptionDisplayDates.Format(DateOnly.FromDateTime(addonNext))
                    : null,
                // Recurrente SOLO si la fuente es TiloPay (un acceso manual nunca muestra "próximo cobro"
                // ni el botón de cancelar renovación, aunque un override haya conservado ids del proveedor).
                WhatsAppAddonIsRecurring = hasActiveWhatsAppAddon &&
                                           addonEntitlement?.Source == WhatsAppAddonBillingSource.ProviderRecurring &&
                                           addon?.TilopayRecurringPlanId != null,
                WhatsAppAddonCancelAtPeriodEnd = hasActiveWhatsAppAddon && (addon?.CancelAtPeriodEnd ?? false),
                WhatsAppAddonEndsDisplay = hasActiveWhatsAppAddon && (addon?.CancelAtPeriodEnd ?? false) &&
                    (addon?.CancellationEffectiveAtUtc ?? addon?.FechaFin) is { } addonEnds
                    ? Billing.SubscriptionDisplayDates.Format(DateOnly.FromDateTime(addonEnds))
                    : null
            };
        }

        private static string ResolveStatusLabel(EstadoSuscripcion? status) =>
            status switch
            {
                EstadoSuscripcion.Trial => "Trial",
                EstadoSuscripcion.Activa => "Activo",
                EstadoSuscripcion.Morosa => "En gracia",
                EstadoSuscripcion.Suspendida => "Suspendido",
                EstadoSuscripcion.Cancelada => "Cancelado",
                EstadoSuscripcion.Pendiente => "Pendiente",
                EstadoSuscripcion.Fallida => "Fallido",
                EstadoSuscripcion.Vencida => "Vencido",
                _ => "Sin suscripcion"
            };

        private static string ResolveStatusTone(EstadoSuscripcion? status) =>
            status switch
            {
                EstadoSuscripcion.Trial => "info",
                EstadoSuscripcion.Activa => "success",
                EstadoSuscripcion.Morosa => "warning",
                EstadoSuscripcion.Suspendida => "danger",
                EstadoSuscripcion.Cancelada => "secondary",
                EstadoSuscripcion.Pendiente => "secondary",
                EstadoSuscripcion.Fallida => "danger",
                EstadoSuscripcion.Vencida => "warning",
                _ => "secondary"
            };
    }
}
