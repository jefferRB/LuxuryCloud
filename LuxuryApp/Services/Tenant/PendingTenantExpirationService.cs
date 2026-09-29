using System.Text.Json;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Tenant
{
    public sealed record PendingTenantExpirationResult(
        bool Enabled,
        int ExpiredCount,
        IReadOnlyList<Guid> TenantIds);

    public interface IPendingTenantExpirationService
    {
        Task<PendingTenantExpirationResult> ExpirePendingTenantsAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Soft-expira registros publicos que quedaron pendientes de verificacion. No borra datos,
    /// no llama proveedores y solo actua sobre tenants PendingVerification sin pago ni actividad.
    /// </summary>
    public sealed class PendingTenantExpirationService : IPendingTenantExpirationService
    {
        private const string SystemActor = "system:pending-tenant-expiration";

        private readonly ApplicationDbContext _context;
        private readonly IOptionsMonitor<RegistrationSecurityOptions> _options;
        private readonly ITenantCommercialAccessCache _accessCache;
        private readonly IPlatformAuditService _auditService;
        private readonly ILogger<PendingTenantExpirationService> _logger;

        public PendingTenantExpirationService(
            ApplicationDbContext context,
            IOptionsMonitor<RegistrationSecurityOptions> options,
            ITenantCommercialAccessCache accessCache,
            IPlatformAuditService auditService,
            ILogger<PendingTenantExpirationService> logger)
        {
            _context = context;
            _options = options;
            _accessCache = accessCache;
            _auditService = auditService;
            _logger = logger;
        }

        public async Task<PendingTenantExpirationResult> ExpirePendingTenantsAsync(
            CancellationToken cancellationToken = default)
        {
            var options = _options.CurrentValue;
            if (!options.ExpirePendingTenantsEnabled || options.PendingTenantExpirationDays <= 0)
            {
                return new PendingTenantExpirationResult(false, 0, Array.Empty<Guid>());
            }

            var nowUtc = DateTime.UtcNow;
            var cutoffUtc = nowUtc.AddDays(-options.PendingTenantExpirationDays);

            var candidateIds = await _context.Tenants
                .IgnoreQueryFilters()
                .Where(tenant =>
                    tenant.Activo &&
                    tenant.CommercialAccessMode == TenantCommercialAccessMode.PendingVerification &&
                    tenant.FechaCreacion <= cutoffUtc)
                .Where(tenant => !_context.Users
                    .IgnoreQueryFilters()
                    .Any(user => user.TenantId == tenant.Id && user.EmailConfirmed))
                .Where(tenant => !_context.PagosSuscripcion
                    .IgnoreQueryFilters()
                    .Any(payment => payment.TenantId == tenant.Id && payment.Estado == EstadoPagoProveedor.Confirmado))
                .Where(tenant => !_context.Citas
                    .IgnoreQueryFilters()
                    .Any(cita => cita.TenantId == tenant.Id))
                .Where(tenant => !_context.Cobros
                    .IgnoreQueryFilters()
                    .Any(cobro => cobro.TenantId == tenant.Id))
                .Where(tenant => !_context.BookingRequests
                    .IgnoreQueryFilters()
                    .Any(request => request.TenantId == tenant.Id))
                .Select(tenant => tenant.Id)
                .ToListAsync(cancellationToken);

            if (candidateIds.Count == 0)
            {
                return new PendingTenantExpirationResult(true, 0, Array.Empty<Guid>());
            }

            var tenants = await _context.Tenants
                .IgnoreQueryFilters()
                .Where(tenant => candidateIds.Contains(tenant.Id))
                .ToListAsync(cancellationToken);

            var users = await _context.Users
                .IgnoreQueryFilters()
                .Where(user => candidateIds.Contains(user.TenantId))
                .ToListAsync(cancellationToken);

            var reason = $"Registro pendiente expirado automaticamente tras {options.PendingTenantExpirationDays} dias sin verificacion/pago/actividad.";

            foreach (var tenant in tenants)
            {
                var before = new
                {
                    tenant.Activo,
                    Mode = tenant.CommercialAccessMode.ToString(),
                    tenant.CommercialNotes
                };

                tenant.Activo = false;
                tenant.CommercialNotes = reason;
                tenant.CommercialUpdatedUtc = nowUtc;
                tenant.CommercialUpdatedByUserId = SystemActor;

                foreach (var user in users.Where(user => user.TenantId == tenant.Id))
                {
                    user.State = false;
                    user.SecurityStamp = Guid.NewGuid().ToString("N");
                }

                await _auditService.TryLogAsync(new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.TenantPendingRegistrationExpired,
                    EntityType = PlatformAuditEntityTypes.Tenant,
                    EntityId = tenant.Id.ToString(),
                    TenantId = tenant.Id,
                    TenantName = tenant.Nombre,
                    BeforeJson = JsonSerializer.Serialize(before),
                    AfterJson = JsonSerializer.Serialize(new
                    {
                        Activo = false,
                        Mode = tenant.CommercialAccessMode.ToString(),
                        tenant.CommercialNotes,
                        DisabledUsers = users.Count(user => user.TenantId == tenant.Id)
                    }),
                    Reason = reason
                }, cancellationToken);
            }

            await _context.SaveChangesAsync(cancellationToken);

            foreach (var tenantId in candidateIds)
            {
                _accessCache.Invalidate(tenantId);
            }

            _logger.LogInformation(
                "Expirados {ExpiredCount} tenant(s) pendientes de verificacion. CutoffUtc {CutoffUtc}.",
                candidateIds.Count,
                cutoffUtc);

            return new PendingTenantExpirationResult(true, candidateIds.Count, candidateIds);
        }
    }
}
