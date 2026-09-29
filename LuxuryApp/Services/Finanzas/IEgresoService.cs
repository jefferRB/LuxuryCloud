using LuxuryApp.Models.Finanzas;

namespace LuxuryApp.Services.Finanzas
{
    public interface IEgresoService
    {
        Task RegistrarAsync(EgresoCreateRequest request, CancellationToken cancellationToken = default);
        Task<bool> ActualizarAsync(EgresoUpdateRequest request, CancellationToken cancellationToken = default);
        Task<bool> EliminarAsync(int idEgreso, CancellationToken cancellationToken = default);
    }
}
