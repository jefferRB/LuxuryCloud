namespace LuxuryApp.Models.DataBase
{
    public sealed class ClienteServicioRealizadoItemViewModel
    {
        public int Id { get; init; }
        public DateTime FechaHora { get; init; }
        public string? NombreServicio { get; init; }
        public string? NombreFuncionario { get; init; }
        public string Origen { get; init; } = string.Empty;
        public decimal? Monto { get; init; }
        public string? Notas { get; init; }
    }
}
