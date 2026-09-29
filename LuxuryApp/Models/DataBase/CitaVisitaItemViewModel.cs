namespace LuxuryApp.Models.DataBase
{
    public sealed class CitaVisitaItemViewModel
    {
        public int Id { get; init; }
        public DateTime FechaHoraCita { get; init; }
        public string? NombreServicio { get; init; }
        public string? NombreFuncionario { get; init; }
    }
}
