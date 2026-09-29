using LuxuryApp.Services.Comprobantes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Comprobantes
{
    /// <summary>
    /// Ruta PÚBLICA del comprobante interno: sin login, resuelta por un token largo aleatorio
    /// (no adivinable, no incremental). Solo muestra/descarga ese comprobante; no permite listar
    /// ni modificar nada, ni expone datos internos del sistema. Es la única excepción al filtro
    /// de tenant y por eso pasa por <see cref="IComprobanteCobroService.ObtenerPorTokenPublicoAsync"/>.
    /// </summary>
    [AllowAnonymous]
    [Route("comprobantes")]
    public sealed class ComprobantesController : Controller
    {
        private readonly IComprobanteCobroService _service;

        public ComprobantesController(IComprobanteCobroService service)
        {
            _service = service;
        }

        [HttpGet("{token}")]
        public async Task<IActionResult> Ver(string token, CancellationToken cancellationToken)
        {
            var comprobante = await _service.ObtenerPorTokenPublicoAsync(token, cancellationToken);
            if (comprobante is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return View("NoEncontrado");
            }

            // Evita que buscadores indexen comprobantes individuales.
            Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            return View(comprobante);
        }

        [HttpGet("{token}/pdf")]
        public async Task<IActionResult> Pdf(string token, CancellationToken cancellationToken)
        {
            var comprobante = await _service.ObtenerPorTokenPublicoAsync(token, cancellationToken);
            if (comprobante is null)
            {
                return NotFound();
            }

            var pdf = _service.GenerarPdf(comprobante);
            return File(pdf, "application/pdf", $"{comprobante.NumeroInterno}.pdf");
        }
    }
}
