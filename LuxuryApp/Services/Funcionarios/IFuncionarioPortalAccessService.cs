using LuxuryApp.Models.Funcionarios;

namespace LuxuryApp.Services.Funcionarios
{
    /// <summary>
    /// Gestiona las cuentas de acceso al portal de los funcionarios del tenant actual.
    /// Todas las operaciones son tenant-safe: el funcionario debe pertenecer al tenant
    /// del administrador autenticado.
    /// </summary>
    public interface IFuncionarioPortalAccessService
    {
        /// <summary>Estado de acceso del funcionario para mostrar en la UI.</summary>
        Task<FuncionarioAccesoViewModel> ObtenerEstadoAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default);

        /// <summary>Habilita el acceso por primera vez creando la cuenta del funcionario.</summary>
        Task<FuncionarioAccesoResultado> ActivarAccesoAsync(
            int funcionarioId,
            string email,
            FuncionarioAccesoCredencialModo modo,
            string? contrasenaTemporal,
            CancellationToken cancellationToken = default);

        /// <summary>Bloquea el inicio de sesión del funcionario sin borrar nada.</summary>
        Task<FuncionarioAccesoResultado> DesactivarAccesoAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default);

        /// <summary>Reactiva el acceso de un funcionario previamente bloqueado.</summary>
        Task<FuncionarioAccesoResultado> ReactivarAccesoAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default);

        /// <summary>Genera un token para reenviar invitación / enlace de cambio de contraseña.</summary>
        Task<FuncionarioAccesoResultado> GenerarEnlaceInvitacionAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default);

        /// <summary>Cambia el correo de acceso del funcionario validando unicidad.</summary>
        Task<FuncionarioAccesoResultado> CambiarCorreoAsync(
            int funcionarioId,
            string nuevoEmail,
            CancellationToken cancellationToken = default);
    }
}
