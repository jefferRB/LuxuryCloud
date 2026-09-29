namespace LuxuryApp.Models.Funcionarios
{
    public sealed class HistorialPagoFuncionarioViewModel
    {
        public int ReferenciaId { get; init; }

        public int FuncionarioId { get; init; }

        public decimal MontoPagado { get; init; }

        public DateTime FechaPago { get; init; }

        public DateTime InicioSemana { get; init; }

        public DateTime FinSemana { get; init; }

        public string? Observacion { get; init; }

        public string OrigenRegistro { get; init; } = string.Empty;

        /// <summary>Método de pago (viene del egreso de la liquidación; null en pagos legacy).</summary>
        public string? MetodoPago { get; init; }

        /// <summary>Usuario que registró el pago (CreadoPor de la liquidación; null en legacy).</summary>
        public string? RegistradoPor { get; init; }
    }
}
