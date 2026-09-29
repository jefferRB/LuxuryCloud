using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Identity;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Tenant
{
    /// <summary>
    /// Implementacion determinista del contacto principal del tenant.
    ///
    /// Contexto del bug que resuelve: el listado de plataforma, la ficha del tenant y cuatro
    /// servicios de cobro resolvian "el correo del tenant" con <c>OrderBy(u =&gt; u.Email).First()</c>.
    /// En un tenant real eso hacia ganar la cuenta de un colaborador (<c>d...@</c>, rol Funcionario) sobre
    /// la del dueño (<c>l...@</c>, Registrado + Administrador) solo porque "d" &lt; "l".
    ///
    /// Orden de preferencia (nunca un Funcionario por encima de un Administrador):
    ///   1. Administrador + Registrado, activo.
    ///   2. Administrador activo.
    ///   3. Administrador inactivo (con advertencia).
    ///   4. Cuenta activa que no es de funcionario (con advertencia).
    ///   5. Cuenta de funcionario, ultimo recurso (con advertencia).
    ///
    /// Nota de diseño: <c>AspNetUsers</c> no tiene fecha de creacion ni marca de owner, asi que
    /// "el mas antiguo" no es derivable sin migracion. El desempate dentro de cada nivel es
    /// estable (Registrado, correo confirmado, correo alfabetico) y cuando hay mas de un
    /// administrador se emite una advertencia operativa para que plataforma lo vea en vez de
    /// asumir en silencio.
    /// </summary>
    public sealed class TenantOwnerResolver : ITenantOwnerResolver
    {
        private readonly ApplicationDbContext _context;

        public TenantOwnerResolver(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<TenantOwnerResolution> ResolveAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            if (tenantId == Guid.Empty)
            {
                return TenantOwnerResolution.Empty(tenantId);
            }

            var batch = await ResolveBatchAsync([tenantId], cancellationToken);
            return batch.TryGetValue(tenantId, out var resolution)
                ? resolution
                : TenantOwnerResolution.Empty(tenantId);
        }

        public async Task<Dictionary<Guid, TenantOwnerResolution>> ResolveBatchAsync(
            IReadOnlyList<Guid> tenantIds,
            CancellationToken cancellationToken = default)
        {
            var wanted = tenantIds.Where(id => id != Guid.Empty).Distinct().ToList();
            if (wanted.Count == 0)
            {
                return new Dictionary<Guid, TenantOwnerResolution>();
            }

            // AppUsuario NO es ITenantEntity: no tiene query filter global, se filtra explicito
            // por TenantId. Cross-tenant intencional y seguro para plataforma.
            var users = await _context.Users
                .AsNoTracking()
                .Where(user => wanted.Contains(user.TenantId))
                .Select(user => new
                {
                    user.Id,
                    user.TenantId,
                    user.Email,
                    user.Name,
                    user.State,
                    user.EmailConfirmed,
                    user.IsPlatformSuperAdmin,
                    user.FuncionarioId
                })
                .ToListAsync(cancellationToken);

            var rolesByUser = await LoadRolesAsync(
                users.Select(user => user.Id).ToList(),
                cancellationToken);

            var result = new Dictionary<Guid, TenantOwnerResolution>(wanted.Count);
            var usersByTenant = users.GroupBy(user => user.TenantId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var tenantId in wanted)
            {
                if (!usersByTenant.TryGetValue(tenantId, out var tenantUsers) || tenantUsers.Count == 0)
                {
                    result[tenantId] = TenantOwnerResolution.Empty(tenantId);
                    continue;
                }

                var summaries = tenantUsers
                    .Select(user =>
                    {
                        var roles = rolesByUser.TryGetValue(user.Id, out var list)
                            ? list
                            : (IReadOnlyList<string>)Array.Empty<string>();

                        return new TenantUserSummary
                        {
                            UserId = user.Id,
                            Email = user.Email,
                            Name = user.Name,
                            State = user.State,
                            EmailConfirmed = user.EmailConfirmed,
                            IsPlatformSuperAdmin = user.IsPlatformSuperAdmin,
                            IsRegistrado = HasRole(roles, AppRoles.Registrado),
                            Kind = ClassifyKind(roles, user.FuncionarioId),
                            Roles = roles
                        };
                    })
                    .ToList();

                result[tenantId] = Build(tenantId, summaries);
            }

            return result;
        }

        public async Task<string?> ResolveOwnerEmailAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            var resolution = await ResolveAsync(tenantId, cancellationToken);
            return string.IsNullOrWhiteSpace(resolution.OwnerEmail) ? null : resolution.OwnerEmail;
        }

        /// <summary>
        /// Un Administrador es Administrador aunque tenga tambien rol Funcionario o FuncionarioId:
        /// la clasificacion privilegia el rol administrativo y la mezcla se reporta como advertencia
        /// (AppRoles documenta que no deben combinarse).
        /// </summary>
        private static TenantUserKind ClassifyKind(IReadOnlyList<string> roles, int? funcionarioId)
        {
            if (HasRole(roles, AppRoles.Administrador))
            {
                return TenantUserKind.Administrador;
            }

            if (funcionarioId.HasValue || HasRole(roles, AppRoles.Funcionario))
            {
                return TenantUserKind.Funcionario;
            }

            return TenantUserKind.Otro;
        }

        private static bool HasRole(IReadOnlyList<string> roles, string role) =>
            roles.Any(current => string.Equals(current, role, StringComparison.OrdinalIgnoreCase));

        private static TenantOwnerResolution Build(Guid tenantId, List<TenantUserSummary> all)
        {
            var warnings = new List<string>();

            // Las cuentas de super admin de plataforma no representan al negocio.
            var candidates = all.Where(user => !user.IsPlatformSuperAdmin).ToList();
            if (candidates.Count == 0)
            {
                return new TenantOwnerResolution
                {
                    TenantId = tenantId,
                    Source = TenantOwnerSource.None,
                    AllUsers = all,
                    Warnings = new[] { "El tenant solo tiene cuentas de super admin de plataforma; no hay contacto del negocio." }
                };
            }

            var admins = candidates.Where(user => user.IsAdmin).ToList();
            var funcionarios = candidates.Where(user => user.IsFuncionario).ToList();
            var others = candidates.Where(user => user.Kind == TenantUserKind.Otro).ToList();

            TenantUserSummary? owner;
            TenantOwnerSource source;

            var activeAdmins = admins.Where(user => user.State).ToList();
            if (activeAdmins.Count > 0)
            {
                owner = Best(activeAdmins);
                source = owner!.IsRegistrado ? TenantOwnerSource.AdminRegistrado : TenantOwnerSource.Administrador;
            }
            else if (admins.Count > 0)
            {
                owner = Best(admins);
                source = TenantOwnerSource.Administrador;
                warnings.Add("El unico administrador del tenant esta desactivado.");
            }
            else
            {
                warnings.Add("El tenant no tiene ninguna cuenta con rol Administrador.");

                var activeNonFuncionario = others.Where(user => user.State).ToList();
                if (activeNonFuncionario.Count > 0)
                {
                    owner = Best(activeNonFuncionario);
                    source = TenantOwnerSource.FallbackUsuarioActivo;
                }
                else
                {
                    var pool = funcionarios.Where(user => user.State).ToList();
                    if (pool.Count == 0)
                    {
                        pool = candidates;
                    }

                    owner = Best(pool);
                    source = owner?.IsFuncionario == true
                        ? TenantOwnerSource.FallbackFuncionario
                        : TenantOwnerSource.FallbackUsuarioActivo;

                    if (source == TenantOwnerSource.FallbackFuncionario)
                    {
                        warnings.Add(
                            "El contacto mostrado es una cuenta de FUNCIONARIO porque el tenant no tiene administrador. " +
                            "Revisar antes de usarlo para cobros o notificaciones.");
                    }
                }
            }

            if (admins.Count > 1)
            {
                warnings.Add($"El tenant tiene {admins.Count} administradores; se muestra el principal resuelto por regla.");
            }

            if (owner is not null && string.IsNullOrWhiteSpace(owner.Email))
            {
                warnings.Add("El contacto principal no tiene correo registrado.");
            }

            var adminAlsoFuncionario = admins
                .Where(user => user.Roles.Any(role => string.Equals(role, AppRoles.Funcionario, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (adminAlsoFuncionario.Count > 0)
            {
                warnings.Add(
                    "Hay cuentas con rol Administrador y Funcionario a la vez " +
                    $"({string.Join(", ", adminAlsoFuncionario.Select(user => user.Email ?? user.UserId))}). Los roles no deben combinarse.");
            }

            return new TenantOwnerResolution
            {
                TenantId = tenantId,
                Owner = owner,
                Source = source,
                AdditionalAdmins = admins
                    .Where(user => !ReferenceEquals(user, owner))
                    .OrderBy(user => user.Email, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Funcionarios = funcionarios
                    .OrderBy(user => user.Email, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                OtherUsers = others
                    .Where(user => !ReferenceEquals(user, owner))
                    .OrderBy(user => user.Email, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                AllUsers = all,
                Warnings = warnings
            };
        }

        /// <summary>
        /// Desempate estable dentro de un mismo nivel de preferencia. No hay fecha de creacion en
        /// AspNetUsers, asi que el correo alfabetico se usa solo como ultimo criterio para que el
        /// resultado sea reproducible entre ejecuciones.
        /// </summary>
        private static TenantUserSummary? Best(IEnumerable<TenantUserSummary> pool) =>
            pool.OrderByDescending(user => user.IsRegistrado)
                .ThenByDescending(user => user.State)
                .ThenByDescending(user => user.EmailConfirmed)
                .ThenBy(user => user.Email, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

        private async Task<Dictionary<string, IReadOnlyList<string>>> LoadRolesAsync(
            List<string> userIds,
            CancellationToken cancellationToken)
        {
            if (userIds.Count == 0)
            {
                return new Dictionary<string, IReadOnlyList<string>>();
            }

            var rows = await _context.UserRoles
                .AsNoTracking()
                .Where(userRole => userIds.Contains(userRole.UserId))
                .Join(
                    _context.Roles.AsNoTracking(),
                    userRole => userRole.RoleId,
                    role => role.Id,
                    (userRole, role) => new { userRole.UserId, RoleName = role.Name })
                .ToListAsync(cancellationToken);

            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.RoleName))
                .GroupBy(row => row.UserId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group
                        .Select(row => row.RoleName!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
                        .ToList());
        }
    }
}
