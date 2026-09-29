using LuxuryApp.Models.Reports;
using LuxuryApp.Services.Identity;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Reports
{
    public sealed class MonthlyReportRecipientResolver : IMonthlyReportRecipientResolver
    {
        private readonly ApplicationDbContext _context;

        public MonthlyReportRecipientResolver(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<MonthlyReportRecipientResolution> ResolveAsync(
            Guid tenantId,
            TenantMonthlyReportSettings settings,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(settings);

            if (tenantId == Guid.Empty)
            {
                return MonthlyReportRecipientResolution.Empty;
            }

            var included = new List<MonthlyReportRecipient>();
            var excluded = new List<MonthlyReportExcludedRecipient>();

            // Índice case-insensitive de correos ya incluidos, para deduplicar entre orígenes.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var users = await LoadTenantUsersAsync(tenantId, cancellationToken);

            // Correos de funcionarios (por FuncionarioId o rol): exclusión dura de cualquier origen.
            var funcionarioEmails = users
                .Where(u => u.IsFuncionario)
                .Select(u => MonthlyBusinessReportService.TryNormalizeEmail(u.Email))
                .Where(email => email is not null)
                .Select(email => email!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // SendToAllAdmins es el gate de administradores. La UI lo mantiene sincronizado con
            // SendToOwnerEmail y la migración lo deja en true por defecto (compat. Fase 1).
            if (settings.SendToAllAdmins)
            {
                foreach (var user in users.Where(u => u.IsAdmin).OrderBy(u => u.Email))
                {
                    ClassifyAdmin(user, settings, funcionarioEmails, seen, included, excluded);
                }
            }

            if (settings.IncludeManualRecipients)
            {
                foreach (var raw in MonthlyBusinessReportService.ParseAdditionalRecipients(settings.AdditionalRecipients))
                {
                    ClassifyManual(raw, funcionarioEmails, seen, included, excluded);
                }
            }

            return new MonthlyReportRecipientResolution(included, excluded);
        }

        private static void ClassifyAdmin(
            TenantUserProjection user,
            TenantMonthlyReportSettings settings,
            HashSet<string> funcionarioEmails,
            HashSet<string> seen,
            List<MonthlyReportRecipient> included,
            List<MonthlyReportExcludedRecipient> excluded)
        {
            var displayName = string.IsNullOrWhiteSpace(user.Name) ? null : user.Name;
            var normalized = MonthlyBusinessReportService.TryNormalizeEmail(user.Email);

            if (string.IsNullOrWhiteSpace(user.Email))
            {
                excluded.Add(new MonthlyReportExcludedRecipient(
                    "(sin correo)", displayName, MonthlyReportRecipientSource.Admin, MonthlyReportExclusionReason.NoEmail));
                return;
            }

            if (normalized is null)
            {
                excluded.Add(new MonthlyReportExcludedRecipient(
                    user.Email, displayName, MonthlyReportRecipientSource.Admin, MonthlyReportExclusionReason.InvalidEmail));
                return;
            }

            if (!user.State)
            {
                excluded.Add(new MonthlyReportExcludedRecipient(
                    normalized, displayName, MonthlyReportRecipientSource.Admin, MonthlyReportExclusionReason.InactiveUser));
                return;
            }

            // Defensivo: nunca a un correo que también pertenece a una cuenta de funcionario.
            if (funcionarioEmails.Contains(normalized))
            {
                excluded.Add(new MonthlyReportExcludedRecipient(
                    normalized, displayName, MonthlyReportRecipientSource.Admin, MonthlyReportExclusionReason.Funcionario));
                return;
            }

            if (settings.RequireConfirmedEmail && !user.EmailConfirmed)
            {
                excluded.Add(new MonthlyReportExcludedRecipient(
                    normalized, displayName, MonthlyReportRecipientSource.Admin, MonthlyReportExclusionReason.Unconfirmed));
                return;
            }

            if (!seen.Add(normalized))
            {
                excluded.Add(new MonthlyReportExcludedRecipient(
                    normalized, displayName, MonthlyReportRecipientSource.Admin, MonthlyReportExclusionReason.Duplicate));
                return;
            }

            included.Add(new MonthlyReportRecipient(normalized, displayName, MonthlyReportRecipientSource.Admin));
        }

        private static void ClassifyManual(
            string raw,
            HashSet<string> funcionarioEmails,
            HashSet<string> seen,
            List<MonthlyReportRecipient> included,
            List<MonthlyReportExcludedRecipient> excluded)
        {
            var normalized = MonthlyBusinessReportService.TryNormalizeEmail(raw);

            if (normalized is null)
            {
                excluded.Add(new MonthlyReportExcludedRecipient(
                    raw.Trim(), null, MonthlyReportRecipientSource.Manual, MonthlyReportExclusionReason.InvalidEmail));
                return;
            }

            if (funcionarioEmails.Contains(normalized))
            {
                excluded.Add(new MonthlyReportExcludedRecipient(
                    normalized, null, MonthlyReportRecipientSource.Manual, MonthlyReportExclusionReason.Funcionario));
                return;
            }

            if (!seen.Add(normalized))
            {
                excluded.Add(new MonthlyReportExcludedRecipient(
                    normalized, null, MonthlyReportRecipientSource.Manual, MonthlyReportExclusionReason.Duplicate));
                return;
            }

            included.Add(new MonthlyReportRecipient(normalized, null, MonthlyReportRecipientSource.Manual));
        }

        private async Task<List<TenantUserProjection>> LoadTenantUsersAsync(
            Guid tenantId,
            CancellationToken cancellationToken)
        {
            var adminRoleId = await _context.Roles
                .AsNoTracking()
                .Where(role =>
                    role.Name == AppRoles.Administrador ||
                    role.NormalizedName == AppRoles.Administrador.ToUpperInvariant())
                .Select(role => role.Id)
                .FirstOrDefaultAsync(cancellationToken);

            var funcionarioRoleId = await _context.Roles
                .AsNoTracking()
                .Where(role =>
                    role.Name == AppRoles.Funcionario ||
                    role.NormalizedName == AppRoles.Funcionario.ToUpperInvariant())
                .Select(role => role.Id)
                .FirstOrDefaultAsync(cancellationToken);

            // AppUsuario no es ITenantEntity: se filtra por TenantId explícito.
            return await _context.Users
                .AsNoTracking()
                .Where(user => user.TenantId == tenantId)
                .Select(user => new TenantUserProjection
                {
                    Email = user.Email,
                    Name = user.Name,
                    State = user.State,
                    EmailConfirmed = user.EmailConfirmed,
                    IsAdmin = adminRoleId != null &&
                        _context.UserRoles.Any(ur => ur.UserId == user.Id && ur.RoleId == adminRoleId),
                    IsFuncionario = user.FuncionarioId != null ||
                        (funcionarioRoleId != null &&
                         _context.UserRoles.Any(ur => ur.UserId == user.Id && ur.RoleId == funcionarioRoleId))
                })
                .ToListAsync(cancellationToken);
        }

        private sealed class TenantUserProjection
        {
            public string? Email { get; init; }
            public string? Name { get; init; }
            public bool State { get; init; }
            public bool EmailConfirmed { get; init; }
            public bool IsAdmin { get; init; }
            public bool IsFuncionario { get; init; }
        }
    }
}
