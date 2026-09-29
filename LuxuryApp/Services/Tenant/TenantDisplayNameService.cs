using System.Globalization;
using System.Security.Claims;
using System.Text;
using LuxuryApp.Models.Identity;
using LuxuryApp.Services.Identity;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Tenant
{
    public sealed class TenantDisplayNameService : ITenantDisplayNameService
    {
        public const string DefaultDisplayName = "LuxuryCloud";
        private const int MaxDisplayNameLength = 100;
        private const string ResolvedTenantItemKey = "__resolved_tenant_id";

        private readonly ApplicationDbContext _context;
        private readonly ITenantProvider _tenantProvider;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public TenantDisplayNameService(
            ApplicationDbContext context,
            ITenantProvider tenantProvider,
            IHttpContextAccessor httpContextAccessor)
        {
            _context = context;
            _tenantProvider = tenantProvider;
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task<string> GetCurrentTenantDisplayNameAsync(CancellationToken cancellationToken = default)
        {
            if (!_tenantProvider.HasTenant())
            {
                return DefaultDisplayName;
            }

            return await GetTenantDisplayNameAsync(_tenantProvider.GetTenantId(), cancellationToken);
        }

        public async Task<string> GetTenantDisplayNameAsync(
            Guid tenantId,
            CancellationToken cancellationToken = default)
        {
            if (tenantId == Guid.Empty)
            {
                return DefaultDisplayName;
            }

            var preferredUserId = ResolvePreferredUserId(tenantId);
            var accountDisplayName = await ResolveAccountDisplayNameAsync(
                tenantId,
                preferredUserId,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(accountDisplayName))
            {
                return accountDisplayName;
            }

            var tenantName = await _context.Tenants
                .AsNoTracking()
                .Where(tenant => tenant.Id == tenantId)
                .Select(tenant => tenant.Nombre)
                .FirstOrDefaultAsync(cancellationToken);

            var normalizedTenantName = NormalizeDisplayName(tenantName);
            return IsPublicSafeFallback(normalizedTenantName)
                ? normalizedTenantName
                : DefaultDisplayName;
        }

        public async Task<string?> GetPublicTenantDisplayNameBySlugAsync(
            string slug,
            CancellationToken cancellationToken = default)
        {
            var normalizedSlug = NormalizeSlug(slug);
            if (string.IsNullOrWhiteSpace(normalizedSlug))
            {
                return null;
            }

            var tenantId = await _context.TenantBookingSettings
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(settings =>
                    settings.PublicBookingSlug == normalizedSlug &&
                    settings.PublicBookingEnabled &&
                    _context.Tenants.Any(tenant => tenant.Id == settings.TenantId && tenant.Activo))
                .Select(settings => (Guid?)settings.TenantId)
                .FirstOrDefaultAsync(cancellationToken);

            return tenantId.HasValue
                ? await GetTenantDisplayNameAsync(tenantId.Value, cancellationToken)
                : null;
        }

        public string NormalizeDisplayName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var buffer = new StringBuilder(value.Length);
            foreach (var character in value.Trim())
            {
                buffer.Append(IsInvalidDisplayNameCharacter(character) ? ' ' : character);
            }

            var collapsed = CollapseWhitespace(buffer.ToString());
            if (collapsed.Length <= MaxDisplayNameLength)
            {
                return collapsed;
            }

            return collapsed[..MaxDisplayNameLength].Trim();
        }

        public bool ContainsInvalidDisplayNameCharacters(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            return value.Any(IsInvalidDisplayNameCharacter);
        }

        private async Task<string?> ResolveAccountDisplayNameAsync(
            Guid tenantId,
            string? preferredUserId,
            CancellationToken cancellationToken)
        {
            var baseUsers = _context.Users
                .AsNoTracking()
                .Where(user =>
                    user.TenantId == tenantId &&
                    user.State &&
                    user.FuncionarioId == null);

            if (!string.IsNullOrWhiteSpace(preferredUserId))
            {
                var preferredName = await baseUsers
                    .Where(user => user.Id == preferredUserId)
                    .Select(user => user.Name)
                    .FirstOrDefaultAsync(cancellationToken);

                var normalizedPreferredName = NormalizeDisplayName(preferredName);
                if (!string.IsNullOrWhiteSpace(normalizedPreferredName))
                {
                    return normalizedPreferredName;
                }
            }

            var adminRoleId = await _context.Roles
                .AsNoTracking()
                .Where(role =>
                    role.Name == AppRoles.Administrador ||
                    role.NormalizedName == AppRoles.Administrador.ToUpperInvariant())
                .Select(role => role.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(adminRoleId))
            {
                var adminNames = await (
                        from user in baseUsers
                        join userRole in _context.UserRoles.AsNoTracking()
                            on user.Id equals userRole.UserId
                        where userRole.RoleId == adminRoleId
                        orderby user.UserName
                        select user.Name)
                    .ToListAsync(cancellationToken);

                var adminDisplayName = FirstValidDisplayName(adminNames);
                if (!string.IsNullOrWhiteSpace(adminDisplayName))
                {
                    return adminDisplayName;
                }
            }

            var visibleNames = await baseUsers
                .OrderBy(user => user.UserName)
                .Select(user => user.Name)
                .ToListAsync(cancellationToken);

            return FirstValidDisplayName(visibleNames);
        }

        private string? ResolvePreferredUserId(Guid tenantId)
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext is null)
            {
                return null;
            }

            if (!TryResolveHttpTenantId(httpContext, out var httpTenantId) || httpTenantId != tenantId)
            {
                return null;
            }

            return httpContext.User.FindFirstValue(CustomClaimTypes.UserId) ??
                   httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        }

        private static bool TryResolveHttpTenantId(HttpContext httpContext, out Guid tenantId)
        {
            if (httpContext.Items.TryGetValue(ResolvedTenantItemKey, out var resolved) &&
                resolved is Guid itemTenantId &&
                itemTenantId != Guid.Empty)
            {
                tenantId = itemTenantId;
                return true;
            }

            var tenantClaim = httpContext.User.FindFirstValue(CustomClaimTypes.TenantId);
            if (Guid.TryParse(tenantClaim, out tenantId) && tenantId != Guid.Empty)
            {
                return true;
            }

            tenantId = Guid.Empty;
            return false;
        }

        private string? FirstValidDisplayName(IEnumerable<string?> names)
        {
            foreach (var name in names)
            {
                var normalized = NormalizeDisplayName(name);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return normalized;
                }
            }

            return null;
        }

        private static bool IsInvalidDisplayNameCharacter(char character)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            return category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.Surrogate;
        }

        private static string CollapseWhitespace(string value)
        {
            var builder = new StringBuilder(value.Length);
            var previousWasWhitespace = true;

            foreach (var character in value)
            {
                if (char.IsWhiteSpace(character))
                {
                    if (!previousWasWhitespace)
                    {
                        builder.Append(' ');
                    }

                    previousWasWhitespace = true;
                    continue;
                }

                builder.Append(character);
                previousWasWhitespace = false;
            }

            return builder.ToString().Trim();
        }

        private static bool IsPublicSafeFallback(string value) =>
            !string.IsNullOrWhiteSpace(value) &&
            !value.Contains('@', StringComparison.Ordinal);

        private static string NormalizeSlug(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Trim().ToLowerInvariant();
        }
    }
}
