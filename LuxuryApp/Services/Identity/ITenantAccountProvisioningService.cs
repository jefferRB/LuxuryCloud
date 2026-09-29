using LuxuryApp.Models.Identity;

namespace LuxuryApp.Services.Identity
{
    /// <summary>Cómo se define la contraseña al crear una cuenta de acceso.</summary>
    public enum TenantAccountCredentialMode
    {
        /// <summary>Contraseña aleatoria + enlace por correo para que la persona defina la suya.</summary>
        Invitacion = 0,

        /// <summary>El administrador escribe una contraseña temporal.</summary>
        ContrasenaTemporal = 1
    }

    /// <summary>Datos para crear una cuenta de acceso dentro de un tenant.</summary>
    public sealed record TenantAccountRequest
    {
        public required string Email { get; init; }

        public required string DisplayName { get; init; }

        public string? Telefono { get; init; }

        public required Guid TenantId { get; init; }

        /// <summary>Rol de aplicación que recibe la cuenta (Funcionario, Asociado…).</summary>
        public required string Role { get; init; }

        public TenantAccountCredentialMode Modo { get; init; }

        public string? ContrasenaTemporal { get; init; }

        /// <summary>
        /// Ajustes extra sobre la entidad antes de crearla (por ejemplo, <c>FuncionarioId</c>).
        /// Se ejecuta antes de <c>CreateAsync</c> de Identity.
        /// </summary>
        public Action<AppUsuario>? Configure { get; init; }
    }

    /// <summary>Resultado de una operación sobre una cuenta de acceso.</summary>
    public sealed record TenantAccountResult
    {
        public bool Exitoso { get; init; }

        public IReadOnlyList<string> Errores { get; init; } = Array.Empty<string>();

        public string? UserId { get; init; }

        public string? Email { get; init; }

        /// <summary>
        /// Token de restablecimiento ya codificado (Base64Url) cuando hay que enviar invitación.
        /// Null si la contraseña la definió el administrador.
        /// </summary>
        public string? EnlaceTokenCodificado { get; init; }

        public static TenantAccountResult Falla(params string[] errores) =>
            new() { Exitoso = false, Errores = errores };

        public static TenantAccountResult Ok(string userId, string email, string? token = null) =>
            new() { Exitoso = true, UserId = userId, Email = email, EnlaceTokenCodificado = token };
    }

    /// <summary>
    /// Infraestructura COMPARTIDA de cuentas de acceso del tenant. La usan el portal de
    /// funcionarios y el módulo de asociados: creación de la cuenta Identity, asignación de rol,
    /// bloqueo/reactivación, tokens de invitación y cambio de correo viven acá una sola vez.
    ///
    /// <para>
    /// Este servicio NO conoce funcionarios ni asociados: recibe el tenant y el rol, y deja que el
    /// llamador enlace su propia entidad dentro de la misma transacción
    /// (<c>onCreatedInTransaction</c>). Así no existen dos implementaciones de Identity que puedan
    /// divergir en validaciones, mensajes o seguridad.
    /// </para>
    /// </summary>
    public interface ITenantAccountProvisioningService
    {
        /// <summary>True si el texto tiene forma de correo electrónico.</summary>
        bool EsEmailValido(string? email);

        /// <summary>
        /// Crea la cuenta y le asigna el rol dentro de una transacción. Si
        /// <paramref name="onCreatedInTransaction"/> devuelve false, se revierte todo: nunca queda
        /// un usuario Identity huérfano sin su entidad de negocio.
        /// </summary>
        Task<TenantAccountResult> CrearCuentaAsync(
            TenantAccountRequest request,
            Func<AppUsuario, CancellationToken, Task<bool>> onCreatedInTransaction,
            CancellationToken cancellationToken = default);

        /// <summary>Impide iniciar sesión de inmediato (State=false + invalidación de sesiones).</summary>
        Task<TenantAccountResult> BloquearAsync(AppUsuario usuario, CancellationToken cancellationToken = default);

        /// <summary>Devuelve el acceso y limpia cualquier bloqueo por intentos fallidos.</summary>
        Task<TenantAccountResult> ReactivarAsync(AppUsuario usuario, CancellationToken cancellationToken = default);

        /// <summary>Genera un token de definición/restablecimiento de contraseña ya codificado.</summary>
        Task<string> GenerarTokenContrasenaAsync(AppUsuario usuario);

        /// <summary>Cambia el correo de acceso validando formato y unicidad global.</summary>
        Task<TenantAccountResult> CambiarCorreoAsync(
            AppUsuario usuario,
            string nuevoEmail,
            CancellationToken cancellationToken = default);

        /// <summary>True si el correo ya pertenece a otra cuenta distinta de la indicada.</summary>
        Task<bool> CorreoEnUsoAsync(string email, string? excluirUserId = null);
    }
}
