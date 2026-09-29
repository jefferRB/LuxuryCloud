using LuxuryApp.Models.Platform;

namespace LuxuryApp.Services.Platform
{
    /// <summary>
    /// Datos de una entrada de auditoría. El actor (UserId, email), IP y UserAgent se
    /// resuelven dentro del servicio desde el <c>HttpContext</c> actual; el llamador solo
    /// describe la acción. La contraseña del SuperAdmin nunca forma parte de este registro.
    /// </summary>
    public sealed record PlatformAuditEntry
    {
        public required string Action { get; init; }
        public required string EntityType { get; init; }
        public string? EntityId { get; init; }
        public Guid? TenantId { get; init; }
        public string? TenantName { get; init; }
        public string? TargetUserId { get; init; }
        public string? TargetUserEmail { get; init; }
        public string? BeforeJson { get; init; }
        public string? AfterJson { get; init; }
        public string? Reason { get; init; }
    }

    public interface IPlatformAuditService
    {
        Task LogAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default);

        /// <summary>
        /// Igual que <see cref="LogAsync"/> pero nunca lanza: la bitácora no debe tumbar una
        /// acción que ya se ejecutó. Un fallo se registra en el log de la aplicación (S6)
        /// en vez de tragarse en silencio.
        /// </summary>
        Task TryLogAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<PlatformAuditLog>> GetRecentAsync(int take = 100, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<PlatformAuditLog>> GetByTenantAsync(Guid tenantId, int take = 100, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<PlatformAuditLog>> GetByUserAsync(string targetUserId, int take = 100, CancellationToken cancellationToken = default);

        /// <summary>
        /// Cuenta intentos fallidos de acción peligrosa del actor desde <paramref name="sinceUtc"/>.
        /// Se usa para frenar fuerza bruta sobre la contraseña del SuperAdmin.
        /// </summary>
        Task<int> CountActorFailuresSinceAsync(string actorUserId, DateTime sinceUtc, CancellationToken cancellationToken = default);
    }
}
