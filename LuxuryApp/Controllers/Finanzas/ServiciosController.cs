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
    [RequirePermission(AppPermissions.ServicesView)]
    public class ServiciosController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly LuxuryApp.Services.Finanzas.ILegacyFinancialImpactService _impactoHistorico;
        private readonly ILogger<ServiciosController> _logger;

        public ServiciosController(
            ApplicationDbContext context,
            ILogger<ServiciosController> logger,
            LuxuryApp.Services.Finanzas.ILegacyFinancialImpactService impactoHistorico)
        {
            _context = context;
            _logger = logger;
            _impactoHistorico = impactoHistorico;
        }

        public Task<IActionResult> Index() => ModalServicios();

        public Task<IActionResult> Create() => FormServicio();

        [HttpPost]
        [RequirePermission(AppPermissions.ServicesManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Create(
            [Bind(nameof(Servicio.Id) + "," + nameof(Servicio.Nombre) + "," + nameof(Servicio.Precio) + "," + nameof(Servicio.DuracionMinutos) + "," + nameof(Servicio.AplicaIva) + "," + nameof(Servicio.TarifaIva) + "," + nameof(Servicio.PrecioIncluyeIva))]
            Servicio servicio) =>
            Save(servicio);

        /// <summary>
        /// ¿Este guardado cambia la fiscalidad de un servicio que YA tiene cobros sin snapshot?
        /// Si es así, y el usuario todavía no confirmó, se devuelve el formulario con el aviso.
        ///
        /// <para>
        /// La comprobación es de SERVIDOR y compara contra la fila real: no alcanza con que el
        /// formulario haya mandado la casilla. Solo se dispara si la fiscalidad cambió de verdad —
        /// renombrar el servicio o corregirle el precio no reinterpreta nada.
        /// </para>
        /// </summary>
        private async Task<bool> RequiereConfirmacionHistoricaAsync(Servicio servicio, bool confirmado)
        {
            if (servicio.Id == 0 || confirmado)
            {
                return false;
            }

            var actual = await _context.Servicios
                .AsNoTracking()
                .Where(s => s.Id == servicio.Id)
                .Select(s => new { s.AplicaIva, s.TarifaIva, s.PrecioIncluyeIva })
                .FirstOrDefaultAsync();

            if (actual is null)
            {
                return false;
            }

            var cambioFiscal =
                actual.AplicaIva != servicio.AplicaIva ||
                actual.TarifaIva != servicio.TarifaIva ||
                actual.PrecioIncluyeIva != servicio.PrecioIncluyeIva;

            if (!cambioFiscal)
            {
                return false;
            }

            var legacy = await _impactoHistorico.ContarCobrosLegacyDeServicioAsync(servicio.Id);
            if (legacy == 0)
            {
                return false;
            }

            ViewData[LuxuryApp.Models.Fiscal.AvisoImpactoHistorico.CampoConfirmacion] =
                LuxuryApp.Models.Fiscal.AvisoImpactoHistorico.Servicio(legacy);

            ModelState.AddModelError(
                string.Empty,
                LuxuryApp.Models.Fiscal.AvisoImpactoHistorico.Confirmacion);

            return true;
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ServicesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(
            [Bind(nameof(Servicio.Id) + "," + nameof(Servicio.Nombre) + "," + nameof(Servicio.Precio) + "," + nameof(Servicio.DuracionMinutos) + "," + nameof(Servicio.AplicaIva) + "," + nameof(Servicio.TarifaIva) + "," + nameof(Servicio.PrecioIncluyeIva))]
            Servicio servicio,
            bool confirmarImpactoHistorico = false)
        {
            NormalizeServicio(servicio);
            await ValidateServicioAsync(servicio);

            if (!ModelState.IsValid)
            {
                return PartialView("_FormServicio", servicio);
            }

            if (await RequiereConfirmacionHistoricaAsync(servicio, confirmarImpactoHistorico))
            {
                return PartialView("_FormServicio", servicio);
            }

            try
            {
                if (servicio.Id == 0)
                {
                    servicio.Activo = true;
                    _context.Servicios.Add(servicio);
                }
                else
                {
                    var servicioDb = await _context.Servicios
                        .FirstOrDefaultAsync(s => s.Id == servicio.Id);

                    if (servicioDb == null)
                    {
                        return NotFound();
                    }

                    servicioDb.Nombre = servicio.Nombre;
                    servicioDb.Precio = servicio.Precio;
                    servicioDb.DuracionMinutos = servicio.DuracionMinutos;
                    servicioDb.AplicaIva = servicio.AplicaIva;
                    servicioDb.TarifaIva = servicio.TarifaIva;
                    servicioDb.PrecioIncluyeIva = servicio.PrecioIncluyeIva;
                }

                await _context.SaveChangesAsync();
                return Ok();
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                _logger.LogWarning(ex, "Intento de duplicar servicio {ServicioNombre}.", servicio.Nombre);
                ModelState.AddModelError(nameof(Servicio.Nombre), "Ya existe un servicio con ese nombre.");
                return PartialView("_FormServicio", servicio);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al guardar servicio {ServicioId}.", servicio.Id);
                ModelState.AddModelError(string.Empty, "No fue posible guardar el servicio.");
                return PartialView("_FormServicio", servicio);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Guard bloqueo la operacion sobre servicio {ServicioId}.", servicio.Id);
                return BadRequest("No fue posible guardar el servicio por una validacion de seguridad o consistencia.");
            }
        }

        public Task<IActionResult> Edit(int id) => FormServicio(id);

        [HttpPost]
        [RequirePermission(AppPermissions.ServicesManage)]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Edit(
            int id,
            [Bind(nameof(Servicio.Id) + "," + nameof(Servicio.Nombre) + "," + nameof(Servicio.Precio) + "," + nameof(Servicio.DuracionMinutos) + "," + nameof(Servicio.AplicaIva) + "," + nameof(Servicio.TarifaIva) + "," + nameof(Servicio.PrecioIncluyeIva))]
            Servicio servicio,
            bool confirmarImpactoHistorico = false)
        {
            if (id != servicio.Id)
            {
                return Task.FromResult<IActionResult>(NotFound());
            }

            return Save(servicio, confirmarImpactoHistorico);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ServicesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleActivo(int id)
        {
            try
            {
                var servicio = await _context.Servicios
                    .FirstOrDefaultAsync(s => s.Id == id);

                if (servicio == null)
                {
                    return NotFound();
                }

                servicio.Activo = !servicio.Activo;
                await _context.SaveChangesAsync();

                return Ok();
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al alternar estado del servicio {ServicioId}.", id);
                return BadRequest("No fue posible actualizar el estado del servicio.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Guard bloqueo el cambio de estado del servicio {ServicioId}.", id);
                return BadRequest("No fue posible actualizar el estado del servicio por una validacion de seguridad.");
            }
        }

        [HttpGet]
        public async Task<JsonResult> ObtenerPrecio(int id)
        {
            var precio = await _context.Servicios
                .AsNoTracking()
                .Where(s => s.Id == id && s.Activo)
                .Select(s => (decimal?)s.Precio)
                .SingleOrDefaultAsync();

            return Json(precio.HasValue ? new { precio = precio.Value } : null);
        }

        public async Task<IActionResult> ModalServicios()
        {
            var servicios = await _context.Servicios
                .AsNoTracking()
                .OrderBy(s => s.Nombre)
                .Select(s => new ServicioListItemViewModel
                {
                    Id = s.Id,
                    Nombre = s.Nombre,
                    Precio = s.Precio,
                    DuracionMinutos = s.DuracionMinutos,
                    Activo = s.Activo
                })
                .ToListAsync();

            return PartialView("_ServiciosModal", servicios);
        }

        public async Task<IActionResult> FormServicio(int? id = null)
        {
            if (!id.HasValue || id.Value <= 0)
            {
                return PartialView("~/Views/Servicios/_FormServicio.cshtml", new Servicio());
            }

            var servicio = await _context.Servicios
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id.Value);

            if (servicio == null)
            {
                return NotFound();
            }

            return PartialView("~/Views/Servicios/_FormServicio.cshtml", servicio);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ServicesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Eliminar(int id)
        {
            var servicio = await _context.Servicios
                .FirstOrDefaultAsync(s => s.Id == id);

            if (servicio == null)
            {
                return NotFound();
            }

            var tieneCobros = await _context.Cobros
                .AsNoTracking()
                .AnyAsync(c => c.ServicioId == id);

            if (tieneCobros)
            {
                return BadRequest("No se puede eliminar este servicio porque tiene cobros asociados.");
            }

            var tieneCitas = await _context.Citas
                .AsNoTracking()
                .AnyAsync(c => c.ServicioId == id);

            if (tieneCitas)
            {
                return BadRequest("No se puede eliminar este servicio porque tiene citas asociadas.");
            }

            try
            {
                _context.Servicios.Remove(servicio);
                await _context.SaveChangesAsync();
                return Ok();
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al eliminar servicio {ServicioId}.", id);
                return BadRequest("No fue posible eliminar el servicio porque tiene relaciones activas.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Guard bloqueo la eliminacion del servicio {ServicioId}.", id);
                return BadRequest("No fue posible eliminar el servicio por una validacion de seguridad.");
            }
        }

        private async Task ValidateServicioAsync(Servicio servicio)
        {
            if (string.IsNullOrWhiteSpace(servicio.Nombre))
            {
                ModelState.AddModelError(nameof(Servicio.Nombre), "Debe indicar el nombre del servicio.");
            }

            if (servicio.DuracionMinutos.HasValue && servicio.DuracionMinutos.Value <= 0)
            {
                ModelState.AddModelError(nameof(Servicio.DuracionMinutos), "La duracion debe ser mayor a cero.");
            }

            var existeDuplicado = await _context.Servicios
                .AsNoTracking()
                .AnyAsync(s => s.Id != servicio.Id && s.Nombre == servicio.Nombre);

            if (existeDuplicado)
            {
                ModelState.AddModelError(nameof(Servicio.Nombre), "Ya existe un servicio con ese nombre.");
            }
        }

        private static void NormalizeServicio(Servicio servicio)
        {
            servicio.Nombre = CollapseWhitespace(servicio.Nombre);
            servicio.Precio = Math.Round(servicio.Precio, 2, MidpointRounding.AwayFromZero);
            servicio.DuracionMinutos = servicio.DuracionMinutos.HasValue
                ? servicio.DuracionMinutos.Value
                : null;
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
                || message.Contains("IX_Servicios_TenantId_Nombre", StringComparison.OrdinalIgnoreCase);
        }
    }
}
