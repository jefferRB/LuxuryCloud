namespace LuxuryApp.Services.Platform
{
    /// <summary>Vista segura de un usuario para la consola de plataforma (datos reales desde DB).</summary>
    public sealed record PlatformUserAdminDto(
        string UserId,
        string Email,
        string? Name,
        Guid TenantId,
        string TenantName,
        bool State,
        bool IsPlatformSuperAdmin,
        bool TenantActive,
        DateTimeOffset? LockoutEnd,
        IReadOnlyList<string> Roles);

    /// <summary>Resultado de validar si un usuario puede desactivarse.</summary>
    public sealed record DeactivationValidationResult(
        bool CanProceed,
        string? BlockReason,
        bool AlreadyInTargetState)
    {
        public static DeactivationValidationResult Ok() => new(true, null, false);
        public static DeactivationValidationResult Blocked(string reason) => new(false, reason, false);
        public static DeactivationValidationResult AlreadyDone() => new(true, null, true);
    }

    /// <summary>
    /// Comando para desactivar/reactivar. <see cref="ExpectedTenantId"/> es lo que el SuperAdmin
    /// creía (viene del formulario) y se contrasta contra el TenantId real del usuario en DB para
    /// frenar IDOR. La operación SIEMPRE actúa sobre el usuario identificado por <see cref="UserId"/>
    /// cargado desde la base; nunca se confía en el tenant del cliente como fuente de verdad.
    /// </summary>
    public sealed record DeactivatePlatformUserCommand
    {
        public required string UserId { get; init; }
        public required Guid ExpectedTenantId { get; init; }
        public required string CurrentSuperAdminId { get; init; }
        public required string CurrentPassword { get; init; }
        public string? ConfirmationEmail { get; init; }
        public string? ConfirmationTenantName { get; init; }
        public required string Reason { get; init; }
    }

    public sealed record PlatformUserActionResult(bool Success, string Message)
    {
        public static PlatformUserActionResult Ok(string message) => new(true, message);
        public static PlatformUserActionResult Fail(string message) => new(false, message);
    }

    public interface IPlatformUserAdminService
    {
        Task<PlatformUserAdminDto?> GetUserForAdminAsync(string userId, CancellationToken cancellationToken = default);

        Task<DeactivationValidationResult> ValidateCanDeactivateAsync(
            string targetUserId,
            string currentSuperAdminId,
            Guid expectedTenantId,
            CancellationToken cancellationToken = default);

        Task<PlatformUserActionResult> DeactivateUserAsync(DeactivatePlatformUserCommand command, CancellationToken cancellationToken = default);

        Task<PlatformUserActionResult> ReactivateUserAsync(DeactivatePlatformUserCommand command, CancellationToken cancellationToken = default);
    }
}
