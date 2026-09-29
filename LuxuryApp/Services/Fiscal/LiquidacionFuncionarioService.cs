using LuxuryApp.Models.Fiscal;

namespace LuxuryApp.Services.Fiscal
{
    /// <summary>
    /// Liquidación del colaborador. Dos ejes independientes:
    ///
    ///  1) Base de la comisión (<see cref="ComisionCalculadaSobre"/>):
    ///       - TotalCobrado → el % se aplica al total con IVA incluido.
    ///       - BaseSinIva   → el % se aplica a la base sin IVA (el IVA de venta se separa antes).
    ///     El resultado es el "monto del colaborador" (su parte según el % acordado).
    ///
    ///  2) Tratamiento del IVA del colaborador (<see cref="ModalidadIvaColaborador"/>), solo para
    ///     <see cref="TipoRelacionColaborador.Independiente"/>:
    ///       A) NoFactura   → IVA colaborador = 0; total = monto.
    ///       B) IvaIncluido → el monto YA incluye su IVA; se descompone (base = monto/(1+t), IVA = resto).
    ///                        El total NO crece. ← caso principal.
    ///       C) IvaAdicional→ el IVA se SUMA por encima del monto (total = monto + monto·t).
    ///
    ///  IVA neto del negocio = IVA de venta − IVA colaborador.
    /// </summary>
    public sealed class LiquidacionFuncionarioService : ILiquidacionFuncionarioService
    {
        public LiquidacionColaboradorResult Liquidar(LiquidacionColaboradorInput input)
        {
            ArgumentNullException.ThrowIfNull(input);

            var sobreBase = input.ComisionCalculadaSobre == ComisionCalculadaSobre.BaseSinIva;

            var baseServicios = sobreBase ? input.BaseVentaServicios : input.TotalVentaServicios;
            var baseProductos = sobreBase ? input.BaseVentaProductos : input.TotalVentaProductos;

            var baseComisionServicios = FiscalMath.Redondear(baseServicios * (input.PorcentajeServicios / 100m));
            var baseComisionProductos = FiscalMath.Redondear(baseProductos * (input.PorcentajeProductos / 100m));

            // Monto del colaborador: su parte según el % acordado (antes de tratar su IVA).
            var montoColaborador = baseComisionServicios + baseComisionProductos;

            var ivaVenta = input.IvaVentaServicios + input.IvaVentaProductos;
            var tarifa = input.TarifaIvaFacturaColaborador;
            var factor = tarifa / 100m;

            // La modalidad solo tiene efecto para independientes con tarifa válida.
            var modalidad = (input.TipoRelacion == TipoRelacionColaborador.Independiente && tarifa > 0m)
                ? input.ModalidadIva
                : ModalidadIvaColaborador.NoFactura;

            decimal baseColaborador;
            decimal ivaColaborador;
            decimal totalAPagar;

            switch (modalidad)
            {
                case ModalidadIvaColaborador.IvaIncluido:
                    // B) El monto ya incluye el IVA del colaborador → se descompone, sin crecer.
                    totalAPagar = montoColaborador;
                    baseColaborador = FiscalMath.Redondear(montoColaborador / (1m + factor));
                    ivaColaborador = montoColaborador - baseColaborador;
                    break;

                case ModalidadIvaColaborador.IvaAdicional:
                    // C) El IVA se suma por encima de la comisión.
                    baseColaborador = montoColaborador;
                    ivaColaborador = FiscalMath.Redondear(montoColaborador * factor);
                    totalAPagar = montoColaborador + ivaColaborador;
                    break;

                default:
                    // A) No factura IVA (o no es independiente).
                    baseColaborador = montoColaborador;
                    ivaColaborador = 0m;
                    totalAPagar = montoColaborador;
                    break;
            }

            return new LiquidacionColaboradorResult
            {
                TotalCobrado = input.TotalVentaServicios + input.TotalVentaProductos,
                BaseVentaSinIva = input.BaseVentaServicios + input.BaseVentaProductos,
                IvaVentaIncluido = ivaVenta,
                BaseComisionServicios = baseComisionServicios,
                BaseComisionProductos = baseComisionProductos,
                MontoColaborador = montoColaborador,
                BaseColaborador = baseColaborador,
                IvaColaborador = ivaColaborador,
                TotalAPagarColaborador = totalAPagar,
                IvaNetoNegocio = ivaVenta - ivaColaborador,
                ModalidadAplicada = modalidad
            };
        }
    }
}
