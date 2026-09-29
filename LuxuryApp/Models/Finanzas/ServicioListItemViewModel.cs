namespace LuxuryApp.Models.Finanzas
{
    public sealed class ServicioListItemViewModel
    {
        public int Id { get; init; }
        public string Nombre { get; init; } = string.Empty;
        public decimal Precio { get; init; }
        public int? DuracionMinutos { get; init; }
        public bool Activo { get; init; }
    }
}
