using System.Security.Claims;
using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Controllers.Platform
{
    [Authorize(Policy = PlatformAuthorizationPolicies.PlatformSuperAdmin)]
    [Route("Platform")]
    public class PlatformUsersController : Controller
    {
        private const int MaxFailedAttemptsInWindow = 5;
        private const int PageSize = 50;
        private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

        private readonly ApplicationDbContext _context;
        private readonly IPlatformUserAdminService _userAdminService;
        private readonly IPlatformAuditService _auditService;

        public PlatformUsersController(
            ApplicationDbContext context,
            IPlatformUserAdminService userAdminService,
            IPlatformAuditService auditService)
        {
            _context = context;
            _userAdminService = userAdminService;
            _auditService = auditService;
        }

        [HttpGet("Usuarios")]
        public async Task<IActionResult> Usuarios(string? q, string? estado, int page = 1, CancellationToken cancellationToken = default)
        {
            var term = q?.Trim();
            var searchActive = !string.IsNullOrWhiteSpace(term);

            // Base query: JOIN Users + Tenants en SQL para evitar cargar toda la tabla
            var baseQuery = _context.Users
                .AsNoTracking()
                .Join(
                    _context.Tenants.AsNoTracking(),
                    user => user.TenantId,
                    tenant => tenant.Id,
                    (user, tenant) => new
                    {
                        user.Id,
                        user.Email,
                        user.UserName,
                        user.Name,
                        user.TenantId,
                        TenantName = tenant.Nombre,
                        TenantActive = tenant.Activo,
                        user.State,
                        user.IsPlatformSuperAdmin,
                        user.FuncionarioId,
                        user.LockoutEnd
                    });

            if (searchActive)
            {
                baseQuery = baseQuery.Where(row =>
                    (row.Email != null && EF.Functions.Like(row.Email, $"%{term}%")) ||
                    (row.UserName != null && EF.Functions.Like(row.UserName, $"%{term}%")) ||
                    (row.Name != null && EF.Functions.Like(row.Name, $"%{term}%")) ||
                    EF.Functions.Like(row.TenantName, $"%{term}%"));
            }

            if (string.Equals(estado, "activos", StringComparison.OrdinalIgnoreCase))
            {
                baseQuery = baseQuery.Where(row => row.State);
            }
            else if (string.Equals(estado, "inactivos", StringComparison.OrdinalIgnoreCase))
            {
                baseQuery = baseQuery.Where(row => !row.State);
            }

            var orderedQuery = baseQuery
                .OrderByDescending(row => row.State)
                .ThenBy(row => row.TenantName)
                .ThenBy(row => row.Email);

            var totalCount = await orderedQuery.CountAsync(cancellationToken);

            var currentPage = Math.Max(1, page);
            var skip = (currentPage - 1) * PageSize;

            var pageData = await orderedQuery
                .Skip(skip)
                .Take(PageSize)
                .ToListAsync(cancellationToken);

            var userIds = pageData.Select(row => row.Id).ToList();
            var rolesByUser = await _context.UserRoles
                .AsNoTracking()
                .Where(userRole => userIds.Contains(userRole.UserId))
                .Join(
                    _context.Roles.AsNoTracking(),
                    userRole => userRole.RoleId,
                    role => role.Id,
                    (userRole, role) => new { userRole.UserId, RoleName = role.Name ?? string.Empty })
                .ToListAsync(cancellationToken);

            var roleLookup = rolesByUser
                .GroupBy(item => item.UserId)
                .ToDictionary(
                    group => group.Key,
                    group => string.Join(", ", group.Select(item => item.RoleName).Where(name => name.Length > 0)));

            var rows = pageData.Select(row => new PlatformTenantUserRowViewModel
            {
                UserId = row.Id,
                Email = row.Email ?? row.UserName ?? string.Empty,
                Name = row.Name,
                TenantId = row.TenantId,
                TenantName = row.TenantName,
                TenantActive = row.TenantActive,
                State = row.State,
                IsPlatformSuperAdmin = row.IsPlatformSuperAdmin,
                IsFuncionario = row.FuncionarioId.HasValue,
                LockoutEnd = row.LockoutEnd,
                Roles = roleLookup.TryGetValue(row.Id, out var roles) ? roles : string.Empty
            }).ToList();

            var model = new PlatformUsersPageViewModel
            {
                Users = rows,
                SearchTerm = q,
                StatusFilter = estado,
                TotalActive = rows.Count(row => row.State),
                TotalInactive = rows.Count(row => !row.State),
                TotalCount = totalCount,
                CurrentPage = currentPage,
                PageSize = PageSize
            };

            // Explicit view path: el controlador es PlatformUsers pero las vistas están en Views/Platform/
            return View("~/Views/Platform/Usuarios.cshtml", model);
        }

        [HttpGet("Usuarios/{userId}/ConfirmarDesactivacion")]
        public async Task<IActionResult> ConfirmarDesactivacion(string userId, CancellationToken cancellationToken = default)
        {
            var dto = await _userAdminService.GetUserForAdminAsync(userId, cancellationToken);
            if (dto is null)
            {
                return NotFound();
            }

            var model = BuildConfirmationModel(dto, isReactivation: false);
            return View("~/Views/Platform/ConfirmarDesactivacion.cshtml", model);
        }

        [HttpPost("Usuarios/{userId}/Desactivar")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Desactivar(string userId, DeactivateUserViewModel model, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(userId, model.UserId, StringComparison.Ordinal))
            {
                return BadRequest();
            }

            var dto = await _userAdminService.GetUserForAdminAsync(userId, cancellationToken);
            if (dto is null)
            {
                return NotFound();
            }

            if (!model.Acknowledge)
            {
                ModelState.AddModelError(nameof(model.Acknowledge), "Debes confirmar que entiendes la consecuencia.");
            }

            if (!ModelState.IsValid)
            {
                return View("~/Views/Platform/ConfirmarDesactivacion.cshtml", RebuildConfirmationModel(model, dto, isReactivation: false));
            }

            var blocked = await IsRateLimitedAsync(cancellationToken);
            if (blocked is not null)
            {
                ModelState.AddModelError(string.Empty, blocked);
                return View("~/Views/Platform/ConfirmarDesactivacion.cshtml", RebuildConfirmationModel(model, dto, isReactivation: false));
            }

            var result = await _userAdminService.DeactivateUserAsync(
                BuildCommand(model, dto),
                cancellationToken);

            if (!result.Success)
            {
                ModelState.AddModelError(string.Empty, result.Message);
                return View("~/Views/Platform/ConfirmarDesactivacion.cshtml", RebuildConfirmationModel(model, dto, isReactivation: false));
            }

            TempData["PlatformSuccess"] = result.Message;
            return RedirectToAction(nameof(Usuarios));
        }

        [HttpGet("Usuarios/{userId}/ConfirmarReactivacion")]
        public async Task<IActionResult> ConfirmarReactivacion(string userId, CancellationToken cancellationToken = default)
        {
            var dto = await _userAdminService.GetUserForAdminAsync(userId, cancellationToken);
            if (dto is null)
            {
                return NotFound();
            }

            var model = BuildConfirmationModel(dto, isReactivation: true);
            return View("~/Views/Platform/ConfirmarReactivacion.cshtml", model);
        }

        [HttpPost("Usuarios/{userId}/Reactivar")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reactivar(string userId, DeactivateUserViewModel model, CancellationToken cancellationToken = default)
        {
            if (!string.Equals(userId, model.UserId, StringComparison.Ordinal))
            {
                return BadRequest();
            }

            var dto = await _userAdminService.GetUserForAdminAsync(userId, cancellationToken);
            if (dto is null)
            {
                return NotFound();
            }

            // En reactivación no exigimos reescribir email/tenant; sí motivo y contraseña.
            ModelState.Remove(nameof(model.ConfirmationEmail));
            ModelState.Remove(nameof(model.ConfirmationTenantName));

            if (!ModelState.IsValid)
            {
                return View("~/Views/Platform/ConfirmarReactivacion.cshtml", RebuildConfirmationModel(model, dto, isReactivation: true));
            }

            var blocked = await IsRateLimitedAsync(cancellationToken);
            if (blocked is not null)
            {
                ModelState.AddModelError(string.Empty, blocked);
                return View("~/Views/Platform/ConfirmarReactivacion.cshtml", RebuildConfirmationModel(model, dto, isReactivation: true));
            }

            var result = await _userAdminService.ReactivateUserAsync(
                BuildCommand(model, dto),
                cancellationToken);

            if (!result.Success)
            {
                ModelState.AddModelError(string.Empty, result.Message);
                return View("~/Views/Platform/ConfirmarReactivacion.cshtml", RebuildConfirmationModel(model, dto, isReactivation: true));
            }

            TempData["PlatformSuccess"] = result.Message;
            return RedirectToAction(nameof(Usuarios));
        }

        [HttpGet("Auditoria")]
        public async Task<IActionResult> Auditoria(
            string? filtroAccion,
            string? filtroTenant,
            string? filtroActor,
            int page = 1,
            CancellationToken cancellationToken = default)
        {
            var query = _context.PlatformAuditLogs
                .IgnoreQueryFilters()
                .AsNoTracking();

            if (!string.IsNullOrWhiteSpace(filtroAccion))
            {
                query = query.Where(log => log.Action == filtroAccion);
            }

            if (!string.IsNullOrWhiteSpace(filtroTenant))
            {
                var t = filtroTenant.Trim();
                query = query.Where(log => log.TenantName != null && EF.Functions.Like(log.TenantName, $"%{t}%"));
            }

            if (!string.IsNullOrWhiteSpace(filtroActor))
            {
                var a = filtroActor.Trim();
                query = query.Where(log => EF.Functions.Like(log.ActorEmail, $"%{a}%"));
            }

            var orderedQuery = query.OrderByDescending(log => log.CreatedAtUtc);

            var totalCount = await orderedQuery.CountAsync(cancellationToken);

            var currentPage = Math.Max(1, page);
            var skip = (currentPage - 1) * PageSize;

            var entries = await orderedQuery
                .Skip(skip)
                .Take(PageSize)
                .Select(log => new PlatformAuditLogRowViewModel
                {
                    CreatedAtUtc = log.CreatedAtUtc,
                    ActorEmail = log.ActorEmail,
                    Action = log.Action,
                    EntityType = log.EntityType,
                    TenantName = log.TenantName,
                    TargetUserEmail = log.TargetUserEmail,
                    Reason = log.Reason,
                    IpAddress = log.IpAddress,
                    UserAgent = log.UserAgent
                })
                .ToListAsync(cancellationToken);

            var model = new PlatformAuditPageViewModel
            {
                Entries = entries,
                FiltroAccion = filtroAccion,
                FiltroTenant = filtroTenant,
                FiltroActor = filtroActor,
                TotalCount = totalCount,
                CurrentPage = currentPage,
                PageSize = PageSize
            };

            return View("~/Views/Platform/Auditoria.cshtml", model);
        }

        private DeactivatePlatformUserCommand BuildCommand(DeactivateUserViewModel model, PlatformUserAdminDto dto) =>
            new()
            {
                UserId = dto.UserId,
                ExpectedTenantId = model.TenantId,
                CurrentSuperAdminId = CurrentUserId,
                CurrentPassword = model.CurrentSuperAdminPassword,
                ConfirmationEmail = model.ConfirmationEmail,
                ConfirmationTenantName = model.ConfirmationTenantName,
                Reason = model.Reason
            };

        private async Task<string?> IsRateLimitedAsync(CancellationToken cancellationToken)
        {
            var failures = await _auditService.CountActorFailuresSinceAsync(
                CurrentUserId,
                DateTime.UtcNow - FailureWindow,
                cancellationToken);

            if (failures >= MaxFailedAttemptsInWindow)
            {
                await _auditService.LogAsync(new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.DangerousActionBlocked,
                    EntityType = PlatformAuditEntityTypes.User,
                    Reason = "Bloqueado temporalmente por exceso de intentos fallidos de contraseña."
                }, cancellationToken);

                return "Demasiados intentos fallidos. Espera unos minutos antes de volver a intentarlo.";
            }

            return null;
        }

        private static DeactivateUserViewModel BuildConfirmationModel(PlatformUserAdminDto dto, bool isReactivation) =>
            new()
            {
                UserId = dto.UserId,
                TenantId = dto.TenantId,
                UserEmail = dto.Email,
                UserName = dto.Name,
                TenantName = dto.TenantName,
                IsCurrentlyActive = dto.State,
                IsPlatformSuperAdmin = dto.IsPlatformSuperAdmin,
                Roles = dto.Roles,
                IsReactivation = isReactivation
            };

        private static DeactivateUserViewModel RebuildConfirmationModel(
            DeactivateUserViewModel posted,
            PlatformUserAdminDto dto,
            bool isReactivation)
        {
            posted.TenantId = dto.TenantId;
            posted.UserEmail = dto.Email;
            posted.UserName = dto.Name;
            posted.TenantName = dto.TenantName;
            posted.IsCurrentlyActive = dto.State;
            posted.IsPlatformSuperAdmin = dto.IsPlatformSuperAdmin;
            posted.Roles = dto.Roles;
            posted.IsReactivation = isReactivation;
            posted.CurrentSuperAdminPassword = string.Empty;
            return posted;
        }

        private string CurrentUserId =>
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue(CustomClaimTypes.UserId)
            ?? string.Empty;
    }
}
