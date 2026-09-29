using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Billing
{
    /// <summary>
    /// El ciclo anual debe extender la vigencia 12 meses (no 1 mes) y guardar el
    /// equivalente mensual en Suscripcion.PrecioMensual, sin ensuciar reportes.
    /// </summary>
    public class AnnualBillingCycleTests
    {
        [Fact]
        public async Task ActivarSuscripcionRecurrente_AnnualPlan_ExtendsTwelveMonthsAndStoresMonthlyEquivalent()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var plan = new Plan
            {
                Id = Guid.NewGuid(),
                Codigo = "LC_A_09",
                Nombre = "LuxuryCloud 9 funcionarios anual",
                PrecioMensual = 390000m,
                MonthlyEquivalentAmount = 32500m,
                BillingCycle = BillingCycle.Annual,
                Moneda = "CRC",
                MaxFuncionarios = 9,
                Activo = true
            };
            context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Tenant Annual", Activo = true });
            context.Planes.Add(plan);
            await context.SaveChangesAsync();

            var service = CreateSubscriptionService(context);

            await service.ActivarSuscripcionRecurrenteAsync(
                tenantId,
                plan,
                tilopayRecurringPlanId: 7209,
                providerSubscriberId: "subscriber-annual-9",
                providerTransactionId: "TX-ANNUAL-9",
                providerReference: "ref-annual-9");

            var subscription = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();

            Assert.Equal(EstadoSuscripcion.Activa, subscription.Estado);
            Assert.NotNull(subscription.FechaFin);
            // 12 meses exactos desde el inicio del periodo.
            Assert.Equal(subscription.FechaInicio.AddYears(1), subscription.FechaFin);
            Assert.Equal(subscription.FechaFin, subscription.FechaProximoCobroUtc);
            // Reportes limpios: equivalente mensual, no el total anual.
            Assert.Equal(32500m, subscription.PrecioMensual);
            Assert.Equal(9, subscription.MaxFuncionarios);
        }

        [Fact]
        public async Task ActivarSuscripcionRecurrente_MonthlyPlan_ExtendsOneMonthAndKeepsMonthlyPrice()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var plan = new Plan
            {
                Id = Guid.NewGuid(),
                Codigo = "LC_M_09",
                Nombre = "LuxuryCloud 9 funcionarios mensual",
                PrecioMensual = 50000m,
                MonthlyEquivalentAmount = 50000m,
                BillingCycle = BillingCycle.Monthly,
                Moneda = "CRC",
                MaxFuncionarios = 9,
                Activo = true
            };
            context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Tenant Monthly", Activo = true });
            context.Planes.Add(plan);
            await context.SaveChangesAsync();

            var service = CreateSubscriptionService(context);

            await service.ActivarSuscripcionRecurrenteAsync(
                tenantId,
                plan,
                tilopayRecurringPlanId: 7109,
                providerSubscriberId: "subscriber-monthly-9",
                providerTransactionId: "TX-MONTHLY-9",
                providerReference: "ref-monthly-9");

            var subscription = await context.Suscripciones.IgnoreQueryFilters().SingleAsync();

            Assert.Equal(subscription.FechaInicio.AddMonths(1), subscription.FechaFin);
            Assert.Equal(50000m, subscription.PrecioMensual);
        }

        private static (ApplicationDbContext Context, IDisposable Connection) CreateSystemContext()
        {
            var tenantProvider = new TestTenantProvider();
            return TestDbContextFactory.CreateSqliteContext(tenantProvider);
        }

        private static SuscripcionService CreateSubscriptionService(ApplicationDbContext context)
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
