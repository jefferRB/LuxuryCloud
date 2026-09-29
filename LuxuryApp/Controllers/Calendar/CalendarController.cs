using LuxuryApp.Services.Identity;
using LuxuryApp.Models.Asociados;
using System.Globalization;
using System.Security.Claims;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.Clientes;
using LuxuryApp.Services.Comprobantes;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Services.Fiscal;
using LuxuryApp.Services.Horarios;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Services.WhatsApp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Calendar
{
    [Authorize]
    [RequirePermission(AppPermissions.CalendarView)]
    public class CalendarController : Controller
    {
        private const string TenantWhatsAppEnabledViewDataKey = "TenantWhatsAppEnabled";

        // Cotas de la vista previa de descansos: coinciden con las que valida el servicio al
        // guardar, y acotan el trabajo que un solo request puede pedir.
        private const int MinDescansoPreviewDuration = 5;
        private const int MaxDescansoPreviewDuration = 180;
        private const int MaxDescansoPreviewDays = 92;
        private readonly ICalendarCommandService _calendarCommandService;
        private readonly ICalendarQueryService _calendarQueryService;
        private readonly IControlCobrosQueryService _controlCobrosQueryService;
        private readonly ICobroService _cobroService;
        private readonly IComprobanteCobroService _comprobanteService;
        private readonly ITenantWhatsAppFeatureService _tenantWhatsAppFeatureService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ICobroFiscalPreviewService _cobroFiscalPreviewService;
        private readonly IFuncionarioAvailabilityService _availabilityService;
        private readonly IBookingRequestService _bookingRequestService;
        private readonly IAuthorizationService _authorizationService;
        private readonly IAppointmentCancellationWhatsAppService _cancellationNotificationService;

        public CalendarController(
            ICalendarCommandService calendarCommandService,
            ICalendarQueryService calendarQueryService,
            IControlCobrosQueryService controlCobrosQueryService,
            ICobroService cobroService,
            IComprobanteCobroService comprobanteService,
            ITenantWhatsAppFeatureService tenantWhatsAppFeatureService,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ICobroFiscalPreviewService cobroFiscalPreviewService,
            IFuncionarioAvailabilityService availabilityService,
            IBookingRequestService bookingRequestService,
            IAuthorizationService authorizationService,
            IAppointmentCancellationWhatsAppService cancellationNotificationService)
        {
            _calendarCommandService = calendarCommandService;
            _calendarQueryService = calendarQueryService;
            _controlCobrosQueryService = controlCobrosQueryService;
            _cobroService = cobroService;
            _comprobanteService = comprobanteService;
            _tenantWhatsAppFeatureService = tenantWhatsAppFeatureService;
            _businessDateTimeProvider = businessDateTimeProvider;
            _cobroFiscalPreviewService = cobroFiscalPreviewService;
            _availabilityService = availabilityService;
            _bookingRequestService = bookingRequestService;
            _authorizationService = authorizationService;
            _cancellationNotificationService = cancellationNotificationService;
        }

        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            var hasAddon = await _tenantWhatsAppFeatureService
                .HasWhatsAppAddonAsync(cancellationToken);

            var whatsAppEnabled = await _tenantWhatsAppFeatureService
                .IsWhatsAppEnabledForCurrentTenantAsync(cancellationToken);

            ViewData[TenantWhatsAppEnabledViewDataKey] = whatsAppEnabled;

            // Tenants sin add-on solo necesitan el conteo simple; evita la query
            // de agrupación por EstadoConfirmacionWhatsApp que es irrelevante para ellos.
            CalendarHeaderStatsResponse stats;
            if (hasAddon)
            {
                stats = await _calendarQueryService.GetHeaderStatsAsync(cancellationToken);
            }
            else
            {
                var citasHoy = await _calendarQueryService.GetCitasHoyCountAsync(cancellationToken);
                stats = new CalendarHeaderStatsResponse { CitasHoy = citasHoy };
            }

            // Los botones Confirmar/Rechazar de un bloque pendiente sólo se pintan si el usuario
            // realmente puede administrarlas. Esconderlos no es la seguridad: los endpoints exigen
            // el mismo permiso y devuelven 403 aunque se llamen a mano.
            var puedeGestionarReservas = (await _authorizationService.AuthorizeAsync(
                User,
                AppAuthorizationPolicies.ForPermission(AppPermissions.ReservationsManage))).Succeeded;

            return View(new CalendarIndexViewModel
            {
                HasWhatsAppAddon = hasAddon,
                TenantWhatsAppEnabled = whatsAppEnabled,
                Stats = stats,
                BusinessTodayIso = _businessDateTimeProvider.Today().ToString("yyyy-MM-dd"),
                PuedeGestionarReservas = puedeGestionarReservas
            });
        }

        // ─────────────── Control de citas y cobros (vista admin) ───────────────

        [HttpGet("Calendar/ControlCobros")]
        public async Task<IActionResult> ControlCobros(
            string? rango,
            string? fecha,
            int? funcionarioId,
            string? estado,
            string? buscar,
            CancellationToken cancellationToken)
        {
            var model = await BuildControlCobrosAsync(rango, fecha, funcionarioId, estado, buscar, cancellationToken);
            return View(model);
        }

        [HttpGet("Calendar/ControlCobrosData")]
        public async Task<IActionResult> ControlCobrosData(
            string? rango,
            string? fecha,
            int? funcionarioId,
            string? estado,
            string? buscar,
            CancellationToken cancellationToken)
        {
            var model = await BuildControlCobrosAsync(rango, fecha, funcionarioId, estado, buscar, cancellationToken);
            return PartialView("_ControlCobrosResultados", model);
        }

        // Desglose fiscal (informativo) para el modal de cobro. El cálculo lo hace el motor
        // fiscal central en el backend → NO hay fórmula de IVA duplicada en el frontend.
        [HttpGet("Calendar/PreviewCobroFiscal")]
        public async Task<IActionResult> PreviewCobroFiscal(
            int citaId,
            decimal monto,
            CancellationToken cancellationToken)
        {
            var preview = await _cobroFiscalPreviewService.PreviewCitaAsync(citaId, monto, cancellationToken);
            if (preview is null)
            {
                return BadRequest(new { error = "La cita no existe o no pertenece a tu negocio." });
            }

            return Ok(new
            {
                total = preview.Total,
                baseSinIva = preview.BaseSinIva,
                iva = preview.IvaIncluido,
                tarifaIva = preview.TarifaIva,
                aplicaIva = preview.AplicaIva,
                tipoLinea = preview.TipoLinea
            });
        }

        [HttpPost("Calendar/CobrarCita")]
        [RequirePermission(AppPermissions.CalendarManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CobrarCita(
            int citaId,
            decimal monto,
            string? metodoPago,
            string? observacion,
            bool enviarComprobante,
            string? emailComprobante,
            bool guardarEmailEnCliente,
            CancellationToken cancellationToken)
        {
            if (citaId <= 0)
            {
                return BadRequest(new { error = "La cita indicada no es válida." });
            }

            if (enviarComprobante && !ComprobanteEmailHelper.EsValido(emailComprobante))
            {
                return BadRequest(new { error = "Indica un correo válido para enviar el comprobante." });
            }

            // Resuelve la cita en backend (tenant-safe). El FuncionarioId del cobro es el de
            // la cita, nunca un valor del formulario, para que la comisión vaya a quien corresponde.
            var cita = await _controlCobrosQueryService.ObtenerCitaParaCobroAsync(citaId, cancellationToken);
            if (cita is null)
            {
                return BadRequest(new { error = "La cita no existe o no pertenece a tu negocio." });
            }

            if (cita.YaCobrada)
            {
                return BadRequest(new { error = "Esta cita ya tiene un cobro registrado." });
            }

            // Una cita con servicio personalizado no tiene ServicioId; el cobro conserva el nombre
            // del servicio como snapshot. El monto final lo captura el modal (puede no haber precio base).
            var request = new CobroCreateRequest
            {
                FechaCobro = _businessDateTimeProvider.Now(),
                NombreCliente = cita.NombreCliente,
                FuncionarioId = cita.FuncionarioId,
                ClienteId = cita.ClienteId,
                ServicioId = cita.ServicioId,
                ServicioNombrePersonalizado = cita.ServicioId.HasValue ? null : cita.ServicioNombrePersonalizado,
                CitaId = cita.CitaId,
                Monto = monto,
                MetodoPago = metodoPago ?? string.Empty,
                Observaciones = observacion
            };

            // 1) Registrar el cobro. Solo los errores DE COBRO devuelven BadRequest.
            int cobroId;
            try
            {
                // CobroService valida monto/método, pertenencia y unicidad (anti doble cobro
                // a nivel de servicio + índice único UX_Cobros_TenantId_CitaId).
                cobroId = await _cobroService.RegistrarAsync(request, cancellationToken);
            }
            catch (CobroValidationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }

            // 2) Cobro YA registrado. Comprobante best-effort (el servicio nunca lanza).
            if (!enviarComprobante)
            {
                return Ok(new { success = true, message = "Cobro registrado correctamente." });
            }

            var comprobante = await _comprobanteService.CrearYEnviarDesdeCobroAsync(
                cobroId,
                emailComprobante!,
                guardarEmailEnCliente,
                User.Identity?.Name,
                funcionarioScopeId: null,
                cancellationToken);

            var enviado = comprobante is not null &&
                comprobante.EstadoEnvio == Models.Comprobantes.ComprobanteEstadoEnvio.Sent;

            return Ok(new
            {
                success = true,
                message = enviado
                    ? "Cobro registrado y comprobante enviado."
                    : "Cobro registrado correctamente, pero no se pudo enviar el comprobante. Puedes reenviarlo desde el historial."
            });
        }

        private async Task<ControlCitasCobrosViewModel> BuildControlCobrosAsync(
            string? rango,
            string? fecha,
            int? funcionarioId,
            string? estado,
            string? buscar,
            CancellationToken cancellationToken)
        {
            var hasAddon = await _tenantWhatsAppFeatureService.HasWhatsAppAddonAsync(cancellationToken);

            var filtro = new ControlCitasCobrosFiltroViewModel
            {
                Rango = rango ?? "dia",
                Fecha = TryParseLocalDate(fecha, out var parsed) ? parsed : _businessDateTimeProvider.Today(),
                FuncionarioId = funcionarioId,
                EstadoPago = estado ?? "todos",
                Buscar = buscar
            };

            return await _controlCobrosQueryService.ObtenerAsync(filtro, hasAddon, cancellationToken);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.CalendarManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([FromBody] CitaCreateVM vm, CancellationToken cancellationToken)
        {
            if (vm == null)
            {
                return BadRequest("Datos invalidos.");
            }

            if (!ModelState.IsValid)
            {
                return BadRequest(GetValidationMessage());
            }

            try
            {
                var created = await _calendarCommandService.CreateAsync(
                    MapUpsertRequest(vm, ResolveCurrentUserId()),
                    cancellationToken);
                return Ok(created);
            }
            catch (CalendarValidationException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetCitasByDay(string date, CancellationToken cancellationToken)
        {
            if (!TryParseLocalDate(date, out var parsedDate))
            {
                return BadRequest("La fecha solicitada no es valida.");
            }

            var citas = await _calendarQueryService.GetAppointmentsByDayAsync(parsedDate, cancellationToken);
            return Ok(citas);
        }

        /// <summary>
        /// Bloqueos recurrentes del día (almuerzo, limpieza...). Se devuelven aparte de las citas
        /// para que el calendario los pinte como bloques de agenda y nunca se confundan con una
        /// cita de cliente. La fuente de verdad es la regla; acá solo se expanden sus ocurrencias.
        /// </summary>
        [HttpGet("Calendar/GetBloqueosRecurrentes")]
        public async Task<IActionResult> GetBloqueosRecurrentes(string date, CancellationToken cancellationToken)
        {
            if (!TryParseLocalDate(date, out var parsedDate))
            {
                return BadRequest("La fecha solicitada no es valida.");
            }

            var fecha = DateOnly.FromDateTime(parsedDate);
            var bloqueos = await _availabilityService.GetRecurringBlocksAsync(
                fecha,
                fecha,
                funcionarioIds: null,
                cancellationToken);

            return Ok(bloqueos
                .OrderBy(bloqueo => bloqueo.Inicio)
                .Select(bloqueo => new
                {
                    reglaId = bloqueo.RuleId,
                    funcionarioId = bloqueo.FuncionarioId,
                    titulo = bloqueo.Titulo,
                    motivo = bloqueo.Motivo,
                    inicio = bloqueo.Inicio.ToString("yyyy-MM-ddTHH:mm:ss"),
                    duracionMinutos = bloqueo.DuracionMinutos,
                    esExcepcion = bloqueo.EsExcepcion,
                    origen = "BLOQUEO_RECURRENTE"
                }));
        }

        /// <summary>
        /// Solicitudes de reserva online PENDIENTES del día. Igual que los bloqueos recurrentes,
        /// viajan aparte de las citas: son un read model propio y nunca se materializan como
        /// citas falsas. Se piden con el mismo permiso con el que se ve el calendario, porque
        /// ocupan la agenda que esa persona ya está viendo.
        /// </summary>
        [HttpGet("Calendar/GetSolicitudesPendientes")]
        public async Task<IActionResult> GetSolicitudesPendientes(string date, CancellationToken cancellationToken)
        {
            if (!TryParseLocalDate(date, out var parsedDate))
            {
                return BadRequest("La fecha solicitada no es valida.");
            }

            var solicitudes = await _bookingRequestService.GetPendingForCalendarAsync(
                DateOnly.FromDateTime(parsedDate),
                cancellationToken);

            return Ok(solicitudes.Select(solicitud => new
            {
                id = solicitud.Id,
                funcionarioId = solicitud.FuncionarioId,
                funcionarioNombre = solicitud.FuncionarioNombre,
                nombreCliente = solicitud.NombreCliente,
                telefonoCliente = solicitud.TelefonoCliente,
                servicioNombre = solicitud.ServicioNombre,
                inicio = solicitud.FechaHoraInicio.ToString("yyyy-MM-ddTHH:mm:ss"),
                duracionMinutos = solicitud.DuracionMinutos,
                solicitoCualquierFuncionario = solicitud.SolicitoCualquierFuncionario,
                aceptaWhatsApp = solicitud.AceptaWhatsApp,
                notasCliente = solicitud.NotasCliente,
                origen = "SOLICITUD_PENDIENTE"
            }));
        }

        /// <summary>
        /// Confirma una solicitud desde el calendario. NO reimplementa nada: delega en el mismo
        /// servicio de aplicación que usa la pantalla "Solicitudes de reserva", así que revalida
        /// disponibilidad, hace la transición Pending → Confirmed, crea la cita, envía el
        /// WhatsApp y deja los mismos rastros.
        /// </summary>
        /// <summary>
        /// Estado del cliente de una solicitud pendiente, para que el calendario muestre la misma
        /// pregunta que la pantalla de Reservas. Delega en el mismo servicio de aplicación.
        /// </summary>
        [HttpGet("Calendar/ClientePrevioSolicitud")]
        [RequirePermission(AppPermissions.ReservationsManage)]
        public async Task<IActionResult> ClientePrevioSolicitud(int id, CancellationToken cancellationToken)
        {
            if (id <= 0)
            {
                return BadRequest(new { success = false, message = "Solicitud invalida." });
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

        [HttpPost("Calendar/ConfirmarSolicitud")]
        [RequirePermission(AppPermissions.ReservationsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmarSolicitud(
            int id,
            string? clienteAccion,
            int? clienteId,
            CancellationToken cancellationToken)
        {
            if (id <= 0)
            {
                return BadRequest(new { success = false, message = "Solicitud invalida." });
            }

            var result = await _bookingRequestService.ConfirmAsync(
                id,
                funcionarioIdOverride: null,
                ResolveCurrentUserId(),
                BookingClienteChoice.Parse(clienteAccion, clienteId),
                cancellationToken);

            if (result.Success)
            {
                return Ok(new
                {
                    success = true,
                    message = result.Message,
                    citaId = result.CitaId,
                    whatsAppStatus = result.WhatsAppStatus
                });
            }

            // 409: la solicitud ya se procesó en otra pestaña o el espacio dejó de estar libre.
            // El calendario refresca y muestra el estado real en vez de insistir.
            return Conflict(new { success = false, message = result.Message });
        }

        [HttpPost("Calendar/RechazarSolicitud")]
        [RequirePermission(AppPermissions.ReservationsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RechazarSolicitud(int id, string? motivo, CancellationToken cancellationToken)
        {
            if (id <= 0)
            {
                return BadRequest(new { success = false, message = "Solicitud invalida." });
            }

            var result = await _bookingRequestService.RejectAsync(
                id,
                motivo,
                ResolveCurrentUserId(),
                cancellationToken);

            return result.Success
                ? Ok(new { success = true, message = result.Message })
                : Conflict(new { success = false, message = result.Message });
        }

        [HttpGet("Calendar/GetById/{id}")]
        public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
        {
            if (id <= 0)
            {
                return NotFound();
            }

            var cita = await _calendarQueryService.GetByIdAsync(id, cancellationToken);
            return cita is null ? NotFound() : Ok(cita);
        }

        [HttpPut("Calendar/Edit/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(int id, [FromBody] CitaCreateVM vm, CancellationToken cancellationToken)
        {
            if (id <= 0)
            {
                return NotFound();
            }

            if (vm == null)
            {
                return BadRequest("Datos invalidos.");
            }

            if (!ModelState.IsValid)
            {
                return BadRequest(GetValidationMessage());
            }

            try
            {
                var updated = await _calendarCommandService.UpdateAsync(
                    id,
                    MapUpsertRequest(vm, ResolveCurrentUserId()),
                    cancellationToken);
                return Ok(updated);
            }
            catch (CalendarValidationException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPut("Calendar/ResizeDuration/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResizeDuration(int id, [FromBody] ResizeDurationVM vm, CancellationToken cancellationToken)
        {
            if (id <= 0)
            {
                return NotFound();
            }

            if (vm == null)
            {
                return BadRequest("Datos invalidos.");
            }

            if (!ModelState.IsValid)
            {
                return BadRequest(GetValidationMessage());
            }

            try
            {
                await _calendarCommandService.ResizeDurationAsync(id, vm.DuracionMinutos, cancellationToken);
                return Ok(new { success = true });
            }
            catch (CalendarValidationException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpDelete("Calendar/Delete/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id, [FromForm] string? motivo, CancellationToken cancellationToken)
        {
            if (id <= 0)
            {
                return NotFound();
            }

            try
            {
                // Se consulta ANTES de borrar: después la cita ya no existe. Misma regla que usa el
                // envío real, así que el aviso al negocio nunca contradice lo que pasó.
                var aviso = await _cancellationNotificationService.PreviewAsync(id, cancellationToken);

                await _calendarCommandService.DeleteAsync(id, motivo, cancellationToken);

                return Ok(new
                {
                    success = true,
                    whatsAppNotificado = aviso.NotificaraPorWhatsApp,
                    whatsAppAviso = aviso.MensajeContactoManual
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetCitasCountByMonth(int year, int month, CancellationToken cancellationToken)
        {
            if (year < 1 || year > 9999 || month < 1 || month > 12)
            {
                return BadRequest("El ano o mes solicitado no es valido.");
            }

            var data = await _calendarQueryService.GetCitasCountByMonthAsync(year, month, cancellationToken);
            return Ok(data);
        }

        [HttpGet]
        public async Task<IActionResult> GetUpcomingAppointments(string date, int? funcionarioId, CancellationToken cancellationToken)
        {
            if (!TryParseLocalDate(date, out var parsedDate))
            {
                return BadRequest("La fecha solicitada no es valida.");
            }

            if (!await ValidateFuncionarioFilterAsync(funcionarioId, cancellationToken))
            {
                return BadRequest("El funcionario solicitado no es valido.");
            }

            var citas = await _calendarQueryService.GetUpcomingAppointmentsAsync(parsedDate, funcionarioId, cancellationToken);
            return Ok(citas);
        }

        private async Task<bool> ValidateFuncionarioFilterAsync(int? funcionarioId, CancellationToken cancellationToken)
        {
            if (!funcionarioId.HasValue)
            {
                return true;
            }

            return await _calendarQueryService.FuncionarioExistsForCurrentTenantAsync(
                funcionarioId.Value,
                cancellationToken);
        }

        [HttpGet]
        public async Task<IActionResult> GetServiciosActivos(CancellationToken cancellationToken)
        {
            var servicios = await _calendarQueryService.GetServiciosActivosAsync(cancellationToken);
            return Ok(servicios);
        }

        [HttpPost("Calendar/ProcesarVisitas")]
        [RequirePermission(AppPermissions.CalendarManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcesarVisitas(CancellationToken cancellationToken)
        {
            await _calendarCommandService.ProcessVisitsAsync(cancellationToken);
            return Ok(new { success = true });
        }

        [HttpGet]
        public async Task<IActionResult> GetFechasOcupadas(
            int funcionarioId,
            string? startDate,
            string? endDate,
            CancellationToken cancellationToken)
        {
            if (funcionarioId <= 0)
            {
                return BadRequest("Debe seleccionar un funcionario valido.");
            }

            if (!TryParseOptionalLocalDate(startDate, out var parsedStartDate) ||
                !TryParseOptionalLocalDate(endDate, out var parsedEndDate))
            {
                return BadRequest("El rango solicitado no es valido.");
            }

            var citas = await _calendarQueryService.GetFechasOcupadasAsync(
                funcionarioId,
                parsedStartDate,
                parsedEndDate,
                cancellationToken);

            return Ok(citas);
        }

        /// <summary>
        /// Vista previa de la disponibilidad de un descanso para VARIOS colaboradores en un rango
        /// de fechas. Solo sirve para pintar el selector: el guardado revalida por su cuenta.
        ///
        /// <para>
        /// Responde con la misma fuente de verdad que usa <c>CalendarCommandService</c> al crear
        /// (<see cref="IFuncionarioAvailabilityService"/>), así el calendario nunca puede ofrecer
        /// un día que el guardado vaya a rechazar por una regla distinta.
        /// </para>
        /// </summary>
        [HttpGet("Calendar/DisponibilidadDescanso")]
        [RequirePermission(AppPermissions.CalendarManage)]
        public async Task<IActionResult> DisponibilidadDescanso(
            [FromQuery] int[] funcionarioIds,
            string? startDate,
            string? endDate,
            string? hora,
            int duracion,
            CancellationToken cancellationToken)
        {
            // Los ids llegan del navegador, pero la consulta va contra el DbContext del tenant
            // actual: un id ajeno simplemente no encuentra ocupación ni nombre.
            var ids = (funcionarioIds ?? Array.Empty<int>())
                .Where(id => id > 0)
                .Distinct()
                .ToArray();

            if (ids.Length == 0)
            {
                return BadRequest("Debe seleccionar al menos un funcionario.");
            }

            if (duracion < MinDescansoPreviewDuration || duracion > MaxDescansoPreviewDuration)
            {
                return BadRequest("La duracion del descanso no es valida.");
            }

            if (!TryParseLocalDate(startDate, out var inicioRango) ||
                !TryParseLocalDate(endDate, out var finRango) ||
                finRango < inicioRango)
            {
                return BadRequest("El rango solicitado no es valido.");
            }

            if ((finRango - inicioRango).TotalDays > MaxDescansoPreviewDays)
            {
                return BadRequest("El rango solicitado es demasiado amplio.");
            }

            if (!TimeOnly.TryParseExact(hora, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var horaInicio))
            {
                return BadRequest("La hora indicada no es valida.");
            }

            var fechas = new List<DateOnly>();
            for (var fecha = DateOnly.FromDateTime(inicioRango); fecha <= DateOnly.FromDateTime(finRango); fecha = fecha.AddDays(1))
            {
                fechas.Add(fecha);
            }

            var disponibilidad = await _availabilityService.CheckManyAsync(
                ids,
                fechas,
                horaInicio,
                duracion,
                excludeCitaId: null,
                cancellationToken);

            var respuesta = disponibilidad
                .Select(dia => new DescansoDisponibilidadResponse
                {
                    Fecha = dia.Fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Disponible = dia.Disponible,
                    Conflictos = dia.Conflictos
                        .Select(conflicto => new DescansoConflictoResponse
                        {
                            FuncionarioId = conflicto.FuncionarioId,
                            FuncionarioNombre = conflicto.FuncionarioNombre,
                            Inicio = conflicto.Inicio.ToString("HH:mm", CultureInfo.InvariantCulture),
                            Fin = conflicto.Fin.ToString("HH:mm", CultureInfo.InvariantCulture),
                            Tipo = conflicto.Tipo,
                            Descripcion = ScheduleConflictDescriber.Describe(conflicto)
                        })
                        .ToList()
                })
                .ToList();

            return Ok(respuesta);
        }

        [HttpPut("Calendar/Move/{id}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Move(int id, [FromBody] MoveCitaVM vm, CancellationToken cancellationToken)
        {
            if (id <= 0)
            {
                return NotFound();
            }

            if (vm == null)
            {
                return BadRequest("Datos invalidos.");
            }

            if (!ModelState.IsValid)
            {
                return BadRequest(GetValidationMessage());
            }

            try
            {
                await _calendarCommandService.MoveAsync(id, MapMoveRequest(vm), cancellationToken);
                return Ok(new { success = true });
            }
            catch (CalendarValidationException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        private static CalendarUpsertRequest MapUpsertRequest(CitaCreateVM vm, string? capturedByUserId) =>
            new()
            {
                NombreCliente = vm.NombreCliente,
                TelefonoCliente = vm.TelefonoCliente,
                ClienteId = vm.ClienteId,
                // El formulario solo dice si además quiere registrar al cliente. La resolución de
                // identidad (y por tanto la decisión de crear o reutilizar) la hace el backend.
                ClienteLinkMode = vm.RegistrarCliente
                    ? ClienteLinkMode.Registrar
                    : ClienteLinkMode.Automatico,
                ServicioId = vm.ServicioId,
                EsServicioPersonalizado = vm.EsServicioPersonalizado,
                ServicioNombrePersonalizado = vm.ServicioNombrePersonalizado,
                FechaHoraCita = vm.FechaHoraCita,
                FuncionarioId = vm.FuncionarioId,
                // Solo tiene efecto en descansos: la normalización del servicio descarta la lista
                // cuando el tipo es CITA, de modo que una cita nunca se vuelve multi-funcionario.
                FuncionarioIds = vm.FuncionarioIds ?? new List<int>(),
                Tipo = vm.Tipo,
                DuracionMinutos = vm.DuracionMinutos,
                WhatsAppConsentAtCreation = vm.WhatsAppConsentAtCreation,
                WhatsAppConsentSource = vm.WhatsAppConsentSource,
                WhatsAppConsentCapturedAtUtc = vm.WhatsAppConsentCapturedAtUtc,
                AutorizarWhatsAppAlGuardar = vm.AutorizarWhatsAppAlGuardar,
                // El id del usuario proviene de los claims autenticados, no del cuerpo enviado.
                WhatsAppConsentCapturedByUserId = capturedByUserId,
                Duplicar = vm.Duplicar,
                FechasDuplicadas = vm.FechasDuplicadas
            };

        private string? ResolveCurrentUserId() =>
            User.FindFirstValue(ClaimTypes.NameIdentifier);

        private static CalendarMoveRequest MapMoveRequest(MoveCitaVM vm) =>
            new()
            {
                FechaHoraCita = vm.FechaHoraCita,
                FuncionarioId = vm.FuncionarioId
            };

        private static bool TryParseLocalDate(string? value, out DateTime parsedDate) =>
            DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out parsedDate);

        private static bool TryParseOptionalLocalDate(string? value, out DateTime? parsedDate)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                parsedDate = null;
                return true;
            }

            var parsed = TryParseLocalDate(value, out var date);
            parsedDate = parsed ? date : null;
            return parsed;
        }

        private string GetValidationMessage()
        {
            var errors = ModelState.Values
                .SelectMany(value => value.Errors)
                .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage)
                    ? "Datos invalidos."
                    : error.ErrorMessage)
                .Distinct()
                .ToList();

            return errors.Count == 0
                ? "Datos invalidos."
                : string.Join(" ", errors);
        }
    }
}
