using System.Globalization;
using System.Security.Claims;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.Comprobantes;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Services.Funcionarios;
using LuxuryApp.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Funcionarios
{
    /// <summary>
    /// Portal limitado del funcionario. Solo accesible con el rol Funcionario.
    /// El FuncionarioId se toma del claim (firmado en la cookie); jamás de la URL.
    /// Los permisos se validan en backend contra base de datos en cada acción sensible.
    /// </summary>
    [Authorize(Policy = AppAuthorizationPolicies.RequireFuncionario)]
    [Route("MiPortal")]
    public sealed class MiPortalController : Controller
    {
        private readonly IFuncionarioPortalQueryService _portalQueryService;
        private readonly IFuncionarioPortalPermissionService _permissionService;
        private readonly ICalendarCommandService _calendarCommandService;
        private readonly ICobroService _cobroService;
        private readonly IComprobanteCobroService _comprobanteService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ILogger<MiPortalController> _logger;

        public MiPortalController(
            IFuncionarioPortalQueryService portalQueryService,
            IFuncionarioPortalPermissionService permissionService,
            ICalendarCommandService calendarCommandService,
            ICobroService cobroService,
            IComprobanteCobroService comprobanteService,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ILogger<MiPortalController> logger)
        {
            _portalQueryService = portalQueryService;
            _permissionService = permissionService;
            _calendarCommandService = calendarCommandService;
            _cobroService = cobroService;
            _comprobanteService = comprobanteService;
            _businessDateTimeProvider = businessDateTimeProvider;
            _logger = logger;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            var ctx = await ResolverContextoAsync(cancellationToken);
            if (ctx.Bloqueo is not null)
            {
                return ctx.Bloqueo;
            }

            var model = await _portalQueryService.ObtenerPanelAsync(
                ctx.FuncionarioId,
                ctx.Permisos!.RegistrarMisCobros,
                cancellationToken);

            return View(model);
        }

        [HttpGet("Calendario")]
        public async Task<IActionResult> Calendario(string? fecha, string? rango, CancellationToken cancellationToken)
        {
            var ctx = await ResolverContextoAsync(cancellationToken);
            if (ctx.Bloqueo is not null)
            {
                return ctx.Bloqueo;
            }

            if (!ctx.Permisos!.VerMiCalendario)
            {
                return SinPermiso("tu calendario");
            }

            var fechaSeleccionada = TryParseFecha(fecha, out var parsed)
                ? parsed
                : _businessDateTimeProvider.Today();

            var model = await _portalQueryService.ObtenerCalendarioAsync(
                ctx.FuncionarioId,
                fechaSeleccionada,
                rango ?? "dia",
                ctx.Permisos.CrearMisCitas,
                ctx.Permisos.EditarMisCitas,
                ctx.Permisos.CancelarMisCitas,
                ctx.Permisos.RegistrarMisCobros,
                cancellationToken);

            return View(model);
        }

        [HttpGet("Calendario/Control")]
        public async Task<IActionResult> Control(string? fecha, string? rango, CancellationToken cancellationToken)
        {
            var ctx = await ResolverContextoAsync(cancellationToken);
            if (ctx.Bloqueo is not null)
            {
                return Unauthorized();
            }

            if (!ctx.Permisos!.VerMiCalendario)
            {
                return StatusCode(403);
            }

            var fechaSeleccionada = TryParseFecha(fecha, out var parsed)
                ? parsed
                : _businessDateTimeProvider.Today();

            // Tenant + FuncionarioId del claim. El rango se sanitiza en el servicio.
            var control = await _portalQueryService.ObtenerControlAsync(
                ctx.FuncionarioId, fechaSeleccionada, rango ?? "dia", cancellationToken);

            ViewData["PuedeRegistrarCobros"] = ctx.Permisos.RegistrarMisCobros;
            return PartialView("_PortalControlBody", control);
        }

        [HttpGet("Calendario/CitasDia")]
        public async Task<IActionResult> CitasDia(string? fecha, CancellationToken cancellationToken)
        {
            var ctx = await ResolverContextoAsync(cancellationToken);
            if (ctx.Bloqueo is not null)
            {
                return Ok(Array.Empty<object>());
            }

            if (!ctx.Permisos!.VerMiCalendario)
            {
                return Ok(Array.Empty<object>());
            }

            var fechaSeleccionada = TryParseFecha(fecha, out var parsed)
                ? parsed
                : _businessDateTimeProvider.Today();

            // Siempre del funcionario del claim. Nunca de la URL/form.
            var citas = await _portalQueryService.ObtenerCitasDiaAsync(ctx.FuncionarioId, fechaSeleccionada, cancellationToken);

            return Ok(citas.Select(c => new
            {
                id = c.Id,
                fechaHora = c.FechaHora.ToString("yyyy-MM-ddTHH:mm:ss"),
                horaMinutos = (c.FechaHora.Hour * 60) + c.FechaHora.Minute,
                duracion = c.DuracionEfectiva,
                cliente = c.Cliente,
                servicio = c.Servicio,
                telefono = c.Telefono,
                tipo = c.Tipo,
                esCita = c.EsCita,
                yaCobrada = c.YaCobrada,
                esCobrable = c.EsCobrable,
                servicioId = c.ServicioId,
                clienteId = c.ClienteId,
                servicioPersonalizado = c.ServicioPersonalizado,
                nombreClienteRaw = c.NombreClienteRaw,
                fechaHoraInput = c.FechaHoraInput,
                precio = c.PrecioServicio,
                puedeEditar = ctx.Permisos.EditarMisCitas,
                puedeCancelar = ctx.Permisos.CancelarMisCitas,
                puedeCobrar = ctx.Permisos.RegistrarMisCobros
            }));
        }

        [HttpGet("Ganancias")]
        public async Task<IActionResult> Ganancias(string? semana, string? mes, int meses = 6, CancellationToken cancellationToken = default)
        {
            var ctx = await ResolverContextoAsync(cancellationToken);
            if (ctx.Bloqueo is not null)
            {
                return ctx.Bloqueo;
            }

            if (!ctx.Permisos!.VerMisGanancias)
            {
                return SinPermiso("tus ganancias");
            }

            DateTime? semanaAnchor = TryParseFecha(semana, out var s) ? s : (DateTime?)null;
            DateTime? mesAnchor = TryParseFecha(mes, out var m) ? m : (DateTime?)null;

            // El gráfico de evolución solo admite 6 ó 12 meses (el servicio re-valida).
            var mesesEvolucion = meses == 12 ? 12 : 6;

            var model = await _portalQueryService.ObtenerGananciasAsync(
                ctx.FuncionarioId, semanaAnchor, mesAnchor, mesesEvolucion, cancellationToken);
            return View(model);
        }

        [HttpGet("Pagos")]
        public async Task<IActionResult> Pagos(int pagina = 1, CancellationToken cancellationToken = default)
        {
            var ctx = await ResolverContextoAsync(cancellationToken);
            if (ctx.Bloqueo is not null)
            {
                return ctx.Bloqueo;
            }

            if (!ctx.Permisos!.VerMisPagos)
            {
                return SinPermiso("tus pagos");
            }

            var model = await _portalQueryService.ObtenerPagosAsync(ctx.FuncionarioId, pagina, cancellationToken);
            return View(model);
        }

        [HttpGet("Cobros")]
        public async Task<IActionResult> Cobros(
            int pagina = 1,
            string? rango = null,
            string? metodo = null,
            string? origen = null,
            CancellationToken cancellationToken = default)
        {
            var ctx = await ResolverContextoAsync(cancellationToken);
            if (ctx.Bloqueo is not null)
            {
                return ctx.Bloqueo;
            }

            if (!ctx.Permisos!.VerMisCobros)
            {
                return SinPermiso("tus cobros");
            }

            var model = await _portalQueryService.ObtenerCobrosAsync(
                ctx.FuncionarioId,
                pagina,
                rango ?? "semana",
                metodo,
                origen,
                ctx.Permisos.RegistrarMisCobros,
                ctx.Permisos.RegistrarMisCobrosManuales,
                cancellationToken);

            return View(model);
        }

        [HttpPost("Calendario/CrearCita")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CrearCita(
            string? fechaHora,
            string? nombreCliente,
            string? telefonoCliente,
            int? servicioId,
            string? servicioPersonalizado,
            int? duracionMinutos,
            CancellationToken cancellationToken)
        {
            var ctx = await ResolverContextoAsync(cancellationToken);
            if (ctx.Bloqueo is not null)
            {
                return ctx.Bloqueo;
            }

            if (!ctx.Permisos!.CrearMisCitas)
            {
                _logger.LogWarning(
                    "Intento de crear cita sin permiso. FuncionarioId {FuncionarioId}. UserId {UserId}.",
                    ctx.FuncionarioId,
                    User.FindFirstValue(CustomClaimTypes.UserId));
                return SinPermiso("agendar citas");
            }

            if (!TryParseFechaHora(fechaHora, out var fechaHoraCita))
            {
                TempData["PortalError"] = "Indica una fecha y hora válidas para la cita.";
                return RedirectToAction(nameof(Calendario));
            }

            var esPersonalizado = !servicioId.HasValue || servicioId.Value <= 0;

            var request = new CalendarUpsertRequest
            {
                // 🔒 FuncionarioId SIEMPRE del claim. Se ignora cualquier valor del navegador.
                FuncionarioId = ctx.FuncionarioId,
                Tipo = "CITA",
                FechaHoraCita = fechaHoraCita,
                NombreCliente = nombreCliente,
                TelefonoCliente = telefonoCliente,
                ServicioId = esPersonalizado ? null : servicioId,
                EsServicioPersonalizado = esPersonalizado,
                ServicioNombrePersonalizado = esPersonalizado ? servicioPersonalizado : null,
                DuracionMinutos = esPersonalizado ? duracionMinutos : null
            };

            try
            {
                var creada = await _calendarCommandService.CreateAsync(request, cancellationToken);
                _logger.LogInformation(
                    "Cita creada desde portal. TenantId-claim {Tenant}. FuncionarioId {FuncionarioId}. CitaId {CitaId}. UserId {UserId}.",
                    User.FindFirstValue(CustomClaimTypes.TenantId),
                    ctx.FuncionarioId,
                    creada.Id,
                    User.FindFirstValue(CustomClaimTypes.UserId));
                TempData["PortalMensaje"] = "Cita agendada correctamente.";
            }
            catch (CalendarValidationException ex)
            {
                TempData["PortalError"] = ex.Message;
            }
            catch (InvalidOperationException ex)
            {
                TempData["PortalError"] = ex.Message;
            }

            return RedirectToAction(nameof(Calendario), new { fecha = fechaHoraCita.ToString("yyyy-MM-dd") });
        }

        [HttpPost("Calendario/EditarCita")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditarCita(
            int citaId,
            string? fechaHora,
            string? nombreCliente,
            string? telefonoCliente,
            int? clienteId,
            int? servicioId,
            string? servicioPersonalizado,
            int? duracionMinutos,
            CancellationToken cancellationToken)
        {
            var ctx = await ResolverContextoAsync(cancellationToken);
            if (ctx.Bloqueo is not null)
            {
                return ctx.Bloqueo;
            }

            if (!ctx.Permisos!.EditarMisCitas)
            {
                _logger.LogWarning(
                    "Intento de editar cita sin permiso. FuncionarioId {FuncionarioId}. UserId {UserId}.",
                    ctx.FuncionarioId,
                    User.FindFirstValue(CustomClaimTypes.UserId));
                return SinPermiso("editar citas");
            }

            // 🔒 Verifica que la cita es del funcionario ANTES de actualizar
            // (UpdateAsync reasigna FuncionarioId, así que sin este check se podría secuestrar una cita ajena).
            var propia = await _portalQueryService.ObtenerCitaCobrableAsync(ctx.FuncionarioId, citaId, cancellationToken);
            if (propia is null)
            {
                TempData["PortalError"] = "La cita no existe o no es tuya.";
                return RedirectToAction(nameof(Calendario));
            }

            if (!TryParseFechaHora(fechaHora, out var fechaHoraCita))
            {
                TempData["PortalError"] = "Indica una fecha y hora válidas para la cita.";
                return RedirectToAction(nameof(Calendario));
            }

            var esPersonalizado = !servicioId.HasValue || servicioId.Value <= 0;

            var request = new CalendarUpsertRequest
            {
                FuncionarioId = ctx.FuncionarioId, // 🔒 del claim
                Tipo = "CITA",
                FechaHoraCita = fechaHoraCita,
                NombreCliente = nombreCliente,
                TelefonoCliente = telefonoCliente,
                ClienteId = clienteId,
                ServicioId = esPersonalizado ? null : servicioId,
                EsServicioPersonalizado = esPersonalizado,
                ServicioNombrePersonalizado = esPersonalizado ? servicioPersonalizado : null,
                DuracionMinutos = esPersonalizado ? duracionMinutos : null
            };

            try
            {
                await _calendarCommandService.UpdateAsync(citaId, request, cancellationToken);
                _logger.LogInformation(
                    "Cita editada desde portal. FuncionarioId {FuncionarioId}. CitaId {CitaId}. UserId {UserId}.",
                    ctx.FuncionarioId,
                    citaId,
                    User.FindFirstValue(CustomClaimTypes.UserId));
                TempData["PortalMensaje"] = "Cita actualizada correctamente.";
            }
            catch (CalendarValidationException ex)
            {
                TempData["PortalError"] = ex.Message;
            }
            catch (InvalidOperationException ex)
            {
                TempData["PortalError"] = ex.Message;
            }

            return RedirectToAction(nameof(Calendario), new { fecha = fechaHoraCita.ToString("yyyy-MM-dd") });
        }

        [HttpPost("Calendario/CancelarCita")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelarCita(int citaId, string? fecha, CancellationToken cancellationToken)
        {
            var ctx = await ResolverContextoAsync(cancellationToken);
            if (ctx.Bloqueo is not null)
            {
                return ctx.Bloqueo;
            }

            if (!ctx.Permisos!.CancelarMisCitas)
            {
                _logger.LogWarning(
                    "Intento de cancelar cita sin permiso. FuncionarioId {FuncionarioId}. UserId {UserId}.",
                    ctx.FuncionarioId,
                    User.FindFirstValue(CustomClaimTypes.UserId));
                return SinPermiso("cancelar citas");
            }

            // 🔒 Solo se puede cancelar una cita propia.
            var propia = await _portalQueryService.ObtenerCitaCobrableAsync(ctx.FuncionarioId, citaId, cancellationToken);
            if (propia is null)
            {
                TempData["PortalError"] = "La cita no existe o no es tuya.";
                return RedirectToAction(nameof(Calendario));
            }

            try
            {
                // El portal del funcionario no pide motivo: el aviso de WhatsApp (solo para citas
                // que vienen de una reserva online) usa el texto neutro por defecto.
                await _calendarCommandService.DeleteAsync(citaId, motivoCancelacion: null, cancellationToken);
                _logger.LogInformation(
                    "Cita cancelada desde portal. FuncionarioId {FuncionarioId}. CitaId {CitaId}. UserId {UserId}.",
                    ctx.FuncionarioId,
                    citaId,
                    User.FindFirstValue(CustomClaimTypes.UserId));
                TempData["PortalMensaje"] = "Cita cancelada correctamente.";
            }
            catch (InvalidOperationException ex)
            {
                TempData["PortalError"] = ex.Message;
            }

            return RedirectToAction(nameof(Calendario), new { fecha });
        }

        [HttpPost("Calendario/RedimensionarCita")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RedimensionarCita(int citaId, int duracionMinutos, CancellationToken cancellationToken)
        {
            var ctx = await ResolverContextoAsync(cancellationToken);
            if (ctx.Bloqueo is not null)
            {
                return Unauthorized();
            }

            if (!ctx.Permisos!.EditarMisCitas)
            {
                _logger.LogWarning(
                    "Intento de redimensionar cita sin permiso. FuncionarioId {FuncionarioId}. UserId {UserId}.",
                    ctx.FuncionarioId,
                    User.FindFirstValue(CustomClaimTypes.UserId));
                return BadRequest(new { error = "No tienes permiso para editar citas." });
            }

            // 🔒 Solo redimensiona una cita propia (ResizeDurationAsync no filtra por funcionario).
            var propia = await _portalQueryService.ObtenerCitaCobrableAsync(ctx.FuncionarioId, citaId, cancellationToken);
            if (propia is null)
            {
                return BadRequest(new { error = "La cita no existe o no es tuya." });
            }

            try
            {
                await _calendarCommandService.ResizeDurationAsync(citaId, duracionMinutos, cancellationToken);
                _logger.LogInformation(
                    "Cita redimensionada desde portal. FuncionarioId {FuncionarioId}. CitaId {CitaId}. Duracion {Dur}.",
                    ctx.FuncionarioId, citaId, duracionMinutos);
                return Ok(new { success = true });
            }
            catch (CalendarValidationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("Cobros/Registrar")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegistrarCobro(
            int citaId,
            decimal monto,
            string? metodoPago,
            string? observacion,
            string? retorno,
            bool enviarComprobante,
            string? emailComprobante,
            bool guardarEmailEnCliente,
            CancellationToken cancellationToken)
        {
            var ctx = await ResolverContextoAsync(cancellationToken);
            if (ctx.Bloqueo is not null)
            {
                return ctx.Bloqueo;
            }

            if (!ctx.Permisos!.RegistrarMisCobros)
            {
                _logger.LogWarning(
                    "Intento de registrar cobro sin permiso. FuncionarioId {FuncionarioId}. UserId {UserId}.",
                    ctx.FuncionarioId,
                    User.FindFirstValue(CustomClaimTypes.UserId));
                return SinPermiso("registrar cobros");
            }

            if (enviarComprobante && !ComprobanteEmailHelper.EsValido(emailComprobante))
            {
                TempData["PortalError"] = "Indica un correo válido para enviar el comprobante.";
                return RedirectToActionRetorno(retorno);
            }

            // Resuelve la cita en backend; valida pertenencia al funcionario y al tenant.
            var cita = await _portalQueryService.ObtenerCitaCobrableAsync(ctx.FuncionarioId, citaId, cancellationToken);
            if (cita is null)
            {
                TempData["PortalError"] = "La cita no existe, no es tuya o no se puede cobrar.";
                return RedirectToActionRetorno(retorno);
            }

            if (!cita.ServicioId.HasValue)
            {
                TempData["PortalError"] = "Esta cita usa un servicio personalizado y debe cobrarse desde el sistema del negocio.";
                return RedirectToActionRetorno(retorno);
            }

            if (cita.YaCobrada)
            {
                TempData["PortalError"] = "Esta cita ya tiene un cobro registrado.";
                return RedirectToActionRetorno(retorno);
            }

            var request = new CobroCreateRequest
            {
                FechaCobro = _businessDateTimeProvider.Now(),
                NombreCliente = string.IsNullOrWhiteSpace(cita.Cliente) ? "Cliente" : cita.Cliente,
                FuncionarioId = ctx.FuncionarioId, // 🔒 del claim, no del form
                ClienteId = cita.ClienteId,        // liga el cobro al cliente para su historial
                ServicioId = cita.ServicioId,
                CitaId = cita.Id,
                Monto = monto,
                MetodoPago = metodoPago ?? string.Empty,
                Observaciones = observacion
            };

            try
            {
                var cobroId = await _cobroService.RegistrarAsync(request, cancellationToken);
                _logger.LogInformation(
                    "Cobro registrado desde portal. FuncionarioId {FuncionarioId}. CitaId {CitaId}. UserId {UserId}.",
                    ctx.FuncionarioId,
                    cita.Id,
                    User.FindFirstValue(CustomClaimTypes.UserId));

                TempData["PortalMensaje"] = await EnviarComprobantePortalAsync(
                    ctx, cobroId, enviarComprobante, emailComprobante, guardarEmailEnCliente,
                    "Cobro registrado correctamente.", cancellationToken);
            }
            catch (CobroValidationException ex)
            {
                TempData["PortalError"] = ex.Message;
            }
            catch (InvalidOperationException ex)
            {
                TempData["PortalError"] = ex.Message;
            }

            return RedirectToActionRetorno(retorno);
        }

        [HttpPost("Cobros/RegistrarManual")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegistrarCobroManual(
            string? nombreCliente,
            int? clienteId,
            int? servicioId,
            decimal monto,
            string? metodoPago,
            string? observacion,
            bool enviarComprobante,
            string? emailComprobante,
            bool guardarEmailEnCliente,
            CancellationToken cancellationToken)
        {
            var ctx = await ResolverContextoAsync(cancellationToken);
            if (ctx.Bloqueo is not null)
            {
                return ctx.Bloqueo;
            }

            if (!ctx.Permisos!.RegistrarMisCobrosManuales)
            {
                _logger.LogWarning(
                    "Intento de cobro manual sin permiso. FuncionarioId {FuncionarioId}. UserId {UserId}.",
                    ctx.FuncionarioId, User.FindFirstValue(CustomClaimTypes.UserId));
                return SinPermiso("registrar cobros manuales");
            }

            if (enviarComprobante && !ComprobanteEmailHelper.EsValido(emailComprobante))
            {
                TempData["PortalError"] = "Indica un correo válido para enviar el comprobante.";
                return RedirectToAction(nameof(Cobros));
            }

            // ClienteId solo se acepta si pertenece al tenant actual; si no, se ignora (queda texto libre).
            int? clienteValidado = null;
            if (clienteId.HasValue && clienteId.Value > 0 &&
                await _portalQueryService.ClienteExisteAsync(clienteId.Value, cancellationToken))
            {
                clienteValidado = clienteId.Value;
            }

            var request = new CobroCreateRequest
            {
                FechaCobro = _businessDateTimeProvider.Now(), // hora del negocio (America/Costa_Rica)
                NombreCliente = string.IsNullOrWhiteSpace(nombreCliente) ? "Cliente" : nombreCliente,
                ClienteId = clienteValidado,
                FuncionarioId = ctx.FuncionarioId, // 🔒 del claim, nunca del form
                ServicioId = servicioId,           // CobroService valida que sea del tenant y esté activo
                CitaId = null,                     // cobro manual: sin cita de origen
                Monto = monto,
                MetodoPago = metodoPago ?? string.Empty,
                Observaciones = observacion
            };

            try
            {
                var cobroId = await _cobroService.RegistrarAsync(request, cancellationToken);
                _logger.LogInformation(
                    "Cobro manual registrado desde portal. FuncionarioId {FuncionarioId}. UserId {UserId}.",
                    ctx.FuncionarioId, User.FindFirstValue(CustomClaimTypes.UserId));

                TempData["PortalMensaje"] = await EnviarComprobantePortalAsync(
                    ctx, cobroId, enviarComprobante, emailComprobante, guardarEmailEnCliente,
                    "Cobro manual registrado correctamente.", cancellationToken);
            }
            catch (CobroValidationException ex)
            {
                TempData["PortalError"] = ex.Message;
            }
            catch (InvalidOperationException ex)
            {
                TempData["PortalError"] = ex.Message;
            }

            return RedirectToAction(nameof(Cobros));
        }

        [HttpPost("Comprobantes/Reenviar")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReenviarComprobante(int comprobanteId, CancellationToken cancellationToken)
        {
            var ctx = await ResolverContextoAsync(cancellationToken);
            if (ctx.Bloqueo is not null)
            {
                return ctx.Bloqueo;
            }

            // Solo si puede enviar comprobantes; el scope obliga a que sea suyo.
            if (!ctx.Permisos!.PuedeEnviarComprobantes)
            {
                return SinPermiso("reenviar comprobantes");
            }

            var comprobante = await _comprobanteService.ReenviarAsync(comprobanteId, ctx.FuncionarioId, cancellationToken);
            if (comprobante is null)
            {
                TempData["PortalError"] = "No se encontró el comprobante o no es tuyo.";
            }
            else
            {
                TempData["PortalMensaje"] =
                    comprobante.EstadoEnvio == Models.Comprobantes.ComprobanteEstadoEnvio.Sent
                        ? "Comprobante reenviado correctamente."
                        : "No fue posible reenviar el comprobante. Intenta de nuevo en unos minutos.";
            }

            return RedirectToAction(nameof(Cobros));
        }

        /// <summary>
        /// Genera y envía el comprobante de un cobro recién registrado, respetando el permiso y
        /// el scope del funcionario. Devuelve el mensaje a mostrar (combina el resultado del cobro
        /// con el del envío). Nunca lanza: el cobro ya quedó registrado.
        /// </summary>
        private async Task<string> EnviarComprobantePortalAsync(
            PortalContexto ctx,
            int cobroId,
            bool enviarComprobante,
            string? emailComprobante,
            bool guardarEmailEnCliente,
            string mensajeBase,
            CancellationToken cancellationToken)
        {
            if (!enviarComprobante)
            {
                return mensajeBase;
            }

            if (!ctx.Permisos!.PuedeEnviarComprobantes)
            {
                _logger.LogWarning(
                    "Intento de enviar comprobante sin permiso. FuncionarioId {FuncionarioId}.",
                    ctx.FuncionarioId);
                return mensajeBase + " (no tienes permiso para enviar comprobantes)";
            }

            try
            {
                var comprobante = await _comprobanteService.CrearYEnviarDesdeCobroAsync(
                    cobroId,
                    emailComprobante!,
                    guardarEmailEnCliente,
                    User.FindFirstValue(CustomClaimTypes.UserId),
                    ctx.FuncionarioId,
                    cancellationToken);

                var enviado = comprobante is not null &&
                    comprobante.EstadoEnvio == Models.Comprobantes.ComprobanteEstadoEnvio.Sent;

                return enviado
                    ? mensajeBase + " Comprobante enviado."
                    : mensajeBase + " El comprobante quedó pendiente: puedes reintentarlo desde tus cobros.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enviando comprobante del cobro {CobroId} desde portal.", cobroId);
                return mensajeBase + " (el comprobante no pudo enviarse)";
            }
        }

        [HttpGet("Clientes/Autocompletado")]
        public async Task<IActionResult> AutocompletarClientes(string? term, CancellationToken cancellationToken)
        {
            var ctx = await ResolverContextoAsync(cancellationToken);
            if (ctx.Bloqueo is not null)
            {
                // Para una petición AJAX devolvemos lista vacía en vez de redirigir.
                return Ok(Array.Empty<object>());
            }

            // Solo necesario para agendar/editar/cobro manual; si no puede, no exponemos el buscador.
            if (!ctx.Permisos!.CrearMisCitas && !ctx.Permisos.EditarMisCitas && !ctx.Permisos.RegistrarMisCobrosManuales)
            {
                return Ok(Array.Empty<object>());
            }

            var clientes = await _portalQueryService.BuscarClientesAsync(term, cancellationToken);
            return Ok(clientes.Select(c => new { id = c.Id, nombre = c.Nombre, telefono = c.Telefono, correo = c.Correo }));
        }

        [HttpGet("NoDisponible")]
        public IActionResult NoDisponible() => View();

        // ─────────────────────────── Helpers ───────────────────────────

        private async Task<PortalContexto> ResolverContextoAsync(CancellationToken cancellationToken)
        {
            var claim = User.FindFirstValue(CustomClaimTypes.FuncionarioId);
            if (!int.TryParse(claim, NumberStyles.Integer, CultureInfo.InvariantCulture, out var funcionarioId) ||
                funcionarioId <= 0)
            {
                return new PortalContexto { Bloqueo = RedirectToAction(nameof(NoDisponible)) };
            }

            var funcionario = await _portalQueryService.ResolverFuncionarioAsync(funcionarioId, cancellationToken);
            if (funcionario is null || !funcionario.Activo)
            {
                return new PortalContexto { FuncionarioId = funcionarioId, Bloqueo = RedirectToAction(nameof(NoDisponible)) };
            }

            var permisos = await _permissionService.ObtenerAsync(funcionarioId, cancellationToken);

            // El layout usa esto para mostrar/ocultar tabs según permisos.
            ViewData["PortalPerms"] = permisos;

            return new PortalContexto
            {
                FuncionarioId = funcionarioId,
                Permisos = permisos
            };
        }

        private IActionResult SinPermiso(string seccion)
        {
            TempData["PortalError"] = $"No tienes permiso para acceder a {seccion}.";
            return RedirectToAction(nameof(Index));
        }

        private IActionResult RedirectToActionRetorno(string? retorno) => retorno switch
        {
            "calendario" => RedirectToAction(nameof(Calendario)),
            "cobros" => RedirectToAction(nameof(Cobros)),
            _ => RedirectToAction(nameof(Index))
        };

        private static bool TryParseFecha(string? value, out DateTime fecha)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                DateTime.TryParseExact(
                    value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out fecha))
            {
                return true;
            }

            fecha = default;
            return false;
        }

        private static bool TryParseFechaHora(string? value, out DateTime fechaHora)
        {
            string[] formatos = { "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm" };
            if (!string.IsNullOrWhiteSpace(value) &&
                DateTime.TryParseExact(
                    value,
                    formatos,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out fechaHora))
            {
                return true;
            }

            fechaHora = default;
            return false;
        }

        private sealed class PortalContexto
        {
            public int FuncionarioId { get; init; }
            public FuncionarioPortalPermisosSet? Permisos { get; init; }
            public IActionResult? Bloqueo { get; init; }
        }
    }
}
