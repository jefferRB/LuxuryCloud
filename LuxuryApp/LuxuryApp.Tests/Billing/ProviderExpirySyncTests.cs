using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.Tilopay;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Billing
{
    /// <summary>
    /// Sincronización de la fecha real de cobro de TiloPay con la vigencia local.
    ///
    /// Regresión: tras un downgrade LC_M_03 → LC_M_02, TiloPay
    /// reactivó el suscriptor 900117 y le puso expire 2026-09-15, pero LuxuryCloud calculó
    /// FechaProximoCobroUtc = 2026-08-15. Sin este fix, en agosto lo marcaríamos moroso por un
    /// cobro que TiloPay no iba a hacer hasta septiembre.
    ///
    /// Principio defendido: el proveedor solo puede EXTENDER la vigencia, nunca acortarla.
    /// </summary>
    public class ProviderExpirySyncTests
    {
        private const string SubscriberId = "900117";
        private const int RecurringPlanId = 7102;

        // ── 1. Provider posterior a local => extiende, no marca moroso ──

        [Fact]
        public async Task Sync_ProviderExpiryLaterThanLocal_ExtendsLocalDatesAndAudits()
        {
            using var h = await Harness.CreateAsync();
            // Escenario: local 2026-08-15, proveedor 2026-09-15.
            var subId = await h.SeedActiveSubscriptionAsync(
                localEndUtc: new DateTime(2026, 8, 15, 22, 3, 57, DateTimeKind.Utc));
            h.SetProviderSubscriber(SubscriberId, status: "Active", expire: "2026-09-15");

            await h.Sync.SyncActiveSubscriptionsAsync(h.Report);

            var sub = await h.GetSubscriptionAsync(subId);
            // El expire real quedó guardado...
            Assert.Equal(ProviderExpiryDate.ParseCostaRicaEndOfDayUtc("2026-09-15"), sub.ProviderExpiresAtUtc);
            Assert.Equal("2026-09-15", sub.ProviderExpiryRaw);
            Assert.NotNull(sub.ProviderExpiryLastSyncedUtc);
            // ...y la vigencia local se extendió a esa fecha.
            Assert.Equal(sub.ProviderExpiresAtUtc, sub.FechaFin);
            Assert.Equal(sub.ProviderExpiresAtUtc, sub.FechaProximoCobroUtc);

            Assert.Equal(1, h.Report.ProviderExpiriesReconciled);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.BillingProviderExpiryReconciled));
        }

        [Fact]
        public async Task Sync_AfterExtension_NotMarkedOverdueAtOldLocalDate()
        {
            // El reloj de la suite (FixedBusinessDateTimeProvider) es un instante fijo; simulamos
            // "ya pasó la fecha local vieja" poniendo local en el pasado y provider en el futuro.
            using var h = await Harness.CreateAsync();
            var subId = await h.SeedActiveSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(-2));
            h.SetProviderSubscriber(SubscriberId, status: "Active", expire: FormatExpire(h.NowUtc.AddDays(28)));

            await h.Sync.SyncActiveSubscriptionsAsync(h.Report);

            var sub = await h.GetSubscriptionAsync(subId);
            // Estado efectivo: Activa, NO morosa/suspendida, porque la fecha efectiva está en el futuro.
            Assert.Equal(EstadoSuscripcion.Activa, h.SubscriptionService.GetEffectiveStatus(sub));
            Assert.True(h.SubscriptionService.CanAccessApp(sub));
        }

        [Fact]
        public async Task Sync_OverdueAlert_NotRaisedWhenProviderCoversLater()
        {
            using var h = await Harness.CreateAsync();
            // Local vencida hace mucho, pero el proveedor cobra en el futuro.
            var subId = await h.SeedActiveSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(-40));
            h.SetProviderSubscriber(SubscriberId, status: "Active", expire: FormatExpire(h.NowUtc.AddDays(20)));

            // Pase completo: incluye SyncProviderExpiry ANTES de AlertOverdueRenewals.
            var report = await h.Reconciliation.RunAsync();

            Assert.Equal(1, report.ProviderExpiriesReconciled);
            Assert.Equal(0, report.OverdueRenewalsAlerted); // la extensión evita el falso positivo
        }

        // ── 2. Provider anterior a local => alerta, NO acorta ──

        [Fact]
        public async Task Sync_ProviderExpiryEarlierThanLocal_DoesNotShortenAndAlerts()
        {
            using var h = await Harness.CreateAsync();
            var localEnd = h.NowUtc.AddDays(30);
            var subId = await h.SeedActiveSubscriptionAsync(localEndUtc: localEnd);
            h.SetProviderSubscriber(SubscriberId, status: "Active", expire: FormatExpire(h.NowUtc.AddDays(2)));

            await h.Sync.SyncActiveSubscriptionsAsync(h.Report);

            var sub = await h.GetSubscriptionAsync(subId);
            // El expire se guarda para auditoría...
            Assert.NotNull(sub.ProviderExpiresAtUtc);
            // ...pero la vigencia local NO se acorta.
            Assert.Equal(localEnd, sub.FechaFin);

            Assert.Equal(1, h.Report.ProviderExpiryEarlierAlerts);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.BillingProviderExpiryEarlierThanLocal));
            Assert.Equal(0, await h.CountAuditAsync(PlatformAuditActions.BillingProviderExpiryReconciled));
        }

        // ── 3. Provider igual (dentro de tolerancia) => guarda pero no cambia ni alerta ──

        [Fact]
        public async Task Sync_ProviderExpiryWithinTolerance_PersistsButDoesNotChangeDatesOrAlert()
        {
            using var h = await Harness.CreateAsync();
            var localEnd = new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);
            var subId = await h.SeedActiveSubscriptionAsync(localEndUtc: localEnd);
            // expire 2026-09-15 end-of-day CR = 2026-09-16 05:59 UTC → ~6h de diferencia, < 12h tolerancia.
            h.SetProviderSubscriber(SubscriberId, status: "Active", expire: "2026-09-15");

            await h.Sync.SyncActiveSubscriptionsAsync(h.Report);

            var sub = await h.GetSubscriptionAsync(subId);
            Assert.NotNull(sub.ProviderExpiresAtUtc);           // guardado
            Assert.Equal(localEnd, sub.FechaFin);               // sin cambio
            Assert.Equal(0, h.Report.ProviderExpiriesReconciled);
            Assert.Equal(0, h.Report.ProviderExpiryEarlierAlerts);
            Assert.Equal(1, h.Report.ProviderExpiriesSynced);
        }

        // ── 4. Solo se confía en suscriptor ACTIVE ──

        [Theory]
        [InlineData("Delete")]
        [InlineData("4")]
        [InlineData("Paused")]
        [InlineData("status-raro")]
        public async Task Sync_NonActiveSubscriber_DoesNotTouchExpiry(string status)
        {
            using var h = await Harness.CreateAsync();
            var localEnd = h.NowUtc.AddDays(5);
            var subId = await h.SeedActiveSubscriptionAsync(localEndUtc: localEnd);
            h.SetProviderSubscriber(SubscriberId, status: status, expire: FormatExpire(h.NowUtc.AddDays(40)));

            await h.Sync.SyncActiveSubscriptionsAsync(h.Report);

            var sub = await h.GetSubscriptionAsync(subId);
            Assert.Null(sub.ProviderExpiresAtUtc); // no se guarda expire de un suscriptor no confiable
            Assert.Equal(localEnd, sub.FechaFin);
            Assert.Equal(0, h.Report.ProviderExpiriesSynced);
        }

        [Fact]
        public async Task Sync_SubscriberAbsentInProvider_DoesNotTouchExpiry()
        {
            using var h = await Harness.CreateAsync();
            var subId = await h.SeedActiveSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(5));
            // No se agrega ningún suscriptor al fake: el plan vuelve vacío.

            await h.Sync.SyncActiveSubscriptionsAsync(h.Report);

            var sub = await h.GetSubscriptionAsync(subId);
            Assert.Null(sub.ProviderExpiresAtUtc);
        }

        [Fact]
        public async Task Sync_ActiveButNoExpireInPayload_DoesNotTouchExpiry()
        {
            using var h = await Harness.CreateAsync();
            var subId = await h.SeedActiveSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(5));
            h.SetProviderSubscriber(SubscriberId, status: "Active", expire: null);

            await h.Sync.SyncActiveSubscriptionsAsync(h.Report);

            Assert.Null((await h.GetSubscriptionAsync(subId)).ProviderExpiresAtUtc);
        }

        // ── 5. Idempotencia y aislamiento ──

        [Fact]
        public async Task Sync_RunTwice_DoesNotDoubleAudit()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedActiveSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(2));
            h.SetProviderSubscriber(SubscriberId, status: "Active", expire: FormatExpire(h.NowUtc.AddDays(40)));

            await h.Sync.SyncActiveSubscriptionsAsync(h.Report);
            var secondReport = new BillingReconciliationReport();
            await h.Sync.SyncActiveSubscriptionsAsync(secondReport);

            // La segunda vez ya no hay que extender (la fecha local ya es la del proveedor).
            Assert.Equal(0, secondReport.ProviderExpiriesReconciled);
            Assert.Equal(1, await h.CountAuditAsync(PlatformAuditActions.BillingProviderExpiryReconciled));
        }

        [Fact]
        public async Task Sync_DoesNotMixTenants()
        {
            using var h = await Harness.CreateAsync();
            var subA = await h.SeedActiveSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(2));
            var otherTenant = Guid.NewGuid();
            var subB = await h.SeedActiveSubscriptionAsync(
                localEndUtc: h.NowUtc.AddDays(2), tenantId: otherTenant, subscriberId: "999999");

            h.SetProviderSubscriber(SubscriberId, status: "Active", expire: FormatExpire(h.NowUtc.AddDays(40)));
            h.SetProviderSubscriber("999999", status: "Active", expire: FormatExpire(h.NowUtc.AddDays(50)));

            await h.Sync.SyncActiveSubscriptionsAsync(h.Report);

            var a = await h.GetSubscriptionAsync(subA);
            var b = await h.GetSubscriptionAsync(subB);
            Assert.Equal(ProviderExpiryDate.ParseCostaRicaEndOfDayUtc(FormatExpire(h.NowUtc.AddDays(40))), a.ProviderExpiresAtUtc);
            Assert.Equal(ProviderExpiryDate.ParseCostaRicaEndOfDayUtc(FormatExpire(h.NowUtc.AddDays(50))), b.ProviderExpiresAtUtc);

            var auditA = await h.Db.PlatformAuditLogs.SingleAsync(l =>
                l.Action == PlatformAuditActions.BillingProviderExpiryReconciled && l.TenantId == h.TenantId);
            Assert.Equal(subA.ToString(), auditA.EntityId);
        }

        [Fact]
        public async Task Sync_RunsInFastWorkerPass()
        {
            using var h = await Harness.CreateAsync();
            var subId = await h.SeedActiveSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(2));
            h.SetProviderSubscriber(SubscriberId, status: "Active", expire: FormatExpire(h.NowUtc.AddDays(40)));

            // No hay que esperar al pase diario para dejar de considerar la suscripción por vencer.
            var report = await h.Reconciliation.RunOldSubscriberCancellationRetryAsync();

            Assert.Equal(1, report.ProviderExpiriesReconciled);
        }

        // ── 6. Health: mismatch ahead vs earlier ──

        [Fact]
        public async Task Health_ProviderAhead_CountsAsAheadNotEarlier()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedActiveSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(2));
            h.SetProviderSubscriber(SubscriberId, status: "Active", expire: FormatExpire(h.NowUtc.AddDays(40)));

            await h.Sync.SyncActiveSubscriptionsAsync(h.Report);
            var health = await h.Health.BuildAsync();

            // Tras extender, local == provider, así que ya no hay mismatch: es lo correcto.
            Assert.Equal(0, health.ProviderExpiryMismatchCount);
            Assert.Equal(1, health.ProviderExpiryReconciledLast7d);
        }

        [Fact]
        public async Task Health_ProviderEarlier_CountsAsEarlierMismatch()
        {
            using var h = await Harness.CreateAsync();
            await h.SeedActiveSubscriptionAsync(localEndUtc: h.NowUtc.AddDays(40));
            h.SetProviderSubscriber(SubscriberId, status: "Active", expire: FormatExpire(h.NowUtc.AddDays(2)));

            await h.Sync.SyncActiveSubscriptionsAsync(h.Report);
            var health = await h.Health.BuildAsync();

            Assert.Equal(1, health.ProviderExpiryMismatchCount);
            Assert.Equal(1, health.ActiveSubscriptionsProviderExpiryEarlierCount);
            Assert.Equal(0, health.ActiveSubscriptionsProviderExpiryAheadCount);
            var item = Assert.Single(health.ProviderExpiryMismatches);
            Assert.False(item.ProviderIsAhead);
        }

        private static string FormatExpire(DateTime utc) => utc.ToString("yyyy-MM-dd");

        // ── Infraestructura ──

        private sealed class FakeAdmin : ITilopayRepeatAdminService
        {
            public bool IsEnabled { get; set; } = true;
            public Dictionary<int, List<TilopaySubscriber>> SubscribersByPlan { get; } = new();

            public Task<IReadOnlyList<TilopaySubscriber>> GetSuscriptorRepeatAsync(int tilopayPlanId, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<TilopaySubscriber>>(
                    SubscribersByPlan.TryGetValue(tilopayPlanId, out var list) ? list.ToList() : new List<TilopaySubscriber>());

            public Task<TargetSubscriberAssessment> AssessTargetSubscribersAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                Task.FromResult(TargetSubscriberAssessment.FromMatches(
                    SubscribersByPlan.TryGetValue(tilopayPlanId, out var list) ? list : new List<TilopaySubscriber>(), tilopayPlanId));

            public Task<SubscriberResolutionResult> ResolveSubscriberAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                Task.FromResult(SubscriberResolutionResult.NotFound());

            public Task<TilopayAdminOperationResult> GetRecurrentUrlAsync(int tilopayPlanId, string? email, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("ok", "https://tp.cr/l/x"));

            public Task<TilopayAdminOperationResult> PauseSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("paused"));

            public Task<TilopayAdminOperationResult> ReactivateSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("reactivated"));

            public Task<TilopayAdminOperationResult> DeleteSubscriberAsync(string subscriberId, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("ok"));

            public Task<TilopayAdminOperationResult> EditSubscriberStatusAsync(string subscriberId, TilopaySubscriberStatus status, CancellationToken cancellationToken = default) =>
                Task.FromResult(TilopayAdminOperationResult.Ok("edited"));
        }

        private sealed class Harness : IDisposable
        {
            private readonly IDisposable _connection;
            private int _seedCount;

            public ApplicationDbContext Db { get; private init; } = null!;
            public FakeAdmin Admin { get; } = new();
            public ProviderExpirySyncService Sync { get; private set; } = null!;
            public BillingReconciliationService Reconciliation { get; private set; } = null!;
            public BillingHealthService Health { get; private set; } = null!;
            public SuscripcionService SubscriptionService { get; private set; } = null!;
            public BillingReconciliationReport Report { get; } = new();
            public Guid TenantId { get; } = Guid.NewGuid();
            public Guid PlanId { get; private set; }

            // Reloj fijo de la suite: se ancla a "ahora" para que las ventanas de 24h/7d del health
            // (que usan DateTime.UtcNow) sean coherentes con las fechas sembradas.
            public DateTime NowUtc { get; } = DateTime.UtcNow;

            private Harness(IDisposable connection) => _connection = connection;

            public static async Task<Harness> CreateAsync()
            {
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
                var h = new Harness(connection) { Db = context };

                var repeatOptions = CalculatorCatalog.BuildRepeatOptions();
                var tenantAccessor = new TenantExecutionContextAccessor();
                var cache = new MemoryCache(new MemoryCacheOptions());
                var clock = new FixedBusinessDateTimeProvider(
                    DateTime.SpecifyKind(h.NowUtc, DateTimeKind.Unspecified));

                h.SubscriptionService = new SuscripcionService(
                    context, cache, new TenantCommercialAccessCache(cache), clock,
                    Options.Create(repeatOptions), NullLogger<SuscripcionService>.Instance);

                var reconOptions = Options.Create(new BillingReconciliationOptions());

                h.Sync = new ProviderExpirySyncService(
                    context, h.Admin, tenantAccessor, clock, reconOptions,
                    NullLogger<ProviderExpirySyncService>.Instance);

                h.Reconciliation = new BillingReconciliationService(
                    context,
                    h.SubscriptionService,
                    tenantAccessor,
                    clock,
                    Options.Create(repeatOptions),
                    reconOptions,
                    NullLogger<BillingReconciliationService>.Instance,
                    subscriberResolutionService: null,
                    adminOptions: Options.Create(new OpcionesTilopayRepeatAdmin { Enabled = true }),
                    providerSubscriptionManager: null,
                    planChangeLateApplicationService: null,
                    providerExpirySyncService: h.Sync);

                h.Health = new BillingHealthService(context, h.SubscriptionService);

                context.Tenants.Add(new Tenant { Id = h.TenantId, Nombre = "Tenant Gamma", Activo = true });
                var plan = new Plan
                {
                    Id = Guid.NewGuid(),
                    Codigo = "LC_M_02",
                    Nombre = "LC_M_02",
                    PrecioMensual = 15000m,
                    MonthlyEquivalentAmount = 15000m,
                    BillingCycle = BillingCycle.Monthly,
                    Moneda = "CRC",
                    MaxFuncionarios = 2,
                    Activo = true
                };
                context.Planes.Add(plan);
                h.PlanId = plan.Id;
                await context.SaveChangesAsync();
                context.ChangeTracker.Clear();

                return h;
            }

            public async Task<Guid> SeedActiveSubscriptionAsync(
                DateTime localEndUtc,
                Guid? tenantId = null,
                string subscriberId = SubscriberId)
            {
                var owner = tenantId ?? TenantId;
                if (owner != TenantId && !await Db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == owner))
                {
                    Db.Tenants.Add(new Tenant { Id = owner, Nombre = $"Tenant {++_seedCount}", Activo = true });
                }

                var id = Guid.NewGuid();
                Db.Suscripciones.Add(new Suscripcion
                {
                    Id = id,
                    TenantId = owner,
                    PlanId = PlanId,
                    CodigoPlan = "LC_M_02",
                    Estado = EstadoSuscripcion.Activa,
                    Proveedor = PaymentProviderType.Tilopay,
                    TilopayRecurringPlanId = RecurringPlanId,
                    ProviderSubscriptionId = subscriberId,
                    ProviderTransactionId = "5397431",
                    FechaInicio = localEndUtc.AddMonths(-1),
                    FechaFin = localEndUtc,
                    FechaProximoCobroUtc = localEndUtc
                });

                await Db.SaveChangesAsync();
                Db.ChangeTracker.Clear();
                return id;
            }

            public void SetProviderSubscriber(string subscriberId, string status, string? expire)
            {
                if (!Admin.SubscribersByPlan.TryGetValue(RecurringPlanId, out var list))
                {
                    list = new List<TilopaySubscriber>();
                    Admin.SubscribersByPlan[RecurringPlanId] = list;
                }

                list.Add(new TilopaySubscriber
                {
                    SubscriberId = subscriberId,
                    Status = status,
                    ExpiresAtUtc = ProviderExpiryDate.ParseCostaRicaEndOfDayUtc(expire),
                    ExpiresRaw = expire
                });
            }

            public async Task<Suscripcion> GetSubscriptionAsync(Guid id)
            {
                Db.ChangeTracker.Clear();
                return await Db.Suscripciones.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == id);
            }

            public Task<int> CountAuditAsync(string action) =>
                Db.PlatformAuditLogs.CountAsync(l => l.Action == action);

            public void Dispose()
            {
                Db.Dispose();
                _connection.Dispose();
            }
        }
    }
}
