using System.Text.Json;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Platform;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class PlatformCommercialSnapshotServiceTests
    {
        [Fact]
        public async Task Capture_ShouldNormalizeAnnualMrrAndExcludeValidationAndMorosa()
        {
            var (context, connection, tenantId) = await SeedBaseAsync();
            using var disposableContext = context;
            using var disposableConnection = connection;

            // Suscripciones.TenantId es único (una suscripción por tenant): cada una en su tenant.
            await SeedSubscriptionAsync(context, tenantId, precioPorCiclo: 10000m, BillingCycle.Monthly, EstadoSuscripcion.Activa);
            await SeedSubscriptionAsync(context, await SeedTenantAsync(context, "Tenant anual"), precioPorCiclo: 108000m, BillingCycle.Annual, EstadoSuscripcion.Activa);
            await SeedSubscriptionAsync(context, await SeedTenantAsync(context, "Tenant moroso"), precioPorCiclo: 5000m, BillingCycle.Monthly, EstadoSuscripcion.Morosa);
            await SeedSubscriptionAsync(context, await SeedTenantAsync(context, "Tenant validacion"), precioPorCiclo: 7000m, BillingCycle.Monthly, EstadoSuscripcion.Activa, esValidacion: true);

            var service = CreateService(context);
            var snapshot = await service.CaptureAsync(2026, 7, PlatformCommercialSnapshotTriggers.Manual, "test@luxurycloud.local");

            // 10000 mensual + 108000/12 = 9000 anual; Morosa y validación fuera del MRR.
            Assert.Equal(19000m, snapshot.MrrTotal);
            Assert.Equal(228000m, snapshot.ArrTotal);
            Assert.Equal(2, snapshot.ActiveSubscriptions);
            Assert.Equal(1, snapshot.MonthlyCycleCount);
            Assert.Equal(1, snapshot.AnnualCycleCount);

            using var detail = JsonDocument.Parse(snapshot.DetailJson!);
            Assert.Equal(5000m, detail.RootElement.GetProperty("MorosaMrr").GetDecimal());
            Assert.Equal(1, detail.RootElement.GetProperty("SuscripcionesValidacion").GetInt32());
        }

        [Fact]
        public async Task Capture_ShouldCountChurnOnlyInsidePeriod()
        {
            var (context, connection, tenantId) = await SeedBaseAsync();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var otherTenantId = await SeedTenantAsync(context, "Tenant churn fuera");

            // Dentro del período (julio 2026).
            await SeedSubscriptionAsync(
                context, tenantId, 8000m, BillingCycle.Monthly, EstadoSuscripcion.Cancelada,
                fechaCancelacionUtc: new DateTime(2026, 7, 15));

            // Fuera del período (junio 2026): no cuenta.
            await SeedSubscriptionAsync(
                context, otherTenantId, 12000m, BillingCycle.Monthly, EstadoSuscripcion.Vencida,
                fechaFin: new DateTime(2026, 6, 20));

            var service = CreateService(context);
            var snapshot = await service.CaptureAsync(2026, 7, PlatformCommercialSnapshotTriggers.Manual, null);

            Assert.Equal(1, snapshot.ChurnedTenants);
            Assert.Equal(8000m, snapshot.ChurnedMrr);
        }

        [Fact]
        public async Task Capture_ShouldUpsertSamePeriodWithoutDuplicates()
        {
            var (context, connection, tenantId) = await SeedBaseAsync();
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedSubscriptionAsync(context, tenantId, 10000m, BillingCycle.Monthly, EstadoSuscripcion.Activa);

            var service = CreateService(context);
            var first = await service.CaptureAsync(2026, 7, PlatformCommercialSnapshotTriggers.Manual, "primera@test.local");
            var second = await service.CaptureAsync(2026, 7, PlatformCommercialSnapshotTriggers.Scheduled, null);

            var rows = await context.PlatformCommercialSnapshots.AsNoTracking().ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal(first.Id, second.Id);
            Assert.Equal(PlatformCommercialSnapshotTriggers.Scheduled, row.TriggerType);
        }

        [Fact]
        public async Task Capture_ShouldNotModifySourceData()
        {
            var (context, connection, tenantId) = await SeedBaseAsync();
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedSubscriptionAsync(context, tenantId, 10000m, BillingCycle.Monthly, EstadoSuscripcion.Activa);
            await SeedSubscriptionAsync(
                context, await SeedTenantAsync(context, "Tenant cancelado"), 8000m, BillingCycle.Monthly, EstadoSuscripcion.Cancelada,
                fechaCancelacionUtc: new DateTime(2026, 7, 10));
            await SeedGrantAsync(context, tenantId, DateTime.UtcNow.AddDays(30));

            var before = await ReadSourceFingerprintAsync(context);

            var service = CreateService(context);
            await service.CaptureAsync(2026, 7, PlatformCommercialSnapshotTriggers.Manual, null);

            var after = await ReadSourceFingerprintAsync(context);

            Assert.Equal(before, after);
        }

        [Fact]
        public async Task Capture_ShouldCountActiveGrantsAndExpiringSoon()
        {
            var (context, connection, tenantId) = await SeedBaseAsync();
            using var disposableContext = context;
            using var disposableConnection = connection;

            await SeedGrantAsync(context, tenantId, DateTime.UtcNow.AddDays(30));
            await SeedGrantAsync(context, tenantId, DateTime.UtcNow.AddDays(3));
            await SeedGrantAsync(context, tenantId, DateTime.UtcNow.AddDays(-1));

            var service = CreateService(context);
            var snapshot = await service.CaptureAsync(2026, 7, PlatformCommercialSnapshotTriggers.Manual, null);

            Assert.Equal(2, snapshot.TrialsActivos);
            Assert.Equal(1, snapshot.TrialsPorVencer7d);
        }

        private static PlatformCommercialSnapshotService CreateService(ApplicationDbContext context) =>
            new(
                context,
                new FakeCommercialAccessResolver(),
                new EmptyMetricsService(),
                new EmptyWhatsAppStatusService(),
                new PlatformHealthService(),
                NullLogger<PlatformCommercialSnapshotService>.Instance);

        private static async Task<(ApplicationDbContext Context, Microsoft.Data.Sqlite.SqliteConnection Connection, Guid TenantId)> SeedBaseAsync()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            var tenantId = await SeedTenantAsync(context, "Tenant snapshot");
            return (context, connection, tenantId);
        }

        private static async Task<Guid> SeedTenantAsync(ApplicationDbContext context, string nombre)
        {
            var tenantId = Guid.NewGuid();
            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = nombre,
                Activo = true
            });
            await context.SaveChangesAsync();
            return tenantId;
        }

        private static async Task SeedSubscriptionAsync(
            ApplicationDbContext context,
            Guid tenantId,
            decimal precioPorCiclo,
            BillingCycle cycle,
            EstadoSuscripcion estado,
            bool esValidacion = false,
            DateTime? fechaCancelacionUtc = null,
            DateTime? fechaFin = null)
        {
            var planId = Guid.NewGuid();
            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = $"Plan {planId:N}"[..20],
                PrecioMensual = precioPorCiclo,
                BillingCycle = cycle,
                Moneda = "CRC",
                Activo = true,
                EsPlanValidacion = esValidacion
            });

            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                Estado = estado,
                FechaInicio = new DateTime(2026, 1, 1),
                FechaCancelacionUtc = fechaCancelacionUtc,
                FechaFin = fechaFin
            });

            await context.SaveChangesAsync();
        }

        private static async Task SeedGrantAsync(ApplicationDbContext context, Guid tenantId, DateTime fechaFinUtc)
        {
            var planId = Guid.NewGuid();
            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = $"Plan grant {planId:N}"[..20],
                PrecioMensual = 8000m,
                Moneda = "CRC",
                Activo = true
            });

            context.TenantCommercialAccessGrants.Add(new TenantCommercialAccessGrant
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                Activo = true,
                FechaInicioUtc = DateTime.UtcNow.AddDays(-10),
                FechaFinUtc = fechaFinUtc
            });

            await context.SaveChangesAsync();
        }

        /// <summary>Huella completa de los datos fuente: si la captura los tocara, cambia.</summary>
        private static async Task<string> ReadSourceFingerprintAsync(ApplicationDbContext context)
        {
            var subscriptions = await context.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .OrderBy(subscription => subscription.Id)
                .Select(subscription => new
                {
                    subscription.Id,
                    subscription.Estado,
                    subscription.FechaCancelacionUtc,
                    subscription.FechaFin,
                    subscription.FechaUltimaActualizacionUtc,
                    subscription.PrecioMensual
                })
                .ToListAsync();

            var grants = await context.TenantCommercialAccessGrants
                .IgnoreQueryFilters()
                .AsNoTracking()
                .OrderBy(grant => grant.Id)
                .Select(grant => new { grant.Id, grant.Activo, grant.FechaFinUtc })
                .ToListAsync();

            var tenants = await context.Tenants
                .AsNoTracking()
                .OrderBy(tenant => tenant.Id)
                .Select(tenant => new { tenant.Id, tenant.Activo, tenant.Nombre })
                .ToListAsync();

            return JsonSerializer.Serialize(new { subscriptions, grants, tenants });
        }

        private sealed class EmptyMetricsService : IPlatformMetricsService
        {
            public Task<Dictionary<Guid, PlatformTenantUsageViewModel>> GetTenantUsageBatchAsync(
                IReadOnlyList<Guid> tenantIds,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new Dictionary<Guid, PlatformTenantUsageViewModel>());

            public Task<PlatformTenantUsageViewModel> GetTenantUsageAsync(
                Guid tenantId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new PlatformTenantUsageViewModel());
        }

        private sealed class EmptyWhatsAppStatusService : IPlatformWhatsAppStatusService
        {
            public Task<Dictionary<Guid, PlatformWhatsAppAddonState>> GetBatchStatusAsync(
                IReadOnlyList<Guid> tenantIds,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new Dictionary<Guid, PlatformWhatsAppAddonState>());

            public Task<PlatformWhatsAppAddonState> GetSingleStatusAsync(
                Guid tenantId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new PlatformWhatsAppAddonState());
        }
    }
}
