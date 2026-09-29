using LuxuryApp.Models.Informacion;

namespace LuxuryApp.Services.Informacion
{
    public interface IInformacionNegocioQueryService
    {
        Task<InformacionViewModel> BuildViewModelAsync(
            int? mes,
            int? anio,
            int top,
            CancellationToken cancellationToken = default);

        Task<CitasSemanaResponse> BuildCitasSemanaAsync(
            DateTime semana,
            CancellationToken cancellationToken = default);
    }
}
