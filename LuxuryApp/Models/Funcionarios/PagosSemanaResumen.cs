namespace LuxuryApp.Models.Funcionarios
{
    public class PagosSemanaResumen
    {
        public DateTime InicioSemana { get; set; }
        public DateTime FinSemana { get; set; }
        public List<PagoFuncionarioVM> Funcionarios { get; set; } = new();
        public decimal TotalGeneradoServicios { get; set; }
        public decimal TotalGeneradoProductos { get; set; }
        public decimal TotalGeneradoGeneral { get; set; }
        public decimal TotalImpuestosGeneral { get; set; }
        public decimal TotalSinImpuestosGeneral { get; set; }
        public decimal TotalPagadoGeneral { get; set; }
        public decimal TotalPendienteGeneral { get; set; }
        public decimal GananciaNegocio { get; set; }

        /// <summary>Suma del pagado realmente aplicado a la planilla = Σ Min(pagado, total a pagar).</summary>
        public decimal TotalPagadoAplicadoGeneral { get; set; }
        /// <summary>Suma de los excedentes (sobrepagos) = Σ Max(pagado − total a pagar, 0).</summary>
        public decimal TotalExcedenteGeneral { get; set; }

        // ─────────────── Desglose fiscal general (IVA incluido) ───────────────
        /// <summary>Base de venta sin IVA del negocio (= <see cref="TotalSinImpuestosGeneral"/>).</summary>
        public decimal TotalBaseVentaSinIvaGeneral { get; set; }
        /// <summary>IVA de la venta incluido del negocio (= <see cref="TotalImpuestosGeneral"/>).</summary>
        public decimal TotalIvaVentaIncluidoGeneral { get; set; }
        /// <summary>Suma del IVA de los colaboradores (el que ellos facturan al negocio).</summary>
        public decimal TotalIvaColaboradorGeneral { get; set; }
        /// <summary>Suma del IVA neto del negocio (IVA de venta − IVA colaborador).</summary>
        public decimal TotalIvaNetoNegocioGeneral { get; set; }
        /// <summary>Suma del total a pagar a colaboradores (planilla del equipo).</summary>
        public decimal TotalAPagarColaboradoresGeneral { get; set; }
        /// <summary>Suma de las bases de comisión (servicios + productos) del equipo.</summary>
        public decimal TotalBaseComisionGeneral { get; set; }
    }
}
