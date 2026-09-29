using LuxuryApp.Models.Finanzas;

namespace LuxuryApp.Services.Finanzas
{
    public interface IDashboardFinancieroQueryService
    {
        Task<DashboardViewModel> BuildViewModelAsync(
            int? mes,
            int? anio,
            CancellationToken cancellationToken = default);
    }
}
