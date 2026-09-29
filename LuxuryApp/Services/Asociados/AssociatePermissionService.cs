using System.Security.Claims;
using LuxuryApp.Models.Asociados;
using LuxuryApp.Models.Platform;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Platform;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Asociados
{
    /// <summary>
    /// Resolución y persistencia de permisos de asociados.
    /// Ver <see cref="IAssociatePermissionService"/> para las reglas.
    /// </summary>
    public sealed class AssociatePermissionService : IAssociatePermissionService
    {
        private const string CacheKeyUsuarioActual = "__associate_permissions_current_user";

        private readonly ApplicationDbContext _context;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly IPlatformAuditService _auditService;
        private readonly ILogger<AssociatePermissionService> _logger;

        public AssociatePermissionService(
            ApplicationDbContext context,
            IHttpContextAccessor httpContextAccessor,
            IBusinessDateTimeProvider businessDateTimeProvider,
            IPlatformAuditService auditService,
            ILogger<AssociatePermissionService> logger)
        {
            _context = context;
            _httpContextAccessor = httpContextAccessor;
            _businessDateTimeProvider = businessDateTimeProvider;
            _auditService = auditService;
            _logger = logger;
        }

        public async Task<AssociatePermissionSet> ObtenerDelUsuarioActualAsync(
            CancellationToken cancellationToken = default)
        {
            var httpContext = _httpContextAccessor.HttpContext;
            var items = httpContext?.Items;

            if (items is not null &&
                items.TryGetValue(CacheKeyUsuarioActual, out var cached) &&
                cached is AssociatePermissionSet cachedSet)
            {
                return cachedSet;
            }

            var set = await ResolverDelUsuarioActualAsync(httpContext?.User, cancellationToken);
            items?.TryAdd(CacheKeyUsuarioActual, set);
            return set;
        }

        private async Task<AssociatePermissionSet> ResolverDelUsuarioActualAsync(
            ClaimsPrincipal? principal,
            CancellationToken cancellationToken)
        {
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return AssociatePermissionSet.Ninguno;
            }

            var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue(CustomClaimTypes.UserId);

            if (string.IsNullOrWhiteSpace(userId))
            {
                return AssociatePermissionSet.Ninguno;
            }

            // Una sola consulta: asociado + estado de su cuenta + permisos concedidos.
            // El filtro global de tenant ya acota a los asociados del negocio actual.
            var datos = await _context.Associates
                .AsNoTracking()
                .Where(associate => associate.AppUsuarioId == userId)
                .Select(associate => new
                {
                    associate.Id,
                    associate.Activo,
                    CuentaActiva = _context.Users
                        .Where(user => user.Id == associate.AppUsuarioId)
                        .Select(user => (bool?)user.State)
                        .FirstOrDefault(),
                    Permisos = associate.Permisos
                        .Where(permiso => permiso.Permitido)
                        .Select(permiso => permiso.Permiso)
                        .ToList()
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (datos is null)
            {
                return AssociatePermissionSet.Ninguno;
            }

            // Asociado inactivo o cuenta bloqueada = sin un solo permiso. Bloquear el acceso no
            // obliga a desmarcar permisos a mano.
            if (!datos.Activo || datos.CuentaActiva != true)
            {
                return AssociatePermissionSet.Ninguno;
            }

            return AssociatePermissionSet.Desde(datos.Permisos);
        }

        public async Task<AssociatePermissionSet> ObtenerDeAsociadoAsync(
            int associateId,
            CancellationToken cancellationToken = default)
        {
            var concedidos = await _context.AssociatePermissions
                .AsNoTracking()
                .Where(permiso => permiso.AssociateId == associateId && permiso.Permitido)
                .Select(permiso => permiso.Permiso)
                .ToListAsync(cancellationToken);

            return AssociatePermissionSet.Desde(concedidos);
        }

        public async Task<bool> GuardarAsync(
            int associateId,
            IEnumerable<string> permisosConcedidos,
            string? actorUserId,
            CancellationToken cancellationToken = default)
        {
            // Tenant-safe: si el asociado no es de este negocio, el filtro global no lo devuelve.
            var associate = await _context.Associates
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == associateId, cancellationToken);

            if (associate is null)
            {
                return false;
            }

            var deseados = permisosConcedidos
                .Where(AppPermissions.EsValido)
                .ToHashSet(StringComparer.Ordinal);

            var existentes = await _context.AssociatePermissions
                .Where(permiso => permiso.AssociateId == associateId)
                .ToListAsync(cancellationToken);

            var antes = existentes
                .Where(permiso => permiso.Permitido)
                .Select(permiso => permiso.Permiso)
                .OrderBy(permiso => permiso, StringComparer.Ordinal)
                .ToArray();

            var ahora = _businessDateTimeProvider.Now();
            var porPermiso = existentes.ToDictionary(permiso => permiso.Permiso, StringComparer.Ordinal);

            foreach (var permiso in AppPermissions.Todos)
            {
                var permitido = deseados.Contains(permiso);

                if (porPermiso.TryGetValue(permiso, out var fila))
                {
                    if (fila.Permitido != permitido)
                    {
                        fila.Permitido = permitido;
                        fila.UpdatedAtUtc = ahora;
                    }
                }
                else if (permitido)
                {
                    // Solo se materializan las concesiones: la ausencia de fila ya significa
                    // denegado, así no se llena la tabla de negaciones inútiles.
                    _context.AssociatePermissions.Add(new AssociatePermission
                    {
                        AssociateId = associateId,
                        Permiso = permiso,
                        Permitido = true,
                        CreatedAtUtc = ahora,
                        UpdatedAtUtc = ahora
                    });
                }
            }

            // Claves de catálogos anteriores que ya no existen: se apagan para que no queden
            // concesiones fantasma si el permiso vuelve a llamarse igual en el futuro.
            foreach (var huerfano in existentes.Where(permiso => !AppPermissions.EsValido(permiso.Permiso) && permiso.Permitido))
            {
                huerfano.Permitido = false;
                huerfano.UpdatedAtUtc = ahora;
            }

            await _context.SaveChangesAsync(cancellationToken);

            var despues = deseados.OrderBy(permiso => permiso, StringComparer.Ordinal).ToArray();

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.AssociatePermissionsUpdated,
                    EntityType = PlatformAuditEntityTypes.Associate,
                    EntityId = associateId.ToString(),
                    TenantId = associate.TenantId,
                    BeforeJson = System.Text.Json.JsonSerializer.Serialize(new { Permisos = antes }),
                    AfterJson = System.Text.Json.JsonSerializer.Serialize(new { Permisos = despues })
                },
                cancellationToken);

            _logger.LogInformation(
                "Permisos de asociado actualizados. TenantId {TenantId}. AssociateId {AssociateId}. Total {Total}.",
                associate.TenantId,
                associateId,
                despues.Length);

            return true;
        }

        public async Task LimpiarAsync(int associateId, CancellationToken cancellationToken = default)
        {
            var existentes = await _context.AssociatePermissions
                .Where(permiso => permiso.AssociateId == associateId)
                .ToListAsync(cancellationToken);

            if (existentes.Count == 0)
            {
                return;
            }

            _context.AssociatePermissions.RemoveRange(existentes);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
