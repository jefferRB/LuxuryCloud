using LuxuryApp.Tests.Support;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// "Servicios generados" del Dashboard debe contar TODOS los servicios, incluidos los
    /// personalizados (cobros nacidos de una cita fuera de catálogo: sin ServicioId y sin
    /// ProductoId, solo con ServicioNombrePersonalizado).
    ///
    /// <para>
    /// El criterio canónico de "esto es un servicio" ya lo usan <c>CobroQueryService</c> y
    /// <c>LiquidacionSemanalService</c>; el Dashboard miraba solo <c>ServicioId</c> y por eso la
    /// tarjeta mostraba menos dinero del que el total sí incluía.
    /// </para>
    /// </summary>
    public class DashboardServiciosGeneradosTests
    {
        private const int Anio = 2026;
        private const int Mes = 9;

        [Fact]
        public async Task Dashboard_TotalServicios_IncluyeServiciosPersonalizados()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Jordan");
            var servicioCatalogo = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", 7_000m);
            var producto = await FinanzasTestSupport.SeedProductoAsync(context, "Cera", 9_500m);

            await FinanzasTestSupport.SeedCobroServicioAsync(
                context, funcionario, servicioCatalogo, new DateTime(Anio, Mes, 3, 9, 0, 0), 7_000m, "EFECTIVO");

            // Servicio fuera de catálogo (cita personalizada): ServicioId null.
            await FinanzasTestSupport.SeedCobroServicioPersonalizadoAsync(
                context, funcionario, "Afeitado", new DateTime(Anio, Mes, 4, 10, 0, 0), 4_000m, "SINPE");

            await FinanzasTestSupport.SeedCobroProductoAsync(
                context, funcionario, producto, new DateTime(Anio, Mes, 5, 11, 0, 0), 9_500m, "TARJETA");

            var dashboard = ControllerTestSupport.CreateDashboardFinancieroQueryService(context, tenantProvider);
            var model = await dashboard.BuildViewModelAsync(Mes, Anio);

            // 7.000 de catálogo + 4.000 personalizado. Antes del fix la tarjeta mostraba solo 7.000
            // mientras el total sí contaba los 11.000.
            Assert.Equal(11_000m, model.TotalServicios);
            Assert.Equal(9_500m, model.TotalProductos);
            Assert.Equal(20_500m, model.TotalGenerado);
        }

        [Fact]
        public async Task Dashboard_TotalServiciosMasProductos_IgualTotalGenerado()
        {
            var model = await BuildDashboardConMezclaAsync();

            Assert.Equal(model.TotalGenerado, model.TotalServicios + model.TotalProductos);
        }

        [Fact]
        public async Task Dashboard_MetodosPago_SumanTotalGenerado()
        {
            var model = await BuildDashboardConMezclaAsync();

            Assert.Equal(
                model.TotalGenerado,
                model.IngresosEfectivo + model.IngresosSinpe + model.IngresosTarjeta);
        }

        [Fact]
        public async Task Dashboard_BaseMasIva_IgualTotalGenerado()
        {
            var model = await BuildDashboardConMezclaAsync();

            Assert.Equal(model.TotalGenerado, model.TotalSinImpuestos + model.TotalImpuestos);
        }

        /// <summary>
        /// Mezcla deliberada: catálogo + personalizado + producto + una venta EXENTA, en los tres
        /// métodos de pago. Si alguna invariante se rompe con esta mezcla, se rompe en producción.
        /// </summary>
        private static async Task<LuxuryApp.Models.Finanzas.DashboardViewModel> BuildDashboardConMezclaAsync()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(context, "Mezcla");
            var gravado = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", 12_000m);
            var exento = await FinanzasTestSupport.SeedServicioAsync(context, "Limpieza facial", 19_500m, aplicaIva: false);
            var producto = await FinanzasTestSupport.SeedProductoAsync(context, "Shampoo", 9_500m);

            await FinanzasTestSupport.SeedCobroServicioAsync(
                context, funcionario, gravado, new DateTime(Anio, Mes, 2, 9, 0, 0), 12_000m, "EFECTIVO");
            await FinanzasTestSupport.SeedCobroServicioAsync(
                context, funcionario, exento, new DateTime(Anio, Mes, 8, 9, 0, 0), 19_500m, "SINPE");
            await FinanzasTestSupport.SeedCobroServicioPersonalizadoAsync(
                context, funcionario, "Afeitado", new DateTime(Anio, Mes, 9, 9, 0, 0), 4_000m, "EFECTIVO");
            await FinanzasTestSupport.SeedCobroProductoAsync(
                context, funcionario, producto, new DateTime(Anio, Mes, 10, 9, 0, 0), 9_500m, "TARJETA");

            var dashboard = ControllerTestSupport.CreateDashboardFinancieroQueryService(context, tenantProvider);
            return await dashboard.BuildViewModelAsync(Mes, Anio);
        }
    }
}
