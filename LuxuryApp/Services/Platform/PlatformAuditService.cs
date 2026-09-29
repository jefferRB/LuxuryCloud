using System.Security.Claims;
using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Platform
{
    public sealed class PlatformAuditService : IPlatformAuditService
    {
        private readonly ApplicationDbContext _context;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<PlatformAuditService> _logger;

        public PlatformAuditService(
            ApplicationDbContext context,
            IHttpContextAccessor httpContextAccessor,
            ILogger<PlatformAuditService> logger)
        {
            _context = context;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        public async Task TryLogAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default)
        {
            try
            {
                await LogAsync(entry, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Fallo al escribir la bitácora de plataforma. Action {Action}. EntityType {EntityType}. EntityId {EntityId}.",
                    entry.Action,
                    entry.EntityType,
                    entry.EntityId);
            }
        }

        public async Task LogAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default)
        {
            var http = _httpContextAccessor.HttpContext;
            var principal = http?.User;

            var actorUserId = principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal?.FindFirstValue(CustomClaimTypes.UserId)
                ?? "unknown";

            var actorEmail = principal?.FindFirstValue(ClaimTypes.Email)
                ?? principal?.FindFirstValue(CustomClaimTypes.UserName)
                ?? principal?.Identity?.Name
                ?? "unknown";

            var ip = http?.Connection?.RemoteIpAddress?.ToString();
            var userAgent = http?.Request?.Headers.UserAgent.ToString();

            var log = new PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = Truncate(actorUserId, 450) ?? string.Empty,
                ActorEmail = Truncate(actorEmail, 256) ?? string.Empty,
                Action = Truncate(entry.Action, 80) ?? string.Empty,
                EntityType = Truncate(entry.EntityType, 60) ?? string.Empty,
                EntityId = Truncate(entry.EntityId, 450),
                TenantId = entry.TenantId,
                TenantName = Truncate(entry.TenantName, 150),
                TargetUserId = Truncate(entry.TargetUserId, 450),
                TargetUserEmail = Truncate(entry.TargetUserEmail, 256),
                BeforeJson = entry.BeforeJson,
                AfterJson = entry.AfterJson,
                Reason = Truncate(entry.Reason, 500),
                IpAddress = Truncate(ip, 64),
                UserAgent = Truncate(userAgent, 512),
                CreatedAtUtc = DateTime.UtcNow
            };

            _context.PlatformAuditLogs.Add(log);
            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task<IReadOnlyList<PlatformAuditLog>> GetRecentAsync(int take = 100, CancellationToken cancellationToken = default) =>
            await _context.PlatformAuditLogs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .OrderByDescending(log => log.CreatedAtUtc)
                .Take(Clamp(take))
                .ToListAsync(cancellationToken);

        public async Task<IReadOnlyList<PlatformAuditLog>> GetByTenantAsync(Guid tenantId, int take = 100, CancellationToken cancellationToken = default) =>
            await _context.PlatformAuditLogs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(log => log.TenantId == tenantId)
                .OrderByDescending(log => log.CreatedAtUtc)
                .Take(Clamp(take))
                .ToListAsync(cancellationToken);

        public async Task<IReadOnlyList<PlatformAuditLog>> GetByUserAsync(string targetUserId, int take = 100, CancellationToken cancellationToken = default) =>
            await _context.PlatformAuditLogs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(log => log.TargetUserId == targetUserId)
                .OrderByDescending(log => log.CreatedAtUtc)
                .Take(Clamp(take))
                .ToListAsync(cancellationToken);

        public async Task<int> CountActorFailuresSinceAsync(string actorUserId, DateTime sinceUtc, CancellationToken cancellationToken = default) =>
            await _context.PlatformAuditLogs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .CountAsync(
                    log => log.ActorUserId == actorUserId &&
                           log.Action == PlatformAuditActions.DangerousActionPasswordFailed &&
                           log.CreatedAtUtc >= sinceUtc,
                    cancellationToken);

        private static int Clamp(int take) => take <= 0 ? 1 : Math.Min(take, 500);

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }
    }
}
