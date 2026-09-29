namespace LuxuryApp.Models.Finanzas
{
    public sealed class EgresoUpdateRequest
    {
        public int IdEgreso { get; init; }
        public DateTime FechaEgreso { get; init; }
        public string Detalle { get; init; } = string.Empty;
        public decimal Monto { get; init; }
        public string MetodoPago { get; init; } = string.Empty;
        public int CategoriaId { get; init; }
    }
}
