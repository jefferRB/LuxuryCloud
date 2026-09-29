using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Platform;

namespace LuxuryApp.Tests.Support
{
    /// <summary>
    /// Bitácora en memoria: permite afirmar QUÉ se auditó sin depender de HttpContext ni de
    /// escrituras adicionales en el DbContext durante una operación bajo prueba.
    /// </summary>
    internal sealed class FakePlatformAuditService : IPlatformAuditService
    {
        public List<PlatformAuditEntry> Entries { get; } = new();

        public Task LogAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task TryLogAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default) =>
            LogAsync(entry, cancellationToken);

        public bool Contains(string action) =>
            Entries.Any(entry => string.Equals(entry.Action, action, StringComparison.Ordinal));

        public int Count(string action) =>
            Entries.Count(entry => string.Equals(entry.Action, action, StringComparison.Ordinal));

        public Task<IReadOnlyList<PlatformAuditLog>> GetRecentAsync(int take = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());

        public Task<IReadOnlyList<PlatformAuditLog>> GetByTenantAsync(Guid tenantId, int take = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());

        public Task<IReadOnlyList<PlatformAuditLog>> GetByUserAsync(string targetUserId, int take = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());

        public Task<int> CountActorFailuresSinceAsync(string actorUserId, DateTime sinceUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
