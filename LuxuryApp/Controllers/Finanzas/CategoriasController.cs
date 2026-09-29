using LuxuryApp.Services.Identity;
using LuxuryApp.Models.Asociados;
using LuxuryApp.Models.Finanzas;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Controllers.Finanzas
{
    [Authorize]
    [RequirePermission(AppPermissions.ExpensesView)]
    public class CategoriasController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<CategoriasController> _logger;

        public CategoriasController(ApplicationDbContext context, ILogger<CategoriasController> logger)
        {
            _context = context;
            _logger = logger;
        }

        public Task<IActionResult> Index() => ModalCategorias();

        public Task<IActionResult> Create() => FormCategoria();

        [HttpPost]
        [RequirePermission(AppPermissions.ExpensesManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Create(
            [Bind(nameof(Categoria.Id) + "," + nameof(Categoria.Nombre) + "," + nameof(Categoria.Detalle))]
            Categoria categoria) =>
            Save(categoria);

        [HttpPost]
        [RequirePermission(AppPermissions.ExpensesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(
            [Bind(nameof(Categoria.Id) + "," + nameof(Categoria.Nombre) + "," + nameof(Categoria.Detalle))]
            Categoria categoria)
        {
            NormalizeCategoria(categoria);
            await ValidateCategoriaAsync(categoria);

            if (!ModelState.IsValid)
            {
                return PartialView("_FormCategoria", categoria);
            }

            try
            {
                if (categoria.Id == 0)
                {
                    categoria.Activo = true;
                    _context.Categorias.Add(categoria);
                }
                else
                {
                    var categoriaDb = await _context.Categorias
                        .FirstOrDefaultAsync(c => c.Id == categoria.Id);

                    if (categoriaDb == null)
                    {
                        return NotFound();
                    }

                    categoriaDb.Nombre = categoria.Nombre;
                    categoriaDb.Detalle = categoria.Detalle;
                }

                await _context.SaveChangesAsync();
                return Ok();
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                _logger.LogWarning(ex, "Intento de duplicar categoria {CategoriaNombre}.", categoria.Nombre);
                ModelState.AddModelError(nameof(Categoria.Nombre), "Ya existe una categoria con ese nombre.");
                return PartialView("_FormCategoria", categoria);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al guardar categoria {CategoriaId}.", categoria.Id);
                ModelState.AddModelError(string.Empty, "No fue posible guardar la categoria.");
                return PartialView("_FormCategoria", categoria);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Guard bloqueo la operacion sobre categoria {CategoriaId}.", categoria.Id);
                return BadRequest("No fue posible guardar la categoria por una validacion de seguridad o consistencia.");
            }
        }

        public Task<IActionResult> Edit(int id) => FormCategoria(id);

        [HttpPost]
        [RequirePermission(AppPermissions.ExpensesManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Edit(
            int id,
            [Bind(nameof(Categoria.Id) + "," + nameof(Categoria.Nombre) + "," + nameof(Categoria.Detalle))]
            Categoria categoria)
        {
            if (id != categoria.Id)
            {
                return Task.FromResult<IActionResult>(NotFound());
            }

            return Save(categoria);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ExpensesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleActivo(int id)
        {
            try
            {
                var categoria = await _context.Categorias
                    .FirstOrDefaultAsync(c => c.Id == id);

                if (categoria == null)
                {
                    return NotFound();
                }

                categoria.Activo = !categoria.Activo;
                await _context.SaveChangesAsync();
                return Ok();
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al alternar el estado de la categoria {CategoriaId}.", id);
                return BadRequest("No fue posible actualizar el estado de la categoria.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Guard bloqueo el cambio de estado de la categoria {CategoriaId}.", id);
                return BadRequest("No fue posible actualizar el estado de la categoria por una validacion de seguridad.");
            }
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ExpensesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var categoria = await _context.Categorias
                .FirstOrDefaultAsync(c => c.Id == id);

            if (categoria == null)
            {
                return NotFound();
            }

            var estaEnUso = await _context.Egresos
                .AsNoTracking()
                .AnyAsync(e => e.CategoriaId == id);

            if (estaEnUso)
            {
                return BadRequest("No se puede eliminar la categoria porque ya esta siendo usada en egresos.");
            }

            try
            {
                _context.Categorias.Remove(categoria);
                await _context.SaveChangesAsync();
                return Ok();
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al eliminar categoria {CategoriaId}.", id);
                return BadRequest("No fue posible eliminar la categoria porque tiene relaciones activas.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Guard bloqueo la eliminacion de la categoria {CategoriaId}.", id);
                return BadRequest("No fue posible eliminar la categoria por una validacion de seguridad.");
            }
        }

        public async Task<IActionResult> ModalCategorias()
        {
            var categorias = await _context.Categorias
                .AsNoTracking()
                .OrderBy(c => c.Nombre)
                .ToListAsync();

            return PartialView("_CategoriasModal", categorias);
        }

        public async Task<IActionResult> FormCategoria(int? id = null)
        {
            if (!id.HasValue || id.Value <= 0)
            {
                return PartialView("~/Views/Categorias/_FormCategoria.cshtml", new Categoria());
            }

            var categoria = await _context.Categorias
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == id.Value);

            if (categoria == null)
            {
                return NotFound();
            }

            return PartialView("~/Views/Categorias/_FormCategoria.cshtml", categoria);
        }

        private async Task ValidateCategoriaAsync(Categoria categoria)
        {
            if (string.IsNullOrWhiteSpace(categoria.Nombre))
            {
                ModelState.AddModelError(nameof(Categoria.Nombre), "Debe indicar el nombre de la categoria.");
            }

            if (string.IsNullOrWhiteSpace(categoria.Detalle))
            {
                ModelState.AddModelError(nameof(Categoria.Detalle), "Debe indicar el detalle de la categoria.");
            }

            var nombreNormalizado = (categoria.Nombre ?? string.Empty).ToUpperInvariant();
            var existe = await _context.Categorias
                .AsNoTracking()
                .Where(c => c.Id != categoria.Id && c.Nombre != null)
                .AnyAsync(c => c.Nombre!.ToUpper() == nombreNormalizado);

            if (existe)
            {
                ModelState.AddModelError(nameof(Categoria.Nombre), "Ya existe una categoria con ese nombre.");
            }
        }

        private static void NormalizeCategoria(Categoria categoria)
        {
            categoria.Nombre = CollapseWhitespace(categoria.Nombre);
            categoria.Detalle = string.IsNullOrWhiteSpace(categoria.Detalle)
                ? null
                : CollapseWhitespace(categoria.Detalle);
        }

        private static string CollapseWhitespace(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return string.Join(
                ' ',
                value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        private static bool IsUniqueConstraintViolation(DbUpdateException exception)
        {
            var message = exception.InnerException?.Message ?? exception.Message;
            return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                || message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                || message.Contains("IX_Categorias_TenantId_Nombre", StringComparison.OrdinalIgnoreCase);
        }
    }
}
