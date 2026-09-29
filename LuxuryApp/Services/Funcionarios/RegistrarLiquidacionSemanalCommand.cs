namespace LuxuryApp.Services.Funcionarios
{
    public class RegistrarLiquidacionSemanalCommand
    {
        public DateTime SemanaInicio { get; set; }
        public DateTime SemanaFin { get; set; }
        public DateTime? FechaPago { get; set; }
        public string MetodoPago { get; set; } = string.Empty;
        public string? Observacion { get; set; }
        public string? CreadoPor { get; set; }

        /// <summary>
        /// Identifica la INTENCIÓN de pago (se genera al abrir el formulario, no en el servidor al
        /// recibir el POST). Dos envíos con la misma clave producen una sola operación financiera.
        /// Null = comportamiento anterior, sin protección de idempotencia.
        /// </summary>
        public Guid? IdempotencyKey { get; set; }

        public List<RegistrarLiquidacionSemanalDetalleCommand> Detalles { get; set; } = new();
    }

    public class RegistrarLiquidacionSemanalDetalleCommand
    {
        public int FuncionarioId { get; set; }
        public decimal MontoPagado { get; set; }
    }
}
