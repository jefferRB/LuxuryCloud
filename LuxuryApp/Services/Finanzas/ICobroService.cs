using LuxuryApp.Models.Finanzas;

namespace LuxuryApp.Services.Finanzas
{
    public interface ICobroService
    {
        /// <summary>Registra el cobro y devuelve el Id del cobro creado.</summary>
        Task<int> RegistrarAsync(CobroCreateRequest request, CancellationToken cancellationToken = default);
        Task<bool> ActualizarAsync(CobroUpdateRequest request, CancellationToken cancellationToken = default);
        Task<bool> EliminarAsync(int idCobro, CancellationToken cancellationToken = default);
    }
}
