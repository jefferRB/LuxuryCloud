using LuxuryApp.Models.Fiscal;
using LuxuryApp.Models.Productos;

namespace LuxuryApp.Models.Funcionarios
{
    public class PagoFuncionarioVM
    {
        public int FuncionarioId { get; set; }

        public string Nombre { get; set; } = string.Empty;

        /// <summary>Color persistido del funcionario (config del calendario); acento visual en planilla.</summary>
        public string ColorCalendario { get; set; } = string.Empty;

        public decimal TotalGenerado { get; set; }

        // LEGACY: mapeados a los conceptos fiscales correctos (ver abajo).
        // Impuestos = IVA de la venta incluido; TotalNeto = base de la venta sin IVA.
        public decimal Impuestos { get; set; }

        public decimal TotalNeto { get; set; }

        // ─────────────── Desglose fiscal de la venta (negocio) ───────────────

        /// <summary>Total cobrado al cliente (IVA incluido). Igual a <see cref="TotalGenerado"/>.</summary>
        public decimal TotalCobrado { get; set; }

        /// <summary>Base de la venta sin IVA (servicios + productos).</summary>
        public decimal BaseVentaSinIva { get; set; }

        /// <summary>IVA contenido en la venta.</summary>
        public decimal IvaVentaIncluido { get; set; }

        // ─────────────── Liquidación del colaborador ───────────────

        public decimal BaseComisionServicios { get; set; }

        public decimal BaseComisionProductos { get; set; }

        /// <summary>Monto del colaborador (% acordado aplicado a la base según la regla).</summary>
        public decimal MontoColaborador { get; set; }

        /// <summary>Base del colaborador (sin su IVA), según la modalidad.</summary>
        public decimal BaseColaborador { get; set; }

        /// <summary>IVA del colaborador (0 si no factura). Es el IVA de su factura, no el de venta.</summary>
        public decimal IvaColaborador { get; set; }

        /// <summary>IVA neto del negocio = IVA de venta − IVA colaborador.</summary>
        public decimal IvaNetoNegocio { get; set; }

        /// <summary>Total a pagar al colaborador.</summary>
        public decimal TotalAPagarColaborador { get; set; }

        /// <summary>Total pagado al colaborador en el periodo (= <see cref="MontoPagado"/>).</summary>
        public decimal TotalPagado { get; set; }

        /// <summary>Pendiente por pagar (= <see cref="MontoPendiente"/>).</summary>
        public decimal Pendiente { get; set; }

        // ─────────────── Configuración fiscal del colaborador ───────────────

        public TipoRelacionColaborador TipoRelacionColaborador { get; set; }

        public ComisionCalculadaSobre ComisionCalculadaSobre { get; set; }

        public bool ColaboradorFacturaIva { get; set; }

        public ModalidadIvaColaborador ModalidadIvaColaborador { get; set; }

        /// <summary>Tarifa de IVA del colaborador (%), para mostrar el divisor real en la fórmula.</summary>
        public decimal TarifaIvaColaborador { get; set; }

        public bool RequiereFacturaAntesDePagar { get; set; }

        public decimal Porcentaje { get; set; }

        public decimal PorcentajeProducto { get; set; }

        public decimal PagoFinal { get; set; }

        /// <summary>Total realmente pagado al colaborador en el periodo (suma de pagos registrados).</summary>
        public decimal MontoPagado { get; set; }

        /// <summary>Pendiente por pagar, nunca negativo = Max(TotalAPagar − pagado, 0).</summary>
        public decimal MontoPendiente { get; set; }

        /// <summary>Parte del pago que se aplica a la planilla = Min(pagado, TotalAPagar).</summary>
        public decimal MontoPagadoAplicado { get; set; }

        /// <summary>Excedente pagado por encima de lo devengado = Max(pagado − TotalAPagar, 0).</summary>
        public decimal Excedente { get; set; }

        public decimal TotalServicios { get; set; }

        public decimal TotalProductos { get; set; }

        public List<DetalleDiaVM> DetalleDias { get; set; } = new();

        // PagoFuncionario queda como fuente legacy solo para lectura historica.
        // Las nuevas escrituras salen de LiquidacionSemanal + Detalles.
        public List<HistorialPagoFuncionarioViewModel> HistorialPagos { get; set; } = new();

        public List<ProductoVendidoVM> ProductosVendidos { get; set; } = new();


        // 🔵 INDICADORES GENERALES
        public decimal TotalGeneradoGeneral { get; set; }

        public decimal TotalSinImpuestosGeneral { get; set; }

        public decimal TotalPagadoGeneral { get; set; }

        public decimal TotalPendienteGeneral { get; set; }

        public decimal GananciaNegocio { get; set; }
    }
}
