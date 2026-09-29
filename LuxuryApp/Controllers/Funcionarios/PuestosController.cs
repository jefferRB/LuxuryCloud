using LuxuryApp.Services.Identity;
using LuxuryApp.Models.Asociados;
using LuxuryApp.Models.Funcionarios;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Controllers.Funcionarios
{
    [Authorize]
    [RequirePermission(AppPermissions.EmployeesView)]
    public class PuestosController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<PuestosController> _logger;

        public PuestosController(ApplicationDbContext context, ILogger<PuestosController> logger)
        {
            _context = context;
            _logger = logger;
        }

        public Task<IActionResult> Index() => ModalPuestos();

        [HttpGet]
        public Task<IActionResult> Create() => FormPuesto(null);

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Create(
            [Bind(nameof(Puesto.IdPuesto) + "," + nameof(Puesto.NombrePuesto) + "," + nameof(Puesto.Detalle))]
            Puesto puesto) =>
            Save(puesto);

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(
            [Bind(nameof(Puesto.IdPuesto) + "," + nameof(Puesto.NombrePuesto) + "," + nameof(Puesto.Detalle))]
            Puesto puesto)
        {
            NormalizePuesto(puesto);
            await ValidatePuestoAsync(puesto);

            if (!ModelState.IsValid)
            {
                return PartialView("_FormPuesto", puesto);
            }

            try
            {
                if (puesto.IdPuesto == 0)
                {
                    puesto.Activo = true;
                    _context.Puestos.Add(puesto);
                }
                else
                {
                    var puestoDb = await _context.Puestos
                        .FirstOrDefaultAsync(p => p.IdPuesto == puesto.IdPuesto);

                    if (puestoDb == null)
                    {
                        return NotFound();
                    }

                    puestoDb.NombrePuesto = puesto.NombrePuesto;
                    puestoDb.Detalle = puesto.Detalle;
                }

                await _context.SaveChangesAsync();
                return Ok();
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                _logger.LogWarning(ex, "Intento duplicado al guardar puesto {NombrePuesto}.", puesto.NombrePuesto);
                ModelState.AddModelError(nameof(Puesto.NombrePuesto), "Ya existe un puesto con ese nombre.");
                return PartialView("_FormPuesto", puesto);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al guardar puesto {PuestoId}.", puesto.IdPuesto);
                ModelState.AddModelError(string.Empty, "No fue posible guardar el puesto.");
                return PartialView("_FormPuesto", puesto);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Guard bloqueo la operacion sobre puesto {PuestoId}.", puesto.IdPuesto);
                return BadRequest("No fue posible guardar el puesto por una validacion de seguridad o consistencia.");
            }
        }

        [HttpGet]
        public Task<IActionResult> Edit(int id) => FormPuesto(id);

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            int id,
            [Bind(nameof(Puesto.IdPuesto) + "," + nameof(Puesto.NombrePuesto) + "," + nameof(Puesto.Detalle))]
            Puesto puesto)
        {
            if (id != puesto.IdPuesto)
            {
                return NotFound();
            }

            NormalizePuesto(puesto);
            await ValidatePuestoAsync(puesto);

            if (!ModelState.IsValid)
            {
                return PartialView("_FormPuesto", puesto);
            }

            try
            {
                var puestoDb = await _context.Puestos
                    .FirstOrDefaultAsync(p => p.IdPuesto == id);

                if (puestoDb == null)
                {
                    return NotFound();
                }

                puestoDb.NombrePuesto = puesto.NombrePuesto;
                puestoDb.Detalle = puesto.Detalle;

                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                _logger.LogWarning(ex, "Intento duplicado al editar puesto {PuestoId}.", puesto.IdPuesto);
                ModelState.AddModelError(nameof(Puesto.NombrePuesto), "Ya existe un puesto con ese nombre.");
                return PartialView("_FormPuesto", puesto);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al editar puesto {PuestoId}.", puesto.IdPuesto);
                ModelState.AddModelError(string.Empty, "No fue posible guardar el puesto.");
                return PartialView("_FormPuesto", puesto);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Guard bloqueo la edicion del puesto {PuestoId}.", puesto.IdPuesto);
                return BadRequest("No fue posible guardar el puesto por una validacion de seguridad o consistencia.");
            }
        }

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleActivo(int id)
        {
            var puesto = await _context.Puestos
                .FirstOrDefaultAsync(p => p.IdPuesto == id);

            if (puesto == null)
            {
                return NotFound();
            }

            if (puesto.Activo)
            {
                var tieneFuncionariosActivos = await _context.Funcionarios
                    .AnyAsync(f => f.IdPuesto == id && f.Activo);

                if (tieneFuncionariosActivos)
                {
                    return BadRequest("No se puede desactivar porque esta asignado a funcionarios activos.");
                }
            }

            puesto.Activo = !puesto.Activo;

            try
            {
                await _context.SaveChangesAsync();
                return Ok();
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al cambiar el estado del puesto {PuestoId}.", id);
                return BadRequest("No fue posible actualizar el estado del puesto.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Guard bloqueo el cambio de estado del puesto {PuestoId}.", id);
                return BadRequest("No fue posible actualizar el estado del puesto por una validacion de seguridad o consistencia.");
            }
        }

        [HttpPost]
        [RequirePermission(AppPermissions.EmployeesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var puesto = await _context.Puestos
                .FirstOrDefaultAsync(p => p.IdPuesto == id);

            if (puesto == null)
            {
                return NotFound();
            }

            var estaEnUso = await _context.Funcionarios
                .AnyAsync(f => f.IdPuesto == id);

            if (estaEnUso)
            {
                return BadRequest("No se puede eliminar el puesto porque tiene funcionarios asociados.");
            }

            try
            {
                _context.Puestos.Remove(puesto);
                await _context.SaveChangesAsync();
                return Ok();
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al eliminar puesto {PuestoId}.", id);
                return BadRequest("No fue posible eliminar el puesto porque tiene relaciones activas.");
            }
        }

        public async Task<IActionResult> ModalPuestos()
        {
            var puestos = await _context.Puestos
                .AsNoTracking()
                .OrderBy(p => p.NombrePuesto)
                .ToListAsync();

            return PartialView("_PuestosModal", puestos);
        }

        public async Task<IActionResult> FormPuesto(int? id)
        {
            if (!id.HasValue || id.Value <= 0)
            {
                return PartialView("~/Views/Puestos/_FormPuesto.cshtml", new Puesto());
            }

            var puesto = await _context.Puestos
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.IdPuesto == id.Value);

            if (puesto == null)
            {
                return NotFound();
            }

            return PartialView("~/Views/Puestos/_FormPuesto.cshtml", puesto);
        }

        private async Task ValidatePuestoAsync(Puesto puesto)
        {
            if (string.IsNullOrWhiteSpace(puesto.NombrePuesto))
            {
                return;
            }

            var existe = await _context.Puestos
                .AsNoTracking()
                .AnyAsync(p => p.NombrePuesto == puesto.NombrePuesto && p.IdPuesto != puesto.IdPuesto);

            if (existe)
            {
                ModelState.AddModelError(nameof(Puesto.NombrePuesto), "Ya existe un puesto con ese nombre.");
            }
        }

        private static void NormalizePuesto(Puesto puesto)
        {
            puesto.NombrePuesto = NormalizeRequiredText(puesto.NombrePuesto);
            puesto.Detalle = NormalizeOptionalText(puesto.Detalle);
        }

        private static string NormalizeRequiredText(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : CollapseWhitespace(value);

        private static string? NormalizeOptionalText(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? null
                : CollapseWhitespace(value);

        private static string CollapseWhitespace(string value) =>
            string.Join(
                " ",
                value.Trim()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        private static bool IsUniqueConstraintViolation(DbUpdateException ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("IX_Puestos_TenantId_NombrePuesto", StringComparison.OrdinalIgnoreCase);
        }
    }
}
