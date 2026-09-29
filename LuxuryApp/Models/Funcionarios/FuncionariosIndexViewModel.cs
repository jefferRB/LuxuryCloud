namespace LuxuryApp.Models.Funcionarios
{
    public sealed class FuncionariosIndexViewModel
    {
        public IReadOnlyList<FuncionarioIndexItemViewModel> Funcionarios { get; init; } =
            Array.Empty<FuncionarioIndexItemViewModel>();

        public int TotalFuncionarios => Funcionarios.Count;

        public int TotalActivos => Funcionarios.Count(f => f.Activo);
    }
}
