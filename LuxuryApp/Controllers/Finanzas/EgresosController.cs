using LuxuryApp.Services.Identity;
using LuxuryApp.Models.Asociados;
using ClosedXML.Excel;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Exports;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Services.Tenant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Finanzas
{
    [Authorize]
    [RequirePermission(AppPermissions.ExpensesView)]
    public class EgresosController : Controller
    {
        private readonly IEgresoService _egresoService;
        private readonly IEgresoQueryService _egresoQueryService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ITenantDisplayNameService _tenantDisplayNameService;

        public EgresosController(
            IEgresoService egresoService,
            IEgresoQueryService egresoQueryService,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ITenantDisplayNameService tenantDisplayNameService)
        {
            _egresoService = egresoService;
            _egresoQueryService = egresoQueryService;
            _businessDateTimeProvider = businessDateTimeProvider;
            _tenantDisplayNameService = tenantDisplayNameService;
        }

        public async Task<IActionResult> Index(EgresoFiltroViewModel filtros)
        {
            var vm = await _egresoQueryService.BuildIndexViewModelAsync(filtros);
            return View(vm);
        }

        public async Task<IActionResult> Create()
        {
            var vm = await _egresoQueryService.BuildCreateViewModelAsync();
            return View(vm);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ExpensesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(EgresoViewModel vm)
        {
            if (!ModelState.IsValid)
            {
                return View(await _egresoQueryService.BuildCreateViewModelAsync(vm.Egreso));
            }

            try
            {
                await _egresoService.RegistrarAsync(MapRequest(vm.Egreso));
                TempData["Mensaje"] = "Egreso registrado correctamente.";
                return RedirectToAction(nameof(Index));
            }
            catch (EgresoValidationException ex)
            {
                ModelState.AddModelError(ex.ModelStateKey ?? string.Empty, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }

            return View(await _egresoQueryService.BuildCreateViewModelAsync(vm.Egreso));
        }

        [HttpGet]
        public async Task<IActionResult> Edit(int id)
        {
            var vm = await _egresoQueryService.BuildEditViewModelAsync(id);

            if (vm is null)
            {
                return NotFound();
            }

            return View(vm);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ExpensesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, EgresoViewModel vm)
        {
            if (id != vm.Egreso.IdEgreso)
            {
                return BadRequest();
            }

            if (!ModelState.IsValid)
            {
                var invalidVm = await _egresoQueryService.BuildEditViewModelAsync(id, vm.Egreso);
                return invalidVm is null ? NotFound() : View(invalidVm);
            }

            try
            {
                var updated = await _egresoService.ActualizarAsync(MapUpdateRequest(vm.Egreso));

                if (!updated)
                {
                    return NotFound();
                }

                TempData["Mensaje"] = "Egreso actualizado correctamente.";
                return RedirectToAction(nameof(Index));
            }
            catch (EgresoValidationException ex)
            {
                ModelState.AddModelError(ex.ModelStateKey ?? string.Empty, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }

            var editVm = await _egresoQueryService.BuildEditViewModelAsync(id, vm.Egreso);
            return editVm is null ? NotFound() : View(editVm);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ExpensesManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            try
            {
                var deleted = await _egresoService.EliminarAsync(id);

                if (!deleted)
                {
                    return NotFound();
                }

                TempData["Mensaje"] = "Egreso eliminado correctamente.";
            }
            catch (EgresoValidationException ex)
            {
                TempData["Error"] = ex.Message;
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = ex.Message;
            }

            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> ExportarExcel(EgresoFiltroViewModel filtros)
        {
            var reporte = await _egresoQueryService.BuildIndexViewModelAsync(
                filtros,
                includeFilterOptions: false);

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Reporte Egresos");

            var colorNegro = XLColor.FromHtml("#1C1C1C");
            var colorDorado = XLColor.FromHtml("#C6A55C");
            var colorGrisSuave = XLColor.FromHtml("#F5F5F5");
            const string excelCurrencyFormat = "CRC #,##0.00";

            var nombreNegocio = await _tenantDisplayNameService.GetCurrentTenantDisplayNameAsync(HttpContext.RequestAborted);

            ws.Range("A1:E1").Merge();
            ws.Cell("A1").Value = nombreNegocio;
            ws.Cell("A1").Style.Font.FontSize = 20;
            ws.Cell("A1").Style.Font.Bold = true;
            ws.Cell("A1").Style.Font.FontColor = colorDorado;
            ws.Cell("A1").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            ws.Range("A2:E2").Merge();
            ws.Cell("A2").Value = "Reporte Financiero de Egresos";
            ws.Cell("A2").Style.Font.FontSize = 14;
            ws.Cell("A2").Style.Font.Bold = true;
            ws.Cell("A2").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            ws.Range("A3:E3").Merge();
            var generatedAt = _businessDateTimeProvider.Now();

            ws.Cell("A3").Value = $"Generado el {generatedAt:dd/MM/yyyy HH:mm}";
            ws.Cell("A3").Style.Font.Italic = true;
            ws.Cell("A3").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            var kpiRow = 5;

            ws.Cell(kpiRow, 1).Value = "Cantidad Registros";
            ws.Cell(kpiRow, 2).Value = reporte.CantidadRegistros;
            ws.Cell(kpiRow, 3).Value = "Monto Total";
            ws.Cell(kpiRow, 4).Value = reporte.TotalEgresos;

            var kpiRange = ws.Range(kpiRow, 1, kpiRow, 5);
            kpiRange.Style.Fill.BackgroundColor = colorGrisSuave;
            kpiRange.Style.Font.Bold = true;
            ws.Cell(kpiRow, 4).Style.NumberFormat.Format = excelCurrencyFormat;

            var headerRow = 7;

            ws.Cell(headerRow, 1).Value = "Fecha";
            ws.Cell(headerRow, 2).Value = "Categoria";
            ws.Cell(headerRow, 3).Value = "Detalle";
            ws.Cell(headerRow, 4).Value = "Monto";
            ws.Cell(headerRow, 5).Value = "Metodo Pago";

            var header = ws.Range(headerRow, 1, headerRow, 5);
            header.Style.Fill.BackgroundColor = colorNegro;
            header.Style.Font.FontColor = XLColor.White;
            header.Style.Font.Bold = true;
            header.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            var fila = headerRow + 1;

            foreach (var egreso in reporte.Egresos)
            {
                ws.Cell(fila, 1).Value = egreso.FechaEgreso;
                ws.Cell(fila, 1).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
                ws.Cell(fila, 2).Value = egreso.CategoriaNombre;
                ws.Cell(fila, 3).Value = egreso.Detalle;
                ws.Cell(fila, 4).Value = egreso.Monto;
                ws.Cell(fila, 4).Style.NumberFormat.Format = excelCurrencyFormat;
                ws.Cell(fila, 5).Value = egreso.MetodoPago;
                fila++;
            }

            ws.Columns().AdjustToContents();

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);

            return File(
                stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ExcelReportFileNameBuilder.Build(nombreNegocio, "Reporte Egresos", generatedAt));
        }

        private static EgresoCreateRequest MapRequest(Egreso egreso) =>
            new()
            {
                FechaEgreso = egreso.FechaEgreso,
                Detalle = egreso.Detalle,
                Monto = egreso.Monto,
                MetodoPago = egreso.MetodoPago,
                CategoriaId = egreso.CategoriaId
            };

        private static EgresoUpdateRequest MapUpdateRequest(Egreso egreso) =>
            new()
            {
                IdEgreso = egreso.IdEgreso,
                FechaEgreso = egreso.FechaEgreso,
                Detalle = egreso.Detalle,
                Monto = egreso.Monto,
                MetodoPago = egreso.MetodoPago,
                CategoriaId = egreso.CategoriaId
            };
    }
}
