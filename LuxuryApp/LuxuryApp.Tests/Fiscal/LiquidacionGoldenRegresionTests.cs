using LuxuryApp.Models.Fiscal;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Services.Fiscal;
using LuxuryApp.Tests.Support;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// GOLDEN / CARACTERIZACIÓN. Escritos ANTES de introducir el snapshot de remuneración y la
    /// agrupación por configuración.
    ///
    /// <para>
    /// La Fase 5 cambia CÓMO se agrega la liquidación: antes era una sola llamada al motor con los
    /// totales del periodo; ahora se agrupa por configuración efectiva y se suma. La invariante que
    /// protege el cambio es ésta:
    /// </para>
    ///
    /// <para>
    /// <b>Si todos los cobros del periodo comparten configuración, el resultado nuevo tiene que ser
    /// IDÉNTICO AL CENTAVO al anterior.</b> No "parecido", no "difiere por redondeo". Idéntico.
    /// </para>
    ///
    /// <para>
    /// Por eso el valor esperado NO se escribe a mano: se construye llamando al motor
    /// <c>ILiquidacionFuncionarioService</c> una sola vez sobre los agregados del periodo, que es
    /// exactamente lo que hacía el algoritmo viejo. Así el test no depende de que yo haya hecho bien
    /// la aritmética: compara el algoritmo nuevo contra el viejo, campo por campo.
    /// </para>
    /// </summary>
    public class LiquidacionGoldenRegresionTests
    {
        private static readonly DateTime Inicio = new(2026, 9, 1);
        private static readonly DateTime Fin = new(2026, 9, 15);

        public static TheoryData<string, ComisionCalculadaSobre, TipoRelacionColaborador, ModalidadIvaColaborador, decimal, decimal, bool>
            Configuraciones => new()
        {
            // nombre, base comisión, relación, modalidad IVA, % servicio, % producto, servicio gravado
            { "empleado sobre total",        ComisionCalculadaSobre.TotalCobrado, TipoRelacionColaborador.Empleado,      ModalidadIvaColaborador.NoFactura,   50m, 11m, true },
            { "empleado sobre base",         ComisionCalculadaSobre.BaseSinIva,   TipoRelacionColaborador.Empleado,      ModalidadIvaColaborador.NoFactura,   50m, 10m, true },
            { "independiente IVA incluido",  ComisionCalculadaSobre.BaseSinIva,   TipoRelacionColaborador.Independiente, ModalidadIvaColaborador.IvaIncluido, 45m, 12m, true },
            { "independiente IVA adicional", ComisionCalculadaSobre.TotalCobrado, TipoRelacionColaborador.Independiente, ModalidadIvaColaborador.IvaAdicional, 40m, 15m, true },
            { "servicio exento",             ComisionCalculadaSobre.BaseSinIva,   TipoRelacionColaborador.Empleado,      ModalidadIvaColaborador.NoFactura,   50m, 10m, false },
            { "porcentaje con decimales",    ComisionCalculadaSobre.BaseSinIva,   TipoRelacionColaborador.Empleado,      ModalidadIvaColaborador.NoFactura,   33.33m, 7.77m, true },
        };

        /// <summary>
        /// §15 — La invariante que autoriza el cambio de algoritmo. Se corre sobre seis
        /// configuraciones distintas, incluidas las que más sufren el redondeo (33,33 % / 7,77 %).
        /// </summary>
        [Theory]
        [MemberData(nameof(Configuraciones))]
        public async Task PeriodoUnaSolaConfiguracion_ResultaIdenticoAlAlgoritmoAnterior(
            string escenario,
            ComisionCalculadaSobre comisionSobre,
            TipoRelacionColaborador tipoRelacion,
            ModalidadIvaColaborador modalidadIva,
            decimal porcentajeServicio,
            decimal porcentajeProducto,
            bool servicioGravado)
        {
            Assert.False(string.IsNullOrEmpty(escenario));

            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var c = context;
            using var cn = connection;

            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(
                context, "Jordan", porcentajeServicio, porcentajeProducto, comisionSobre, tipoRelacion, modalidadIva);

            if (tipoRelacion == TipoRelacionColaborador.Independiente)
            {
                funcionario.TarifaIvaFacturaColaborador = 13m;
                await context.SaveChangesAsync();
            }

            var servicio = await FinanzasTestSupport.SeedServicioAsync(
                context, "Corte", 7_000m, aplicaIva: servicioGravado);
            var producto = await FinanzasTestSupport.SeedProductoAsync(context, "Cera", 9_500m);

            // Montos deliberadamente "feos" para que el redondeo tenga dónde fallar.
            var montosServicio = new[] { 7_000m, 12_333m, 4_567m, 999m };
            foreach (var (monto, dia) in montosServicio.Select((m, i) => (m, i + 2)))
            {
                await FinanzasTestSupport.SeedCobroServicioAsync(
                    context, funcionario, servicio, new DateTime(2026, 9, dia, 10, 0, 0), monto);
            }

            var montosProducto = new[] { 9_500m, 3_333m };
            foreach (var (monto, dia) in montosProducto.Select((m, i) => (m, i + 8)))
            {
                await FinanzasTestSupport.SeedCobroProductoAsync(
                    context, funcionario, producto, new DateTime(2026, 9, dia, 11, 0, 0), monto);
            }

            var esperado = ReferenciaAlgoritmoAnterior(
                montosServicio, montosProducto, servicioGravado,
                porcentajeServicio, porcentajeProducto, comisionSobre, tipoRelacion, modalidadIva,
                funcionario.TarifaIvaFacturaColaborador);

            var resumen = await ControllerTestSupport
                .CreateLiquidacionSemanalService(context, tenantProvider)
                .ObtenerResumenSemanaAsync(Inicio, Fin);

            var fila = Assert.Single(resumen.Funcionarios);

            Assert.Equal(esperado.TotalCobrado, fila.TotalGenerado);
            Assert.Equal(esperado.BaseVentaSinIva, fila.BaseVentaSinIva);
            Assert.Equal(esperado.IvaVentaIncluido, fila.IvaVentaIncluido);
            Assert.Equal(esperado.BaseComisionServicios, fila.BaseComisionServicios);
            Assert.Equal(esperado.BaseComisionProductos, fila.BaseComisionProductos);
            Assert.Equal(esperado.MontoColaborador, fila.MontoColaborador);
            Assert.Equal(esperado.BaseColaborador, fila.BaseColaborador);
            Assert.Equal(esperado.IvaColaborador, fila.IvaColaborador);
            Assert.Equal(esperado.IvaNetoNegocio, fila.IvaNetoNegocio);
            Assert.Equal(esperado.TotalAPagarColaborador, fila.TotalAPagarColaborador);
        }

        /// <summary>
        /// §29 — No regresión del caso de referencia del negocio. Acá sí van a mano:
        /// son el contrato con el negocio, no una derivación del código.
        ///
        /// <para>50 % de ₡1.200.800 = ₡600.400 · 11 % de ₡10.000 = ₡1.100 · total ₡601.500.</para>
        /// </summary>
        [Fact]
        public async Task Dray_50Servicio_11Producto_SobreTotalCobrado_NoCambia()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var c = context;
            using var cn = connection;

            var funcionario = await FinanzasTestSupport.SeedFuncionarioAsync(
                context, "Jordan",
                porcentajeServicio: 50m,
                porcentajeProducto: 11m,
                comisionSobre: ComisionCalculadaSobre.TotalCobrado);

            var servicio = await FinanzasTestSupport.SeedServicioAsync(context, "Corte", 1_000_000m);
            var producto = await FinanzasTestSupport.SeedProductoAsync(context, "Cera", 10_000m);

            // 1.000.000 + 200.800 = 1.200.800
            await FinanzasTestSupport.SeedCobroServicioAsync(
                context, funcionario, servicio, new DateTime(2026, 9, 2, 10, 0, 0), 1_000_000m);
            await FinanzasTestSupport.SeedCobroServicioAsync(
                context, funcionario, servicio, new DateTime(2026, 9, 5, 10, 0, 0), 200_800m);
            await FinanzasTestSupport.SeedCobroProductoAsync(
                context, funcionario, producto, new DateTime(2026, 9, 6, 10, 0, 0), 10_000m);

            var resumen = await ControllerTestSupport
                .CreateLiquidacionSemanalService(context, tenantProvider)
                .ObtenerResumenSemanaAsync(Inicio, Fin);

            var fila = Assert.Single(resumen.Funcionarios);

            Assert.Equal(1_200_800m, fila.TotalServicios);
            Assert.Equal(10_000m, fila.TotalProductos);
            Assert.Equal(600_400m, fila.BaseComisionServicios);
            Assert.Equal(1_100m, fila.BaseComisionProductos);
            Assert.Equal(601_500m, fila.TotalAPagarColaborador);
        }

        /// <summary>
        /// El algoritmo ANTERIOR, reproducido en el test: una sola llamada al motor con los totales
        /// del periodo. Es el patrón exacto que tenía <c>ObtenerResumenSemanaAsync</c> antes de la
        /// Fase 5.
        /// </summary>
        private static LiquidacionColaboradorResult ReferenciaAlgoritmoAnterior(
            IReadOnlyCollection<decimal> montosServicio,
            IReadOnlyCollection<decimal> montosProducto,
            bool servicioGravado,
            decimal porcentajeServicio,
            decimal porcentajeProducto,
            ComisionCalculadaSobre comisionSobre,
            TipoRelacionColaborador tipoRelacion,
            ModalidadIvaColaborador modalidadIva,
            decimal tarifaColaborador)
        {
            var tax = new TaxCalculationService();
            var tenantFiscal = TenantFiscalConfig.Default;

            TaxBreakdown Sumar(IEnumerable<decimal> montos, bool gravado) =>
                tax.Sumar(montos.Select(monto => new TaxLineInput
                {
                    TotalOrBase = monto,
                    TaxRatePercent = tenantFiscal.TarifaIvaPorDefecto,
                    PriceIncludesTax = tenantFiscal.PreciosIncluyenIva,
                    Taxable = gravado
                }));

            var servicios = Sumar(montosServicio, servicioGravado);
            var productos = Sumar(montosProducto, true);

            return new LiquidacionFuncionarioService().Liquidar(new LiquidacionColaboradorInput
            {
                TotalVentaServicios = servicios.GrossTotal,
                BaseVentaServicios = servicios.NetBase,
                IvaVentaServicios = servicios.TaxAmount,
                TotalVentaProductos = productos.GrossTotal,
                BaseVentaProductos = productos.NetBase,
                IvaVentaProductos = productos.TaxAmount,
                PorcentajeServicios = porcentajeServicio,
                PorcentajeProductos = porcentajeProducto,
                ComisionCalculadaSobre = comisionSobre,
                TipoRelacion = tipoRelacion,
                ModalidadIva = modalidadIva,
                TarifaIvaFacturaColaborador = tarifaColaborador
            });
        }
    }
}
