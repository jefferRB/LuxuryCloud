using LuxuryApp.Models.Funcionarios;

namespace LuxuryApp.Services.Funcionarios
{
    /// <summary>
    /// Lectura de datos para el portal del funcionario. Todas las consultas filtran por
    /// TenantId (global query filter) y además por FuncionarioId en backend. Nunca se
    /// confía en un FuncionarioId proveniente del navegador.
    /// </summary>
    public interface IFuncionarioPortalQueryService
    {
        /// <summary>Resuelve y valida el funcionario del portal. Null si no existe en el tenant.</summary>
        Task<PortalFuncionario?> ResolverFuncionarioAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default);

        Task<MiPanelViewModel> ObtenerPanelAsync(
            int funcionarioId,
            bool puedeRegistrarCobros,
            CancellationToken cancellationToken = default);

        Task<MisGananciasViewModel> ObtenerGananciasAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default);

        Task<MisGananciasViewModel> ObtenerGananciasAsync(
            int funcionarioId,
            DateTime? semanaAnchor,
            DateTime? mesAnchor,
            int mesesEvolucion,
            CancellationToken cancellationToken = default);

        Task<MisPagosViewModel> ObtenerPagosAsync(
            int funcionarioId,
            int pagina,
            CancellationToken cancellationToken = default);

        Task<MiCalendarioViewModel> ObtenerCalendarioAsync(
            int funcionarioId,
            DateTime fecha,
            string rango,
            bool puedeCrearCitas,
            bool puedeEditarCitas,
            bool puedeCancelarCitas,
            bool puedeRegistrarCobros,
            CancellationToken cancellationToken = default);

        /// <summary>Citas del funcionario para un día (vista diaria por horas del portal).</summary>
        Task<IReadOnlyList<PortalCitaItem>> ObtenerCitasDiaAsync(
            int funcionarioId,
            DateTime fecha,
            CancellationToken cancellationToken = default);

        /// <summary>Control de citas y cobros del funcionario para un rango (dia/semana/mes).</summary>
        Task<PortalControlCitas> ObtenerControlAsync(
            int funcionarioId,
            DateTime fecha,
            string rango,
            CancellationToken cancellationToken = default);

        Task<MisCobrosViewModel> ObtenerCobrosAsync(
            int funcionarioId,
            int pagina,
            bool puedeRegistrarCobros,
            CancellationToken cancellationToken = default);

        Task<MisCobrosViewModel> ObtenerCobrosAsync(
            int funcionarioId,
            int pagina,
            string rango,
            string? metodo,
            string? origen,
            bool puedeRegistrarCobros,
            bool puedeRegistrarManual,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resuelve una cita concreta del funcionario para registrar su cobro.
        /// Null si la cita no existe, no es del funcionario o no es del tenant actual.
        /// </summary>
        Task<PortalCitaItem?> ObtenerCitaCobrableAsync(
            int funcionarioId,
            int citaId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Autocompletado de clientes del tenant actual (mín. 3 caracteres). Tenant-safe.
        /// Los clientes son compartidos por el negocio, no por funcionario.
        /// </summary>
        Task<IReadOnlyList<PortalClienteOption>> BuscarClientesAsync(
            string? term,
            CancellationToken cancellationToken = default);

        /// <summary>Verifica que un cliente exista en el tenant actual (para cobro manual).</summary>
        Task<bool> ClienteExisteAsync(int clienteId, CancellationToken cancellationToken = default);
    }
}
