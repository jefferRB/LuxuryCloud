using LuxuryApp.Services.Finanzas;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// Identidad ESTRUCTURAL de las categorías financieras.
    ///
    /// <para>
    /// La propiedad que se defiende acá es simple: <b>renombrar una etiqueta visible nunca puede
    /// cambiar una cifra financiera</b>. Antes la exclusión de "Pago Funcionarios" se decidía
    /// comparando texto, así que renombrarla habría restado la planilla dos veces.
    /// </para>
    /// </summary>
    public class CategoriasDelSistemaTests
    {
        private const int Anio = 2026;
        private const int Mes = 8;
        private const decimal MontoCobro = 100_000m;
        private const decimal ComisionDevengada = 44_247.79m;

        [Fact]
        public async Task RenombrarCategoriaEmployeeSettlement_NoCambiaGanancia()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Jordan");
            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", MontoCobro);
            await FinanzasTestSupport.SeedCobroServicioAsync(
                context, funcionario, servicio, new DateTime(Anio, Mes, 5, 9, 0, 0), MontoCobro);

            await FinanzasTestSupport.PagarAsync(
                context, tenantProvider, funcionario,
                new DateTime(Anio, Mes, 3), new DateTime(Anio, Mes, 9),
                new DateTime(Anio, Mes, 10, 10, 0, 0), ComisionDevengada);

            var dashboard = ControllerTestSupport.CreateDashboardFinancieroQueryService(context, tenantProvider);
            var antes = await dashboard.BuildViewModelAsync(Mes, Anio);

            // El negocio renombra la etiqueta a su gusto.
            var categoria = await context.Categorias
                .SingleAsync(c => c.SystemCode == SystemCategoryCodes.EmployeeSettlement);
            categoria.Nombre = "Pagos a mi equipo (2026)";
            await context.SaveChangesAsync();

            var despues = await dashboard.BuildViewModelAsync(Mes, Anio);

            Assert.Equal(antes.Desglose!.GananciaDistribuible, despues.Desglose!.GananciaDistribuible);
            Assert.Equal(antes.Desglose.GastosOperativos, despues.Desglose.GastosOperativos);
            Assert.Equal(0m, despues.Desglose.GastosOperativos);
            Assert.Equal(antes.ResultadoAnalitico, despues.ResultadoAnalitico);
            Assert.Equal(antes.TotalPagadoFuncionarios, despues.TotalPagadoFuncionarios);

            // Y la categoría sigue excluida, ahora identificada por código y no por texto.
            var linea = Assert.Single(
                despues.Desglose.GastosPorCategoria, g => g.CategoriaId == categoria.Id);
            Assert.False(linea.Incluido);
            Assert.Equal("Pagos a mi equipo (2026)", linea.CategoriaNombre);
        }

        [Fact]
        public async Task EmployeeSettlement_NoSePuedeSeleccionarManualEnNuevoEgreso()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var categoriaSistema = await ControllerTestSupport
                .CreateSystemCategoryService(context)
                .EnsureAsync(SystemCategoryCodes.EmployeeSettlement);
            await FinanzasTestSupport.SeedCategoriaAsync(context, "Alquiler");

            // 1) No aparece en el selector de "Nuevo egreso".
            var queryService = ControllerTestSupport.CreateEgresoQueryService(context);
            var form = await queryService.BuildCreateViewModelAsync();

            Assert.DoesNotContain(form.Categorias, c => c.Value == categoriaSistema.Id.ToString());
            Assert.Contains(form.Categorias, c => c.Text == "Alquiler");

            // 2) Y el SERVICIO la rechaza aunque alguien mande el id a mano (la UI no es seguridad).
            var egresoService = ControllerTestSupport.CreateEgresoService(context);
            var ex = await Assert.ThrowsAsync<EgresoValidationException>(() =>
                egresoService.RegistrarAsync(new EgresoCreateRequest
                {
                    FechaEgreso = new DateTime(Anio, Mes, 20, 9, 0, 0),
                    Detalle = "Vacaciones Jordan",
                    Monto = 20_000m,
                    MetodoPago = "EFECTIVO",
                    CategoriaId = categoriaSistema.Id
                }));

            Assert.Contains(SystemCategoryCodes.NombreSugeridoExtraordinaryLaborCost, ex.Message, StringComparison.Ordinal);
            Assert.Empty(await context.Egresos.ToListAsync());
        }

        [Fact]
        public async Task ExtraordinaryLaborCost_SiReduceGanancia()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Jordan");
            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", MontoCobro);
            await FinanzasTestSupport.SeedCobroServicioAsync(
                context, funcionario, servicio, new DateTime(Anio, Mes, 5, 9, 0, 0), MontoCobro);

            var dashboard = ControllerTestSupport.CreateDashboardFinancieroQueryService(context, tenantProvider);
            var antes = await dashboard.BuildViewModelAsync(Mes, Anio);

            // Categoría del sistema pero NO excluida: es un costo real adicional.
            var categoria = await ControllerTestSupport
                .CreateSystemCategoryService(context)
                .EnsureAsync(SystemCategoryCodes.ExtraordinaryLaborCost);

            var egresoService = ControllerTestSupport.CreateEgresoService(context);
            await egresoService.RegistrarAsync(new EgresoCreateRequest
            {
                FechaEgreso = new DateTime(Anio, Mes, 20, 9, 0, 0),
                Detalle = "Vacaciones Jordan",
                Monto = 20_000m,
                MetodoPago = "EFECTIVO",
                CategoriaId = categoria.Id
            });

            var despues = await dashboard.BuildViewModelAsync(Mes, Anio);

            Assert.Equal(20_000m, despues.Desglose!.GastosOperativos);
            Assert.Equal(antes.Desglose!.GananciaDistribuible - 20_000m, despues.Desglose.GananciaDistribuible);
            Assert.Equal(20_000m, despues.SalidasCajaMes);

            var linea = Assert.Single(
                despues.Desglose.GastosPorCategoria, g => g.CategoriaId == categoria.Id);
            Assert.True(linea.Incluido);
            Assert.Null(linea.MotivoExclusion);
        }

        [Fact]
        public async Task EnsureSystemCategory_Concurrente_CreaUnaSola()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            // Otra petición ganó la carrera y ya creó la categoría…
            var ganadora = await ControllerTestSupport
                .CreateSystemCategoryService(context)
                .EnsureAsync(SystemCategoryCodes.EmployeeSettlement);

            // …esta la pide después, con un contexto distinto: debe REUSARLA, no duplicarla.
            using var otroContexto = TestDbContextFactory.CreateSqliteContext(tenantProvider, connection);
            var reusada = await ControllerTestSupport
                .CreateSystemCategoryService(otroContexto)
                .EnsureAsync(SystemCategoryCodes.EmployeeSettlement);

            Assert.Equal(ganadora.Id, reusada.Id);
            Assert.Single(await context.Categorias
                .Where(c => c.SystemCode == SystemCategoryCodes.EmployeeSettlement)
                .ToListAsync());

            // Y una tercera llamada sigue siendo idempotente.
            var tercera = await ControllerTestSupport
                .CreateSystemCategoryService(context)
                .EnsureAsync(SystemCategoryCodes.EmployeeSettlement);
            Assert.Equal(ganadora.Id, tercera.Id);
        }

        [Fact]
        public async Task EnsureSystemCategory_AdoptaLaCategoriaHistoricaSinDuplicar()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            // Tenant anterior al SystemCode: la categoría existe solo por nombre.
            var legacy = await FinanzasTestSupport.SeedCategoriaAsync(
                context, SystemCategoryCodes.NombreLegacyEmployeeSettlement);
            Assert.Null(legacy.SystemCode);

            var resuelta = await ControllerTestSupport
                .CreateSystemCategoryService(context)
                .EnsureAsync(SystemCategoryCodes.EmployeeSettlement);

            // La adopta: mismo id, historial intacto, sin categoría duplicada.
            Assert.Equal(legacy.Id, resuelta.Id);
            Assert.Equal(SystemCategoryCodes.EmployeeSettlement, resuelta.SystemCode);
            Assert.Single(await context.Categorias.ToListAsync());
        }

        [Fact]
        public async Task DosCategoriasMismoSystemCode_MismoTenant_RechazadasPorDB()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            await ControllerTestSupport
                .CreateSystemCategoryService(context)
                .EnsureAsync(SystemCategoryCodes.EmployeeSettlement);

            // Nombre distinto (para no chocar con IX_Categorias_TenantId_Nombre) pero mismo código:
            // lo que debe frenarlo es el índice de identidad estructural, no el del nombre.
            context.Categorias.Add(new Categoria
            {
                Nombre = "Otra etiqueta cualquiera",
                Detalle = "Duplicado estructural",
                Activo = true,
                SystemCode = SystemCategoryCodes.EmployeeSettlement
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        [Fact]
        public async Task MismoSystemCode_DistintoTenant_Permitido()
        {
            var tenantA = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantA);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var deA = await ControllerTestSupport
                .CreateSystemCategoryService(context)
                .EnsureAsync(SystemCategoryCodes.EmployeeSettlement);

            // Mismo código, otro negocio: perfectamente válido.
            var tenantB = new TestTenantProvider { TenantId = Guid.NewGuid() };
            using var contextB = TestDbContextFactory.CreateSqliteContext(tenantB, connection);
            var deB = await ControllerTestSupport
                .CreateSystemCategoryService(contextB)
                .EnsureAsync(SystemCategoryCodes.EmployeeSettlement);

            Assert.NotEqual(deA.Id, deB.Id);

            var todas = await contextB.Categorias.IgnoreQueryFilters().ToListAsync();
            Assert.Equal(2, todas.Count(c => c.SystemCode == SystemCategoryCodes.EmployeeSettlement));
        }
    }
}
