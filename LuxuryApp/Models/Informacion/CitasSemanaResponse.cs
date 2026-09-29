namespace LuxuryApp.Models.Informacion
{
    public sealed class CitasSemanaResponse
    {
        public List<string> Dias { get; init; } = new();
        public List<int> Citas { get; init; } = new();
        public string Inicio { get; init; } = string.Empty;
        public string Fin { get; init; } = string.Empty;
    }
}
