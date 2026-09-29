using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Billing
{
    /// <summary>
    /// Guardas de dinero recurrente para cambio de plan base, centralizadas y testeables:
    /// compra normal, mismo plan, downgrade por límite, ProviderSubscriptionId ausente,
    /// cancelación vieja pendiente (riesgo triple cobro) y cambio válido.
    /// </summary>
    public class PlanChangeDecisionServiceTests
    {
        // Plan actual del tenant: LC_M_02 (recurring 7102). Destino de prueba: LC_M_03 (7103).
        private const int CurrentPlanId = 7102;
        private const int TargetPlanId = 7103;

        [Fact]
        public async Task NoActiveSubscription_ProceedsNormalCheckout()
        {
            var (context, connection) = CreateContext();
            using var c = context; using var d = connection;
            var tenantId = Guid.NewGuid();
            // sin suscripción sembrada

            var result = await CreateService(context).EvaluateAsync(tenantId, TargetPlanId, targetWorkerCount: 3, activeFuncionarios: 1);

            Assert.Equal(PlanChangeDecision.ProceedNormalCheckout, result.Decision);
        }

        [Fact]
        public async Task SamePlan_ReturnsSamePlanNoCheckout()
        {
            var (context, connection) = CreateContext();
            using var c = context; using var d = connection;
            var tenantId = Guid.NewGuid();
            SeedActiveSubscription(context, tenantId, CurrentPlanId, providerSubscriptionId: "900770", maxFunc: 2);
            await context.SaveChangesAsync();

            var result = await CreateService(context).EvaluateAsync(tenantId, CurrentPlanId, targetWorkerCount: 2, activeFuncionarios: 2);

            Assert.Equal(PlanChangeDecision.SamePlan, result.Decision);
        }

        [Fact]
        public async Task Downgrade_WithMoreActiveFuncionariosThanTarget_IsBlocked()
        {
            var (context, connection) = CreateContext();
            using var c = context; using var d = connection;
            var tenantId = Guid.NewGuid();
            SeedActiveSubscription(context, tenantId, CurrentPlanId, providerSubscriptionId: "900770", maxFunc: 3);
            await context.SaveChangesAsync();

            // LC_M_03 (3 activos) → LC_M_02 (cupo 2) con 3 activos: bloqueado.
            var result = await CreateService(context).EvaluateAsync(tenantId, TargetPlanId, targetWorkerCount: 2, activeFuncionarios: 3);

            Assert.Equal(PlanChangeDecision.BlockedFuncionarioLimit, result.Decision);
            Assert.Contains("desactivá funcionarios", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Downgrade_WithinTargetLimit_ProceedsPlanChange()
        {
            var (context, connection) = CreateContext();
            using var c = context; using var d = connection;
            var tenantId = Guid.NewGuid();
            SeedActiveSubscription(context, tenantId, CurrentPlanId, providerSubscriptionId: "900770", maxFunc: 3);
            await context.SaveChangesAsync();

            var result = await CreateService(context).EvaluateAsync(tenantId, TargetPlanId, targetWorkerCount: 2, activeFuncionarios: 2);

            Assert.Equal(PlanChangeDecision.ProceedPlanChange, result.Decision);
        }

        [Fact]
        public async Task ActiveSubscriptionWithoutProviderSubscriptionId_IsBlocked()
        {
            var (context, connection) = CreateContext();
            using var c = context; using var d = connection;
            var tenantId = Guid.NewGuid();
            SeedActiveSubscription(context, tenantId, CurrentPlanId, providerSubscriptionId: null, maxFunc: 2);
            await context.SaveChangesAsync();

            var result = await CreateService(context).EvaluateAsync(tenantId, TargetPlanId, targetWorkerCount: 3, activeFuncionarios: 1);

            Assert.Equal(PlanChangeDecision.BlockedMissingProviderSubscription, result.Decision);
        }

        [Fact]
        public async Task ExistingAppliedIntentWithPendingOldCancellation_BlocksNewChange()
        {
            var (context, connection) = CreateContext();
            using var c = context; using var d = connection;
            var tenantId = Guid.NewGuid();
            SeedActiveSubscription(context, tenantId, CurrentPlanId, providerSubscriptionId: "900770", maxFunc: 2);
            context.PlanChangeIntents.Add(new PlanChangeIntent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                FromPlanCode = "LC_M_01",
                FromProviderSubscriptionId = "OLD-900830",
                ToPlanId = Guid.NewGuid(),
                ToPlanCode = "LC_M_02",
                ToWorkerCount = 2,
                ToBillingCycle = BillingCycle.Monthly,
                ToTilopayRecurringPlanId = CurrentPlanId,
                Estado = PlanChangeIntentState.Applied,
                OldProviderCancellation = ProviderCancellationState.PendingManualCancellation,
                AppliedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();

            var result = await CreateService(context).EvaluateAsync(tenantId, TargetPlanId, targetWorkerCount: 3, activeFuncionarios: 1);

            Assert.Equal(PlanChangeDecision.BlockedPendingOldCancellation, result.Decision);
        }

        // ── Evidencia prod: con AutoCancel=false NUNCA debe poder pagarse un cambio ──
        [Fact]
        public async Task AutoCancelDisabled_ActiveTenantChangingPlan_IsBlockedBeforeCheckout()
        {
            var (context, connection) = CreateContext();
            using var c = context; using var d = connection;
            var tenantId = Guid.NewGuid();
            // Tenant con LC_M_02 activo y suscriptor 900770.
            SeedActiveSubscription(context, tenantId, CurrentPlanId, providerSubscriptionId: "900770", maxFunc: 2);
            await context.SaveChangesAsync();

            var service = CreateService(context, autoCancelOldSubscriber: false);

            // Selecciona LC_M_03 (7103).
            var result = await service.EvaluateAsync(tenantId, TargetPlanId, targetWorkerCount: 3, activeFuncionarios: 1);

            Assert.Equal(PlanChangeDecision.BlockedAutoCancellationDisabled, result.Decision);
            Assert.Contains("contactá soporte", result.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task AutoCancelDisabled_NewTenantWithoutSubscription_StillBuysNormally()
        {
            var (context, connection) = CreateContext();
            using var c = context; using var d = connection;
            var tenantId = Guid.NewGuid();

            var service = CreateService(context, autoCancelOldSubscriber: false);

            // Sin suscripción activa no hay suscriptor viejo que cancelar: la compra normal sigue.
            var result = await service.EvaluateAsync(tenantId, TargetPlanId, targetWorkerCount: 3, activeFuncionarios: 1);

            Assert.Equal(PlanChangeDecision.ProceedNormalCheckout, result.Decision);
        }

        [Fact]
        public async Task ValidPlanChange_ProceedsPlanChange()
        {
            var (context, connection) = CreateContext();
            using var c = context; using var d = connection;
            var tenantId = Guid.NewGuid();
            SeedActiveSubscription(context, tenantId, CurrentPlanId, providerSubscriptionId: "900770", maxFunc: 2);
            await context.SaveChangesAsync();

            var result = await CreateService(context).EvaluateAsync(tenantId, TargetPlanId, targetWorkerCount: 3, activeFuncionarios: 1);

            Assert.Equal(PlanChangeDecision.ProceedPlanChange, result.Decision);
            Assert.NotNull(result.CurrentSubscription);
            Assert.Equal("900770", result.CurrentSubscription!.ProviderSubscriptionId);
        }

        // ── Helpers ──

        private static void SeedActiveSubscription(
            ApplicationDbContext context,
            Guid tenantId,
            int recurringPlanId,
            string? providerSubscriptionId,
            int maxFunc)
        {
            context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Tenant PC", Activo = true });
            var planId = Guid.NewGuid();
            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = $"LC_M_{Guid.NewGuid():N}"[..12],
                Nombre = "Plan actual",
                PrecioMensual = 15000m,
                BillingCycle = BillingCycle.Monthly,
                Moneda = "CRC",
                MaxFuncionarios = maxFunc,
                Activo = true
            });
            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                CodigoPlan = "LC_M_02",
                Estado = EstadoSuscripcion.Activa,
                Proveedor = PaymentProviderType.Tilopay,
                TilopayRecurringPlanId = recurringPlanId,
                ProviderSubscriptionId = providerSubscriptionId,
                MaxFuncionarios = maxFunc,
                FechaInicio = DateTime.UtcNow.AddDays(-5),
                FechaFin = DateTime.UtcNow.AddDays(25),
                FechaUltimaActualizacionUtc = DateTime.UtcNow.AddDays(-1)
            });
        }

        private static PlanChangeDecisionService CreateService(
            ApplicationDbContext context,
            bool autoCancelOldSubscriber = true)
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            var subscriptionService = new SuscripcionService(
                context,
                cache,
                new TenantCommercialAccessCache(cache),
                new FixedBusinessDateTimeProvider(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)),
                Options.Create(new TilopayRepeatOptions()),
                NullLogger<SuscripcionService>.Instance);

            return new PlanChangeDecisionService(
                context,
                subscriptionService,
                Options.Create(new OpcionesTilopayRepeatAdmin
                {
                    Enabled = true,
                    AutoCancelOldSubscriberOnUpgrade = autoCancelOldSubscriber
                }));
        }

        private static (ApplicationDbContext Context, IDisposable Connection) CreateContext() =>
            TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
    }
}
