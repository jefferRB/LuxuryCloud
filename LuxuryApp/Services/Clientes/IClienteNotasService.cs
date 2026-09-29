using LuxuryApp.Models.DataBase;

namespace LuxuryApp.Services.Clientes
{
    /// <summary>
    /// Notas de servicio del cliente. Se almacenan en <see cref="ClienteServicioRealizado"/>,
    /// la entidad que ya existía para esto; no se creó ninguna entidad paralela.
    /// </summary>
    public interface IClienteNotasService
    {
        /// <summary>Longitud máxima de una nota; coincide con la columna del modelo.</summary>
        const int NotaMaxLength = 500;

        /// <summary>Notas del cliente en el tenant actual, de la más reciente a la más antigua.</summary>
        Task<IReadOnlyList<ClienteServicioRealizadoItemViewModel>> GetNotasAsync(
            int clienteId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Registra una nota. Valida en el servidor que el cliente exista dentro del tenant
        /// actual y que el texto sea válido; nunca confía en lo que envía el navegador.
        /// </summary>
        Task<ClienteNotaResultado> AgregarNotaAsync(
            int clienteId,
            string? texto,
            int? funcionarioId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Reemplaza el texto de una nota. La nota se busca acotada por tenant Y por cliente:
        /// un id de otra cuenta o de otro cliente no se encuentra, así que no hay IDOR.
        /// </summary>
        Task<ClienteNotaResultado> ActualizarNotaAsync(
            int clienteId,
            int notaId,
            string? texto,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Elimina una nota, acotada por tenant y cliente igual que la actualización. Si ya no
        /// existe devuelve un resultado limpio, nunca una excepción.
        /// </summary>
        Task<ClienteNotaResultado> EliminarNotaAsync(
            int clienteId,
            int notaId,
            CancellationToken cancellationToken = default);
    }
}
