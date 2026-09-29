using LuxuryApp.Models.Fiscal;
using LuxuryApp.Services.Fiscal;
using Xunit;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// Verifica la liquidación del colaborador: base de comisión (sobre total / sobre base sin IVA)
    /// y las 3 modalidades de IVA del colaborador (A no factura, B incluido en su parte, C adicional).
    /// Las bases de venta se calculan con el mismo motor fiscal (IVA 13% incluido).
    /// </summary>
    public class LiquidacionFuncionarioServiceTests
    {
        private readonly LiquidacionFuncionarioService _sut = new();
        private readonly TaxCalculationService _tax = new();

        private LiquidacionColaboradorInput Servicios(
            decimal totalServicios,
            decimal porcentaje,
            ComisionCalculadaSobre comisionSobre,
            TipoRelacionColaborador tipo,
            ModalidadIvaColaborador modalidad,
            decimal tarifa = 13m)
        {
            var b = _tax.Calcular(totalServicios, 13m, priceIncludesTax: true, taxable: true);
            return new LiquidacionColaboradorInput
            {
                TotalVentaServicios = b.GrossTotal,
                BaseVentaServicios = b.NetBase,
                IvaVentaServicios = b.TaxAmount,
                PorcentajeServicios = porcentaje,
                ComisionCalculadaSobre = comisionSobre,
                TipoRelacion = tipo,
                ModalidadIva = modalidad,
                TarifaIvaFacturaColaborador = tarifa
            };
        }

        // ── Escenario 1: sobre total cobrado + no factura IVA ──
        [Theory]
        [InlineData(209000, 104500.00, 24044.25)]
        [InlineData(36000, 18000.00, 4141.59)]
        public void Escenario1_SobreTotal_SinFactura(decimal total, decimal totalPagar, decimal ivaVenta)
        {
            var r = _sut.Liquidar(Servicios(total, 50m, ComisionCalculadaSobre.TotalCobrado,
                TipoRelacionColaborador.Empleado, ModalidadIvaColaborador.NoFactura));

            Assert.Equal(totalPagar, r.MontoColaborador);
            Assert.Equal(totalPagar, r.TotalAPagarColaborador);
            Assert.Equal(0m, r.IvaColaborador);
            Assert.Equal(ivaVenta, r.IvaVentaIncluido);
            Assert.Equal(ivaVenta, r.IvaNetoNegocio); // sin IVA colaborador → neto = IVA venta
            Assert.Equal(ModalidadIvaColaborador.NoFactura, r.ModalidadAplicada);
        }

        // ── Escenario 2: sobre total cobrado + IVA incluido dentro de su parte (caso principal) ──
        [Fact]
        public void Escenario2_SobreTotal_IvaIncluido_Ejemplo48000()
        {
            var r = _sut.Liquidar(Servicios(48000m, 50m, ComisionCalculadaSobre.TotalCobrado,
                TipoRelacionColaborador.Independiente, ModalidadIvaColaborador.IvaIncluido));

            // Venta
            Assert.Equal(48000m, r.TotalCobrado);
            Assert.Equal(42477.88m, r.BaseVentaSinIva);
            Assert.Equal(5522.12m, r.IvaVentaIncluido);
            // Colaborador: monto = 50% del total; se descompone SIN crecer.
            Assert.Equal(24000m, r.MontoColaborador);
            Assert.Equal(21238.94m, r.BaseColaborador);
            Assert.Equal(2761.06m, r.IvaColaborador);
            Assert.Equal(24000m, r.TotalAPagarColaborador);
            // IVA neto negocio = IVA venta − IVA colaborador
            Assert.Equal(2761.06m, r.IvaNetoNegocio);
            Assert.Equal(ModalidadIvaColaborador.IvaIncluido, r.ModalidadAplicada);
        }

        // ── Escenario 3: sobre base sin IVA + IVA incluido dentro de su parte ──
        [Fact]
        public void Escenario3_SobreBase_IvaIncluido()
        {
            var r = _sut.Liquidar(Servicios(145000m, 50m, ComisionCalculadaSobre.BaseSinIva,
                TipoRelacionColaborador.Independiente, ModalidadIvaColaborador.IvaIncluido));

            // base venta 145000/1.13 = 128318.58 ; comisión 50% = 64159.29 (monto colaborador)
            Assert.Equal(128318.58m, r.BaseVentaSinIva);
            Assert.Equal(64159.29m, r.MontoColaborador);
            Assert.Equal(64159.29m, r.TotalAPagarColaborador); // no crece
            // se descompone: base = 64159.29/1.13, iva = resto
            Assert.Equal(r.MontoColaborador, r.BaseColaborador + r.IvaColaborador);
            Assert.Equal(56778.13m, r.BaseColaborador);
            Assert.Equal(7381.16m, r.IvaColaborador);
        }

        // ── Escenario 4: sobre base sin IVA + IVA adicional sobre la comisión ──
        [Fact]
        public void Escenario4_SobreBase_IvaAdicional()
        {
            var r = _sut.Liquidar(Servicios(209000m, 50m, ComisionCalculadaSobre.BaseSinIva,
                TipoRelacionColaborador.Independiente, ModalidadIvaColaborador.IvaAdicional));

            Assert.Equal(92477.88m, r.MontoColaborador);
            Assert.Equal(92477.88m, r.BaseColaborador);
            Assert.Equal(12022.12m, r.IvaColaborador);           // 92477.88 * 13%
            Assert.Equal(104500.00m, r.TotalAPagarColaborador);  // base + IVA (sí crece)
            Assert.Equal(12022.13m, r.IvaNetoNegocio);           // 24044.25 − 12022.12
        }

        // ── La modalidad solo aplica a Independiente ──
        [Fact]
        public void NoIndependiente_IgnoraModalidad()
        {
            var r = _sut.Liquidar(Servicios(209000m, 50m, ComisionCalculadaSobre.BaseSinIva,
                TipoRelacionColaborador.Empleado, ModalidadIvaColaborador.IvaAdicional));

            Assert.Equal(ModalidadIvaColaborador.NoFactura, r.ModalidadAplicada);
            Assert.Equal(0m, r.IvaColaborador);
            Assert.Equal(92477.88m, r.TotalAPagarColaborador);
        }

        // ── Base de comisión sobre base sin IVA (sin factura), valores históricos ──
        [Theory]
        [InlineData(209000, 92477.88)]
        [InlineData(36000, 15929.20)]
        [InlineData(95000, 42035.40)]
        public void ComisionSobreBaseSinIva_SinFactura(decimal total, decimal montoEsperado)
        {
            var r = _sut.Liquidar(Servicios(total, 50m, ComisionCalculadaSobre.BaseSinIva,
                TipoRelacionColaborador.Empleado, ModalidadIvaColaborador.NoFactura));

            Assert.Equal(montoEsperado, r.MontoColaborador);
            Assert.Equal(montoEsperado, r.TotalAPagarColaborador);
        }

        [Fact]
        public void ServiciosYProductos_SumaAmbasComisiones()
        {
            var servicios = _tax.Calcular(10000m, 13m, true, true);
            var productos = _tax.Calcular(5000m, 13m, true, true);

            var r = _sut.Liquidar(new LiquidacionColaboradorInput
            {
                TotalVentaServicios = servicios.GrossTotal,
                BaseVentaServicios = servicios.NetBase,
                IvaVentaServicios = servicios.TaxAmount,
                TotalVentaProductos = productos.GrossTotal,
                BaseVentaProductos = productos.NetBase,
                IvaVentaProductos = productos.TaxAmount,
                PorcentajeServicios = 50m,
                PorcentajeProductos = 10m,
                ComisionCalculadaSobre = ComisionCalculadaSobre.BaseSinIva,
                TipoRelacion = TipoRelacionColaborador.Empleado,
                ModalidadIva = ModalidadIvaColaborador.NoFactura
            });

            Assert.Equal(4424.78m, r.BaseComisionServicios);  // 8849.56 * 50%
            Assert.Equal(442.48m, r.BaseComisionProductos);   // 4424.78 * 10%
            Assert.Equal(4867.26m, r.MontoColaborador);
            Assert.Equal(4867.26m, r.TotalAPagarColaborador);
        }
    }
}
