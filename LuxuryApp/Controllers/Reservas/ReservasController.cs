using LuxuryApp.Services.Identity;
using LuxuryApp.Models.Asociados;
using System.Security.Claims;
using LuxuryApp.Models.Horarios;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Services.Common;
using LuxuryApp.Services.Horarios;
using LuxuryApp.Services.Reservas;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Controllers.Reservas
{
    /// <summary>
    /// Panel privado de "Solicitudes de reserva" y su configuración. Solo el dueño del negocio
    /// (Administrador). Todas las operaciones son tenant-scoped por el global query filter.
    /// </summary>
    [Authorize]
    [RequirePermission(AppPermissions.ReservationsView)]
    public sealed class ReservasController : Controller
    {
        private readonly IBookingRequestService _bookingRequestService;
        private readonly IBookingSettingsService _bookingSettingsService;
        private readonly IBookingCatalogService _bookingCatalogService;
        private readonly IBookingQrCodeService _bookingQrCodeService;
        private readonly IRecurringScheduleService _recurringScheduleService;
        private readonly PublicSiteOptions _publicSiteOptions;

        public ReservasController(
            IBookingRequestService bookingRequestService,
            IBookingSettingsService bookingSettingsService,
            IBookingCatalogService bookingCatalogService,
            IBookingQrCodeService bookingQrCodeService,
            IRecurringScheduleService recurringScheduleService,
            IOptions<PublicSiteOptions> publicSiteOptions)
        {
            _bookingRequestService = bookingRequestService;
            _bookingSettingsService = bookingSettingsService;
            _bookingCatalogService = bookingCatalogService;
            _bookingQrCodeService = bookingQrCodeService;
            _recurringScheduleService = recurringScheduleService;
            _publicSiteOptions = publicSiteOptions.Value;
        }

        [HttpGet]
        public async Task<IActionResult> Index(string? estado, string? rango, CancellationToken cancellationToken)
        {
            var model = await _bookingRequestService.BuildPageAsync(estado, rango, cancellationToken);
            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Lista(string? estado, string? rango, CancellationToken cancellationToken)
        {
            var model = await _bookingRequestService.BuildPageAsync(estado, rango, cancellationToken);
            return PartialView("_SolicitudesList", model);
        }

        /// <summary>
        /// Estado del cliente de una solicitud pendiente. Consulta administrativa: exige el mismo
        /// permiso que confirmar, porque devuelve datos de la base de Clientes.
        /// </summary>
        [HttpGet]
        [RequirePermission(AppPermissions.ReservationsManage)]
        public async Task<IActionResult> ClientePrevio(int id, CancellationToken cancellationToken)
        {
            if (id <= 0)
            {
                return BadRequest(new { success = false, message = "Solicitud inválida." });
            }

            var preview = await _bookingRequestService.PreviewClienteAsync(id, cancellationToken);

            if (preview is null)
            {
                return NotFound(new { success = false, message = "La solicitud no existe o ya fue procesada." });
            }

            return Ok(new
            {
                success = true,
                estado = preview.Status.ToString(),
                puedeConfirmarDirecto = preview.PuedeConfirmarDirecto,
                nombreReserva = preview.NombreCliente,
                telefonoReserva = preview.TelefonoCliente,
                coincidencias = preview.Matches.Select(m => new
                {
                    id = m.ClienteId,
                    nombre = m.Nombre,
                    telefono = m.NumeroTelefono
                })
            });
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ReservationsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Confirmar(
            int id,
            int? funcionarioId,
            string? clienteAccion,
            int? clienteId,
            CancellationToken cancellationToken)
        {
            if (id <= 0)
            {
                return BadRequest(new { success = false, message = "Solicitud inválida." });
            }

            var result = await _bookingRequestService.ConfirmAsync(
                id,
                funcionarioId,
                CurrentUserId(),
                BookingClienteChoice.Parse(clienteAccion, clienteId),
                cancellationToken);

            return result.Success
                ? Ok(new { success = true, message = result.Message, citaId = result.CitaId, whatsAppStatus = result.WhatsAppStatus })
                : BadRequest(new { success = false, message = result.Message });
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ReservationsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Rechazar(int id, string? motivo, CancellationToken cancellationToken)
        {
            if (id <= 0)
            {
                return BadRequest(new { success = false, message = "Solicitud inválida." });
            }

            var result = await _bookingRequestService.RejectAsync(id, motivo, CurrentUserId(), cancellationToken);
            return result.Success
                ? Ok(new { success = true, message = result.Message })
                : BadRequest(new { success = false, message = result.Message });
        }

        [HttpGet]
        public async Task<IActionResult> Servicios(CancellationToken cancellationToken)
        {
            var model = await _bookingCatalogService.BuildManagementAsync(cancellationToken);
            return View(model);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ReservationsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GuardarServicios(
            [FromBody] BookingCatalogSaveInput input,
            CancellationToken cancellationToken)
        {
            if (input is null)
            {
                return BadRequest(new { success = false, message = "Datos inválidos." });
            }

            await _bookingCatalogService.SaveAsync(input, CurrentUserId(), cancellationToken);
            return Ok(new { success = true, message = "Servicios publicados actualizados." });
        }

        [HttpGet]
        public async Task<IActionResult> Configuracion(CancellationToken cancellationToken)
        {
            var model = await _bookingSettingsService.BuildSettingsViewModelAsync(cancellationToken);
            await DecorateAsync(model, cancellationToken);
            return View(model);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ReservationsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Configuracion(BookingSettingsViewModel model, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return View(await RehydrateAsync(model, cancellationToken));
            }

            try
            {
                await _bookingSettingsService.SaveSettingsAsync(model, CurrentUserId(), cancellationToken);

                // POST-REDIRECT-GET: refrescar no reenvía el formulario y la pantalla siempre
                // muestra el estado realmente persistido, no el que se acaba de enviar.
                TempData["ReservasConfigOk"] = "Configuración guardada correctamente.";
                return RedirectToAction(nameof(Configuracion));
            }
            catch (BookingValidationException ex)
            {
                ModelState.AddModelError(ex.Field ?? string.Empty, ex.Message);
                return View(await RehydrateAsync(model, cancellationToken));
            }
        }

        /// <summary>
        /// QR del enlace público del TENANT ACTUAL. El slug se resuelve en el servidor a partir del
        /// contexto autenticado: no se acepta por query string, así nadie puede pedir el QR de otro
        /// negocio. La imagen se genera al vuelo y no se guarda en ninguna parte.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> QrCode(string? formato, CancellationToken cancellationToken)
        {
            var settings = await _bookingSettingsService.BuildSettingsViewModelAsync(cancellationToken);
            var slug = settings.PublicBookingSlug;
            var url = BookingLinkBuilder.BuildCanonical(_publicSiteOptions, Request, slug);

            if (string.IsNullOrWhiteSpace(url))
            {
                return NotFound();
            }

            var esSvg = string.Equals(formato, "svg", StringComparison.OrdinalIgnoreCase);
            var imagen = esSvg
                ? _bookingQrCodeService.CreateSvg(url, slug!)
                : _bookingQrCodeService.CreatePng(url, slug!);

            var descarga = string.Equals(
                Request.Query["descargar"], "1", StringComparison.Ordinal);

            return descarga
                ? File(imagen.Contenido, imagen.ContentType, imagen.NombreArchivo)
                : File(imagen.Contenido, imagen.ContentType);
        }

        /// <summary>
        /// Repone lo que el formulario no envía (nombre del negocio, enlace, QR, bloqueos) para que
        /// un re-render por error de validación no pierda contexto.
        /// </summary>
        private async Task<BookingSettingsViewModel> RehydrateAsync(
            BookingSettingsViewModel model,
            CancellationToken cancellationToken)
        {
            var persistido = await _bookingSettingsService.BuildSettingsViewModelAsync(cancellationToken);
            model.NombreNegocio = persistido.NombreNegocio;

            // Si el POST vino sin jornada (formulario manipulado o parcial), se muestra la guardada
            // en vez de siete filas vacías.
            if (model.Horario.Count == 0)
            {
                model.Horario = persistido.Horario;
            }

            await DecorateAsync(model, cancellationToken);
            return model;
        }

        private async Task DecorateAsync(BookingSettingsViewModel model, CancellationToken cancellationToken)
        {
            model.LinkPublico = BookingLinkBuilder.BuildCanonical(
                _publicSiteOptions, Request, model.PublicBookingSlug);

            if (!string.IsNullOrWhiteSpace(model.LinkPublico))
            {
                model.QrPngUrl = Url.Action(nameof(QrCode), new { formato = "png" });
                model.QrDescargaUrl = Url.Action(nameof(QrCode), new { formato = "png", descargar = "1" });
            }

            model.BloqueosRecurrentes = await BuildBloqueosAsync(cancellationToken);
        }

        /// <summary>
        /// Resumen de solo lectura de los bloqueos recurrentes vigentes. Se lee del MISMO servicio
        /// que administra el módulo de Bloqueos de horario: acá no hay una segunda definición de
        /// "almuerzo de 12 a 1", y por eso tampoco puede divergir de lo que aplica la agenda.
        /// </summary>
        private async Task<IReadOnlyList<BookingRecurringBlockSummary>> BuildBloqueosAsync(
            CancellationToken cancellationToken)
        {
            var pagina = await _recurringScheduleService.BuildPageAsync(cancellationToken);

            return pagina.Reglas
                .Where(regla => regla.Activa)
                .Select(regla => new BookingRecurringBlockSummary(
                    regla.Id,
                    regla.Nombre,
                    regla.Horario,
                    regla.Dias))
                .ToList();
        }

        private string? CurrentUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
