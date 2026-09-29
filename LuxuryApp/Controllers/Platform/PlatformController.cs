using System.Security.Claims;
using System.Text.Json;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.Platform.MissionControl;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.WhatsApp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Controllers.Platform
{
    [Authorize(Policy = PlatformAuthorizationPolicies.PlatformSuperAdmin)]
    public class PlatformController : Controller
    {
        private const string PromotionalCodeFormPrefix = nameof(PlatformPromotionalCodesPageViewModel.CreateForm);
        private readonly ApplicationDbContext _context;
        private readonly ITenantCommercialAccessResolver _commercialAccessResolver;
        private readonly ITenantCommercialAccessCache _accessCache;
        private readonly TenantExecutionService _tenantExecutionService;
        private readonly IMetaWhatsAppClient _metaWhatsAppClient;
        private readonly IPlatformAuditService _auditService;
        private readonly IPlatformMetricsService _metricsService;
        private readonly IPlatformHealthService _healthService;
        private readonly IPlatformWhatsAppStatusService _whatsAppStatusService;
        private readonly IPlatformMissionControlService _missionControlService;
        private readonly ITenantOwnerResolver _ownerResolver;

        public PlatformController(
            ApplicationDbContext context,
            ITenantCommercialAccessResolver commercialAccessResolver,
            ITenantCommercialAccessCache accessCache,
            TenantExecutionService tenantExecutionService,
            IMetaWhatsAppClient metaWhatsAppClient,
            IPlatformAuditService auditService,
            IPlatformMetricsService metricsService,
            IPlatformHealthService healthService,
            IPlatformWhatsAppStatusService whatsAppStatusService,
            IPlatformMissionControlService missionControlService,
            ITenantOwnerResolver ownerResolver)
        {
            _context = context;
            _commercialAccessResolver = commercialAccessResolver;
            _accessCache = accessCache;
            _tenantExecutionService = tenantExecutionService;
            _metaWhatsAppClient = metaWhatsAppClient;
            _auditService = auditService;
            _metricsService = metricsService;
            _healthService = healthService;
            _whatsAppStatusService = whatsAppStatusService;
            _missionControlService = missionControlService;
            _ownerResolver = ownerResolver;
        }

        /// <summary>
        /// Audita una acción de plataforma sin romper el flujo si la bitácora falla.
        /// El fallo queda en el log de la aplicación (S6), nunca tragado en silencio.
        /// </summary>
        private Task SafeAuditAsync(PlatformAuditEntry entry, CancellationToken cancellationToken) =>
            _auditService.TryLogAsync(entry, cancellationToken);

        /// <summary>
        /// Mission Control: nueva pantalla principal de la consola. Señales de salud,
        /// colas de trabajo y pulso del día.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Index(bool refresh = false, CancellationToken cancellationToken = default)
        {
            var snapshot = await _missionControlService.GetSnapshotAsync(refresh, cancellationToken);
            return View(snapshot);
        }

        /// <summary>Misma fotografía en JSON para monitoreo externo autenticado (patrón BillingHealth).</summary>
        [HttpGet("/Platform/MissionControl/json")]
        public async Task<IActionResult> MissionControlJson(CancellationToken cancellationToken)
        {
            var snapshot = await _missionControlService.GetSnapshotAsync(cancellationToken: cancellationToken);
            return Json(snapshot);
        }

        /// <summary>Gobierno de tenants (antes la portada de /Platform; el contenido no cambió).</summary>
        [HttpGet]
        public async Task<IActionResult> Tenants(CancellationToken cancellationToken)
        {
            var plans = await _context.Planes
                .AsNoTracking()
                .Where(plan => plan.Activo)
                .ToListAsync(cancellationToken);

            // Selector de PLAN BASE forzado: solo planes que pueden definir un limite de
            // funcionarios. Los add-ons WhatsApp quedan fuera por completo (tienen su propio
            // control) y legacy/validacion van a una seccion avanzada colapsada.
            var basePlans = plans
                .Where(plan => PlanCatalogRules.Classify(plan) == PlanCatalogKind.BaseCommercial)
                .OrderBy(PlanCatalogRules.SortKey)
                .ThenBy(plan => plan.Nombre)
                .ToList();

            var advancedPlans = plans
                .Where(plan => PlanCatalogRules.IsAdvancedOnly(plan))
                .OrderBy(PlanCatalogRules.SortKey)
                .ThenBy(plan => plan.Nombre)
                .ToList();

            var tenants = await _context.Tenants
                .AsNoTracking()
                .Include(tenant => tenant.ForcedPlan)
                .OrderBy(tenant => tenant.Nombre)
                .ToListAsync(cancellationToken);

            var tenantIds = tenants.Select(t => t.Id).ToList();

            // Contacto principal por regla determinista (admin > funcionario). Reemplaza el
            // OrderBy(email).First() que hacia ganar a un funcionario por orden alfabetico.
            var ownersByTenant = await _ownerResolver.ResolveBatchAsync(tenantIds, cancellationToken);

            // Batch: todos los datos WhatsApp en 4 queries totales (no N scopes DI)
            var whatsAppByTenant = await _whatsAppStatusService.GetBatchStatusAsync(tenantIds, cancellationToken);

            // Batch: métricas de uso (citas/cobros/reservas/última actividad) — 8 queries fijas
            var usageByTenant = await _metricsService.GetTenantUsageBatchAsync(tenantIds, cancellationToken);

            // Batch: tenants con checkouts pendientes (1 query antes del loop)
            var pendingCheckoutTenants = (await _context.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(p => tenantIds.Contains(p.TenantId) &&
                            (p.Estado == EstadoPagoProveedor.Pendiente || p.Estado == EstadoPagoProveedor.ManualReview))
                .Select(p => p.TenantId)
                .Distinct()
                .ToListAsync(cancellationToken))
                .ToHashSet();

            // Batch: suscripciones próximas a vencer (1 query antes del loop)
            var nowUtcForExpiry = DateTime.UtcNow;
            var expirySoonCutoff = nowUtcForExpiry.AddDays(7);
            var expiringSoonTenants = (await _context.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(s => tenantIds.Contains(s.TenantId) &&
                            (s.Estado == EstadoSuscripcion.Activa || s.Estado == EstadoSuscripcion.Trial) &&
                            s.FechaFin.HasValue && s.FechaFin.Value >= nowUtcForExpiry && s.FechaFin.Value <= expirySoonCutoff)
                .Select(s => s.TenantId)
                .Distinct()
                .ToListAsync(cancellationToken))
                .ToHashSet();

            var tenantRows = new List<PlatformTenantRowViewModel>(tenants.Count);
            foreach (var tenant in tenants)
            {
                var owner = ownersByTenant.TryGetValue(tenant.Id, out var resolved)
                    ? resolved
                    : Models.SaaS.TenantOwnerResolution.Empty(tenant.Id);
                var access = await _commercialAccessResolver.ResolveAsync(tenant.Id, cancellationToken: cancellationToken);
                var whatsApp = whatsAppByTenant[tenant.Id];
                usageByTenant.TryGetValue(tenant.Id, out var usage);
                usage ??= new PlatformTenantUsageViewModel();

                var health = _healthService.ComputeHealth(
                    access.CanAccessApp,
                    usage,
                    // Misma definicion de "WhatsApp operativo" que usa la ficha: paquete efectivo
                    // Y automatizacion configurada. Antes el listado y la ficha pasaban entradas
                    // distintas y el mismo tenant podia pintar salud diferente en cada pantalla.
                    whatsApp.AddonActive && whatsApp.SettingsEnabled,
                    whatsApp.LastErrorCode is not null,
                    hasPendingCheckout: pendingCheckoutTenants.Contains(tenant.Id),
                    isExpiringSoon: expiringSoonTenants.Contains(tenant.Id));

                tenantRows.Add(new PlatformTenantRowViewModel
                {
                    TenantId = tenant.Id,
                    TenantName = tenant.Nombre,
                    TenantActive = tenant.Activo,
                    CommercialAccessMode = tenant.CommercialAccessMode,
                    ForcedPlanId = tenant.ForcedPlanId,
                    ForcedPlanName = tenant.ForcedPlan?.Nombre,
                    OwnerEmail = owner.OwnerEmail,
                    OwnerName = owner.OwnerName,
                    OwnerSource = owner.Source,
                    OwnerWarnings = owner.Warnings,
                    CommercialNotes = tenant.CommercialNotes,
                    CanAccessApp = access.CanAccessApp,
                    EffectivePlanName = access.EffectivePlanName,
                    EffectivePlanCode = access.EffectivePlanCode,
                    EffectivePlanKind = access.EffectivePlanKind,
                    EffectiveEmployeeLimit = access.EffectiveEmployeeLimit,
                    IsForcedByPlatform = access.IsForcedByPlatform,
                    AccessBillingSource = access.BillingSource,
                    CommercialWarnings = access.Warnings,
                    Reason = access.Reason,
                    WhatsAppEnabled = whatsApp.SettingsEnabled,
                    WhatsAppAddonActive = whatsApp.AddonActive,
                    SendWhatsAppConfirmationOnCreate = whatsApp.SendConfirmationOnCreate,
                    SendWhatsAppReminderThreeHoursBefore = whatsApp.SendReminderThreeHoursBefore,
                    WhatsAppDailyMessageLimit = whatsApp.DailyMessageLimit,
                    WhatsAppTodayUsage = whatsApp.TodayUsage,
                    WhatsAppTimeZoneId = whatsApp.TimeZoneId,
                    WhatsAppNotes = whatsApp.Notes,
                    WhatsAppLastErrorCode = whatsApp.LastErrorCode,
                    WhatsAppLastErrorMessage = whatsApp.LastErrorMessage,
                    WhatsAppLastErrorAtUtc = whatsApp.LastErrorAtUtc,
                    WhatsAppAddonCode = whatsApp.AddonCode,
                    WhatsAppAddonIsManual = whatsApp.AddonIsManual,
                    WhatsAppAddonFechaFin = whatsApp.AddonFechaFin,
                    WhatsAppAddonMonthlyLimit = whatsApp.AddonMonthlyLimit,
                    WhatsAppAddonSource = whatsApp.AddonSource,
                    WhatsAppAddonManualIndefinite = whatsApp.AddonManualIndefinite,
                    WhatsAppAddonManualExpiresAtUtc = whatsApp.AddonManualExpiresAtUtc,
                    WhatsAppAddonManualExpired = whatsApp.AddonManualExpired,
                    WhatsAppAddonProviderRisk = whatsApp.AddonProviderRisk,
                    WhatsAppHasActiveProviderAddon = whatsApp.AddonActive &&
                        whatsApp.AddonSource == LuxuryApp.Models.SaaS.WhatsAppAddonBillingSource.ProviderRecurring,
                    Health = health,
                    Citas30d = usage.Citas30d,
                    Cobros30d = usage.Cobros30d,
                    BookingRequests30d = usage.BookingRequests30d,
                    BookingRequestsPending = usage.BookingRequestsPending,
                    LastActivityUtc = usage.LastActivityUtc
                });
            }

            var totalActiveSubscriptions = await _context.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(
                    subscription => subscription.Estado == EstadoSuscripcion.Activa || subscription.Estado == EstadoSuscripcion.Trial,
                    cancellationToken);

            var recentUsers = await _context.Users
                .AsNoTracking()
                .Join(
                    _context.Tenants.AsNoTracking(),
                    user => user.TenantId,
                    tenant => tenant.Id,
                    (user, tenant) => new PlatformRecentUserViewModel
                    {
                        Email = user.Email ?? user.UserName ?? string.Empty,
                        Name = user.Name,
                        TenantName = tenant.Nombre,
                        IsPlatformSuperAdmin = user.IsPlatformSuperAdmin
                    })
                .OrderByDescending(user => user.IsPlatformSuperAdmin)
                .ThenBy(user => user.Email)
                .Take(10)
                .ToListAsync(cancellationToken);

            var recentPayments = await _context.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(payment => payment.Plan)
                .Join(
                    _context.Tenants.AsNoTracking(),
                    payment => payment.TenantId,
                    tenant => tenant.Id,
                    (payment, tenant) => new PlatformRecentPaymentViewModel
                    {
                        TenantName = tenant.Nombre,
                        PlanName = payment.Plan != null ? payment.Plan.Nombre : "Sin plan",
                        Amount = payment.Monto,
                        Currency = payment.Moneda,
                        Status = payment.Estado,
                        CreatedUtc = payment.FechaCreacionUtc
                    })
                .OrderByDescending(payment => payment.CreatedUtc)
                .Take(10)
                .ToListAsync(cancellationToken);

            var latestSubscriptions = await _context.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(subscription => subscription.Plan)
                .Where(subscription =>
                    subscription.Proveedor == PaymentProviderType.Tilopay ||
                    subscription.TilopayRecurringPlanId.HasValue)
                .OrderByDescending(subscription => subscription.FechaUltimaActualizacionUtc ?? subscription.FechaInicio)
                .ThenByDescending(subscription => subscription.FechaInicio)
                .ToListAsync(cancellationToken);

            var latestSubscriptionByTenant = latestSubscriptions
                .GroupBy(subscription => subscription.TenantId)
                .ToDictionary(group => group.Key, group => group.First());

            var ownerByTenantId = tenantRows
                .ToDictionary(row => row.TenantId, row => row.OwnerEmail, EqualityComparer<Guid>.Default);

            var activeFuncionariosByTenant = await _context.Funcionarios
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(funcionario => tenantIds.Contains(funcionario.TenantId) && funcionario.Activo)
                .GroupBy(funcionario => funcionario.TenantId)
                .Select(group => new { TenantId = group.Key, Count = group.Count() })
                .ToDictionaryAsync(row => row.TenantId, row => row.Count, cancellationToken);

            foreach (var row in tenantRows)
            {
                row.ActiveFuncionarios = activeFuncionariosByTenant.TryGetValue(row.TenantId, out var count)
                    ? count
                    : 0;
            }

            var pendingRecurringCheckouts = await _context.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(payment => payment.Plan)
                .Where(payment =>
                    payment.Proveedor == PaymentProviderType.Tilopay &&
                    (payment.Estado == EstadoPagoProveedor.Pendiente ||
                     payment.Estado == EstadoPagoProveedor.ManualReview))
                .OrderByDescending(payment => payment.FechaCreacionUtc)
                .Take(25)
                .ToListAsync(cancellationToken);

            var pendingRecurringCheckoutRows = pendingRecurringCheckouts
                .Select(payment =>
                {
                    latestSubscriptionByTenant.TryGetValue(payment.TenantId, out var currentSubscription);
                    ownerByTenantId.TryGetValue(payment.TenantId, out var ownerEmail);
                    return new PlatformBillingPendingCheckoutViewModel
                    {
                        PaymentId = payment.Id,
                        TenantName = tenants.FirstOrDefault(tenant => tenant.Id == payment.TenantId)?.Nombre ?? payment.TenantId.ToString(),
                        OwnerEmail = ownerEmail,
                        PlanName = payment.Plan?.Nombre ?? "Sin plan",
                        PlanCode = payment.Plan?.Codigo,
                        CheckoutKind = ResolveCheckoutKind(payment, currentSubscription),
                        Amount = payment.Monto,
                        Currency = payment.Moneda,
                        Status = payment.Estado,
                        CreatedUtc = payment.FechaCreacionUtc,
                        CorrelationToken = payment.CorrelationToken ?? payment.ProviderReference,
                        ProviderSubscriberId = payment.ProviderSubscriberId,
                        ProviderTransactionId = payment.ProviderTransactionId,
                        ProviderResultMessage = payment.ProviderResultMessage
                    };
                })
                .ToArray();

            var recentBillingEvents = await _context.EventosPago
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(evento => evento.Proveedor == PaymentProviderType.Tilopay)
                .OrderByDescending(evento => evento.FechaRecepcionUtc)
                .Take(25)
                .ToListAsync(cancellationToken);

            var recentBillingEventRows = recentBillingEvents
                .Select(evento => new PlatformBillingEventViewModel
                {
                    TenantName = tenants.FirstOrDefault(tenant => tenant.Id == evento.TenantId)?.Nombre ?? (evento.TenantId?.ToString() ?? "Sin correlacion"),
                    PlanName = plans.FirstOrDefault(plan => plan.Id == evento.PlanId)?.Nombre,
                    EventType = evento.Tipo,
                    ProcessingStatus = evento.EstadoProcesamiento,
                    ReceivedUtc = evento.FechaRecepcionUtc,
                    Amount = evento.Monto,
                    Currency = evento.Moneda,
                    CorrelationId = evento.CorrelationId,
                    TilopayRecurringPlanId = evento.TilopayRecurringPlanId,
                    ProviderTransactionId = evento.ProviderTransactionId,
                    ProviderSubscriberId = evento.ProviderSubscriberId,
                    Error = evento.Error
                })
                .ToArray();

            var activeRecurringSubscriptionRows = latestSubscriptions
                .Where(subscription => subscription.Estado is EstadoSuscripcion.Activa or EstadoSuscripcion.Trial or EstadoSuscripcion.Morosa)
                .Take(25)
                .Select(subscription => new PlatformSubscriptionStatusViewModel
                {
                    TenantName = tenants.FirstOrDefault(tenant => tenant.Id == subscription.TenantId)?.Nombre ?? subscription.TenantId.ToString(),
                    PlanName = subscription.Plan?.Nombre ?? "Sin plan",
                    PlanCode = subscription.CodigoPlan ?? subscription.Plan?.Codigo,
                    Status = subscription.Estado,
                    CurrentPeriodEndUtc = subscription.FechaFin,
                    NextBillingDateUtc = subscription.FechaProximoCobroUtc,
                    MaxFuncionarios = subscription.MaxFuncionarios ?? subscription.Plan?.MaxFuncionarios,
                    ProviderSubscriberId = subscription.ProviderSubscriptionId,
                    ProviderTransactionId = subscription.ProviderTransactionId
                })
                .ToArray();

            var activeRecurringAddons = await _context.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(addon => addon.Plan)
                .Where(addon =>
                    addon.Estado == EstadoSuscripcion.Activa ||
                    addon.Estado == EstadoSuscripcion.Morosa)
                .OrderByDescending(addon => addon.UpdatedAtUtc)
                .Take(25)
                .ToListAsync(cancellationToken);

            var activeRecurringAddonRows = activeRecurringAddons
                .Select(addon => new PlatformSubscriptionStatusViewModel
                {
                    TenantName = tenants.FirstOrDefault(tenant => tenant.Id == addon.TenantId)?.Nombre ?? addon.TenantId.ToString(),
                    PlanName = addon.Plan?.Nombre ?? "Sin add-on",
                    PlanCode = addon.AddonCode ?? addon.Plan?.Codigo,
                    Status = addon.Estado,
                    CurrentPeriodEndUtc = addon.FechaFin,
                    NextBillingDateUtc = addon.FechaProximoCobroUtc,
                    MonthlyMessageLimit = addon.MonthlyMessageLimit > 0 ? addon.MonthlyMessageLimit : addon.Plan?.LimiteMensajesMensual,
                    ProviderSubscriberId = addon.ProviderSubscriptionId,
                    ProviderTransactionId = addon.ProviderTransactionId
                })
                .ToArray();

            var model = new PlatformDashboardViewModel
            {
                TotalTenants = tenants.Count,
                TotalUsers = await _context.Users.CountAsync(cancellationToken),
                TotalActiveSubscriptions = totalActiveSubscriptions,
                TotalPromotionalCodes = await _context.PromotionalCodes.CountAsync(cancellationToken),
                AvailablePlans = plans,
                BasePlanOptions = basePlans,
                AdvancedPlanOptions = advancedPlans,
                Tenants = tenantRows,
                RecentUsers = recentUsers,
                RecentPayments = recentPayments,
                PendingRecurringCheckouts = pendingRecurringCheckoutRows,
                RecentBillingEvents = recentBillingEventRows,
                ActiveRecurringSubscriptions = activeRecurringSubscriptionRows,
                ActiveRecurringAddons = activeRecurringAddonRows
            };

            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateTenantWhatsAppSettings(
            Guid tenantId,
            [Bind(Prefix = "whatsappSettings")] TenantWhatsAppSettingsUpdateDto whatsappSettings,
            CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                TempData["PlatformError"] = "Revisa la configuracion de WhatsApp. El limite no puede ser negativo y la zona horaria es obligatoria.";
                return RedirectToAction(nameof(Tenants));
            }

            var tenantExists = await _context.Tenants
                .AsNoTracking()
                .AnyAsync(tenant => tenant.Id == tenantId, cancellationToken);

            if (!tenantExists)
            {
                return NotFound();
            }

            // ── ACCESO COMERCIAL (entitlement): otorgar / revocar acceso manual ──
            // Separado de la configuración técnica (settings). El acceso manual NUNCA llama a TiloPay
            // y NO crea settings (Opción A). Un paquete TiloPay se gestiona por billing, no por aquí.
            var actorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "platform";
            var addonAction = (whatsappSettings.AddonCode ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(addonAction))
            {
                if (string.IsNullOrWhiteSpace(whatsappSettings.ManualAssignmentObservation))
                {
                    TempData["PlatformError"] = "El motivo/nota es obligatorio para otorgar o revocar el acceso WhatsApp manual.";
                    return RedirectToAction(nameof(Tenants));
                }

                var isRevoke = addonAction.Equals("NONE", StringComparison.OrdinalIgnoreCase);
                ManualWhatsAppGrantResult grantResult = new(ManualWhatsAppGrantOutcome.NoChange, string.Empty);

                DateTime? expiresAtUtc = null;
                if (!isRevoke && !whatsappSettings.ManualGrantIndefinite && whatsappSettings.ManualGrantExpiresOn is { } expiresOn)
                {
                    // Fin del día en Costa Rica (UTC-6) de la fecha elegida.
                    expiresAtUtc = new DateTimeOffset(
                        expiresOn.Date.AddDays(1).AddTicks(-1),
                        TimeSpan.FromHours(-6)).UtcDateTime;
                }

                await _tenantExecutionService.RunForTenantAsync(
                    tenantId,
                    async (serviceProvider, scopedTenantId, ct) =>
                    {
                        var svc = serviceProvider.GetRequiredService<SuscripcionService>();
                        grantResult = isRevoke
                            ? await svc.RevokeWhatsAppAddonAsync(
                                scopedTenantId,
                                actorUserId,
                                whatsappSettings.ManualAssignmentObservation,
                                ct)
                            : await svc.GrantManualWhatsAppAddonAsync(
                                scopedTenantId,
                                addonAction,
                                whatsappSettings.ManualGrantType,
                                whatsappSettings.ManualAssignmentObservation!,
                                whatsappSettings.ManualGrantIndefinite,
                                expiresAtUtc,
                                actorUserId,
                                whatsappSettings.ManualGrantAllowOverride,
                                ct);
                    },
                    cancellationToken);

                if (!grantResult.Success)
                {
                    TempData["PlatformError"] = grantResult.Message;
                    return RedirectToAction(nameof(Tenants));
                }

                var tenantNameForGrant = await _context.Tenants
                    .AsNoTracking()
                    .Where(tenant => tenant.Id == tenantId)
                    .Select(tenant => tenant.Nombre)
                    .FirstOrDefaultAsync(cancellationToken);

                await SafeAuditAsync(new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.WhatsAppSettingsUpdated,
                    EntityType = PlatformAuditEntityTypes.WhatsAppAddon,
                    EntityId = tenantId.ToString(),
                    TenantId = tenantId,
                    TenantName = tenantNameForGrant,
                    Reason = isRevoke
                        ? $"Acceso manual WhatsApp REVOCADO. {whatsappSettings.ManualAssignmentObservation}"
                        : $"Acceso manual WhatsApp {addonAction} ({whatsappSettings.ManualGrantType}, {(whatsappSettings.ManualGrantIndefinite ? "indefinido" : "hasta " + whatsappSettings.ManualGrantExpiresOn?.ToString("yyyy-MM-dd"))}). {whatsappSettings.ManualAssignmentObservation}"
                }, cancellationToken);

                TempData["PlatformSuccess"] = grantResult.Message;
                return RedirectToAction(nameof(Tenants));
            }

            // ── CONFIGURACIÓN TÉCNICA (settings): IsEnabled, límite diario, horarios ──
            await _tenantExecutionService.RunForTenantAsync(
                tenantId,
                async (serviceProvider, scopedTenantId, ct) =>
                {
                    var settingsService = serviceProvider.GetRequiredService<ITenantWhatsAppSettingsService>();
                    await settingsService.UpdateSettingsAsync(
                        scopedTenantId,
                        whatsappSettings,
                        actorUserId,
                        ct);
                },
                cancellationToken);

            var tenantNameForAudit = await _context.Tenants
                .AsNoTracking()
                .Where(tenant => tenant.Id == tenantId)
                .Select(tenant => tenant.Nombre)
                .FirstOrDefaultAsync(cancellationToken);

            await SafeAuditAsync(new PlatformAuditEntry
            {
                Action = PlatformAuditActions.WhatsAppSettingsUpdated,
                EntityType = PlatformAuditEntityTypes.Tenant,
                EntityId = tenantId.ToString(),
                TenantId = tenantId,
                TenantName = tenantNameForAudit,
                Reason = null
            }, cancellationToken);

            TempData["PlatformSuccess"] = "Configuracion WhatsApp del tenant actualizada.";
            return RedirectToAction(nameof(Tenants));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> TestMetaWhatsAppConfiguration(
            Guid? tenantId,
            CancellationToken cancellationToken)
        {
            if (tenantId.HasValue)
            {
                var tenantExists = await _context.Tenants
                    .AsNoTracking()
                    .AnyAsync(tenant => tenant.Id == tenantId.Value, cancellationToken);

                if (!tenantExists)
                {
                    return NotFound();
                }
            }

            var diagnostic = await _metaWhatsAppClient.TestConfigurationAsync(cancellationToken);

            await SafeAuditAsync(new PlatformAuditEntry
            {
                Action = PlatformAuditActions.MetaDiagnosticExecuted,
                EntityType = PlatformAuditEntityTypes.Tenant,
                EntityId = tenantId?.ToString(),
                TenantId = tenantId,
                Reason = diagnostic.Success ? "Diagnóstico Meta OK." : "Diagnóstico Meta con errores."
            }, cancellationToken);

            TenantWhatsAppSettingsSnapshot? tenantSettings = null;
            if (tenantId.HasValue)
            {
                await _tenantExecutionService.RunForTenantAsync(
                    tenantId.Value,
                    async (serviceProvider, scopedTenantId, ct) =>
                    {
                        var settingsService = serviceProvider.GetRequiredService<ITenantWhatsAppSettingsService>();
                        tenantSettings = await settingsService.GetSettingsForTenantAsync(scopedTenantId, ct);
                    },
                    cancellationToken);
            }

            return Json(new
            {
                success = diagnostic.Success,
                tenantId,
                configuration = diagnostic.Configuration,
                phoneNumberProbe = diagnostic.PhoneNumberProbe,
                wabaPhoneNumbersProbe = diagnostic.WabaPhoneNumbersProbe,
                phoneNumberBelongsToConfiguredWaba = diagnostic.PhoneNumberBelongsToConfiguredWaba,
                tenantSettings
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateTenantCommercialSettings(
            Guid tenantId,
            TenantCommercialAccessMode commercialAccessMode,
            Guid? forcedPlanId,
            string? commercialNotes,
            CancellationToken cancellationToken,
            Guid? legacyForcedPlanId = null)
        {
            var tenant = await _context.Tenants.FirstOrDefaultAsync(currentTenant => currentTenant.Id == tenantId, cancellationToken);
            if (tenant is null)
            {
                return NotFound();
            }

            if (!Enum.IsDefined(commercialAccessMode))
            {
                TempData["PlatformError"] = "Modo comercial no valido.";
                return RedirectToAction(nameof(Tenants));
            }

            // PendingVerification pertenece al flujo de registro/confirmacion de correo. Mantenerlo
            // es valido (guardar notas sin sacar al tenant del estado); MOVER un tenant ya
            // verificado a "pendiente" desde plataforma no lo es: le cortaria el acceso sin razon.
            if (commercialAccessMode == TenantCommercialAccessMode.PendingVerification &&
                tenant.CommercialAccessMode != TenantCommercialAccessMode.PendingVerification)
            {
                TempData["PlatformError"] =
                    "El modo 'Pendiente de verificacion' lo asigna el registro al confirmar el correo; no se puede establecer manualmente.";
                return RedirectToAction(nameof(Tenants));
            }

            // El selector avanzado (legacyForcedPlanId) es un campo APARTE y gana cuando trae valor:
            // asi el selector comercial nunca tiene que contener planes legacy/prueba.
            var effectiveForcedPlanId = legacyForcedPlanId ?? forcedPlanId;

            var requiresForcedPlan =
                commercialAccessMode is TenantCommercialAccessMode.Exempt or TenantCommercialAccessMode.Internal;

            Plan? selectedForcedPlan = null;
            if (requiresForcedPlan)
            {
                selectedForcedPlan = effectiveForcedPlanId.HasValue
                    ? await _context.Planes.AsNoTracking()
                        .FirstOrDefaultAsync(plan => plan.Id == effectiveForcedPlanId.Value && plan.Activo, cancellationToken)
                    : null;

                if (selectedForcedPlan is null)
                {
                    TempData["PlatformError"] = "Los tenants exentos o internos requieren un plan forzado activo.";
                    return RedirectToAction(nameof(Tenants));
                }

                // Un add-on de WhatsApp NO es un plan base: no tiene MaxFuncionarios, asi que
                // forzarlo dejaba al tenant con limite de funcionarios indefinido. Se rechaza aqui
                // (server-side) y no solo se oculta del selector.
                var kind = PlanCatalogRules.Classify(selectedForcedPlan);
                if (!PlanCatalogRules.IsBasePlan(kind))
                {
                    TempData["PlatformError"] =
                        $"'{selectedForcedPlan.Nombre}' es {PlanCatalogRules.DescribeKind(kind).ToLowerInvariant()} " +
                        "y no puede usarse como plan base forzado. Los paquetes de WhatsApp se administran en su propio control.";
                    return RedirectToAction(nameof(Tenants));
                }
            }

            var previousMode = tenant.CommercialAccessMode;
            var previousForcedPlanId = tenant.ForcedPlanId;

            tenant.CommercialAccessMode = commercialAccessMode;
            tenant.ForcedPlanId = requiresForcedPlan ? effectiveForcedPlanId : null;
            tenant.CommercialNotes = string.IsNullOrWhiteSpace(commercialNotes) ? null : commercialNotes.Trim();
            tenant.CommercialUpdatedUtc = DateTime.UtcNow;
            tenant.CommercialUpdatedByUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            await _context.SaveChangesAsync(cancellationToken);
            _accessCache.Invalidate(tenant.Id);

            var selectedKind = PlanCatalogRules.Classify(selectedForcedPlan);
            var isAdvancedChoice = selectedForcedPlan is not null && PlanCatalogRules.IsAdvancedOnly(selectedKind);

            await SafeAuditAsync(new PlatformAuditEntry
            {
                Action = PlatformAuditActions.TenantCommercialAccessUpdated,
                EntityType = PlatformAuditEntityTypes.Tenant,
                EntityId = tenant.Id.ToString(),
                TenantId = tenant.Id,
                TenantName = tenant.Nombre,
                BeforeJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Mode = previousMode.ToString(),
                    ForcedPlanId = previousForcedPlanId
                }),
                AfterJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    Mode = commercialAccessMode.ToString(),
                    ForcedPlanId = tenant.ForcedPlanId,
                    ForcedPlanCode = selectedForcedPlan?.Codigo,
                    ForcedPlanKind = selectedKind.ToString(),
                    // Marca explicita: se eligio un plan de la seccion avanzada (legacy/validacion),
                    // no un plan comercial vigente de la calculadora.
                    AdvancedLegacyChoice = isAdvancedChoice,
                    EffectiveEmployeeLimit = selectedForcedPlan?.MaxFuncionarios
                }),
                Reason = tenant.CommercialNotes
            }, cancellationToken);

            TempData["PlatformSuccess"] = isAdvancedChoice
                ? $"Configuracion comercial actualizada con un plan {PlanCatalogRules.DescribeKind(selectedKind).ToLowerInvariant()} " +
                  $"('{selectedForcedPlan!.Nombre}'). Quedo registrado en auditoria; migrar a un plan LC_M_/LC_A_ cuando se pueda."
                : "Configuracion comercial del tenant actualizada.";
            return RedirectToAction(nameof(Tenants));
        }

        [HttpGet]
        public async Task<IActionResult> PromotionalCodes(CancellationToken cancellationToken)
        {
            var model = await BuildPromotionalCodesPageAsync(new PlatformPromotionalCodeCreateViewModel(), cancellationToken);
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreatePromotionalCode(
            [Bind(Prefix = PromotionalCodeFormPrefix)] PlatformPromotionalCodeCreateViewModel model,
            CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return View("PromotionalCodes", await BuildPromotionalCodesPageAsync(model, cancellationToken));
            }

            var plan = await _context.Planes
                .AsNoTracking()
                .FirstOrDefaultAsync(currentPlan => currentPlan.Id == model.PlanId && currentPlan.Activo, cancellationToken);

            if (plan is null)
            {
                ModelState.AddModelError(
                    $"{PromotionalCodeFormPrefix}.{nameof(model.PlanId)}",
                    "Debes seleccionar un plan activo.");
                return View("PromotionalCodes", await BuildPromotionalCodesPageAsync(model, cancellationToken));
            }

            var code = new PromotionalCode
            {
                Id = Guid.NewGuid(),
                Codigo = model.Codigo.Trim().ToUpperInvariant(),
                Activo = model.Activo,
                TipoBeneficio = PromotionalBenefitType.FreeAccessDays,
                DiasGratis = model.DiasGratis,
                PlanId = model.PlanId,
                MaxUsos = model.MaxUsos,
                FechaExpiracionUtc = model.FechaExpiracionUtc,
                SoloPrimerRegistro = model.SoloPrimerRegistro,
                EmailObjetivo = string.IsNullOrWhiteSpace(model.EmailObjetivo) ? null : model.EmailObjetivo.Trim(),
                CreadoPorUserId = User.FindFirstValue(ClaimTypes.NameIdentifier),
                NotasInternas = string.IsNullOrWhiteSpace(model.NotasInternas) ? null : model.NotasInternas.Trim(),
                FechaCreacionUtc = DateTime.UtcNow
            };

            _context.PromotionalCodes.Add(code);

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                TempData["PlatformSuccess"] = "Codigo promocional creado correctamente.";
                return RedirectToAction(nameof(PromotionalCodes));
            }
            catch (DbUpdateException)
            {
                ModelState.AddModelError(
                    $"{PromotionalCodeFormPrefix}.{nameof(model.Codigo)}",
                    "Ya existe un codigo con ese valor.");
                return View("PromotionalCodes", await BuildPromotionalCodesPageAsync(model, cancellationToken));
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> TogglePromotionalCode(Guid id, CancellationToken cancellationToken)
        {
            var code = await _context.PromotionalCodes.FirstOrDefaultAsync(currentCode => currentCode.Id == id, cancellationToken);
            if (code is null)
            {
                return NotFound();
            }

            var estadoAnterior = code.Activo;
            code.Activo = !code.Activo;
            code.FechaActualizacionUtc = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            await SafeAuditAsync(new PlatformAuditEntry
            {
                Action = PlatformAuditActions.PromotionalCodeToggled,
                EntityType = PlatformAuditEntityTypes.PromotionalCode,
                EntityId = code.Id.ToString(),
                BeforeJson = JsonSerializer.Serialize(new { code.Codigo, Activo = estadoAnterior }),
                AfterJson = JsonSerializer.Serialize(new { code.Codigo, code.Activo })
            }, cancellationToken);

            TempData["PlatformSuccess"] = code.Activo
                ? "Codigo promocional activado."
                : "Codigo promocional desactivado.";

            return RedirectToAction(nameof(PromotionalCodes));
        }

        [HttpGet]
        public async Task<IActionResult> PromotionalCode(Guid id, CancellationToken cancellationToken)
        {
            var code = await _context.PromotionalCodes
                .AsNoTracking()
                .Include(currentCode => currentCode.Plan)
                .FirstOrDefaultAsync(currentCode => currentCode.Id == id, cancellationToken);

            if (code is null)
            {
                return NotFound();
            }

            var redemptions = await _context.PromotionalCodeRedemptions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(redemption => redemption.PromotionalCodeId == id)
                .Join(
                    _context.Tenants.AsNoTracking(),
                    redemption => redemption.TenantId,
                    tenant => tenant.Id,
                    (redemption, tenant) => new
                    {
                        redemption.EmailConsumidor,
                        redemption.FechaConsumoUtc,
                        TenantName = tenant.Nombre,
                        redemption.TenantCommercialAccessGrantId
                    })
                .ToListAsync(cancellationToken);

            var grantIds = redemptions
                .Where(redemption => redemption.TenantCommercialAccessGrantId.HasValue)
                .Select(redemption => redemption.TenantCommercialAccessGrantId!.Value)
                .Distinct()
                .ToList();

            var grants = await _context.TenantCommercialAccessGrants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(grant => grantIds.Contains(grant.Id))
                .ToDictionaryAsync(grant => grant.Id, cancellationToken);

            var model = new PlatformPromotionalCodeDetailsViewModel
            {
                Code = new PlatformPromotionalCodeListItemViewModel
                {
                    Id = code.Id,
                    Codigo = code.Codigo,
                    Activo = code.Activo,
                    PlanName = code.Plan?.Nombre ?? "Sin plan",
                    DiasGratis = code.DiasGratis,
                    MaxUsos = code.MaxUsos,
                    UsosActuales = code.UsosActuales,
                    FechaExpiracionUtc = code.FechaExpiracionUtc,
                    SoloPrimerRegistro = code.SoloPrimerRegistro,
                    EmailObjetivo = code.EmailObjetivo
                },
                NotasInternas = code.NotasInternas,
                Redemptions = redemptions
                    .OrderByDescending(redemption => redemption.FechaConsumoUtc)
                    .Select(redemption => new PlatformPromotionalCodeRedemptionItemViewModel
                    {
                        TenantName = redemption.TenantName,
                        EmailConsumidor = redemption.EmailConsumidor,
                        FechaConsumoUtc = redemption.FechaConsumoUtc,
                        AccessEndsUtc = redemption.TenantCommercialAccessGrantId.HasValue &&
                                        grants.TryGetValue(redemption.TenantCommercialAccessGrantId.Value, out var grant)
                            ? grant.FechaFinUtc
                            : null
                    })
                    .ToList()
            };

            return View(model);
        }

        private async Task<PlatformPromotionalCodesPageViewModel> BuildPromotionalCodesPageAsync(
            PlatformPromotionalCodeCreateViewModel createModel,
            CancellationToken cancellationToken)
        {
            var plans = await _context.Planes
                .AsNoTracking()
                .Where(plan => plan.Activo)
                .OrderBy(plan => plan.PrecioMensual)
                .ToListAsync(cancellationToken);

            var codes = await _context.PromotionalCodes
                .AsNoTracking()
                .Include(code => code.Plan)
                .OrderByDescending(code => code.FechaCreacionUtc)
                .Select(code => new PlatformPromotionalCodeListItemViewModel
                {
                    Id = code.Id,
                    Codigo = code.Codigo,
                    Activo = code.Activo,
                    PlanName = code.Plan != null ? code.Plan.Nombre : "Sin plan",
                    DiasGratis = code.DiasGratis,
                    MaxUsos = code.MaxUsos,
                    UsosActuales = code.UsosActuales,
                    FechaExpiracionUtc = code.FechaExpiracionUtc,
                    SoloPrimerRegistro = code.SoloPrimerRegistro,
                    EmailObjetivo = code.EmailObjetivo
                })
                .ToListAsync(cancellationToken);

            return new PlatformPromotionalCodesPageViewModel
            {
                AvailablePlans = plans,
                CreateForm = createModel,
                Codes = codes
            };
        }

        private static string ResolveCheckoutKind(PagoSuscripcion payment, Suscripcion? currentSubscription)
        {
            var planCode = payment.Plan?.Codigo?.Trim();
            if (!string.IsNullOrWhiteSpace(planCode) &&
                PlanCodes.WhatsAppAddons.Contains(planCode, StringComparer.OrdinalIgnoreCase))
            {
                return "PendingAddonCheckout";
            }

            if (currentSubscription is null ||
                currentSubscription.Estado is EstadoSuscripcion.Pendiente or EstadoSuscripcion.Fallida or EstadoSuscripcion.Suspendida or EstadoSuscripcion.Cancelada or EstadoSuscripcion.Vencida)
            {
                return "PendingSubscriptionSignup";
            }

            if (currentSubscription.PlanId != payment.PlanId)
            {
                return "PendingPlanChange";
            }

            return "PendingRecurringCheckout";
        }

    }
}
