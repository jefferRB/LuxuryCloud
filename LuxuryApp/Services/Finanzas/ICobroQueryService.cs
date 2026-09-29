using LuxuryApp.Models.Finanzas;

namespace LuxuryApp.Services.Finanzas
{
    public interface ICobroQueryService
    {
        Task<CobroIndexViewModel> BuildIndexViewModelAsync(
            CobroFiltroViewModel filtros,
            bool includeFilterOptions = true,
            CancellationToken cancellationToken = default);

        /// <summary>Datos de exportación: resumen + TODAS las filas filtradas (sin paginar) con IVA incluido.</summary>
        Task<CobroExportViewModel> BuildExportAsync(
            CobroFiltroViewModel filtros,
            CancellationToken cancellationToken = default);

        Task<CobroViewModel> BuildCreateViewModelAsync(
            Cobro? cobro = null,
            CancellationToken cancellationToken = default);

        Task<CobroViewModel?> BuildEditViewModelAsync(
            int id,
            Cobro? cobro = null,
            CancellationToken cancellationToken = default);

        Task<decimal?> ObtenerPrecioServicioAsync(int id, CancellationToken cancellationToken = default);
        Task<decimal?> ObtenerPrecioProductoAsync(int id, CancellationToken cancellationToken = default);
    }
}
