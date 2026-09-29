using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Billing
{
    /// <summary>
    /// BillingHealth clasifica los add-ons por FUENTE: solo el recurrente pagado activo sin provider
    /// sub cuenta como riesgo de dinero; manuales vigentes son informativos; manuales vencidos y legacy
    /// son operativos/informativos. (rule 10)
    /// </summary>
    public class WhatsAppAddonSourceHealthTests
    {
        private static readonly DateTime NowUtc =
            new DateTimeOffset(new DateTime(2026, 5, 26, 10, 30, 0), TimeSpan.FromHours(-6)).UtcDateTime;

        [Fact]
        public async Task Build_ClassifiesAddonsBySource_MoneyRiskOnlyProviderRecurringWithoutSub()
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            using var _c = context;
            using var _n = connection;

            // 1) ProviderRecurring pagado CON provider sub → efectivo, NO riesgo.
            await SeedAddonAsync(context, a =>
            {
                a.BillingSource = WhatsAppAddonBillingSource.ProviderRecurring;
                a.TilopayRecurringPlanId = 7011;
                a.ProviderSubscriptionId = "prov-sub-ok";
            });
            // 2) ProviderRecurring recurrente SIN provider sub → RIESGO DE DINERO.
            await SeedAddonAsync(context, a =>
            {
                a.BillingSource = WhatsAppAddonBillingSource.ProviderRecurring;
                a.TilopayRecurringPlanId = 7011;
                a.ProviderSubscriptionId = null;
            });
            // 3) ManualGrant indefinido → informativo, NO riesgo.
            await SeedAddonAsync(context, a =>
            {
                a.BillingSource = WhatsAppAddonBillingSource.ManualGrant;
                a.IsManualGrantIndefinite = true;
                a.FechaFin = null;
                a.ProviderSubscriptionId = null;
                a.TilopayRecurringPlanId = null;
            });
            // 4) ManualGrant VENCIDO (fila activa) → alerta operativa, NO riesgo.
            await SeedAddonAsync(context, a =>
            {
                a.BillingSource = WhatsAppAddonBillingSource.ManualGrant;
                a.IsManualGrantIndefinite = false;
                a.ManualGrantExpiresAtUtc = NowUtc.AddDays(-5);
                a.FechaFin = NowUtc.AddDays(-5);
                a.ProviderSubscriptionId = null;
                a.TilopayRecurringPlanId = null;
            });
            // 5) Legacy activo → informativo/limpieza, NO riesgo, NO efectivo.
            await SeedAddonAsync(context, a =>
            {
                a.BillingSource = WhatsAppAddonBillingSource.Legacy;
                a.ProviderSubscriptionId = null;
                a.TilopayRecurringPlanId = null;
            });

            var health = new BillingHealthService(context, CreateSuscripcionService(context));
            var snapshot = await health.BuildAsync();

            Assert.Equal(1, snapshot.PaidAddonsActiveWithoutProviderRisk);
            Assert.Equal(1, snapshot.ManualWhatsAppGrantsActive);
            Assert.Equal(1, snapshot.ManualWhatsAppGrantsExpiredStillActive);
            Assert.Equal(1, snapshot.LegacyWhatsAppAddonsActive);
            // Riesgo de dinero del add-on = SOLO el recurrente sin provider (sin pendientes ni doble activo).
            Assert.Equal(1, snapshot.WhatsAppAddonMoneyRiskCount);
            // Add-ons efectivos: provider ok + provider-risk (activo) + manual indefinido = 3.
            Assert.Equal(3, snapshot.ActiveWhatsAppAddons);
        }

        /// <summary>
        /// Escenario: base EXENTO con plan forzado (no hay fila en Suscripciones) + add-on WhatsApp
        /// ManualGrant indefinido. El acceso base es legitimo, asi que NO es riesgo de dinero y
        /// tampoco cuenta como "add-on sin plan base" (regla 11), que era la alerta falsa.
        /// </summary>
        [Fact]
        public async Task Build_ExemptTenantWithForcedPlan_ManualAddon_NoEsRiesgoNiAddonSinBase()
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            using var _c = context;
            using var _n = connection;

            var tenantId = Guid.NewGuid();
            var basePlanId = Guid.NewGuid();

            context.Planes.Add(new Plan
            {
                Id = basePlanId,
                Codigo = "LC_M_05",
                Nombre = "LuxuryCloud Mensual 5 funcionarios",
                Moneda = "CRC",
                PrecioMensual = 50_000m,
                MaxFuncionarios = 5,
                Activo = true
            });

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Northside Studio",
                Activo = true,
                CommercialAccessMode = TenantCommercialAccessMode.Exempt,
                ForcedPlanId = basePlanId,
                CommercialNotes = "Canje / acceso manual"
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            await SeedAddonAsync(context, addon =>
            {
                addon.BillingSource = WhatsAppAddonBillingSource.ManualGrant;
                addon.IsManualGrantIndefinite = true;
                addon.FechaFin = null;
                addon.ProviderSubscriptionId = null;
                addon.TilopayRecurringPlanId = null;
            }, tenantId, addonCode: PlanCodes.WhatsApp1200);

            var health = new BillingHealthService(context, CreateSuscripcionService(context));
            var snapshot = await health.BuildAsync();

            // Sin dinero en riesgo por ningun concepto de add-on.
            Assert.Equal(0, snapshot.PaidAddonsActiveWithoutProviderRisk);
            Assert.Equal(0, snapshot.WhatsAppAddonMoneyRiskCount);
            // Regla 11: el acceso base otorgado por plataforma cuenta como base valida.
            Assert.Equal(0, snapshot.WhatsAppAddonsWithoutActiveBase);
            // El acceso manual sigue siendo visible como informativo.
            Assert.Equal(1, snapshot.ManualWhatsAppGrantsActive);
            Assert.Equal(0, snapshot.ManualWhatsAppGrantsExpiredStillActive);
        }

        /// <summary>
        /// Contraprueba de la regla 11: un tenant que REQUIERE suscripcion y no la tiene sigue
        /// contando como add-on sin plan base. La excepcion es solo para exento/interno con plan forzado.
        /// </summary>
        [Fact]
        public async Task Build_TenantSinSuscripcionNiExencion_SigueContandoComoAddonSinBase()
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            using var _c = context;
            using var _n = connection;

            await SeedAddonAsync(context, addon =>
            {
                addon.BillingSource = WhatsAppAddonBillingSource.ManualGrant;
                addon.IsManualGrantIndefinite = true;
                addon.FechaFin = null;
                addon.ProviderSubscriptionId = null;
                addon.TilopayRecurringPlanId = null;
            });

            var health = new BillingHealthService(context, CreateSuscripcionService(context));
            var snapshot = await health.BuildAsync();

            Assert.Equal(1, snapshot.WhatsAppAddonsWithoutActiveBase);
            Assert.Equal(0, snapshot.WhatsAppAddonMoneyRiskCount);
        }

        /// <summary>
        /// Un tenant exento SIN plan forzado no tiene acceso base valido: la excepcion de la regla 11
        /// exige plan forzado, no solo el modo comercial.
        /// </summary>
        [Fact]
        public async Task Build_TenantExentoSinPlanForzado_SigueContandoComoAddonSinBase()
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            using var _c = context;
            using var _n = connection;

            var tenantId = Guid.NewGuid();
            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Exento mal configurado",
                Activo = true,
                CommercialAccessMode = TenantCommercialAccessMode.Exempt,
                ForcedPlanId = null
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            await SeedAddonAsync(context, addon =>
            {
                addon.BillingSource = WhatsAppAddonBillingSource.ManualGrant;
                addon.IsManualGrantIndefinite = true;
                addon.FechaFin = null;
                addon.ProviderSubscriptionId = null;
                addon.TilopayRecurringPlanId = null;
            }, tenantId);

            var health = new BillingHealthService(context, CreateSuscripcionService(context));
            var snapshot = await health.BuildAsync();

            Assert.Equal(1, snapshot.WhatsAppAddonsWithoutActiveBase);
        }

        private static async Task SeedAddonAsync(
            ApplicationDbContext context,
            Action<TenantSubscriptionAddon> configure,
            Guid? existingTenantId = null,
            string addonCode = PlanCodes.WhatsApp400)
        {
            var tenantId = existingTenantId ?? Guid.NewGuid();
            var planId = Guid.NewGuid();

            if (existingTenantId is null)
            {
                context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "T", Activo = true });
            }

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = "WA-" + planId.ToString("N")[..8], // Planes.Codigo es único
                Nombre = "WhatsApp 400",
                Moneda = "CRC",
                PrecioMensual = 6000m,
                LimiteMensajesMensual = 400,
                Activo = true
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var addon = new TenantSubscriptionAddon
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                AddonCode = addonCode,
                Estado = EstadoSuscripcion.Activa,
                MonthlyMessageLimit = 400,
                FechaInicio = NowUtc.AddDays(-2),
                FechaFin = NowUtc.AddDays(28),
                CreatedAtUtc = NowUtc,
                UpdatedAtUtc = NowUtc
            };
            configure(addon);
            context.TenantSubscriptionAddons.Add(addon);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }

        private static SuscripcionService CreateSuscripcionService(ApplicationDbContext context)
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            return new SuscripcionService(
                context,
                cache,
                new TenantCommercialAccessCache(cache),
                new FixedBusinessDateTimeProvider(),
                Options.Create(new TilopayRepeatOptions()),
                NullLogger<SuscripcionService>.Instance);
        }
    }
}
