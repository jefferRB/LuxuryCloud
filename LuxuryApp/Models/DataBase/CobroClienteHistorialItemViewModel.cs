namespace LuxuryApp.Models.DataBase
{
    public sealed class CobroClienteHistorialItemViewModel
    {
        public int IdCobro { get; init; }
        public DateTime FechaCobro { get; init; }
        public string? Detalle { get; init; }
        public string? NombreFuncionario { get; init; }
        public string MetodoPago { get; init; } = string.Empty;
        public decimal Monto { get; init; }
        public bool EsServicio { get; init; }
    }
}
