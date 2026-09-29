using System.Text.Json;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Platform
{
    public sealed class PlatformUserAdminService : IPlatformUserAdminService
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<AppUsuario> _userManager;
        private readonly IPlatformAuditService _auditService;
        private readonly ILogger<PlatformUserAdminService> _logger;

        public PlatformUserAdminService(
            ApplicationDbContext context,
            UserManager<AppUsuario> userManager,
            IPlatformAuditService auditService,
            ILogger<PlatformUserAdminService> logger)
        {
            _context = context;
            _userManager = userManager;
            _auditService = auditService;
            _logger = logger;
        }

        public async Task<PlatformUserAdminDto?> GetUserForAdminAsync(string userId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return null;
            }

            var user = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);

            if (user is null)
            {
                return null;
            }

            var tenant = await _context.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == user.TenantId, cancellationToken);

            var roles = await GetRolesAsync(user.Id, cancellationToken);

            return new PlatformUserAdminDto(
                user.Id,
                user.Email ?? user.UserName ?? string.Empty,
                user.Name,
                user.TenantId,
                tenant?.Nombre ?? "(tenant desconocido)",
                user.State,
                user.IsPlatformSuperAdmin,
                tenant?.Activo ?? false,
                user.LockoutEnd,
                roles);
        }

        public async Task<DeactivationValidationResult> ValidateCanDeactivateAsync(
            string targetUserId,
            string currentSuperAdminId,
            Guid expectedTenantId,
            CancellationToken cancellationToken = default)
        {
            var target = await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(user => user.Id == targetUserId, cancellationToken);

            if (target is null)
            {
                return DeactivationValidationResult.Blocked("El usuario indicado no existe.");
            }

            // Regla 5 (anti-IDOR): el tenant esperado por el SuperAdmin debe coincidir con el real.
            if (target.TenantId != expectedTenantId)
            {
                return DeactivationValidationResult.Blocked(
                    "El usuario no pertenece al negocio indicado. Recarga la página e inténtalo de nuevo.");
            }

            // Regla 2: no puede desactivarse a sí mismo.
            if (string.Equals(target.Id, currentSuperAdminId, StringComparison.Ordinal))
            {
                return DeactivationValidationResult.Blocked("No puedes desactivar tu propia cuenta.");
            }

            // Regla 6: idempotencia.
            if (!target.State)
            {
                return DeactivationValidationResult.AlreadyDone();
            }

            // Regla 3: no desactivar al último SuperAdmin activo.
            if (target.IsPlatformSuperAdmin)
            {
                var activeSuperAdmins = await _context.Users
                    .AsNoTracking()
                    .CountAsync(user => user.IsPlatformSuperAdmin && user.State, cancellationToken);

                if (activeSuperAdmins <= 1)
                {
                    return DeactivationValidationResult.Blocked(
                        "No puedes desactivar al último SuperAdmin activo de la plataforma.");
                }
            }

            // Regla 4: no dejar un tenant activo sin administrador.
            var tenant = await _context.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == target.TenantId, cancellationToken);

            if (tenant is { Activo: true } && await IsUserAdminAsync(target.Id, cancellationToken))
            {
                var otherActiveAdmins = await CountOtherActiveAdminsAsync(target.TenantId, target.Id, cancellationToken);
                if (otherActiveAdmins == 0)
                {
                    return DeactivationValidationResult.Blocked(
                        "Este usuario es el único administrador activo del negocio. Asigna otro administrador antes de desactivarlo.");
                }
            }

            return DeactivationValidationResult.Ok();
        }

        public async Task<PlatformUserActionResult> DeactivateUserAsync(
            DeactivatePlatformUserCommand command,
            CancellationToken cancellationToken = default)
        {
            var superAdmin = await VerifySuperAdminPasswordAsync(command, cancellationToken);
            if (superAdmin is null)
            {
                return PlatformUserActionResult.Fail("Contraseña del SuperAdmin incorrecta. La acción no se ejecutó.");
            }

            var target = await _context.Users
                .FirstOrDefaultAsync(user => user.Id == command.UserId, cancellationToken);

            if (target is null)
            {
                return PlatformUserActionResult.Fail("El usuario indicado no existe.");
            }

            var tenant = await _context.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == target.TenantId, cancellationToken);

            // Confirmaciones escritas (segunda barrera server-side, no solo en JS).
            var confirmationError = ValidateWrittenConfirmations(command, target, tenant);
            if (confirmationError is not null)
            {
                await AuditBlockedAsync(target, tenant, $"Confirmación inválida: {confirmationError}", command.Reason, cancellationToken);
                return PlatformUserActionResult.Fail(confirmationError);
            }

            var validation = await ValidateCanDeactivateAsync(
                command.UserId,
                command.CurrentSuperAdminId,
                command.ExpectedTenantId,
                cancellationToken);

            if (!validation.CanProceed)
            {
                await AuditBlockedAsync(target, tenant, validation.BlockReason, command.Reason, cancellationToken);
                return PlatformUserActionResult.Fail(validation.BlockReason ?? "No se puede desactivar este usuario.");
            }

            if (validation.AlreadyInTargetState)
            {
                return PlatformUserActionResult.Ok("El usuario ya estaba desactivado.");
            }

            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                target.State = false;
                var updateResult = await _userManager.UpdateAsync(target);
                if (!updateResult.Succeeded)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return PlatformUserActionResult.Fail("No fue posible actualizar el usuario. Intenta de nuevo.");
                }

                await _userManager.SetLockoutEnabledAsync(target, true);
                await _userManager.SetLockoutEndDateAsync(target, DateTimeOffset.MaxValue);
                // Invalida cookies/sesiones ya emitidas para este usuario.
                await _userManager.UpdateSecurityStampAsync(target);

                await _auditService.LogAsync(new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.UserDeactivated,
                    EntityType = PlatformAuditEntityTypes.User,
                    EntityId = target.Id,
                    TenantId = target.TenantId,
                    TenantName = tenant?.Nombre,
                    TargetUserId = target.Id,
                    TargetUserEmail = target.Email,
                    BeforeJson = JsonSerializer.Serialize(new { State = true }),
                    AfterJson = JsonSerializer.Serialize(new { State = false }),
                    Reason = command.Reason
                }, cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                return PlatformUserActionResult.Ok("Usuario desactivado. No podrá iniciar sesión y sus sesiones activas se cerrarán.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex, "Error desactivando al usuario {UserId} desde plataforma.", command.UserId);
                return PlatformUserActionResult.Fail("Ocurrió un error al desactivar el usuario. No se aplicaron cambios.");
            }
        }

        public async Task<PlatformUserActionResult> ReactivateUserAsync(
            DeactivatePlatformUserCommand command,
            CancellationToken cancellationToken = default)
        {
            var superAdmin = await VerifySuperAdminPasswordAsync(command, cancellationToken);
            if (superAdmin is null)
            {
                return PlatformUserActionResult.Fail("Contraseña del SuperAdmin incorrecta. La acción no se ejecutó.");
            }

            var target = await _context.Users
                .FirstOrDefaultAsync(user => user.Id == command.UserId, cancellationToken);

            if (target is null)
            {
                return PlatformUserActionResult.Fail("El usuario indicado no existe.");
            }

            var tenant = await _context.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == target.TenantId, cancellationToken);

            // Anti-IDOR también al reactivar.
            if (target.TenantId != command.ExpectedTenantId)
            {
                await AuditBlockedAsync(target, tenant, "Tenant no coincide al reactivar.", command.Reason, cancellationToken);
                return PlatformUserActionResult.Fail(
                    "El usuario no pertenece al negocio indicado. Recarga la página e inténtalo de nuevo.");
            }

            if (target.State)
            {
                return PlatformUserActionResult.Ok("El usuario ya estaba activo.");
            }

            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                target.State = true;
                var updateResult = await _userManager.UpdateAsync(target);
                if (!updateResult.Succeeded)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return PlatformUserActionResult.Fail("No fue posible actualizar el usuario. Intenta de nuevo.");
                }

                await _userManager.SetLockoutEndDateAsync(target, null);
                await _userManager.UpdateSecurityStampAsync(target);

                await _auditService.LogAsync(new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.UserReactivated,
                    EntityType = PlatformAuditEntityTypes.User,
                    EntityId = target.Id,
                    TenantId = target.TenantId,
                    TenantName = tenant?.Nombre,
                    TargetUserId = target.Id,
                    TargetUserEmail = target.Email,
                    BeforeJson = JsonSerializer.Serialize(new { State = false }),
                    AfterJson = JsonSerializer.Serialize(new { State = true }),
                    Reason = command.Reason
                }, cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                return PlatformUserActionResult.Ok("Usuario reactivado. Ya puede iniciar sesión nuevamente.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                _logger.LogError(ex, "Error reactivando al usuario {UserId} desde plataforma.", command.UserId);
                return PlatformUserActionResult.Fail("Ocurrió un error al reactivar el usuario. No se aplicaron cambios.");
            }
        }

        /// <summary>
        /// Verifica la contraseña del SuperAdmin. Si falla, registra el intento en auditoría
        /// (sin guardar jamás la contraseña) y devuelve null.
        /// </summary>
        private async Task<AppUsuario?> VerifySuperAdminPasswordAsync(
            DeactivatePlatformUserCommand command,
            CancellationToken cancellationToken)
        {
            var superAdmin = await _userManager.FindByIdAsync(command.CurrentSuperAdminId);
            if (superAdmin is null || !superAdmin.IsPlatformSuperAdmin)
            {
                return null;
            }

            if (string.IsNullOrEmpty(command.CurrentPassword) ||
                !await _userManager.CheckPasswordAsync(superAdmin, command.CurrentPassword))
            {
                await _auditService.LogAsync(new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.DangerousActionPasswordFailed,
                    EntityType = PlatformAuditEntityTypes.User,
                    EntityId = command.UserId,
                    TargetUserId = command.UserId,
                    Reason = "Contraseña del SuperAdmin incorrecta al intentar una acción peligrosa."
                }, cancellationToken);

                return null;
            }

            return superAdmin;
        }

        private async Task AuditBlockedAsync(
            AppUsuario target,
            Models.SaaS.Tenant? tenant,
            string? blockReason,
            string? actorReason,
            CancellationToken cancellationToken)
        {
            await _auditService.LogAsync(new PlatformAuditEntry
            {
                Action = PlatformAuditActions.DangerousActionBlocked,
                EntityType = PlatformAuditEntityTypes.User,
                EntityId = target.Id,
                TenantId = target.TenantId,
                TenantName = tenant?.Nombre,
                TargetUserId = target.Id,
                TargetUserEmail = target.Email,
                Reason = string.IsNullOrWhiteSpace(actorReason) ? blockReason : $"{blockReason} | Motivo dado: {actorReason}"
            }, cancellationToken);
        }

        private static string? ValidateWrittenConfirmations(
            DeactivatePlatformUserCommand command,
            AppUsuario target,
            Models.SaaS.Tenant? tenant)
        {
            var expectedEmail = target.Email ?? target.UserName ?? string.Empty;
            if (!string.Equals(command.ConfirmationEmail?.Trim(), expectedEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return "El correo escrito no coincide exactamente con el del usuario.";
            }

            var expectedTenantName = tenant?.Nombre ?? string.Empty;
            if (!string.Equals(command.ConfirmationTenantName?.Trim(), expectedTenantName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return "El nombre del negocio escrito no coincide exactamente.";
            }

            if (string.IsNullOrWhiteSpace(command.Reason))
            {
                return "El motivo es obligatorio.";
            }

            return null;
        }

        private async Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken cancellationToken)
        {
            return await _context.UserRoles
                .AsNoTracking()
                .Where(userRole => userRole.UserId == userId)
                .Join(_context.Roles.AsNoTracking(),
                    userRole => userRole.RoleId,
                    role => role.Id,
                    (_, role) => role.Name ?? string.Empty)
                .Where(name => name != string.Empty)
                .ToListAsync(cancellationToken);
        }

        private async Task<bool> IsUserAdminAsync(string userId, CancellationToken cancellationToken)
        {
            var adminRoleId = await GetAdminRoleIdAsync(cancellationToken);
            if (adminRoleId is null)
            {
                return false;
            }

            return await _context.UserRoles
                .AsNoTracking()
                .AnyAsync(userRole => userRole.UserId == userId && userRole.RoleId == adminRoleId, cancellationToken);
        }

        private async Task<int> CountOtherActiveAdminsAsync(Guid tenantId, string excludedUserId, CancellationToken cancellationToken)
        {
            var adminRoleId = await GetAdminRoleIdAsync(cancellationToken);
            if (adminRoleId is null)
            {
                return 0;
            }

            return await _context.UserRoles
                .AsNoTracking()
                .Where(userRole => userRole.RoleId == adminRoleId)
                .Join(_context.Users.AsNoTracking(),
                    userRole => userRole.UserId,
                    user => user.Id,
                    (_, user) => user)
                .CountAsync(
                    user => user.TenantId == tenantId && user.State && user.Id != excludedUserId,
                    cancellationToken);
        }

        private async Task<string?> GetAdminRoleIdAsync(CancellationToken cancellationToken) =>
            await _context.Roles
                .AsNoTracking()
                .Where(role => role.Name == AppRoles.Administrador)
                .Select(role => role.Id)
                .FirstOrDefaultAsync(cancellationToken);
    }
}
