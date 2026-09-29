using LuxuryApp.Models.Asociados;

namespace LuxuryApp.Services.Asociados
{
    /// <summary>Resultado de una operación sobre el acceso de un asociado.</summary>
    public sealed record AssociateAccessResult
    {
        public bool Exitoso { get; init; }

        public IReadOnlyList<string> Errores { get; init; } = Array.Empty<string>();

        public string? UserId { get; init; }

        public string? Email { get; init; }

        /// <summary>Nombre del asociado, para el saludo del correo.</summary>
        public string? NombreParaCorreo { get; init; }

        /// <summary>Token de contraseña ya codificado cuando hay que enviar invitación.</summary>
        public string? EnlaceTokenCodificado { get; init; }

        /// <summary>True cuando el controlador debe enviar el correo de invitación.</summary>
        public bool RequiereCorreoInvitacion { get; init; }

        public static AssociateAccessResult Falla(params string[] errores) =>
            new() { Exitoso = false, Errores = errores };
    }

    /// <summary>
    /// Acceso al sistema de los asociados. Reutiliza
    /// <c>ITenantAccountProvisioningService</c> (misma infraestructura de Identity, invitaciones y
    /// bloqueo que usan los funcionarios); acá solo viven las reglas propias del asociado.
    ///
    /// <para>
    /// Un asociado puede existir perfectamente SIN acceso: es el caso normal de un inversionista
    /// que solo recibe estados de cuenta. En ese caso no se crea usuario ni se envía correo.
    /// </para>
    /// </summary>
    public interface IAssociateAccessService
    {
        Task<AssociateAccessViewModel> ObtenerEstadoAsync(
            int associateId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Crea la cuenta del asociado y le concede los permisos indicados en la MISMA transacción
        /// lógica: nunca queda una cuenta activa sin permisos ni permisos sin cuenta.
        /// </summary>
        Task<AssociateAccessResult> ActivarAccesoAsync(
            int associateId,
            string email,
            AssociateCredentialMode modo,
            string? contrasenaTemporal,
            IEnumerable<string> permisosIniciales,
            string? actorUserId,
            CancellationToken cancellationToken = default);

        /// <summary>Impide iniciar sesión sin borrar datos, participación ni estados de cuenta.</summary>
        Task<AssociateAccessResult> BloquearAccesoAsync(
            int associateId,
            string? actorUserId,
            CancellationToken cancellationToken = default);

        Task<AssociateAccessResult> ReactivarAccesoAsync(
            int associateId,
            string? actorUserId,
            CancellationToken cancellationToken = default);

        Task<AssociateAccessResult> GenerarEnlaceInvitacionAsync(
            int associateId,
            string? actorUserId,
            CancellationToken cancellationToken = default);

        Task<AssociateAccessResult> CambiarCorreoAsync(
            int associateId,
            string nuevoEmail,
            string? actorUserId,
            CancellationToken cancellationToken = default);
    }
}
