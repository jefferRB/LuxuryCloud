using LuxuryApp.Models.Finanzas;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Tests.Support;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class EgresoQueryServiceTests
    {
        [Fact]
        public async Task BuildIndexViewModelAsync_ShouldApplyDayFilter()
        {
            var businessToday = ControllerTestSupport.BusinessDateTimeProvider.Today();
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoria = await SeedCategoriaAsync(context, "Caja", "Operativo");
            await SeedEgresoAsync(context, categoria, businessToday.AddHours(9), 100m, "EFECTIVO", "Hoy");
            await SeedEgresoAsync(context, categoria, businessToday.AddDays(-1).AddHours(9), 200m, "TARJETA", "Ayer");

            var queryService = ControllerTestSupport.CreateEgresoQueryService(context);
            var result = await queryService.BuildIndexViewModelAsync(new EgresoFiltroViewModel { VistaTiempo = "dia" }, includeFilterOptions: false);

            var row = Assert.Single(result.Egresos);
            Assert.Equal("Hoy", row.Detalle);
            Assert.Equal(100m, result.TotalEgresos);
            Assert.Equal(1, result.CantidadRegistros);
        }

        [Fact]
        public async Task BuildIndexViewModelAsync_ShouldApplyWeekFilter_FromMondayToSunday()
        {
            var businessToday = ControllerTestSupport.BusinessDateTimeProvider.Today();
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoria = await SeedCategoriaAsync(context, "Semana", "Operativo");
            var monday = StartOfWeek(businessToday);
            var sunday = monday.AddDays(6);

            await SeedEgresoAsync(context, categoria, monday.AddHours(8), 75m, "EFECTIVO", "Lunes");
            await SeedEgresoAsync(context, categoria, sunday.AddHours(19), 80m, "SINPE", "Domingo");
            await SeedEgresoAsync(context, categoria, monday.AddDays(7).AddHours(9), 90m, "TARJETA", "Siguiente Semana");

            var queryService = ControllerTestSupport.CreateEgresoQueryService(context);
            var result = await queryService.BuildIndexViewModelAsync(new EgresoFiltroViewModel { VistaTiempo = "semana" }, includeFilterOptions: false);

            Assert.Equal(2, result.Egresos.Count);
            Assert.Contains(result.Egresos, egreso => egreso.Detalle == "Lunes");
            Assert.Contains(result.Egresos, egreso => egreso.Detalle == "Domingo");
            Assert.DoesNotContain(result.Egresos, egreso => egreso.Detalle == "Siguiente Semana");
        }

        [Fact]
        public async Task BuildIndexViewModelAsync_ShouldApplyMonthAndYearFilters()
        {
            var businessToday = ControllerTestSupport.BusinessDateTimeProvider.Today();
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoria = await SeedCategoriaAsync(context, "Tiempo", "Operativo");
            var currentMonthRow = new DateTime(businessToday.Year, businessToday.Month, 5, 9, 0, 0);
            var currentYearOtherMonth = new DateTime(businessToday.Year, Math.Min(businessToday.Month == 1 ? 2 : 1, 12), 10, 10, 0, 0);
            var previousYearRow = new DateTime(businessToday.Year - 1, 12, 15, 11, 0, 0);

            await SeedEgresoAsync(context, categoria, currentMonthRow, 100m, "EFECTIVO", "Mes Actual");
            await SeedEgresoAsync(context, categoria, currentYearOtherMonth, 110m, "TARJETA", "Mismo Anio");
            await SeedEgresoAsync(context, categoria, previousYearRow, 120m, "SINPE", "Anio Anterior");

            var queryService = ControllerTestSupport.CreateEgresoQueryService(context);

            var monthResult = await queryService.BuildIndexViewModelAsync(new EgresoFiltroViewModel { VistaTiempo = "mes" }, includeFilterOptions: false);
            var yearResult = await queryService.BuildIndexViewModelAsync(new EgresoFiltroViewModel { VistaTiempo = "anio" }, includeFilterOptions: false);

            Assert.Contains(monthResult.Egresos, egreso => egreso.Detalle == "Mes Actual");
            Assert.DoesNotContain(monthResult.Egresos, egreso => egreso.Detalle == "Anio Anterior");
            Assert.Equal(2, yearResult.Egresos.Count);
            Assert.DoesNotContain(yearResult.Egresos, egreso => egreso.Detalle == "Anio Anterior");
        }

        [Fact]
        public async Task BuildIndexViewModelAsync_ShouldApplyRangeCategoriaMetodoAndKpis()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoriaA = await SeedCategoriaAsync(context, "Operativo", "A");
            var categoriaB = await SeedCategoriaAsync(context, "Planilla", "B");

            await SeedEgresoAsync(context, categoriaA, new DateTime(2026, 4, 12, 8, 0, 0), 100m, "EFECTIVO", "Dentro Rango Efectivo");
            await SeedEgresoAsync(context, categoriaA, new DateTime(2026, 4, 15, 9, 0, 0), 200m, "TARJETA", "Dentro Rango Tarjeta");
            await SeedEgresoAsync(context, categoriaA, new DateTime(2026, 4, 22, 10, 0, 0), 300m, "TARJETA", "Fuera Rango");
            await SeedEgresoAsync(context, categoriaB, new DateTime(2026, 4, 15, 11, 0, 0), 400m, "TARJETA", "Otra Categoria");

            var queryService = ControllerTestSupport.CreateEgresoQueryService(context);
            var result = await queryService.BuildIndexViewModelAsync(new EgresoFiltroViewModel
            {
                VistaTiempo = "fechas",
                FechaInicio = new DateTime(2026, 4, 10),
                FechaFin = new DateTime(2026, 4, 20),
                CategoriaId = categoriaA.Id,
                MetodoPago = "tarjeta"
            }, includeFilterOptions: false);

            var row = Assert.Single(result.Egresos);
            Assert.Equal("Dentro Rango Tarjeta", row.Detalle);
            Assert.Equal(200m, result.TotalEgresos);
            Assert.Equal(1, result.CantidadRegistros);
        }

        [Fact]
        public async Task BuildIndexViewModelAsync_ShouldRespectTenantIsolation_AndIncludeExistingGeneratedExpenses()
        {
            var currentTenantId = Guid.NewGuid();
            var foreignTenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = foreignTenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var foreignCategoria = await SeedCategoriaAsync(context, "Privada", "Tenant B");
            await SeedEgresoAsync(context, foreignCategoria, new DateTime(2026, 4, 23, 8, 0, 0), 500m, "EFECTIVO", "Egreso Privado");

            tenantProvider.TenantId = currentTenantId;
            context.ChangeTracker.Clear();

            var currentCategoria = await SeedCategoriaAsync(context, "Pago Funcionarios", "Generado por liquidacion");
            context.Egresos.Add(new Egreso
            {
                FechaEgreso = new DateTime(2026, 4, 23, 9, 0, 0),
                Detalle = "Egreso Automatico",
                Monto = 750m,
                MetodoPago = "SINPE",
                CategoriaId = currentCategoria.Id
            });
            await context.SaveChangesAsync();

            var queryService = ControllerTestSupport.CreateEgresoQueryService(context);
            var result = await queryService.BuildIndexViewModelAsync(new EgresoFiltroViewModel { VistaTiempo = "todo" }, includeFilterOptions: false);

            var row = Assert.Single(result.Egresos);
            Assert.Equal("Egreso Automatico", row.Detalle);
            Assert.Equal(750m, result.TotalEgresos);
        }

        private static DateTime StartOfWeek(DateTime today)
        {
            var diff = (7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7;
            return today.AddDays(-diff).Date;
        }

        private static async Task<Categoria> SeedCategoriaAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            string detalle,
            bool activo = true)
        {
            var categoria = new Categoria
            {
                Nombre = nombre,
                Detalle = detalle,
                Activo = activo
            };

            context.Categorias.Add(categoria);
            await context.SaveChangesAsync();
            return categoria;
        }

        private static async Task SeedEgresoAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Categoria categoria,
            DateTime fecha,
            decimal monto,
            string metodoPago,
            string detalle)
        {
            context.Egresos.Add(new Egreso
            {
                FechaEgreso = fecha,
                CategoriaId = categoria.Id,
                Detalle = detalle,
                Monto = monto,
                MetodoPago = metodoPago
            });

            await context.SaveChangesAsync();
        }
    }
}
