namespace LuxuryApp.Models.Funcionarios
{
    public sealed class PagosSemanaPageViewModel
    {
        // InicioSemana/FinSemana conservan el nombre por compatibilidad, pero ahora representan
        // el rango del PERIODO activo (semana o quincena), no necesariamente una semana.
        public DateTime InicioSemana { get; init; }

        public DateTime FinSemana { get; init; }

        // ─────────────── Periodo de liquidación (semanal / quincenal) ───────────────
        public PayrollPeriodType PeriodoTipo { get; init; } = PayrollPeriodType.Semanal;

        public string PeriodoTipoLabel { get; init; } = "Semanal";

        public string PeriodoEtiqueta { get; init; } = string.Empty;

        public string PeriodoCtaTexto { get; init; } = "Pagar semana";

        public DateTime PeriodoReferenciaAnterior { get; init; }

        public DateTime PeriodoReferenciaSiguiente { get; init; }

        public DateTime FechaPagoSugerida { get; init; }

        /// <summary>Fecha de negocio "hoy": base para que los tabs muestren el periodo actual.</summary>
        public DateTime Hoy { get; init; }

        public IReadOnlyList<string> MetodosPago { get; init; } = Array.Empty<string>();

        public IReadOnlyList<PagoFuncionarioVM> Funcionarios { get; init; } =
            Array.Empty<PagoFuncionarioVM>();

        public decimal TotalGeneradoServicios { get; init; }

        public decimal TotalGeneradoProductos { get; init; }

        public decimal TotalGeneradoGeneral { get; init; }

        public decimal TotalImpuestosGeneral { get; init; }

        public decimal TotalSinImpuestosGeneral { get; init; }

        public decimal TotalPagadoGeneral { get; init; }

        public decimal TotalPagadoAplicadoGeneral { get; init; }

        public decimal TotalPendienteGeneral { get; init; }

        public decimal TotalExcedenteGeneral { get; init; }

        public decimal GananciaNegocio { get; init; }

        // ─────────────── Desglose fiscal general (IVA incluido) ───────────────
        public decimal TotalBaseVentaSinIvaGeneral { get; init; }

        public decimal TotalIvaVentaIncluidoGeneral { get; init; }

        public decimal TotalIvaColaboradorGeneral { get; init; }

        public decimal TotalIvaNetoNegocioGeneral { get; init; }

        public decimal TotalAPagarColaboradoresGeneral { get; init; }

        public decimal TotalBaseComisionGeneral { get; init; }
    }
}
