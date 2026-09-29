using LuxuryApp.Models.Asociados;
using LuxuryApp.Services.Asociados;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Finanzas
{
    /// <summary>
    /// Dashboard financiero del negocio.
    ///
    /// <para>
    /// <c>Dashboard.View</c> ES el permiso financiero: quien llega acá puede ver ingresos,
    /// egresos y ganancia. Por eso el KPI de participación de asociados se construye únicamente
    /// dentro de esta acción y solo cuando hay participaciones vigentes.
    /// </para>
    /// </summary>
    [Authorize]
    [RequirePermission(AppPermissions.DashboardView)]
    public class DashboardController : Controller
    {
        private readonly IDashboardFinancieroQueryService _dashboardFinancieroQueryService;
        private readonly IAssociateProfitAllocationService _allocationService;

        public DashboardController(
            IDashboardFinancieroQueryService dashboardFinancieroQueryService,
            IAssociateProfitAllocationService allocationService)
        {
            _dashboardFinancieroQueryService = dashboardFinancieroQueryService;
            _allocationService = allocationService;
        }

        public async Task<IActionResult> Index(int? mes, int? anio, CancellationToken cancellationToken)
        {
            var vm = await _dashboardFinancieroQueryService.BuildViewModelAsync(mes, anio, cancellationToken);

            // Devuelve null si nadie participa del periodo: en ese caso ni siquiera se ejecuta el
            // cálculo de la ganancia distribuible.
            vm.ParticipacionAsociados = await _allocationService.BuildMonthlyKpiAsync(
                vm.MesSeleccionado,
                vm.AnioSeleccionado,
                cancellationToken);

            return View(vm);
        }
    }
}
