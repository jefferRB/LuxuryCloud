using LuxuryApp.Controllers.Platform;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.Platform.MissionControl;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.Reports;
using LuxuryApp.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class PlatformMissionControlServiceTests
    {
        [Fact]
        public async Task Snapshot_ShouldFlagStoppedWorkers_ByHeartbeatStaleness()
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            using var disposableContext = context;
            using var disposableConnection = connection;

            var nowUtc = DateTime.UtcNow;
            var heartbeats = new FakeWorkerHeartbeatService();
            heartbeats.Seed(PlatformWorkerNames.Reminder, nowUtc.AddMinutes(-20));
            heartbeats.Seed(PlatformWorkerNames.Visitas, nowUtc.AddMinutes(-1));
            // BillingReconciliation: sin latido pero deshabilitado por options.
            // MonthlyReportScheduler: habilitado y sin latido.

            var service = CreateService(
                context,
                heartbeats,
                reconciliationEnabled: false,
                monthlyReportsEnabled: true);

            var snapshot = await service.GetSnapshotAsync();
            var byKey = snapshot.Signals.ToDictionary(signal => signal.Key);

            Assert.Equal(SignalState.Critical, byKey[$"worker:{PlatformWorkerNames.Reminder}"].State);
            Assert.Equal(SignalState.Ok, byKey[$"worker:{PlatformWorkerNames.Visitas}"].State);
            Assert.Equal(SignalState.Disabled, byKey[$"worker:{PlatformWorkerNames.BillingReconciliation}"].State);
            Assert.Equal(SignalState.Warning, byKey[$"worker:{PlatformWorkerNames.MonthlyReportScheduler}"].State);

            // Un worker crítico eleva el semáforo global.
            Assert.Equal(SignalState.Critical, snapshot.OverallState);
        }

        [Fact]
        public async Task Snapshot_ShouldCountQueues_FromSeededBillingAndWhatsAppData()
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            using var disposableContext = context;
            using var disposableConnection = connection;

            var nowUtc = DateTime.UtcNow;
            var tenantMorosoId = Guid.NewGuid();
            var tenantTrialId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Basico",
                PrecioMensual = 8000,
                Moneda = "CRC",
                Activo = true
            });
            context.Tenants.Add(new Tenant { Id = tenantMorosoId, Nombre = "Salon Moroso", Activo = true });
            context.Tenants.Add(new Tenant { Id = tenantTrialId, Nombre = "Salon Trial", Activo = true });
            await context.SaveChangesAsync();

            // Hay índice único de una suscripción por tenant, y el guard de sistema exige
            // un solo TenantId por SaveChanges: cada tenant se siembra por separado.
            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantMorosoId,
                PlanId = planId,
                Estado = EstadoSuscripcion.Morosa
            });
            await context.SaveChangesAsync();

            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantTrialId,
                PlanId = planId,
                Estado = EstadoSuscripcion.Trial,
                FechaTrialFin = nowUtc.AddDays(3)
            });
            context.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantTrialId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                Estado = EstadoPagoProveedor.ManualReview,
                Monto = 8000,
                ReferenciaInterna = "mc-test-manual-review",
                FechaCreacionUtc = nowUtc.AddHours(-30)
            });
            context.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantTrialId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                Estado = EstadoPagoProveedor.Pendiente,
                Monto = 8000,
                ReferenciaInterna = "mc-test-pendiente",
                FechaCreacionUtc = nowUtc.AddHours(-2)
            });
            context.EventosPago.Add(new EventoPago
            {
                Id = Guid.NewGuid(),
                Proveedor = PaymentProviderType.Tilopay,
                ProveedorEventId = Guid.NewGuid().ToString("N"),
                Tipo = "payment",
                Procesado = false,
                EstadoProcesamiento = "Error",
                FechaRecepcionUtc = nowUtc.AddHours(-2)
            });
            context.WhatsAppMessageLogs.Add(new WhatsAppMessageLog
            {
                TenantId = tenantTrialId,
                Direction = WhatsAppMessageDirections.Outbound,
                ErrorCode = "131047",
                CreatedAtUtc = nowUtc.AddHours(-1)
            });
            await context.SaveChangesAsync();

            var service = CreateService(context, new FakeWorkerHeartbeatService());
            var snapshot = await service.GetSnapshotAsync();

            var queues = snapshot.Queues.ToDictionary(queue => queue.Key);
            Assert.Equal(1, queues["manual-review"].Count);
            Assert.True(queues["manual-review"].IsMoneyRelated);
            Assert.Equal(1, queues["pending-checkouts"].Count);
            Assert.Equal(1, queues["webhooks-unprocessed"].Count);
            Assert.Equal(1, queues["morosas"].Count);
            Assert.Equal(1, queues["expiring-trials"].Count);
            Assert.Equal(1, queues["whatsapp-errors"].Count);

            // La edad del ítem más antiguo acompaña a la cola de revisión manual.
            Assert.NotNull(queues["manual-review"].OldestItemUtc);

            var signals = snapshot.Signals.ToDictionary(signal => signal.Key);
            Assert.Equal(SignalState.Warning, signals["webhooks"].State);
            Assert.Equal(SignalState.Warning, signals["whatsapp"].State);
            Assert.Contains("1 tenants afectados", signals["whatsapp"].Evidence);

            Assert.False(snapshot.IsAllClear);
        }

        [Fact]
        public async Task Snapshot_ShouldBeCached_UntilForceRefresh()
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            using var disposableContext = context;
            using var disposableConnection = connection;

            var service = CreateService(context, new FakeWorkerHeartbeatService());

            var first = await service.GetSnapshotAsync();
            var second = await service.GetSnapshotAsync();
            var refreshed = await service.GetSnapshotAsync(forceRefresh: true);

            Assert.Same(first, second);
            Assert.NotSame(first, refreshed);
        }

        [Fact]
        public async Task WorkerHeartbeat_TryBeat_ShouldUpsertSingleRow()
        {
            var tenantProvider = new TestTenantProvider();
            using var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;

            using (var seedContext = new ApplicationDbContext(options, tenantProvider, NullLogger<ApplicationDbContext>.Instance))
            {
                seedContext.Database.EnsureCreated();
            }

            var services = new ServiceCollection();
            services.AddScoped(_ => new ApplicationDbContext(options, tenantProvider, NullLogger<ApplicationDbContext>.Instance));
            using var provider = services.BuildServiceProvider();

            var heartbeatService = new WorkerHeartbeatService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WorkerHeartbeatService>.Instance);

            await heartbeatService.TryBeatAsync(PlatformWorkerNames.Reminder, "primero");
            await heartbeatService.TryBeatAsync(PlatformWorkerNames.Reminder, "segundo");

            var all = await heartbeatService.GetAllAsync();
            var row = Assert.Single(all);
            Assert.Equal(PlatformWorkerNames.Reminder, row.WorkerName);
            Assert.Equal("segundo", row.LastCycleSummary);
        }

        [Fact]
        public async Task WorkerHeartbeat_TryBeat_ShouldSwallowDatabaseFailures()
        {
            var tenantProvider = new TestTenantProvider();
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(connection)
                .Options;

            var services = new ServiceCollection();
            services.AddScoped(_ => new ApplicationDbContext(options, tenantProvider, NullLogger<ApplicationDbContext>.Instance));
            using var provider = services.BuildServiceProvider();

            var heartbeatService = new WorkerHeartbeatService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WorkerHeartbeatService>.Instance);

            // Conexión cerrada y sin schema: la escritura falla, pero el latido nunca lanza.
            connection.Dispose();

            var exception = await Record.ExceptionAsync(() =>
                heartbeatService.TryBeatAsync(PlatformWorkerNames.Reminder, "no debe lanzar"));

            Assert.Null(exception);
        }

        [Fact]
        public void PlatformController_ShouldExposeMissionControlAndTenantsActions()
        {
            // Protege el rename Index→Tenants: las tres acciones deben existir y quedar
            // cubiertas por la policy PlatformSuperAdmin declarada a nivel de clase
            // (verificada en ControllerAuthorizationTests).
            Assert.NotNull(typeof(PlatformController).GetMethod(nameof(PlatformController.Index)));
            Assert.NotNull(typeof(PlatformController).GetMethod(nameof(PlatformController.Tenants)));
            Assert.NotNull(typeof(PlatformController).GetMethod(nameof(PlatformController.MissionControlJson)));
        }

        private static PlatformMissionControlService CreateService(
            ApplicationDbContext context,
            IWorkerHeartbeatService heartbeatService,
            bool reconciliationEnabled = true,
            bool monthlyReportsEnabled = false,
            bool investorStatementsEnabled = false)
        {
            return new PlatformMissionControlService(
                context,
                heartbeatService,
                new MemoryCache(new MemoryCacheOptions()),
                new TestOptionsMonitor<BillingReconciliationOptions>(new BillingReconciliationOptions
                {
                    Enabled = reconciliationEnabled,
                    IntervalHours = 24
                }),
                new TestOptionsMonitor<MonthlyReportSchedulerOptions>(new MonthlyReportSchedulerOptions
                {
                    SchedulerEnabled = monthlyReportsEnabled,
                    PollingIntervalMinutes = 15
                }),
                new TestOptionsMonitor<LuxuryApp.Services.Inversionistas.InvestorStatementSchedulerOptions>(
                    new LuxuryApp.Services.Inversionistas.InvestorStatementSchedulerOptions
                    {
                        SchedulerEnabled = investorStatementsEnabled,
                        PollingIntervalMinutes = 30
                    }),
                new FixedBusinessDateTimeProvider(),
                NullLogger<PlatformMissionControlService>.Instance);
        }

        private sealed class FakeWorkerHeartbeatService : IWorkerHeartbeatService
        {
            private readonly Dictionary<string, PlatformWorkerHeartbeat> _beats = new(StringComparer.Ordinal);

            public void Seed(string workerName, DateTime lastBeatUtc, string? summary = null) =>
                _beats[workerName] = new PlatformWorkerHeartbeat
                {
                    WorkerName = workerName,
                    LastBeatUtc = lastBeatUtc,
                    LastCycleSummary = summary
                };

            public Task TryBeatAsync(string workerName, string? cycleSummary = null, CancellationToken cancellationToken = default)
            {
                Seed(workerName, DateTime.UtcNow, cycleSummary);
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<PlatformWorkerHeartbeat>> GetAllAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PlatformWorkerHeartbeat>>(_beats.Values.ToList());
        }

        private sealed class TestOptionsMonitor<TOptions> : Microsoft.Extensions.Options.IOptionsMonitor<TOptions>
        {
            public TestOptionsMonitor(TOptions value)
            {
                CurrentValue = value;
            }

            public TOptions CurrentValue { get; }

            public TOptions Get(string? name) => CurrentValue;

            public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
        }
    }
}
