namespace LuxuryApp.Models.Finanzas
{
    public sealed class EgresoIndexItemViewModel
    {
        public int IdEgreso { get; init; }
        public DateTime FechaEgreso { get; init; }
        public string CategoriaNombre { get; init; } = string.Empty;
        public string Detalle { get; init; } = string.Empty;
        public decimal Monto { get; init; }
        public string MetodoPago { get; init; } = string.Empty;
    }
}
