using System.Security.Claims;
using LuxuryApp.Models.Horarios;
using LuxuryApp.Services.Horarios;
using LuxuryApp.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Horarios
{
    /// <summary>
    /// Bloqueos recurrentes de horario (almuerzo, limpieza, capacitación...).
    ///
    /// <para>
    /// La regla es la fuente de verdad: acá no se crean citas falsas. La disponibilidad efectiva
    /// la resuelve <see cref="IFuncionarioAvailabilityService"/>, que también usan el calendario y
    /// las reservas públicas.
    /// </para>
    /// </summary>
    [Authorize(Roles = AppRoles.Administrador)]
    public class BloqueosRecurrentesController : Controller
    {
        private readonly IRecurringScheduleService _scheduleService;

        public BloqueosRecurrentesController(IRecurringScheduleService scheduleService)
        {
            _scheduleService = scheduleService;
        }

        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            var vm = await _scheduleService.BuildPageAsync(cancellationToken);
            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> Crear(CancellationToken cancellationToken)
        {
            var vm = await _scheduleService.BuildCreateFormAsync(cancellationToken);
            return View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Crear(
            RecurringScheduleRuleFormViewModel form,
            CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return View("Form", await RehydrateAsync(form, cancellationToken));
            }

            try
            {
                var resultado = await _scheduleService.CreateAsync(form, CurrentUserId(), cancellationToken);

                // Hay citas que coinciden: no se guarda nada todavía. La vista muestra cuántas son
                // y el usuario decide. Las citas existentes NUNCA se mueven ni se cancelan.
                if (resultado.RequiereConfirmacion)
                {
                    form.Conflictos = resultado.Conflictos.Conflictos;
                    ViewData["ConflictosMensaje"] = resultado.Conflictos.Mensaje;
                    return View("Form", await RehydrateAsync(form, cancellationToken));
                }

                TempData["Mensaje"] = resultado.Conflictos.TieneConflictos
                    ? $"Bloqueo creado. {resultado.Conflictos.Mensaje}"
                    : "Bloqueo recurrente creado correctamente.";

                return RedirectToAction(nameof(Index));
            }
            catch (RecurringScheduleValidationException ex)
            {
                ModelState.AddModelError(ex.ModelStateKey ?? string.Empty, ex.Message);
            }

            return View("Form", await RehydrateAsync(form, cancellationToken));
        }

        [HttpGet]
        public async Task<IActionResult> Editar(int id, CancellationToken cancellationToken)
        {
            var vm = await _scheduleService.BuildEditFormAsync(id, cancellationToken);
            return vm is null ? NotFound() : View("Form", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Editar(
            int id,
            RecurringScheduleRuleFormViewModel form,
            CancellationToken cancellationToken)
        {
            form.Id = id;

            if (!ModelState.IsValid)
            {
                return View("Form", await RehydrateAsync(form, cancellationToken));
            }

            try
            {
                var resultado = await _scheduleService.UpdateAsync(id, form, CurrentUserId(), cancellationToken);

                if (resultado.RequiereConfirmacion)
                {
                    form.Conflictos = resultado.Conflictos.Conflictos;
                    ViewData["ConflictosMensaje"] = resultado.Conflictos.Mensaje;
                    return View("Form", await RehydrateAsync(form, cancellationToken));
                }

                TempData["Mensaje"] = "Bloqueo actualizado. Los cambios aplican desde hoy hacia adelante.";
                return RedirectToAction(nameof(Index));
            }
            catch (RecurringScheduleValidationException ex)
            {
                ModelState.AddModelError(ex.ModelStateKey ?? string.Empty, ex.Message);
            }

            return View("Form", await RehydrateAsync(form, cancellationToken));
        }

        [HttpGet]
        public async Task<IActionResult> Detalle(int id, CancellationToken cancellationToken)
        {
            var vm = await _scheduleService.BuildDetailAsync(id, cancellationToken);
            return vm is null ? NotFound() : View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CambiarEstado(int id, bool activa, CancellationToken cancellationToken)
        {
            try
            {
                await _scheduleService.SetActivaAsync(id, activa, CurrentUserId(), cancellationToken);
                TempData["Mensaje"] = activa ? "Bloqueo reactivado." : "Bloqueo pausado.";
            }
            catch (RecurringScheduleValidationException ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Finalizar(int id, CancellationToken cancellationToken)
        {
            try
            {
                await _scheduleService.EndAsync(id, CurrentUserId(), cancellationToken);
                TempData["Mensaje"] = "Bloqueo finalizado. Deja de aplicar desde hoy; el historial se conserva.";
            }
            catch (RecurringScheduleValidationException ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AgregarExcepcion(
            RecurringScheduleExceptionFormViewModel form,
            CancellationToken cancellationToken)
        {
            try
            {
                await _scheduleService.AddExceptionAsync(form, CurrentUserId(), cancellationToken);
                TempData["Mensaje"] = "Excepción registrada. La regla general no cambió.";
            }
            catch (RecurringScheduleValidationException ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction(nameof(Detalle), new { id = form.RuleId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> QuitarExcepcion(int id, int ruleId, CancellationToken cancellationToken)
        {
            try
            {
                await _scheduleService.RemoveExceptionAsync(id, CurrentUserId(), cancellationToken);
                TempData["Mensaje"] = "Excepción eliminada.";
            }
            catch (RecurringScheduleValidationException ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction(nameof(Detalle), new { id = ruleId });
        }

        private async Task<RecurringScheduleRuleFormViewModel> RehydrateAsync(
            RecurringScheduleRuleFormViewModel form,
            CancellationToken cancellationToken)
        {
            var contexto = await _scheduleService.BuildCreateFormAsync(cancellationToken);
            form.FuncionariosDisponibles = contexto.FuncionariosDisponibles;
            return form;
        }

        private string? CurrentUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
