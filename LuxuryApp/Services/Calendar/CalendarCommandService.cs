using System.Data;
using System.Globalization;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.Clientes;
using LuxuryApp.Services.Horarios;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Calendar
{
    public sealed class CalendarCommandService : ICalendarCommandService
    {
        internal const int DefaultDurationMinutes = 30;
        private const int MinDescansoDurationMinutes = 5;
        private const int MaxDescansoDurationMinutes = 180;
        private const int MinCustomServicioDurationMinutes = 5;
        private const int MaxCustomServicioDurationMinutes = 480;

        /// <summary>Tope de fechas repetidas de un descanso: acota el lote (fechas × colaboradores).</summary>
        private const int MaxDescansoFechasRepetidas = 60;

        /// <summary>Conflictos que se enumeran en el mensaje antes de resumir el resto.</summary>
        private const int MaxConflictosReportados = 6;
        private static readonly string[] SupportedTipos = ["CITA", "DESCANSO"];
        private readonly ApplicationDbContext _context;
        private readonly ICalendarWhatsAppNotificationService _notificationService;
        private readonly IAppointmentCancellationWhatsAppService _cancellationNotificationService;
        private readonly VisitasAutomaticasService _visitasAutomaticasService;
        private readonly IFuncionarioAvailabilityService _availabilityService;
        private readonly IClienteIdentityService _clienteIdentityService;
        private readonly ILogger<CalendarCommandService> _logger;

        public CalendarCommandService(
            ApplicationDbContext context,
            ICalendarWhatsAppNotificationService notificationService,
            IAppointmentCancellationWhatsAppService cancellationNotificationService,
            VisitasAutomaticasService visitasAutomaticasService,
            IFuncionarioAvailabilityService availabilityService,
            IClienteIdentityService clienteIdentityService,
            ILogger<CalendarCommandService> logger)
        {
            _context = context;
            _notificationService = notificationService;
            _cancellationNotificationService = cancellationNotificationService;
            _visitasAutomaticasService = visitasAutomaticasService;
            _availabilityService = availabilityService;
            _clienteIdentityService = clienteIdentityService;
            _logger = logger;
        }

        public async Task<CalendarAppointmentResponse> CreateAsync(
            CalendarUpsertRequest request,
            CancellationToken cancellationToken = default)
        {
            var normalizedRequest = NormalizeRequest(request);
            var appointmentIdsToQueue = new List<int>();
            CalendarAppointmentResponse? response = null;

            var executionStrategy = _context.Database.CreateExecutionStrategy();

            try
            {
                await executionStrategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken);

                    // Una CITA sigue siendo de un solo funcionario: la normalización ya redujo la
                    // lista a uno. Un DESCANSO puede traer varios y todos se validan igual, contra
                    // el tenant actual, antes de tocar nada.
                    var funcionarios = await EnsureFuncionariosActivosAsync(
                        normalizedRequest.FuncionarioIds,
                        cancellationToken);
                    var funcionario = funcionarios[0];
                    var servicio = await ResolveServicioAsync(normalizedRequest, cancellationToken);
                    var duracion = ResolveDuracion(normalizedRequest, servicio);
                    var esPersonalizado = IsCustomServicio(normalizedRequest);
                    var resolvedAppointment = await ResolveAppointmentDataAsync(normalizedRequest, cancellationToken);

                    await ValidateRequestAsync(normalizedRequest, resolvedAppointment, servicio, duracion, cancellationToken);

                    var targets = BuildCreationTargets(normalizedRequest);
                    EnsureNoDuplicateTargets(targets);

                    // Misma regla de conflictos en ambos casos (IFuncionarioAvailabilityService);
                    // lo que cambia es cuánto se reporta. La cita conserva su comprobación y su
                    // mensaje históricos; el descanso, que puede chocar en varias combinaciones
                    // fecha × colaborador a la vez, las evalúa todas de una pasada y las enumera.
                    if (normalizedRequest.Tipo == "DESCANSO")
                    {
                        await EnsureDescansoDisponibleAsync(funcionarios, targets, duracion, cancellationToken);
                    }
                    else
                    {
                        foreach (var target in targets)
                        {
                            await EnsureNoOverlapAsync(
                                funcionario.IdFuncionario,
                                target,
                                duracion,
                                excludeCitaId: null,
                                cancellationToken);
                        }
                    }

                    var persistedAppointments = new List<Cita>(targets.Count * funcionarios.Count);
                    foreach (var target in targets)
                    {
                        foreach (var destinatario in funcionarios)
                        {
                            var cita = new Cita
                            {
                                NombreCliente = resolvedAppointment.NombreCliente,
                                TelefonoCliente = resolvedAppointment.TelefonoCliente,
                                ClienteId = resolvedAppointment.ClienteId,
                                ServicioId = (normalizedRequest.Tipo == "DESCANSO" || esPersonalizado) ? null : servicio!.Id,
                                ServicioNombrePersonalizado = esPersonalizado ? normalizedRequest.ServicioNombrePersonalizado : null,
                                FechaHoraCita = target,
                                FuncionarioId = destinatario.IdFuncionario,
                                Tipo = normalizedRequest.Tipo,
                                DuracionMinutos = (normalizedRequest.Tipo == "DESCANSO" || esPersonalizado) ? duracion : null,
                                WhatsAppConsentAtCreation = resolvedAppointment.WhatsAppConsentAtCreation,
                                WhatsAppConsentSource = resolvedAppointment.WhatsAppConsentSource,
                                WhatsAppConsentCapturedAtUtc = resolvedAppointment.WhatsAppConsentCapturedAtUtc,
                                ConfirmacionEnviada = false,
                                Recordatorio24hEnviado = false,
                                Recordatorio3hEnviado = false,
                                VisitaProcesada = false
                            };

                            _context.Citas.Add(cita);
                            persistedAppointments.Add(cita);
                        }
                    }

                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    var primaryAppointment = persistedAppointments[0];
                    response = BuildAppointmentResponse(
                        primaryAppointment,
                        duracion,
                        funcionario.Nombre,
                        funcionario.ColorCalendario,
                        esPersonalizado ? normalizedRequest.ServicioNombrePersonalizado : servicio?.Nombre,
                        esPersonalizado);

                    appointmentIdsToQueue.AddRange(
                        persistedAppointments
                            .Where(appointment =>
                                string.Equals(appointment.Tipo, "CITA", StringComparison.Ordinal) &&
                                !string.IsNullOrWhiteSpace(appointment.TelefonoCliente))
                            .Select(appointment => appointment.Id));

                    _logger.LogInformation(
                        "Se registraron {CantidadCitas} entradas de agenda ({TipoEntrada}) para {CantidadFuncionarios} funcionario(s) en {CantidadFechas} fecha(s), desde el {FechaHora:yyyy-MM-dd HH:mm}.",
                        persistedAppointments.Count,
                        normalizedRequest.Tipo,
                        funcionarios.Count,
                        targets.Count,
                        primaryAppointment.FechaHoraCita);
                });

                foreach (var appointmentId in appointmentIdsToQueue)
                {
                    try
                    {
                        // La confirmación se omite automáticamente si la cita ya entró en la ventana
                        // de recordatorio (solo se enviará el recordatorio).
                        await _notificationService.QueueAppointmentConfirmationAsync(appointmentId, cancellationToken);

                        // Si la cita se crea ya dentro de la ventana del recordatorio, se envía de inmediato
                        // (según la preferencia del tenant), sin esperar al ciclo del worker.
                        await _notificationService.QueueImmediateReminderOnCreateAsync(appointmentId, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "La cita {CitaId} se creo correctamente, pero fallo la cola de notificaciones de WhatsApp.",
                            appointmentId);
                    }
                }

                return response ?? throw new InvalidOperationException("No fue posible construir la respuesta de la cita creada.");
            }
            catch (CalendarValidationException)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al registrar una cita para funcionario {FuncionarioId}.", normalizedRequest.FuncionarioId);
                throw new InvalidOperationException("No fue posible registrar la cita.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Operacion invalida al registrar una cita para funcionario {FuncionarioId}.", normalizedRequest.FuncionarioId);
                throw;
            }
        }

        public async Task<CalendarAppointmentResponse> UpdateAsync(
            int id,
            CalendarUpsertRequest request,
            CancellationToken cancellationToken = default)
        {
            var normalizedRequest = NormalizeRequest(request);
            CalendarAppointmentResponse? response = null;
            var rescheduleAfterUpdate = false;
            var newFechaHoraCita = default(DateTime);

            var executionStrategy = _context.Database.CreateExecutionStrategy();

            try
            {
                await executionStrategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken);

                    var cita = await _context.Citas
                        .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

                    if (cita is null)
                    {
                        throw new InvalidOperationException("La cita indicada no existe o no pertenece al tenant actual.");
                    }

                    var funcionario = await EnsureFuncionarioActivoAsync(normalizedRequest.FuncionarioId, cancellationToken);
                    var servicio = await ResolveServicioAsync(normalizedRequest, cancellationToken);
                    var duracion = ResolveDuracion(normalizedRequest, servicio);
                    var esPersonalizado = IsCustomServicio(normalizedRequest);
                    var resolvedAppointment = await ResolveAppointmentDataAsync(normalizedRequest, cancellationToken);

                    await ValidateRequestAsync(normalizedRequest, resolvedAppointment, servicio, duracion, cancellationToken);
                    await EnsureNoOverlapAsync(
                        funcionario.IdFuncionario,
                        normalizedRequest.FechaHoraCita,
                        duracion,
                        excludeCitaId: cita.Id,
                        cancellationToken);

                    cita.NombreCliente = resolvedAppointment.NombreCliente;
                    cita.TelefonoCliente = resolvedAppointment.TelefonoCliente;
                    cita.ClienteId = resolvedAppointment.ClienteId;
                    cita.ServicioId = (normalizedRequest.Tipo == "DESCANSO" || esPersonalizado) ? null : servicio!.Id;
                    cita.ServicioNombrePersonalizado = esPersonalizado ? normalizedRequest.ServicioNombrePersonalizado : null;
                    cita.FechaHoraCita = normalizedRequest.FechaHoraCita;
                    cita.FuncionarioId = funcionario.IdFuncionario;
                    cita.Tipo = normalizedRequest.Tipo;
                    cita.DuracionMinutos = (normalizedRequest.Tipo == "DESCANSO" || esPersonalizado) ? duracion : null;
                    cita.WhatsAppConsentAtCreation = resolvedAppointment.WhatsAppConsentAtCreation;
                    cita.WhatsAppConsentSource = resolvedAppointment.WhatsAppConsentSource;
                    cita.WhatsAppConsentCapturedAtUtc = resolvedAppointment.WhatsAppConsentCapturedAtUtc;

                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    response = BuildAppointmentResponse(
                        cita,
                        duracion,
                        funcionario.Nombre,
                        funcionario.ColorCalendario,
                        esPersonalizado ? normalizedRequest.ServicioNombrePersonalizado : servicio?.Nombre,
                        esPersonalizado);

                    if (string.Equals(cita.Tipo, "CITA", StringComparison.Ordinal))
                    {
                        rescheduleAfterUpdate = true;
                        newFechaHoraCita = cita.FechaHoraCita;
                    }

                    _logger.LogInformation(
                        "Se actualizo la cita {CitaId} del funcionario {FuncionarioId}.",
                        cita.Id,
                        funcionario.IdFuncionario);
                });

                if (rescheduleAfterUpdate)
                {
                    try
                    {
                        await _notificationService.RescheduleConfirmationIfPendingAsync(id, newFechaHoraCita, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "La cita {CitaId} se actualizo correctamente, pero fallo la reprogramacion de WhatsApp.",
                            id);
                    }
                }

                return response ?? throw new InvalidOperationException("No fue posible construir la respuesta de la cita actualizada.");
            }
            catch (CalendarValidationException)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al actualizar la cita {CitaId}.", id);
                throw new InvalidOperationException("No fue posible actualizar la cita.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Operacion invalida al actualizar la cita {CitaId}.", id);
                throw;
            }
        }

        public async Task MoveAsync(int id, CalendarMoveRequest request, CancellationToken cancellationToken = default)
        {
            var normalizedRequest = NormalizeMoveRequest(request);
            var executionStrategy = _context.Database.CreateExecutionStrategy();
            var newFechaHoraCita = default(DateTime);
            var isCita = false;

            try
            {
                await executionStrategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken);

                    var cita = await _context.Citas
                        .Select(c => new
                        {
                            Entity = c,
                            Duracion = c.Tipo == "DESCANSO"
                                ? (c.DuracionMinutos ?? DefaultDurationMinutes)
                                : (c.DuracionMinutos ?? (c.Servicio != null ? c.Servicio.DuracionMinutos : null) ?? DefaultDurationMinutes)
                        })
                        .FirstOrDefaultAsync(c => c.Entity.Id == id, cancellationToken);

                    if (cita is null)
                    {
                        throw new InvalidOperationException("La cita indicada no existe o no pertenece al tenant actual.");
                    }

                    var funcionarioId = normalizedRequest.FuncionarioId ?? cita.Entity.FuncionarioId;
                    var funcionario = await EnsureFuncionarioActivoAsync(funcionarioId, cancellationToken);

                    await EnsureNoOverlapAsync(
                        funcionario.IdFuncionario,
                        normalizedRequest.FechaHoraCita,
                        cita.Duracion,
                        excludeCitaId: cita.Entity.Id,
                        cancellationToken);

                    cita.Entity.FechaHoraCita = normalizedRequest.FechaHoraCita;
                    cita.Entity.FuncionarioId = funcionario.IdFuncionario;

                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    isCita = string.Equals(cita.Entity.Tipo, "CITA", StringComparison.Ordinal);
                    newFechaHoraCita = normalizedRequest.FechaHoraCita;

                    _logger.LogInformation(
                        "Se movio la cita {CitaId} al funcionario {FuncionarioId} para {FechaHoraCita:yyyy-MM-dd HH:mm}.",
                        cita.Entity.Id,
                        funcionario.IdFuncionario,
                        normalizedRequest.FechaHoraCita);
                });

                if (isCita)
                {
                    try
                    {
                        await _notificationService.RescheduleConfirmationIfPendingAsync(id, newFechaHoraCita, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "La cita {CitaId} se movio correctamente, pero fallo la reprogramacion de WhatsApp.",
                            id);
                    }
                }
            }
            catch (CalendarValidationException)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al mover la cita {CitaId}.", id);
                throw new InvalidOperationException("No fue posible mover la cita.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Operacion invalida al mover la cita {CitaId}.", id);
                throw;
            }
        }

        public async Task ResizeDurationAsync(int id, int duracionMinutos, CancellationToken cancellationToken = default)
        {
            if (duracionMinutos < 5)
                throw new CalendarValidationException("La duración mínima es de 5 minutos.");
            if (duracionMinutos > 600)
                throw new CalendarValidationException("La duración no puede superar 600 minutos.");

            var executionStrategy = _context.Database.CreateExecutionStrategy();

            try
            {
                await executionStrategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken);

                    var cita = await _context.Citas
                        .FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
                        ?? throw new InvalidOperationException("La cita indicada no existe o no pertenece al tenant actual.");

                    await EnsureNoOverlapAsync(
                        cita.FuncionarioId,
                        cita.FechaHoraCita,
                        duracionMinutos,
                        excludeCitaId: cita.Id,
                        cancellationToken);

                    cita.DuracionMinutos = duracionMinutos;

                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    _logger.LogInformation(
                        "Se ajusto la duracion de la cita {CitaId} a {DuracionMinutos} min.",
                        cita.Id,
                        duracionMinutos);
                });
            }
            catch (CalendarValidationException)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al ajustar la duracion de la cita {CitaId}.", id);
                throw new InvalidOperationException("No fue posible actualizar la duración de la cita.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Operacion invalida al ajustar la duracion de la cita {CitaId}.", id);
                throw;
            }
        }

        public async Task DeleteAsync(
            int id,
            string? motivoCancelacion = null,
            CancellationToken cancellationToken = default)
        {
            var existe = await _context.Citas
                .AsNoTracking()
                .AnyAsync(c => c.Id == id, cancellationToken);

            if (!existe)
            {
                throw new InvalidOperationException("La cita indicada no existe o no pertenece al tenant actual.");
            }

            // Cancelar mensajes pendientes antes de eliminar para evitar envíos post-eliminación.
            // Va ANTES de preparar el aviso de cancelación: si no, este barrería el aviso recién creado.
            try
            {
                await _notificationService.CancelPendingNotificationsAsync(id, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Fallo cancelar notificaciones WhatsApp pendientes para la cita {CitaId} antes de eliminarla.",
                    id);
            }

            // El aviso al cliente se decide y se reserva DENTRO de la transacción del borrado: al
            // eliminar la cita se pierde el vínculo BookingRequest.ConvertedCitaId (SetNull), y si
            // la cancelación se revierte, la reserva del aviso se revierte con ella.
            PreparedAppointmentCancellation? avisoCancelacion = null;
            var executionStrategy = _context.Database.CreateExecutionStrategy();

            try
            {
                await executionStrategy.ExecuteAsync(async () =>
                {
                    avisoCancelacion = null;

                    await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

                    var cita = await _context.Citas
                        .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

                    if (cita is null)
                    {
                        throw new InvalidOperationException("La cita indicada no existe o no pertenece al tenant actual.");
                    }

                    avisoCancelacion = await _cancellationNotificationService.PrepareAsync(
                        id,
                        motivoCancelacion,
                        cancellationToken);

                    _context.Citas.Remove(cita);
                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                });

                _logger.LogInformation("Se elimino la cita {CitaId}.", id);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al eliminar la cita {CitaId}.", id);
                throw new InvalidOperationException("No fue posible eliminar la cita.");
            }

            // La cancelación ya está confirmada: WhatsApp nunca la revierte. SendAsync no lanza.
            if (avisoCancelacion is not null)
            {
                await _cancellationNotificationService.SendAsync(avisoCancelacion, cancellationToken);
            }
        }

        public Task ProcessVisitsAsync(CancellationToken cancellationToken = default) =>
            _visitasAutomaticasService.ProcesarCitasFinalizadas(cancellationToken);

        private async Task ValidateRequestAsync(
            CalendarUpsertRequest request,
            ResolvedAppointmentData resolvedAppointment,
            Servicio? servicio,
            int duracion,
            CancellationToken cancellationToken)
        {
            if (request.FechaHoraCita == default)
            {
                throw new CalendarValidationException("Debe indicar una fecha y hora valida.", nameof(CitaCreateVM.FechaHoraCita));
            }

            if (!SupportedTipos.Contains(request.Tipo, StringComparer.Ordinal))
            {
                throw new CalendarValidationException("El tipo de cita indicado no es valido.", nameof(CitaCreateVM.Tipo));
            }

            if (request.FuncionarioId <= 0)
            {
                throw new CalendarValidationException("Debe seleccionar un funcionario valido.", nameof(CitaCreateVM.FuncionarioId));
            }

            if (request.Tipo == "DESCANSO" && request.FuncionarioIds.Count == 0)
            {
                throw new CalendarValidationException(
                    "Debe seleccionar al menos un funcionario para el descanso.",
                    nameof(CitaCreateVM.FuncionarioIds));
            }

            if (request.Tipo == "CITA")
            {
                if (string.IsNullOrWhiteSpace(resolvedAppointment.NombreCliente))
                {
                    throw new CalendarValidationException("Debe indicar el nombre del cliente.", nameof(CitaCreateVM.NombreCliente));
                }

                if (request.EsServicioPersonalizado)
                {
                    if (string.IsNullOrWhiteSpace(request.ServicioNombrePersonalizado))
                    {
                        throw new CalendarValidationException("Debe indicar el nombre del servicio personalizado.", nameof(CitaCreateVM.ServicioNombrePersonalizado));
                    }

                    if (request.ServicioNombrePersonalizado.Length > 100)
                    {
                        throw new CalendarValidationException("El nombre del servicio personalizado no puede exceder 100 caracteres.", nameof(CitaCreateVM.ServicioNombrePersonalizado));
                    }

                    if (!request.DuracionMinutos.HasValue)
                    {
                        throw new CalendarValidationException("Debe indicar la duracion del servicio personalizado.", nameof(CitaCreateVM.DuracionMinutos));
                    }

                    if (request.DuracionMinutos.Value < MinCustomServicioDurationMinutes ||
                        request.DuracionMinutos.Value > MaxCustomServicioDurationMinutes)
                    {
                        throw new CalendarValidationException(
                            $"La duracion del servicio personalizado debe estar entre {MinCustomServicioDurationMinutes} y {MaxCustomServicioDurationMinutes} minutos.",
                            nameof(CitaCreateVM.DuracionMinutos));
                    }
                }
                else
                {
                    if (!request.ServicioId.HasValue || request.ServicioId.Value <= 0)
                    {
                        throw new CalendarValidationException("Debe seleccionar un servicio.", nameof(CitaCreateVM.ServicioId));
                    }

                    if (servicio is null)
                    {
                        throw new CalendarValidationException("El servicio seleccionado no existe, no esta activo o no pertenece al tenant actual.", nameof(CitaCreateVM.ServicioId));
                    }
                }
            }
            else
            {
                if (!request.DuracionMinutos.HasValue)
                {
                    throw new CalendarValidationException("Debe indicar la duracion del descanso.", nameof(CitaCreateVM.DuracionMinutos));
                }

                if (request.DuracionMinutos.Value < MinDescansoDurationMinutes ||
                    request.DuracionMinutos.Value > MaxDescansoDurationMinutes)
                {
                    throw new CalendarValidationException(
                        $"La duracion del descanso debe estar entre {MinDescansoDurationMinutes} y {MaxDescansoDurationMinutes} minutos.",
                        nameof(CitaCreateVM.DuracionMinutos));
                }
            }

            if (duracion <= 0)
            {
                throw new CalendarValidationException("La duracion de la cita debe ser mayor a cero.");
            }

            if (!string.IsNullOrWhiteSpace(resolvedAppointment.TelefonoCliente) && resolvedAppointment.TelefonoCliente.Length > 20)
            {
                throw new CalendarValidationException("El telefono no puede exceder 20 caracteres.", nameof(CitaCreateVM.TelefonoCliente));
            }

            if (!string.IsNullOrWhiteSpace(resolvedAppointment.NombreCliente) && resolvedAppointment.NombreCliente.Length > 100)
            {
                throw new CalendarValidationException("El nombre del cliente no puede exceder 100 caracteres.", nameof(CitaCreateVM.NombreCliente));
            }

            if (request.Duplicar)
            {
                // Un descanso repetido es el mismo bloque a la misma hora en otros días: usa la
                // repetición que ya existía para las citas, sin una segunda implementación.
                if (request.Tipo == "DESCANSO" && request.FechasDuplicadas.Count > MaxDescansoFechasRepetidas)
                {
                    throw new CalendarValidationException(
                        $"No se pueden repetir mas de {MaxDescansoFechasRepetidas} fechas en una sola operacion.",
                        nameof(CitaCreateVM.FechasDuplicadas));
                }

                foreach (var fecha in request.FechasDuplicadas)
                {
                    if (!TryParseDuplicateDate(fecha, out _))
                    {
                        throw new CalendarValidationException("Una de las fechas duplicadas no es valida.", nameof(CitaCreateVM.FechasDuplicadas));
                    }
                }
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Valida TODOS los colaboradores de la operación contra el tenant actual en UNA consulta y
        /// los devuelve en el orden en que se seleccionaron (el primero es el titular de la
        /// respuesta). Un id inventado, inactivo o de otro tenant aborta la operación completa:
        /// nunca se crea un lote a medias.
        /// </summary>
        private async Task<List<FuncionarioSnapshot>> EnsureFuncionariosActivosAsync(
            IReadOnlyList<int> funcionarioIds,
            CancellationToken cancellationToken)
        {
            if (funcionarioIds.Count == 0)
            {
                throw new CalendarValidationException(
                    "Debe seleccionar un funcionario valido.",
                    nameof(CitaCreateVM.FuncionarioId));
            }

            var encontrados = await _context.Funcionarios
                .AsNoTracking()
                .Where(f => funcionarioIds.Contains(f.IdFuncionario) && f.Activo)
                .Select(f => new FuncionarioSnapshot
                {
                    IdFuncionario = f.IdFuncionario,
                    Nombre = f.Nombre,
                    ColorCalendario = f.ColorCalendario
                })
                .ToListAsync(cancellationToken);

            var porId = encontrados.ToDictionary(f => f.IdFuncionario);
            var ordenados = new List<FuncionarioSnapshot>(funcionarioIds.Count);

            foreach (var funcionarioId in funcionarioIds)
            {
                if (!porId.TryGetValue(funcionarioId, out var snapshot))
                {
                    throw new CalendarValidationException(
                        funcionarioIds.Count == 1
                            ? "El funcionario seleccionado no existe, esta inactivo o no pertenece al tenant actual."
                            : "Uno de los funcionarios seleccionados no existe, esta inactivo o no pertenece al tenant actual.",
                        nameof(CitaCreateVM.FuncionarioId));
                }

                ordenados.Add(snapshot);
            }

            return ordenados;
        }

        /// <summary>
        /// Revalida la matriz completa <c>fechas × colaboradores</c> dentro de la transacción, con
        /// los datos de ESTE instante. Lo que el navegador vio en la vista previa es UX: si entre
        /// la consulta y el guardado alguien agendó encima, acá se detecta y no se crea nada.
        /// </summary>
        private async Task EnsureDescansoDisponibleAsync(
            IReadOnlyList<FuncionarioSnapshot> funcionarios,
            IReadOnlyList<DateTime> targets,
            int duracionMinutos,
            CancellationToken cancellationToken)
        {
            if (funcionarios.Count == 0 || targets.Count == 0)
            {
                return;
            }

            // Todas las ocurrencias comparten hora por construcción (BuildCreationTargets copia la
            // hora de la fecha base en cada día repetido).
            var hora = TimeOnly.FromDateTime(targets[0]);

            var disponibilidad = await _availabilityService.CheckManyAsync(
                funcionarios.Select(f => f.IdFuncionario).ToList(),
                targets.Select(DateOnly.FromDateTime).ToList(),
                hora,
                duracionMinutos,
                excludeCitaId: null,
                cancellationToken);

            var conflictos = disponibilidad
                .Where(fecha => !fecha.Disponible)
                .SelectMany(fecha => fecha.Conflictos)
                .ToList();

            if (conflictos.Count == 0)
            {
                return;
            }

            throw new CalendarValidationException(BuildDescansoConflictMessage(conflictos));
        }

        /// <summary>
        /// Convierte conflictos estructurados en el texto que ve el usuario. La descripción del
        /// conflicto es del dominio; armar la frase es de esta frontera, y a propósito solo nombra
        /// colaborador, franja y tipo de bloque: nunca el cliente de la cita que estorba.
        /// </summary>
        private static string BuildDescansoConflictMessage(IReadOnlyList<ScheduleConflict> conflictos)
        {
            var detalle = conflictos
                .OrderBy(conflicto => conflicto.Fecha)
                .ThenBy(conflicto => conflicto.Inicio)
                .Take(MaxConflictosReportados)
                .Select(ScheduleConflictDescriber.DescribeWithDate);

            var mensaje = "No se puede registrar este descanso. " + string.Join("; ", detalle) + ".";

            var restantes = conflictos.Count - MaxConflictosReportados;
            if (restantes > 0)
            {
                mensaje += $" Y {restantes} conflicto(s) mas.";
            }

            return mensaje;
        }

        private async Task<FuncionarioSnapshot> EnsureFuncionarioActivoAsync(int funcionarioId, CancellationToken cancellationToken)
        {
            var funcionario = await _context.Funcionarios
                .AsNoTracking()
                .Where(f => f.IdFuncionario == funcionarioId && f.Activo)
                .Select(f => new FuncionarioSnapshot
                {
                    IdFuncionario = f.IdFuncionario,
                    Nombre = f.Nombre,
                    ColorCalendario = f.ColorCalendario
                })
                .SingleOrDefaultAsync(cancellationToken);

            if (funcionario is null)
            {
                throw new CalendarValidationException(
                    "El funcionario seleccionado no existe, esta inactivo o no pertenece al tenant actual.",
                    nameof(CitaCreateVM.FuncionarioId));
            }

            return funcionario;
        }

        private static bool IsCustomServicio(CalendarUpsertRequest request) =>
            request.Tipo == "CITA" && request.EsServicioPersonalizado;

        private async Task<Servicio?> ResolveServicioAsync(
            CalendarUpsertRequest request,
            CancellationToken cancellationToken)
        {
            // Las citas personalizadas y los descansos no usan el catálogo.
            if (request.Tipo != "CITA" || request.EsServicioPersonalizado || !request.ServicioId.HasValue)
            {
                return null;
            }

            return await _context.Servicios
                .AsNoTracking()
                .Where(s => s.Id == request.ServicioId.Value && s.Activo)
                .Select(s => new Servicio
                {
                    Id = s.Id,
                    Nombre = s.Nombre,
                    DuracionMinutos = s.DuracionMinutos,
                    Precio = s.Precio,
                    Activo = s.Activo
                })
                .SingleOrDefaultAsync(cancellationToken);
        }

        /// <summary>
        /// Resuelve a qué Cliente pertenece la cita. Es el ÚNICO lugar del sistema donde una cita
        /// queda vinculada a un cliente (y donde nace un Cliente a partir de una cita): lo usan el
        /// calendario y la confirmación de reservas online, así que no existen dos reglas distintas.
        ///
        /// <para>
        /// El navegador puede mandar un <c>ClienteId</c> (lo que vio el usuario) y un
        /// <c>ClienteLinkMode</c> (lo que pidió), pero la decisión final se toma acá, dentro de la
        /// transacción que guarda la cita: si entre la comprobación del formulario y el guardado
        /// alguien registró ese teléfono, se reutiliza ese cliente en vez de duplicarlo.
        /// </para>
        /// </summary>
        private async Task<ResolvedAppointmentData> ResolveAppointmentDataAsync(
            CalendarUpsertRequest request,
            CancellationToken cancellationToken)
        {
            if (request.Tipo == "DESCANSO")
            {
                return new ResolvedAppointmentData(
                    NombreCliente: "DESCANSO",
                    TelefonoCliente: null,
                    ClienteId: null,
                    WhatsAppConsentAtCreation: false,
                    WhatsAppConsentSource: null,
                    WhatsAppConsentCapturedAtUtc: null);
            }

            if (request.ClienteId.HasValue)
            {
                // Cliente elegido explícitamente. El filtro global por tenant garantiza que un
                // ClienteId de otro negocio no se encuentre (→ null): el id del navegador nunca
                // alcanza para leer o vincular datos ajenos.
                var clienteSeleccionado = await LoadClienteForWriteAsync(request.ClienteId.Value, cancellationToken);

                if (clienteSeleccionado is null)
                {
                    throw new CalendarValidationException(
                        "El cliente seleccionado no existe o no pertenece al tenant actual.",
                        nameof(CitaCreateVM.ClienteId));
                }

                return BuildLinkedAppointmentData(clienteSeleccionado, request, soloAutorizacionExplicita: true);
            }

            if (request.ClienteLinkMode == ClienteLinkMode.SinVincular)
            {
                return BuildManualAppointmentData(request);
            }

            // Re-resolución autoritativa por teléfono normalizado (regla única del sistema).
            var resolucion = await _clienteIdentityService.ResolveAsync(
                request.NombreCliente,
                request.TelefonoCliente,
                cancellationToken);

            if (resolucion.IsAmbiguous)
            {
                // Dos o más clientes con el mismo teléfono (datos históricos): NO se elige uno al
                // azar. La cita se guarda sin vincular y el administrador puede resolverlo luego.
                _logger.LogInformation(
                    "Telefono con {Coincidencias} clientes registrados: la cita se guarda sin vincular.",
                    resolucion.Matches.Count);

                return BuildManualAppointmentData(request);
            }

            var coincidencia = resolucion.SingleMatch;

            if (coincidencia is null && request.ClienteLinkMode == ClienteLinkMode.Registrar)
            {
                if (resolucion.Status == ClienteIdentityStatus.InsufficientData ||
                    string.IsNullOrWhiteSpace(request.NombreCliente))
                {
                    // Sin teléfono utilizable no se registra a nadie: crear un cliente sin la señal
                    // de identidad sería fabricar duplicados.
                    return BuildManualAppointmentData(request);
                }

                coincidencia = await _clienteIdentityService.RegisterAsync(
                    new ClienteRegistrationRequest(
                        request.NombreCliente!,
                        request.TelefonoCliente!,
                        request.WhatsAppConsentAtCreation,
                        request.WhatsAppConsentSource,
                        request.WhatsAppConsentCapturedAtUtc,
                        request.WhatsAppConsentCapturedByUserId),
                    cancellationToken);
            }

            if (coincidencia is null)
            {
                return BuildManualAppointmentData(request);
            }

            var cliente = await LoadClienteForWriteAsync(coincidencia.ClienteId, cancellationToken);

            return cliente is null
                ? BuildManualAppointmentData(request)
                : BuildLinkedAppointmentData(cliente, request, soloAutorizacionExplicita: false);
        }

        /// <summary>
        /// Carga el cliente CON seguimiento (sin AsNoTracking) para poder persistir una autorización
        /// recién otorgada dentro de la MISMA transacción que guarda la cita.
        /// </summary>
        private Task<ClientesModel?> LoadClienteForWriteAsync(int clienteId, CancellationToken cancellationToken) =>
            _context.Clientes.FirstOrDefaultAsync(current => current.Id == clienteId, cancellationToken);

        private ResolvedAppointmentData BuildLinkedAppointmentData(
            ClientesModel cliente,
            CalendarUpsertRequest request,
            bool soloAutorizacionExplicita)
        {
            // Cuando el cliente lo resolvió el servidor (el formulario creía que no existía) y el
            // administrador marcó la autorización de WhatsApp en la cita, esa autorización se
            // conserva aplicándola al Cliente. De lo contrario se perdería en silencio: al haber
            // ClienteId, la política de consentimiento solo mira el flag del cliente.
            var otorgarAutorizacion = request.AutorizarWhatsAppAlGuardar ||
                (!soloAutorizacionExplicita &&
                 request.WhatsAppConsentAtCreation &&
                 string.Equals(
                     request.WhatsAppConsentSource,
                     WhatsAppConsentSources.CitaManual,
                     StringComparison.Ordinal));

            var effectiveConsent = ApplyClienteWhatsAppAuthorizationIfRequested(
                cliente,
                request,
                otorgarAutorizacion);

            return new ResolvedAppointmentData(
                // Datos canónicos del cliente registrado: el nombre abreviado que se escribió en la
                // cita no reescribe el maestro ni se guarda en su lugar.
                NombreCliente: cliente.Nombre,
                TelefonoCliente: cliente.NumeroTelefono,
                ClienteId: cliente.Id,
                // El consentimiento efectivo refleja el valor persistido del cliente (ya sea el
                // que tenía o el recién otorgado). La decisión de envío la reevalúa el servicio
                // de WhatsApp releyendo el cliente, por lo que la fuente de verdad es el Cliente.
                WhatsAppConsentAtCreation: effectiveConsent,
                WhatsAppConsentSource: WhatsAppConsentSources.ClienteRegistrado,
                WhatsAppConsentCapturedAtUtc: DateTime.UtcNow);
        }

        private static ResolvedAppointmentData BuildManualAppointmentData(CalendarUpsertRequest request)
        {
            var consentGranted = request.WhatsAppConsentAtCreation;

            return new ResolvedAppointmentData(
                NombreCliente: request.NombreCliente,
                TelefonoCliente: request.TelefonoCliente,
                ClienteId: null,
                WhatsAppConsentAtCreation: consentGranted,
                WhatsAppConsentSource: consentGranted
                    ? WhatsAppConsentSources.CitaManual
                    : WhatsAppConsentSources.SinConsentimiento,
                WhatsAppConsentCapturedAtUtc: consentGranted
                    ? request.WhatsAppConsentCapturedAtUtc ?? DateTime.UtcNow
                    : null);
        }

        // Aplica la autorización de WhatsApp otorgada desde el formulario de la cita para un cliente
        // existente. Devuelve el consentimiento efectivo (persistido) del cliente. Reglas:
        //  - Solo actúa si se marcó explícitamente AutorizarWhatsAppAlGuardar (checkbox del formulario).
        //  - Nunca desautoriza: si el cliente ya autorizaba, no se toca (evita re-sellar auditoría).
        //  - Nunca autoriza sin un teléfono válido (no basta con "tener teléfono" vacío/espacios).
        //  - El cambio queda en el ChangeTracker y se persiste con el SaveChanges de la transacción,
        //    de modo que si la cita falla, la autorización tampoco se guarda (consistencia atómica).
        private bool ApplyClienteWhatsAppAuthorizationIfRequested(
            ClientesModel cliente,
            CalendarUpsertRequest request,
            bool otorgarAutorizacion)
        {
            if (!otorgarAutorizacion ||
                cliente.AceptaMensajesWhatsApp ||
                string.IsNullOrWhiteSpace(cliente.NumeroTelefono))
            {
                return cliente.AceptaMensajesWhatsApp;
            }

            var previo = cliente.AceptaMensajesWhatsApp;
            cliente.AceptaMensajesWhatsApp = true;
            cliente.WhatsAppConsentUpdatedAtUtc = DateTime.UtcNow;
            cliente.WhatsAppConsentSource = WhatsAppConsentSources.CitaManual;
            cliente.WhatsAppConsentTextVersion = WhatsAppConsentTextVersions.WaOptInV1;
            cliente.WhatsAppConsentCapturedByUserId = request.WhatsAppConsentCapturedByUserId;

            _logger.LogInformation(
                "Autorizacion de WhatsApp registrada desde el formulario de cita. TenantId {TenantId}. ClienteId {ClienteId}. UsuarioId {UsuarioId}. ValorAnterior {ValorAnterior}. ValorNuevo {ValorNuevo}.",
                cliente.TenantId,
                cliente.Id,
                request.WhatsAppConsentCapturedByUserId,
                previo,
                true);

            return true;
        }

        /// <summary>
        /// Valida el horario contra la ÚNICA fuente de disponibilidad
        /// (<see cref="IFuncionarioAvailabilityService"/>), que combina citas, descansos y bloqueos
        /// recurrentes. Antes esta comprobación vivía duplicada acá y en reservas públicas.
        /// </summary>
        private async Task EnsureNoOverlapAsync(
            int funcionarioId,
            DateTime inicio,
            int duracionMinutos,
            int? excludeCitaId,
            CancellationToken cancellationToken)
        {
            var resultado = await _availabilityService.CheckAsync(
                funcionarioId,
                inicio,
                duracionMinutos,
                excludeCitaId,
                cancellationToken);

            if (!resultado.Disponible)
            {
                throw new CalendarValidationException(
                    resultado.Motivo ?? "Ya existe una cita o descanso en ese horario.");
            }
        }

        private static int ResolveDuracion(CalendarUpsertRequest request, Servicio? servicio)
        {
            // Descanso y servicio personalizado toman la duración del propio request.
            if (request.Tipo == "DESCANSO" || request.EsServicioPersonalizado)
            {
                return request.DuracionMinutos ?? DefaultDurationMinutes;
            }

            return servicio?.DuracionMinutos ?? DefaultDurationMinutes;
        }

        private static List<DateTime> BuildCreationTargets(CalendarUpsertRequest request)
        {
            var targets = new List<DateTime>
            {
                request.FechaHoraCita
            };

            if (!request.Duplicar || request.FechasDuplicadas.Count == 0)
            {
                return targets;
            }

            foreach (var fecha in request.FechasDuplicadas)
            {
                if (!TryParseDuplicateDate(fecha, out var parsedDate))
                {
                    throw new CalendarValidationException("Una de las fechas duplicadas no es valida.", nameof(CitaCreateVM.FechasDuplicadas));
                }

                targets.Add(new DateTime(
                    parsedDate.Year,
                    parsedDate.Month,
                    parsedDate.Day,
                    request.FechaHoraCita.Hour,
                    request.FechaHoraCita.Minute,
                    0));
            }

            return targets;
        }

        private static void EnsureNoDuplicateTargets(IReadOnlyList<DateTime> targets)
        {
            var duplicatedTarget = targets
                .GroupBy(target => target)
                .FirstOrDefault(group => group.Count() > 1);

            if (duplicatedTarget is not null)
            {
                throw new CalendarValidationException(
                    $"La fecha duplicada {duplicatedTarget.Key:yyyy-MM-dd} genera un horario repetido en la solicitud.",
                    nameof(CitaCreateVM.FechasDuplicadas));
            }
        }

        private static CalendarAppointmentResponse BuildAppointmentResponse(
            Cita cita,
            int duracion,
            string funcionarioNombre,
            string colorCalendario,
            string? servicioNombre,
            bool esServicioPersonalizado) =>
            new()
            {
                Id = cita.Id,
                Tipo = cita.Tipo,
                NombreCliente = cita.NombreCliente,
                TelefonoCliente = cita.TelefonoCliente,
                ClienteId = cita.ClienteId,
                FechaHoraCita = cita.FechaHoraCita,
                DuracionMinutos = duracion,
                FuncionarioId = cita.FuncionarioId,
                FuncionarioNombre = funcionarioNombre,
                ColorCalendario = colorCalendario,
                ServicioId = cita.ServicioId,
                ServicioNombre = servicioNombre,
                EsServicioPersonalizado = esServicioPersonalizado,
                WhatsAppConsentAtCreation = cita.WhatsAppConsentAtCreation,
                WhatsAppConsentSource = cita.WhatsAppConsentSource,
                WhatsAppConsentCapturedAtUtc = cita.WhatsAppConsentCapturedAtUtc,
                EstadoConfirmacionWhatsApp = cita.EstadoConfirmacionWhatsApp,
                ConfirmacionWhatsAppEnviadaUtc = cita.ConfirmacionWhatsAppEnviadaUtc,
                RecordatorioWhatsAppTresHorasEnviadoUtc = cita.RecordatorioWhatsAppTresHorasEnviadoUtc
            };

        private static CalendarUpsertRequest NormalizeRequest(CalendarUpsertRequest request)
        {
            var tipo = NormalizeTipo(request.Tipo);
            var funcionarioIds = ResolveFuncionarioIds(request, tipo);

            return new CalendarUpsertRequest
            {
                NombreCliente = NormalizeOptionalText(request.NombreCliente),
                TelefonoCliente = NormalizeOptionalPhone(request.TelefonoCliente),
                ClienteId = request.ClienteId.HasValue && request.ClienteId.Value > 0
                    ? request.ClienteId.Value
                    : null,
                ClienteLinkMode = request.ClienteLinkMode,
                ServicioId = request.ServicioId,
                EsServicioPersonalizado = request.EsServicioPersonalizado,
                ServicioNombrePersonalizado = NormalizeOptionalText(request.ServicioNombrePersonalizado),
                FechaHoraCita = request.FechaHoraCita == default
                    ? default
                    : NormalizeToMinute(request.FechaHoraCita),
                FuncionarioId = funcionarioIds.Count > 0 ? funcionarioIds[0] : request.FuncionarioId,
                FuncionarioIds = funcionarioIds,
                Tipo = tipo,
                DuracionMinutos = request.DuracionMinutos,
                WhatsAppConsentAtCreation = request.WhatsAppConsentAtCreation,
                WhatsAppConsentSource = NormalizeOptionalText(request.WhatsAppConsentSource),
                WhatsAppConsentCapturedAtUtc = NormalizeUtcTimestamp(request.WhatsAppConsentCapturedAtUtc),
                AutorizarWhatsAppAlGuardar = request.AutorizarWhatsAppAlGuardar,
                WhatsAppConsentCapturedByUserId = NormalizeOptionalText(request.WhatsAppConsentCapturedByUserId),
                Duplicar = request.Duplicar,
                FechasDuplicadas = request.FechasDuplicadas
                    .Where(fecha => !string.IsNullOrWhiteSpace(fecha))
                    .Select(fecha => fecha.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
        }

        /// <summary>
        /// Resuelve a quién se aplica la entrada de agenda. Es la barrera que mantiene la CITA
        /// como operación de UN funcionario: por muchos ids que traiga el formulario, una cita se
        /// reduce siempre a <see cref="CalendarUpsertRequest.FuncionarioId"/>. Solo el DESCANSO
        /// acepta varios, y aun así se deduplican conservando el orden de selección.
        /// </summary>
        private static IReadOnlyList<int> ResolveFuncionarioIds(CalendarUpsertRequest request, string tipo)
        {
            if (tipo != "DESCANSO")
            {
                return request.FuncionarioId > 0
                    ? new[] { request.FuncionarioId }
                    : Array.Empty<int>();
            }

            var seleccionados = request.FuncionarioIds
                .Where(id => id > 0)
                .Distinct()
                .ToArray();

            if (seleccionados.Length > 0)
            {
                return seleccionados;
            }

            // Compatibilidad: un descanso de un solo colaborador enviado como antes.
            return request.FuncionarioId > 0
                ? new[] { request.FuncionarioId }
                : Array.Empty<int>();
        }

        private static CalendarMoveRequest NormalizeMoveRequest(CalendarMoveRequest request)
        {
            if (request.FechaHoraCita == default)
            {
                throw new CalendarValidationException("Debe indicar una fecha y hora valida.");
            }

            if (request.FuncionarioId.HasValue && request.FuncionarioId.Value <= 0)
            {
                throw new CalendarValidationException("Debe seleccionar un funcionario valido.");
            }

            return new CalendarMoveRequest
            {
                FechaHoraCita = NormalizeToMinute(request.FechaHoraCita),
                FuncionarioId = request.FuncionarioId
            };
        }

        private static string NormalizeTipo(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "CITA";
            }

            return value.Trim().ToUpperInvariant();
        }

        private static string? NormalizeOptionalText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return string.Join(
                ' ',
                value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        private static string? NormalizeOptionalPhone(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static DateTime? NormalizeUtcTimestamp(DateTime? value)
        {
            if (!value.HasValue)
            {
                return null;
            }

            return value.Value.Kind switch
            {
                DateTimeKind.Utc => value.Value,
                DateTimeKind.Local => value.Value.ToUniversalTime(),
                _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
            };
        }

        private static DateTime NormalizeToMinute(DateTime value) =>
            new(
                value.Year,
                value.Month,
                value.Day,
                value.Hour,
                value.Minute,
                0);

        private static bool TryParseDuplicateDate(string value, out DateTime parsedDate) =>
            DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out parsedDate);

        private sealed class FuncionarioSnapshot
        {
            public int IdFuncionario { get; init; }

            public string Nombre { get; init; } = string.Empty;

            public string ColorCalendario { get; init; } = string.Empty;
        }

        private sealed record ResolvedAppointmentData(
            string? NombreCliente,
            string? TelefonoCliente,
            int? ClienteId,
            bool WhatsAppConsentAtCreation,
            string? WhatsAppConsentSource,
            DateTime? WhatsAppConsentCapturedAtUtc);

    }
}
