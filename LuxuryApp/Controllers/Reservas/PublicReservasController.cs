using LuxuryApp.Models.Reservas;
using LuxuryApp.Services.Reservas;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LuxuryApp.Controllers.Reservas
{
    /// <summary>
    /// Ruta PÚBLICA de reservas: /reservar/{slug}. Sin login. Resuelve el tenant por slug,
    /// fija el contexto de tenant para el request y solo expone datos públicos y seguros.
    /// No crea citas: registra una solicitud Pending que el negocio confirma desde la plataforma.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting("PublicBooking")]
    [Route("reservar")]
    public sealed class PublicReservasController : Controller
    {
        private readonly IPublicBookingService _publicBookingService;

        public PublicReservasController(IPublicBookingService publicBookingService)
        {
            _publicBookingService = publicBookingService;
        }

        [HttpGet("{slug}")]
        public async Task<IActionResult> Index(
            string slug,
            [FromQuery(Name = "servicioId")] int? servicioId,
            CancellationToken cancellationToken)
        {
            var context = await _publicBookingService.ResolveContextAsync(slug, cancellationToken);
            if (context is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
                return View("NoDisponible");
            }

            Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            var page = await _publicBookingService.BuildPageAsync(context, servicioId, cancellationToken);
            return View(page);
        }

        /// <summary>
        /// Próximos espacios disponibles sin que el visitante haya elegido fecha. Solo acepta
        /// servicio y profesional: la fecha de inicio y el horizonte los decide el servidor, así
        /// que nadie puede pedir un barrido arbitrario de meses desde la URL.
        /// </summary>
        [HttpGet("{slug}/proximos")]
        [EnableRateLimiting("PublicBookingAvailability")]
        public async Task<IActionResult> Proximos(
            string slug,
            int servicioId,
            int? funcionarioId,
            CancellationToken cancellationToken)
        {
            var context = await _publicBookingService.ResolveContextAsync(slug, cancellationToken);
            if (context is null)
            {
                return NotFound();
            }

            var result = await _publicBookingService.GetNextSlotsAsync(
                context,
                servicioId,
                funcionarioId,
                cancellationToken);

            return Json(result);
        }

        [HttpGet("{slug}/disponibilidad")]
        [EnableRateLimiting("PublicBookingAvailability")]
        public async Task<IActionResult> Disponibilidad(
            string slug,
            int servicioId,
            string? fecha,
            int? funcionarioId,
            CancellationToken cancellationToken)
        {
            var context = await _publicBookingService.ResolveContextAsync(slug, cancellationToken);
            if (context is null)
            {
                return NotFound();
            }

            var result = await _publicBookingService.GetAvailabilityAsync(
                context,
                servicioId,
                fecha,
                funcionarioId,
                cancellationToken);

            return Json(result);
        }

        /// <summary>
        /// Crea la solicitud. Desde que queda Pending OCUPA el intervalo en la agenda del
        /// profesional que el servidor le asigna, así que lleva su propia cuota (más estricta que
        /// la de navegación) además del antiforgery, el honeypot, el token de idempotencia y el
        /// tope de pendientes por teléfono que aplica el servicio.
        /// </summary>
        [HttpPost("{slug}/solicitar")]
        [EnableRateLimiting("PublicBookingSubmit")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Solicitar(
            string slug,
            [FromForm] PublicBookingRequestInput input,
            CancellationToken cancellationToken)
        {
            var context = await _publicBookingService.ResolveContextAsync(slug, cancellationToken);
            if (context is null)
            {
                return NotFound();
            }

            var result = await _publicBookingService.SubmitAsync(context, input, cancellationToken);
            return Json(new { success = result.Success, message = result.Message });
        }
    }
}
