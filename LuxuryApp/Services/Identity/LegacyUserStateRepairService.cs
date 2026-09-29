using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Identity
{
    /// <summary>
    /// Corrige usuarios heredados que quedaron con State = false por la migracion
    /// que materializo el campo como NOT NULL. Hoy no existe un flujo legitimo que
    /// desactive usuarios, asi que mantenerlos bloqueados rompe accesos validos.
    /// </summary>
    public sealed class LegacyUserStateRepairService
    {
        private static readonly Guid EmptyTenantId = Guid.Empty;

        private readonly ApplicationDbContext _context;
        private readonly ILogger<LegacyUserStateRepairService> _logger;

        public LegacyUserStateRepairService(
            ApplicationDbContext context,
            ILogger<LegacyUserStateRepairService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public async Task<int> RepairAsync(CancellationToken cancellationToken = default)
        {
            var affectedUsers = await _context.Users
                .Where(user => !user.State && user.TenantId != EmptyTenantId)
                .Join(
                    _context.Tenants.Where(tenant => tenant.Activo),
                    user => user.TenantId,
                    tenant => tenant.Id,
                    (user, _) => user)
                .ToListAsync(cancellationToken);

            if (affectedUsers.Count == 0)
            {
                return 0;
            }

            foreach (var user in affectedUsers)
            {
                user.State = true;
            }

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Se reactivaron {Count} usuarios legacy con tenant valido que quedaron deshabilitados por migracion previa.",
                affectedUsers.Count);

            return affectedUsers.Count;
        }
    }
}
