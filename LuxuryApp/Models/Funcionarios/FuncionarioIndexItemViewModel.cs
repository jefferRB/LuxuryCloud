namespace LuxuryApp.Models.Funcionarios
{
    public sealed class FuncionarioIndexItemViewModel
    {
        public int IdFuncionario { get; init; }

        public string Nombre { get; init; } = string.Empty;

        public string? Telefono { get; init; }

        public string NombrePuesto { get; init; } = string.Empty;

        public decimal PorcentajeGanancia { get; init; }

        public decimal PorcentajeProducto { get; init; }

        public string ColorCalendario { get; init; } = string.Empty;

        public DateTime FechaIngreso { get; init; }

        public bool Activo { get; init; }

        public FuncionarioAccesoEstado AccesoEstado { get; init; }
    }
}
