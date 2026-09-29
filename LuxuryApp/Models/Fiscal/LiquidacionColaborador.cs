namespace LuxuryApp.Models.Fiscal
{
    /// <summary>
    /// Entrada para liquidar el pago de un colaborador en un periodo. Las bases de venta ya
    /// vienen calculadas por el motor fiscal (base sin IVA e IVA por línea agregados).
    /// </summary>
    public sealed record LiquidacionColaboradorInput
    {
        // ── Venta de servicios ──
        public decimal TotalVentaServicios { get; init; }
        public decimal BaseVentaServicios { get; init; }
        public decimal IvaVentaServicios { get; init; }

        // ── Venta de productos ──
        public decimal TotalVentaProductos { get; init; }
        public decimal BaseVentaProductos { get; init; }
        public decimal IvaVentaProductos { get; init; }

        // ── Configuración del colaborador ──
        public decimal PorcentajeServicios { get; init; }
        public decimal PorcentajeProductos { get; init; }
        public ComisionCalculadaSobre ComisionCalculadaSobre { get; init; } = ComisionCalculadaSobre.BaseSinIva;
        public TipoRelacionColaborador TipoRelacion { get; init; } = TipoRelacionColaborador.Empleado;

        /// <summary>Modalidad de IVA del colaborador (A/B/C). Solo aplica a Independiente.</summary>
        public ModalidadIvaColaborador ModalidadIva { get; init; } = ModalidadIvaColaborador.NoFactura;

        public decimal TarifaIvaFacturaColaborador { get; init; } = FiscalDefaults.TarifaIvaPorDefecto;
    }

    /// <summary>
    /// Resultado de liquidar a un colaborador. Separa los conceptos de la venta (del negocio)
    /// de los conceptos del pago al colaborador. Todos redondeados a 2 decimales.
    /// </summary>
    public sealed record LiquidacionColaboradorResult
    {
        // ── Venta (negocio) ──
        public decimal TotalCobrado { get; init; }
        public decimal BaseVentaSinIva { get; init; }
        public decimal IvaVentaIncluido { get; init; }

        // ── Pago al colaborador ──
        public decimal BaseComisionServicios { get; init; }
        public decimal BaseComisionProductos { get; init; }
        public decimal BaseComisionTotal => BaseComisionServicios + BaseComisionProductos;

        /// <summary>Monto del colaborador = % acordado aplicado a la base según la regla (antes de tratar su IVA).</summary>
        public decimal MontoColaborador { get; init; }

        /// <summary>Base del colaborador (sin su IVA), según la modalidad.</summary>
        public decimal BaseColaborador { get; init; }

        /// <summary>IVA del colaborador (0 si no factura). Es su IVA de factura, NO el IVA de venta.</summary>
        public decimal IvaColaborador { get; init; }

        /// <summary>Total a pagar al colaborador.</summary>
        public decimal TotalAPagarColaborador { get; init; }

        /// <summary>IVA neto del negocio = IVA de venta − IVA colaborador.</summary>
        public decimal IvaNetoNegocio { get; init; }

        /// <summary>Modalidad de IVA efectivamente aplicada (NoFactura si no es independiente).</summary>
        public ModalidadIvaColaborador ModalidadAplicada { get; init; }
    }
}
