using LuxuryApp.Models.Finanzas;

namespace LuxuryApp.Services.Finanzas
{
    public interface IEgresoQueryService
    {
        Task<EgresoIndexViewModel> BuildIndexViewModelAsync(
            EgresoFiltroViewModel filtros,
            bool includeFilterOptions = true,
            CancellationToken cancellationToken = default);

        Task<EgresoViewModel> BuildCreateViewModelAsync(
            Egreso? egreso = null,
            CancellationToken cancellationToken = default);

        Task<EgresoViewModel?> BuildEditViewModelAsync(
            int id,
            Egreso? egreso = null,
            CancellationToken cancellationToken = default);
    }
}
