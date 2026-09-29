using LuxuryApp.Models.Productos;
using LuxuryApp.Services.Identity;
using LuxuryApp.Models.Asociados;
using LuxuryApp.Services.Productos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Productos
{
    [Authorize]
    [RequirePermission(AppPermissions.ProductsView)]
    public class ProductosController : Controller
    {
        private readonly IProductoService _productoService;
        private readonly IProductoQueryService _productoQueryService;
        private readonly LuxuryApp.Services.Finanzas.ILegacyFinancialImpactService _impactoHistorico;
        private readonly ILogger<ProductosController> _logger;

        public ProductosController(
            IProductoService productoService,
            IProductoQueryService productoQueryService,
            ILogger<ProductosController> logger,
            LuxuryApp.Services.Finanzas.ILegacyFinancialImpactService impactoHistorico)
        {
            _productoService = productoService;
            _productoQueryService = productoQueryService;
            _logger = logger;
            _impactoHistorico = impactoHistorico;
        }

        // =========================
        // INDEX
        // =========================
        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            var vm = await _productoQueryService.BuildIndexViewModelAsync(cancellationToken);
            return View(vm);
        }

        // =========================
        // CREATE GET
        // =========================
        public IActionResult Create()
        {
            return View(_productoQueryService.BuildFormViewModel());
        }

        // =========================
        // CREATE POST
        // =========================
        [HttpPost]
        [RequirePermission(AppPermissions.ProductsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(
            [Bind(
                nameof(Producto.NombreProducto),
                nameof(Producto.DetalleProducto),
                nameof(Producto.PrecioProducto),
                nameof(Producto.CantidadProducto),
                nameof(Producto.StockMinimo),
                nameof(Producto.AplicaIva),
                nameof(Producto.TarifaIva),
                nameof(Producto.PrecioIncluyeIva),
                Prefix = "Producto")]
            Producto producto,
            CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return View(_productoQueryService.BuildFormViewModel(producto));
            }

            try
            {
                await _productoService.RegistrarAsync(MapRequest(producto), cancellationToken);
                return RedirectToAction(nameof(Index));
            }
            catch (ProductoValidationException ex)
            {
                ModelState.AddModelError(ex.ModelStateKey ?? string.Empty, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }

            return View(_productoQueryService.BuildFormViewModel(producto));
        }

        // =========================
        // EDIT GET
        // =========================
        public async Task<IActionResult> Edit(int id, CancellationToken cancellationToken)
        {
            var vm = await _productoQueryService.BuildEditViewModelAsync(id, cancellationToken);

            if (vm is null)
            {
                return NotFound();
            }

            return View(vm);
        }

        // =========================
        // EDIT POST
        // =========================
        [HttpPost]
        [RequirePermission(AppPermissions.ProductsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(
            int id,
            [Bind(
                nameof(Producto.IdProducto),
                nameof(Producto.NombreProducto),
                nameof(Producto.DetalleProducto),
                nameof(Producto.PrecioProducto),
                nameof(Producto.CantidadProducto),
                nameof(Producto.StockMinimo),
                nameof(Producto.AplicaIva),
                nameof(Producto.TarifaIva),
                nameof(Producto.PrecioIncluyeIva),
                Prefix = "Producto")]
            Producto producto,
            CancellationToken cancellationToken,
            bool confirmarImpactoHistorico = false)
        {
            if (id != producto.IdProducto)
            {
                return NotFound();
            }

            if (!ModelState.IsValid)
            {
                return View(_productoQueryService.BuildFormViewModel(producto));
            }

            if (await RequiereConfirmacionHistoricaAsync(producto, confirmarImpactoHistorico, cancellationToken))
            {
                return View(_productoQueryService.BuildFormViewModel(producto));
            }

            try
            {
                await _productoService.ActualizarAsync(id, MapRequest(producto), cancellationToken);
                return RedirectToAction(nameof(Index));
            }
            catch (ProductoValidationException ex)
            {
                ModelState.AddModelError(ex.ModelStateKey ?? string.Empty, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
            }

            return View(_productoQueryService.BuildFormViewModel(producto));
        }

        // =========================
        // ACTIVAR / DESACTIVAR
        // =========================
        [HttpPost]
        [RequirePermission(AppPermissions.ProductsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleActivo(int id, CancellationToken cancellationToken)
        {
            try
            {
                var toggled = await _productoService.ToggleActivoAsync(id, cancellationToken);

                if (!toggled)
                {
                    return NotFound();
                }

                return Ok();
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Error al cambiar el estado del producto {ProductoId}.", id);
                return BadRequest(new { message = ex.Message });
            }
        }

        private static ProductoWriteRequest MapRequest(Producto producto)
        {
            return new ProductoWriteRequest
            {
                NombreProducto = producto.NombreProducto,
                DetalleProducto = producto.DetalleProducto,
                PrecioProducto = producto.PrecioProducto,
                CantidadProducto = producto.CantidadProducto,
                StockMinimo = producto.StockMinimo,
                AplicaIva = producto.AplicaIva,
                TarifaIva = producto.TarifaIva,
                PrecioIncluyeIva = producto.PrecioIncluyeIva
            };
        }

        /// <summary>
        /// Igual que en Servicios: si la fiscalidad del producto cambia y existen cobros suyos SIN
        /// snapshot, el guardado se devuelve una vez con el aviso hasta que el usuario confirme.
        /// Se compara contra la fila real en el servidor; la casilla sola no basta.
        /// </summary>
        private async Task<bool> RequiereConfirmacionHistoricaAsync(
            Producto producto,
            bool confirmado,
            CancellationToken cancellationToken)
        {
            if (confirmado)
            {
                return false;
            }

            var actual = await _productoQueryService.ObtenerFiscalidadAsync(producto.IdProducto, cancellationToken);
            if (actual is null)
            {
                return false;
            }

            var cambioFiscal =
                actual.AplicaIva != producto.AplicaIva ||
                actual.TarifaIva != producto.TarifaIva ||
                actual.PrecioIncluyeIva != producto.PrecioIncluyeIva;

            if (!cambioFiscal)
            {
                return false;
            }

            var legacy = await _impactoHistorico.ContarCobrosLegacyDeProductoAsync(
                producto.IdProducto, cancellationToken);

            if (legacy == 0)
            {
                return false;
            }

            ViewData[LuxuryApp.Models.Fiscal.AvisoImpactoHistorico.CampoConfirmacion] =
                LuxuryApp.Models.Fiscal.AvisoImpactoHistorico.Producto(legacy);

            ModelState.AddModelError(
                string.Empty,
                LuxuryApp.Models.Fiscal.AvisoImpactoHistorico.Confirmacion);

            return true;
        }
    }
}