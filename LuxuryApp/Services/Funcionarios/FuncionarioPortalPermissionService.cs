using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Services.BusinessTime;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Funcionarios
{
    public sealed class FuncionarioPortalPermissionService : IFuncionarioPortalPermissionService
    {
        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ILogger<FuncionarioPortalPermissionService> _logger;

        public FuncionarioPortalPermissionService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider,
            IHttpContextAccessor httpContextAccessor,
            ILogger<FuncionarioPortalPermissionService> logger)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
            _httpContextAccessor = httpContextAccessor;
            _logger = logger;
        }

        public async Task<FuncionarioPortalPermisosSet> ObtenerAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default)
        {
            // Cache por request para no repetir la consulta entre controlador y layout.
            var cacheKey = $"__portal_perms_{funcionarioId}";
            var items = _httpContextAccessor.HttpContext?.Items;
            if (items is not null && items.TryGetValue(cacheKey, out var cached) &&
                cached is FuncionarioPortalPermisosSet cachedSet)
            {
                return cachedSet;
            }

            // Tenant-safe por el global query filter.
            var overrides = await _context.FuncionarioPortalPermisos
                .AsNoTracking()
                .Where(p => p.FuncionarioId == funcionarioId)
                .Select(p => new { p.Permiso, p.Permitido })
                .ToListAsync(cancellationToken);

            var valores = new Dictionary<string, bool>(FuncionarioPortalPermissions.Defaults, StringComparer.Ordinal);
            foreach (var item in overrides)
            {
                if (FuncionarioPortalPermissions.EsPermisoValido(item.Permiso))
                {
                    valores[item.Permiso] = item.Permitido;
                }
            }

            var set = new FuncionarioPortalPermisosSet(valores);
            items?.TryAdd(cacheKey, set);
            return set;
        }

        public async Task<bool> TienePermisoAsync(
            int funcionarioId,
            string permiso,
            CancellationToken cancellationToken = default)
        {
            if (!FuncionarioPortalPermissions.EsPermisoValido(permiso))
            {
                return false;
            }

            var set = await ObtenerAsync(funcionarioId, cancellationToken);
            return set.Tiene(permiso);
        }

        public async Task CrearDefaultsAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default)
        {
            // Valida que el funcionario pertenece al tenant actual.
            var funcionario = await _context.Funcionarios
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.IdFuncionario == funcionarioId, cancellationToken);

            if (funcionario is null)
            {
                return;
            }

            var existentes = await _context.FuncionarioPortalPermisos
                .Where(p => p.FuncionarioId == funcionarioId)
                .Select(p => p.Permiso)
                .ToListAsync(cancellationToken);

            var existentesSet = existentes.ToHashSet(StringComparer.Ordinal);
            var ahora = _businessDateTimeProvider.Now();
            var creados = 0;

            foreach (var permiso in FuncionarioPortalPermissions.Todos)
            {
                if (existentesSet.Contains(permiso))
                {
                    continue;
                }

                _context.FuncionarioPortalPermisos.Add(new FuncionarioPortalPermiso
                {
                    FuncionarioId = funcionarioId,
                    Permiso = permiso,
                    Permitido = FuncionarioPortalPermissions.DefaultDe(permiso),
                    CreatedAtUtc = ahora,
                    UpdatedAtUtc = ahora
                });
                creados++;
            }

            if (creados > 0)
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
        }

        public async Task<bool> GuardarAsync(
            int funcionarioId,
            IReadOnlyDictionary<string, bool> valores,
            CancellationToken cancellationToken = default)
        {
            // Valida tenant: el funcionario debe pertenecer al tenant actual.
            var funcionario = await _context.Funcionarios
                .AsNoTracking()
                .FirstOrDefaultAsync(f => f.IdFuncionario == funcionarioId, cancellationToken);

            if (funcionario is null)
            {
                return false;
            }

            var existentes = await _context.FuncionarioPortalPermisos
                .Where(p => p.FuncionarioId == funcionarioId)
                .ToListAsync(cancellationToken);

            var porPermiso = existentes.ToDictionary(p => p.Permiso, StringComparer.Ordinal);
            var ahora = _businessDateTimeProvider.Now();

            foreach (var permiso in FuncionarioPortalPermissions.Todos)
            {
                // VerMiPanel siempre activo: el portal necesita una landing.
                var valor = permiso == FuncionarioPortalPermissions.VerMiPanel
                    ? true
                    : valores.TryGetValue(permiso, out var enviado) && enviado;

                if (porPermiso.TryGetValue(permiso, out var fila))
                {
                    if (fila.Permitido != valor)
                    {
                        fila.Permitido = valor;
                        fila.UpdatedAtUtc = ahora;
                    }
                }
                else
                {
                    _context.FuncionarioPortalPermisos.Add(new FuncionarioPortalPermiso
                    {
                        FuncionarioId = funcionarioId,
                        Permiso = permiso,
                        Permitido = valor,
                        CreatedAtUtc = ahora,
                        UpdatedAtUtc = ahora
                    });
                }
            }

            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Permisos de portal actualizados. TenantId {TenantId}. FuncionarioId {FuncionarioId}.",
                funcionario.TenantId,
                funcionarioId);

            return true;
        }
    }
}
