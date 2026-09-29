using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.WhatsApp;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Tests.TenantIsolation
{
    /// <summary>
    /// Accesos manuales/cortesía/canje de WhatsApp (BillingSource=ManualGrant). Formaliza el entitlement
    /// comercial manual sin romper los add-ons pagados por TiloPay ni la Opción A (no crea settings).
    /// </summary>
    public class ManualWhatsAppAddonAssignmentTests
    {
        [Theory]
        [InlineData(PlanCodes.WhatsApp400, 400)]
        [InlineData(PlanCodes.WhatsApp800, 800)]
        [InlineData(PlanCodes.WhatsApp1200, 1200)]
        public async Task Grant_CreatesManualGrantWithCorrectMonthlyLimit_NoSettings(
            string addonCode,
            int expectedMonthlyLimit)
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _c = context;
            using var _n = connection;

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId);
            await SeedWhatsAppPlanAsync(context, addonCode, expectedMonthlyLimit);

            var svc = CreateSuscripcionService(context);
            var result = await svc.GrantManualWhatsAppAddonAsync(
                tenantId, addonCode, ManualWhatsAppGrantType.Courtesy, "Alta manual inicial",
                isIndefinite: false, expiresAtUtc: GetFixedNowUtc().AddDays(30),
                grantedByUserId: "platform-admin", allowOverrideProviderRecurring: false);

            Assert.Equal(ManualWhatsAppGrantOutcome.Granted, result.Outcome);

            var addon = await context.TenantSubscriptionAddons.IgnoreQueryFilters()
                .FirstOrDefaultAsync(a => a.TenantId == tenantId);
            Assert.NotNull(addon);
            Assert.Equal(EstadoSuscripcion.Activa, addon!.Estado);
            Assert.Equal(addonCode, addon.AddonCode);
            Assert.Equal(WhatsAppAddonBillingSource.ManualGrant, addon.BillingSource);
            Assert.Equal(expectedMonthlyLimit, addon.MonthlyMessageLimit);
            Assert.Equal("platform-admin", addon.GrantedByUserId);
            Assert.Null(addon.TilopayRecurringPlanId);
            Assert.Null(addon.ProviderSubscriptionId);

            // Opción A: otorgar acceso manual NO crea configuración técnica.
            Assert.Empty(context.TenantWhatsAppSettings);
        }

        [Fact]
        public async Task Grant_Indefinite_IsEffectiveEntitlementWithoutProvider()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _c = context;
            using var _n = connection;

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId);
            await SeedWhatsAppPlanAsync(context, PlanCodes.WhatsApp1200, 1200);

            var svc = CreateSuscripcionService(context);
            await svc.GrantManualWhatsAppAddonAsync(
                tenantId, PlanCodes.WhatsApp1200, ManualWhatsAppGrantType.Barter, "Canje permanente Northside",
                isIndefinite: true, expiresAtUtc: null,
                grantedByUserId: "platform-admin", allowOverrideProviderRecurring: false);

            var addon = await context.TenantSubscriptionAddons.IgnoreQueryFilters()
                .FirstAsync(a => a.TenantId == tenantId);

            var entitlement = svc.ResolveWhatsAppEntitlement(addon);
            Assert.True(addon.IsManualGrantIndefinite);
            Assert.Null(addon.ManualGrantExpiresAtUtc);
            Assert.True(entitlement.IsEffective);        // entitlement válido sin provider
            Assert.False(entitlement.IsProviderRisk);    // manual jamás es riesgo de dinero
            Assert.True(svc.IsWhatsAppAddonActive(addon));
        }

        [Fact]
        public async Task Grant_TemporalExpired_IsNotEffective_AndFlaggedOperational()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _c = context;
            using var _n = connection;

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId);
            await SeedWhatsAppPlanAsync(context, PlanCodes.WhatsApp400, 400);

            var svc = CreateSuscripcionService(context);
            await svc.GrantManualWhatsAppAddonAsync(
                tenantId, PlanCodes.WhatsApp400, ManualWhatsAppGrantType.Trial, "Prueba vencida",
                isIndefinite: false, expiresAtUtc: GetFixedNowUtc().AddDays(-5),
                grantedByUserId: "platform-admin", allowOverrideProviderRecurring: false);

            var addon = await context.TenantSubscriptionAddons.IgnoreQueryFilters()
                .FirstAsync(a => a.TenantId == tenantId);
            var entitlement = svc.ResolveWhatsAppEntitlement(addon);

            Assert.False(entitlement.IsEffective);            // vencido: no da acceso
            Assert.True(entitlement.IsManualGrantExpired);    // alerta operativa
            Assert.False(entitlement.IsProviderRisk);         // no es dinero
            Assert.False(svc.IsWhatsAppAddonActive(addon));

            // Vencido no permite envíos aunque haya settings habilitados.
            var settingsSvc = CreateSettingsService(context, tenantProvider, svc);
            await settingsSvc.UpdateSettingsAsync(tenantId,
                new TenantWhatsAppSettingsUpdateDto { IsEnabled = true, DailyMessageLimit = 30 }, "admin");
            var decision = await settingsSvc.CanSendNotificationAsync(tenantId, WhatsAppNotificationTypes.Confirmation);
            Assert.False(decision.CanSend);
            Assert.Equal(WhatsAppErrorCodes.NoActiveWhatsAppAddon, decision.ErrorCode);
        }

        [Fact]
        public async Task Grant_TemporalActive_AllowsSendOnlyWithEnabledSettings()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _c = context;
            using var _n = connection;

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId);
            await SeedWhatsAppPlanAsync(context, PlanCodes.WhatsApp400, 400);

            var svc = CreateSuscripcionService(context);
            await svc.GrantManualWhatsAppAddonAsync(
                tenantId, PlanCodes.WhatsApp400, ManualWhatsAppGrantType.Courtesy, "Cortesía vigente",
                isIndefinite: false, expiresAtUtc: GetFixedNowUtc().AddDays(20),
                grantedByUserId: "platform-admin", allowOverrideProviderRecurring: false);

            var settingsSvc = CreateSettingsService(context, tenantProvider, svc);

            // Sin settings persistidos: NotConfigured (Opción A), no envía.
            var denied = await settingsSvc.CanSendNotificationAsync(tenantId, WhatsAppNotificationTypes.Confirmation);
            Assert.False(denied.CanSend);
            Assert.Equal(WhatsAppErrorCodes.NotConfigured, denied.ErrorCode);
            Assert.Equal(400, denied.MonthlyMessageLimit); // cuota mensual desde el grant

            // Con settings habilitados: permite enviar.
            await settingsSvc.UpdateSettingsAsync(tenantId,
                new TenantWhatsAppSettingsUpdateDto { IsEnabled = true, DailyMessageLimit = 30 }, "admin");
            var allowed = await settingsSvc.CanSendNotificationAsync(tenantId, WhatsAppNotificationTypes.Confirmation);
            Assert.True(allowed.CanSend);
        }

        [Fact]
        public async Task SettingsEnabled_WithoutEffectiveEntitlement_BlocksSend()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _c = context;
            using var _n = connection;

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId);

            var svc = CreateSuscripcionService(context);
            var settingsSvc = CreateSettingsService(context, tenantProvider, svc);
            // Settings habilitados pero SIN add-on/grant efectivo → bloqueado por entitlement.
            await settingsSvc.UpdateSettingsAsync(tenantId,
                new TenantWhatsAppSettingsUpdateDto { IsEnabled = true, DailyMessageLimit = 30 }, "admin");

            var decision = await settingsSvc.CanSendNotificationAsync(tenantId, WhatsAppNotificationTypes.Confirmation);
            Assert.False(decision.CanSend);
            Assert.Equal(WhatsAppErrorCodes.NoActiveWhatsAppAddon, decision.ErrorCode);
        }

        [Fact]
        public async Task Grant_OnActiveProviderAddon_WithoutOverride_IsBlocked_AndProviderUntouched()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _c = context;
            using var _n = connection;

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId);
            await SeedWhatsAppPlanAsync(context, PlanCodes.WhatsApp400, 400);
            await SeedProviderRecurringAddonAsync(context, tenantId, PlanCodes.WhatsApp800, 800, "prov-sub-800", 7012);

            var svc = CreateSuscripcionService(context);
            var result = await svc.GrantManualWhatsAppAddonAsync(
                tenantId, PlanCodes.WhatsApp400, ManualWhatsAppGrantType.Courtesy, "intento override",
                isIndefinite: true, expiresAtUtc: null,
                grantedByUserId: "platform-admin", allowOverrideProviderRecurring: false);

            Assert.Equal(ManualWhatsAppGrantOutcome.BlockedProviderRecurringActive, result.Outcome);

            // El add-on TiloPay quedó intacto: NO se sobreescribió ni se canceló.
            var addon = await context.TenantSubscriptionAddons.IgnoreQueryFilters()
                .FirstAsync(a => a.TenantId == tenantId);
            Assert.Equal(WhatsAppAddonBillingSource.ProviderRecurring, addon.BillingSource);
            Assert.Equal(PlanCodes.WhatsApp800, addon.AddonCode);
            Assert.Equal("prov-sub-800", addon.ProviderSubscriptionId);
            Assert.Equal(7012, addon.TilopayRecurringPlanId);
        }

        [Fact]
        public async Task Grant_OnActiveProviderAddon_WithOverride_ConvertsToManual_WithoutCancellingTilopay()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _c = context;
            using var _n = connection;

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId);
            await SeedWhatsAppPlanAsync(context, PlanCodes.WhatsApp400, 400);
            await SeedProviderRecurringAddonAsync(context, tenantId, PlanCodes.WhatsApp800, 800, "prov-sub-800", 7012);

            var svc = CreateSuscripcionService(context);
            var result = await svc.GrantManualWhatsAppAddonAsync(
                tenantId, PlanCodes.WhatsApp400, ManualWhatsAppGrantType.Barter, "override confirmado",
                isIndefinite: true, expiresAtUtc: null,
                grantedByUserId: "platform-admin", allowOverrideProviderRecurring: true);

            Assert.Equal(ManualWhatsAppGrantOutcome.Granted, result.Outcome);

            var addon = await context.TenantSubscriptionAddons.IgnoreQueryFilters()
                .FirstAsync(a => a.TenantId == tenantId);
            Assert.Equal(WhatsAppAddonBillingSource.ManualGrant, addon.BillingSource);
            Assert.Equal(PlanCodes.WhatsApp400, addon.AddonCode);
            // No hubo cancelación de TiloPay: no se marcó ninguna baja pendiente del suscriptor.
            Assert.Equal(ProviderCancellationState.NotRequired, addon.ProviderCancellation);
            Assert.Null(addon.ProviderCancelledAtUtc);
        }

        [Fact]
        public async Task Grant_WithoutReason_IsInvalid()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _c = context;
            using var _n = connection;

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId);
            await SeedWhatsAppPlanAsync(context, PlanCodes.WhatsApp400, 400);

            var svc = CreateSuscripcionService(context);
            var result = await svc.GrantManualWhatsAppAddonAsync(
                tenantId, PlanCodes.WhatsApp400, ManualWhatsAppGrantType.Courtesy, "   ",
                isIndefinite: true, expiresAtUtc: null,
                grantedByUserId: "platform-admin", allowOverrideProviderRecurring: false);

            Assert.Equal(ManualWhatsAppGrantOutcome.Invalid, result.Outcome);
            Assert.Empty(context.TenantSubscriptionAddons);
        }

        [Fact]
        public async Task Revoke_ManualGrant_CancelsAddon_LeavesTrail_BasePlanUnchanged()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _c = context;
            using var _n = connection;

            await SeedTenantAsync(context, tenantId);
            var basePlanId = await SeedActiveBaseSubscriptionAsync(context, tenantId);
            await SeedWhatsAppPlanAsync(context, PlanCodes.WhatsApp400, 400);

            var svc = CreateSuscripcionService(context);
            await svc.GrantManualWhatsAppAddonAsync(
                tenantId, PlanCodes.WhatsApp400, ManualWhatsAppGrantType.Courtesy, "Alta",
                isIndefinite: true, expiresAtUtc: null, grantedByUserId: "admin", allowOverrideProviderRecurring: false);

            var result = await svc.RevokeWhatsAppAddonAsync(tenantId, "admin", "Fin del canje");
            Assert.Equal(ManualWhatsAppGrantOutcome.Revoked, result.Outcome);

            var addon = await context.TenantSubscriptionAddons.IgnoreQueryFilters()
                .FirstAsync(a => a.TenantId == tenantId);
            Assert.Equal(EstadoSuscripcion.Cancelada, addon.Estado);
            Assert.NotNull(addon.RevokedAtUtc);
            Assert.Equal("admin", addon.RevokedByUserId);
            Assert.False(svc.IsWhatsAppAddonActive(addon));

            var baseSub = await context.Suscripciones.IgnoreQueryFilters()
                .FirstAsync(s => s.TenantId == tenantId && s.PlanId == basePlanId);
            Assert.Equal(EstadoSuscripcion.Activa, baseSub.Estado);
        }

        [Fact]
        public async Task Revoke_ActiveProviderAddon_IsBlocked()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _c = context;
            using var _n = connection;

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId);
            await SeedProviderRecurringAddonAsync(context, tenantId, PlanCodes.WhatsApp400, 400, "prov-sub", 7011);

            var svc = CreateSuscripcionService(context);
            var result = await svc.RevokeWhatsAppAddonAsync(tenantId, "admin", "intento revoke");

            Assert.Equal(ManualWhatsAppGrantOutcome.BlockedProviderRecurringActive, result.Outcome);
            var addon = await context.TenantSubscriptionAddons.IgnoreQueryFilters()
                .FirstAsync(a => a.TenantId == tenantId);
            Assert.Equal(EstadoSuscripcion.Activa, addon.Estado); // no se tocó
        }

        [Fact]
        public async Task Grant_DoesNotTouchBasePlanOrMaxFuncionarios()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _c = context;
            using var _n = connection;

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId, maxFuncionarios: 5);
            await SeedWhatsAppPlanAsync(context, PlanCodes.WhatsApp800, 800);

            var svc = CreateSuscripcionService(context);
            await svc.GrantManualWhatsAppAddonAsync(
                tenantId, PlanCodes.WhatsApp800, ManualWhatsAppGrantType.Internal, "Interno",
                isIndefinite: true, expiresAtUtc: null, grantedByUserId: "admin", allowOverrideProviderRecurring: false);

            var basePlan = await context.Suscripciones.IgnoreQueryFilters()
                .Include(s => s.Plan)
                .Where(s => s.TenantId == tenantId && s.CodigoPlan == PlanCodes.Basic)
                .Select(s => s.Plan)
                .FirstOrDefaultAsync();

            Assert.NotNull(basePlan);
            Assert.Equal(5, basePlan!.MaxFuncionarios);
        }

        // --- Helpers ---

        private static SuscripcionService CreateSuscripcionService(
            ProyectoIdentity.Datos.ApplicationDbContext context)
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            var accessCache = new TenantCommercialAccessCache(cache);
            var businessDateTimeProvider = new FixedBusinessDateTimeProvider();
            return new SuscripcionService(
                context,
                cache,
                accessCache,
                businessDateTimeProvider,
                Options.Create(new TilopayRepeatOptions()),
                NullLogger<SuscripcionService>.Instance);
        }

        private static TenantWhatsAppSettingsService CreateSettingsService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            TestTenantProvider tenantProvider,
            SuscripcionService suscripcionService)
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            var accessCache = new TenantCommercialAccessCache(cache);
            var businessDateTimeProvider = new FixedBusinessDateTimeProvider();
            var commercialAccessResolver = new TenantCommercialAccessResolver(
                context, cache, accessCache, suscripcionService, businessDateTimeProvider);
            return new TenantWhatsAppSettingsService(
                context,
                tenantProvider,
                new StaticOptionsMonitor<MetaWhatsAppOptions>(new MetaWhatsAppOptions { Enabled = true }),
                suscripcionService,
                businessDateTimeProvider,
                commercialAccessResolver,
                NullLogger<TenantWhatsAppSettingsService>.Instance);
        }

        private static async Task SeedTenantAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context, Guid tenantId)
        {
            context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Tenant Manual Addon" });
            await context.SaveChangesAsync();
        }

        private static async Task<Guid> SeedActiveBaseSubscriptionAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context, Guid tenantId, int maxFuncionarios = 1)
        {
            var planId = Guid.NewGuid();
            var nowUtc = GetFixedNowUtc();
            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.Basic,
                Nombre = "Basico",
                Moneda = "CRC",
                PrecioMensual = 8000m,
                MaxFuncionarios = maxFuncionarios,
                Activo = true
            });
            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                CodigoPlan = PlanCodes.Basic,
                Estado = EstadoSuscripcion.Activa,
                Proveedor = PaymentProviderType.Tilopay,
                FechaInicio = nowUtc.AddDays(-3),
                FechaFin = nowUtc.AddDays(27),
                FechaProximoCobroUtc = nowUtc.AddDays(27),
                FechaUltimaActualizacionUtc = nowUtc
            });
            await context.SaveChangesAsync();
            return planId;
        }

        private static async Task SeedWhatsAppPlanAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context, string addonCode, int monthlyLimit)
        {
            context.Planes.Add(new Plan
            {
                Id = Guid.NewGuid(),
                Codigo = addonCode,
                Nombre = $"WhatsApp {addonCode}",
                Moneda = "CRC",
                PrecioMensual = 0m,
                LimiteMensajesMensual = monthlyLimit,
                Activo = true
            });
            await context.SaveChangesAsync();
        }

        private static async Task SeedProviderRecurringAddonAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId, string addonCode, int monthlyLimit, string providerSubscriberId, int recurringPlanId)
        {
            var planId = Guid.NewGuid();
            var nowUtc = GetFixedNowUtc();
            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = addonCode,
                Nombre = $"WhatsApp {addonCode}",
                Moneda = "CRC",
                PrecioMensual = 6000m,
                LimiteMensajesMensual = monthlyLimit,
                Activo = true
            });
            context.TenantSubscriptionAddons.Add(new TenantSubscriptionAddon
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                AddonCode = addonCode,
                Estado = EstadoSuscripcion.Activa,
                BillingSource = WhatsAppAddonBillingSource.ProviderRecurring,
                TilopayRecurringPlanId = recurringPlanId,
                ProviderSubscriptionId = providerSubscriberId,
                ProviderTransactionId = "TX-" + providerSubscriberId,
                MonthlyMessageLimit = monthlyLimit,
                FechaInicio = nowUtc.AddDays(-1),
                FechaFin = nowUtc.AddDays(29),
                FechaProximoCobroUtc = nowUtc.AddDays(29),
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            });
            await context.SaveChangesAsync();
        }

        private static DateTime GetFixedNowUtc() =>
            new DateTimeOffset(new DateTime(2026, 5, 26, 10, 30, 0), TimeSpan.FromHours(-6)).UtcDateTime;
    }
}
