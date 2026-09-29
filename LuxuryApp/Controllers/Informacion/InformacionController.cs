using LuxuryApp.Services.Identity;
using LuxuryApp.Models.Asociados;
using LuxuryApp.Services.Informacion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers
{
    [Authorize]
    [RequirePermission(AppPermissions.InformationView)]
    public class InformacionController : Controller
    {
        private readonly IInformacionNegocioQueryService _informacionNegocioQueryService;

        public InformacionController(IInformacionNegocioQueryService informacionNegocioQueryService)
        {
            _informacionNegocioQueryService = informacionNegocioQueryService;
        }

        public async Task<IActionResult> Index(int? mes, int? anio, int top = 10)
        {
            var vm = await _informacionNegocioQueryService.BuildViewModelAsync(mes, anio, top);
            return View(vm);
        }

        [HttpGet]
        public async Task<IActionResult> ObtenerCitasSemana(DateTime semana)
        {
            var resultado = await _informacionNegocioQueryService.BuildCitasSemanaAsync(semana);
            return Json(resultado);
        }
    }
}
