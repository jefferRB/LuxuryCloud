using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.Clientes;
using LuxuryApp.Services.Notifications;
using LuxuryApp.Services.WhatsApp;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Reservas
{
    public sealed class PublicBookingService : IPublicBookingService
    {
        private const string ResolvedTenantItemKey = "__resolved_tenant_id";
        private const int MaxPendingPerPhone = 3;

        /// <summary>Cuántos próximos espacios se sugieren. Corto a propósito: es un atajo, no una agenda.</summary>
        private const int MaxNextSlots = 5;

        private readonly ApplicationDbContext _context;
        private readonly IBookingSettingsService _settingsService;
        private readonly IBookingAvailabilityService _availabilityService;
        private readonly IBookingCatalogService _catalogService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ITenantWhatsAppFeatureService _whatsAppFeatureService;
        private readonly INotificationService _notificationService;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IClienteIdentityService _clienteIdentityService;
        private readonly ILogger<PublicBookingService> _logger;

        public PublicBookingService(
            ApplicationDbContext context,
            IBookingSettingsService settingsService,
            IBookingAvailabilityService availabilityService,
            IBookingCatalogService catalogService,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ITenantWhatsAppFeatureService whatsAppFeatureService,
            INotificationService notificationService,
            IHttpContextAccessor httpContextAccessor,
            IClienteIdentityService clienteIdentityService,
            ILogger<PublicBookingService> logger)
        {
            _context = context;
            _settingsService = settingsService;
            _availabilityService = availabilityService;
            _catalogService = catalogService;
            _businessDateTimeProvider = businessDateTimeProvider;
            _whatsAppFeatureService = whatsAppFeatureService;
            _notificationService = notificationService;
            _httpContextAccessor = httpContextAccessor;
            _clienteIdentityService = clienteIdentityService;
            _logger = logger;
        }

        public async Task<PublicBookingTenantContext?> ResolveContextAsync(
            string slug,
            CancellationToken cancellationToken = default)
        {
            var context = await _settingsService.ResolvePublicBySlugAsync(slug, cancellationToken);
            if (context is null)
            {
                return null;
            }

            // Fija el tenant en el request para que el resto de consultas tenant-scoped
            // (servicios, funcionarios, citas) y el SaveChanges blindado lo apliquen.
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext is not null)
            {
                httpContext.Items[ResolvedTenantItemKey] = context.TenantId;
            }

            return context;
        }

        public async Task<PublicBookingPageViewModel> BuildPageAsync(
            PublicBookingTenantContext context,
            int? preselectedServiceId = null,
            CancellationToken cancellationToken = default)
        {
            // Solo servicios publicados (con fallback a todos los activos si no hay configuración).
            var servicios = await _catalogService.GetPublicServicesAsync(cancellationToken);
            var preselectedService = preselectedServiceId.HasValue
                ? servicios.FirstOrDefault(servicio => servicio.Id == preselectedServiceId.Value)
                : null;

            var funcionarios = context.PermiteElegirFuncionario
                ? await _context.Funcionarios
                    .AsNoTracking()
                    .Where(f => f.Activo)
                    .OrderBy(f => f.Nombre)
                    .Select(f => new PublicBookingEmployeeOption
                    {
                        Id = f.IdFuncionario,
                        Nombre = f.Nombre,
                        Puesto = f.Puesto != null ? f.Puesto.NombrePuesto : null,
                        // Foto solo si el negocio la habilita y el funcionario lo permite.
                        FotoUrl = (context.MostrarFotosFuncionarios && f.MostrarFotoEnReservas) ? f.FotoUrl : null,
                        ColorAvatar = f.ColorCalendario
                    })
                    .ToListAsync(cancellationToken)
                : new List<PublicBookingEmployeeOption>();

            var today = _businessDateTimeProvider.Today();
            var maxDate = today.AddDays(Math.Max(1, context.MaxDaysAhead));

            // Solo ofrecemos el checkbox de WhatsApp si el tenant lo tiene activo,
            // para no prometer una confirmación que no se enviará.
            var mostrarWhatsApp = await _whatsAppFeatureService
                .IsWhatsAppEnabledForCurrentTenantAsync(cancellationToken);

            return new PublicBookingPageViewModel
            {
                Slug = context.Slug,
                NombreNegocio = context.NombreNegocio,
                MensajeBienvenida = context.MensajeBienvenida,
                PermiteElegirFuncionario = context.PermiteElegirFuncionario,
                PermiteCualquierFuncionario = context.PermiteCualquierFuncionario,
                MostrarWhatsApp = mostrarWhatsApp,
                MinAdvanceMinutes = context.MinAdvanceMinutes,
                MaxDaysAhead = context.MaxDaysAhead,
                MinDateIso = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                MaxDateIso = maxDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                SubmissionToken = Guid.NewGuid().ToString("N"),
                PreselectedServiceId = preselectedService?.Id,
                PreselectedServiceName = preselectedService?.Nombre,
                Servicios = servicios,
                Funcionarios = funcionarios
            };
        }

        public async Task<BookingAvailabilityResult> GetAvailabilityAsync(
            PublicBookingTenantContext context,
            int servicioId,
            string? fecha,
            int? funcionarioId,
            CancellationToken cancellationToken = default)
        {
            if (!TryParseDate(fecha, out var fechaParsed))
            {
                return new BookingAvailabilityResult
                {
                    Success = false,
                    Fecha = fecha ?? string.Empty,
                    Mensaje = "Selecciona una fecha válida."
                };
            }

            // Seguridad: el servicio debe estar publicado online (bloquea ids manipulados/ocultos).
            var servicios = await _catalogService.GetPublicServicesAsync(cancellationToken);
            var servicio = servicios.FirstOrDefault(s => s.Id == servicioId);
            if (servicio is null)
            {
                return new BookingAvailabilityResult
                {
                    Success = false,
                    Fecha = fechaParsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Mensaje = "Ese servicio no está disponible para reservas online."
                };
            }

            var fechaIso = fechaParsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var funcionarioFiltro = ResolveFuncionarioFiltro(context, funcionarioId);

            // Nombre del profesional elegido (solo si es compatible con el servicio).
            string? nombreFuncionario = null;
            if (funcionarioFiltro.HasValue)
            {
                if (!servicio.FuncionarioIds.Contains(funcionarioFiltro.Value))
                {
                    // Profesional no compatible con el servicio: no exponemos horarios.
                    return new BookingAvailabilityResult
                    {
                        Success = true,
                        Fecha = fechaIso,
                        DurationMinutes = servicio.DuracionMinutos,
                        ServiceName = servicio.Nombre,
                        Horas = Array.Empty<string>(),
                        Mensaje = "Ese profesional no atiende este servicio. Elegí otro profesional."
                    };
                }

                nombreFuncionario = await _context.Funcionarios
                    .AsNoTracking()
                    .Where(f => f.IdFuncionario == funcionarioFiltro.Value && f.Activo)
                    .Select(f => f.Nombre)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            var horas = await _availabilityService.GetAvailableSlotsAsync(
                servicioId,
                fechaParsed,
                funcionarioFiltro,
                cancellationToken);

            var result = new BookingAvailabilityResult
            {
                Success = true,
                Fecha = fechaIso,
                DurationMinutes = servicio.DuracionMinutos,
                ServiceName = servicio.Nombre,
                SelectedEmployeeName = nombreFuncionario,
                Horas = horas
            };

            if (horas.Count > 0)
            {
                // Criterio: las alternativas siguen visibles aunque la fecha elegida tenga espacio.
                // Se buscan desde el día SIGUIENTE, así la lista nunca repite lo que ya se muestra
                // arriba y no hace falta filtrar duplicados en el navegador.
                result.NextAvailableSlots = await BuildNextAvailableSlotsAsync(
                    context, servicioId, fechaParsed.AddDays(1), funcionarioFiltro, cancellationToken);

                return result;
            }

            // ── Sin horas ese día: construir mensaje inteligente + próximas disponibilidades ──
            var fechaLabel = FormatFechaLabel(fechaParsed);

            if (funcionarioFiltro.HasValue)
            {
                // ¿Hay disponibilidad con otros profesionales compatibles esa misma fecha?
                var horasCualquiera = await _availabilityService.GetAvailableSlotsAsync(
                    servicioId, fechaParsed, funcionarioId: null, cancellationToken);
                result.HasAvailabilityWithOtherEmployees = horasCualquiera.Count > 0;

                var nombre = nombreFuncionario ?? "ese profesional";
                result.Mensaje =
                    $"No encontramos espacios de {servicio.DuracionMinutos} min para {servicio.Nombre} con {nombre} el {fechaLabel}. " +
                    $"Este servicio requiere un bloque continuo de {servicio.DuracionMinutos} minutos.";
            }
            else
            {
                result.Mensaje =
                    $"No encontramos espacios de {servicio.DuracionMinutos} min para {servicio.Nombre} en esa fecha. " +
                    "Probá con otra fecha o revisá los próximos espacios disponibles.";
            }

            result.NextAvailableSlots = await BuildNextAvailableSlotsAsync(
                context, servicioId, fechaParsed, funcionarioFiltro, cancellationToken);

            return result;
        }

        public async Task<BookingNextSlotsResult> GetNextSlotsAsync(
            PublicBookingTenantContext context,
            int servicioId,
            int? funcionarioId,
            CancellationToken cancellationToken = default)
        {
            // Seguridad: el servicio debe estar publicado online en ESTE tenant. El catálogo es
            // tenant-scoped, así que un id de otro tenant sencillamente no aparece en la lista.
            var servicios = await _catalogService.GetPublicServicesAsync(cancellationToken);
            var servicio = servicios.FirstOrDefault(s => s.Id == servicioId);
            if (servicio is null)
            {
                return new BookingNextSlotsResult
                {
                    Success = false,
                    Mensaje = "Ese servicio no está disponible para reservas online."
                };
            }

            var funcionarioFiltro = ResolveFuncionarioFiltro(context, funcionarioId);

            // El profesional debe poder atender ESTE servicio. Un id ajeno o incompatible NO cae de
            // vuelta en "cualquiera": devuelve vacío, para no revelar la agenda de otra persona.
            if (funcionarioFiltro.HasValue && !servicio.FuncionarioIds.Contains(funcionarioFiltro.Value))
            {
                return new BookingNextSlotsResult
                {
                    Success = true,
                    Mensaje = "Ese profesional no atiende este servicio. Elegí otro profesional."
                };
            }

            // El punto de partida lo fija SIEMPRE el servidor (hoy en hora del negocio). El cliente
            // no puede pedir un rango arbitrario, ni mirar hacia atrás, ni saltarse el horizonte.
            var desde = DateOnly.FromDateTime(_businessDateTimeProvider.Today());

            return new BookingNextSlotsResult
            {
                Success = true,
                NextAvailableSlots = await BuildNextAvailableSlotsAsync(
                    context, servicioId, desde, funcionarioFiltro, cancellationToken)
            };
        }

        /// <summary>
        /// ÚNICO constructor de "próximos espacios" del módulo. Lo comparten el endpoint que se
        /// consulta al elegir servicio/profesional y la respuesta de una fecha concreta, para que
        /// no existan dos listas calculadas con criterios distintos.
        /// </summary>
        private async Task<IReadOnlyList<NextAvailableSlot>> BuildNextAvailableSlotsAsync(
            PublicBookingTenantContext context,
            int servicioId,
            DateOnly fromDate,
            int? funcionarioId,
            CancellationToken cancellationToken)
        {
            var sugerencias = await _availabilityService.GetNextAvailableSlotsAsync(
                servicioId, fromDate, funcionarioId, MaxNextSlots, cancellationToken);

            if (sugerencias.Count == 0)
            {
                return Array.Empty<NextAvailableSlot>();
            }

            // Minimización de datos: si el negocio no deja elegir profesional, tampoco publicamos
            // QUIÉN está libre a qué hora. Además el backend reasigna al enviar la solicitud, así
            // que mostrar un nombre aquí sería una promesa que el flujo no puede cumplir.
            var exponeFuncionario = context.PermiteElegirFuncionario;
            var nombres = new Dictionary<int, string>();

            if (exponeFuncionario)
            {
                // Nombres de las sugerencias en una sola consulta.
                var ids = sugerencias.Select(s => s.FuncionarioId).Distinct().ToList();
                nombres = await _context.Funcionarios
                    .AsNoTracking()
                    .Where(f => ids.Contains(f.IdFuncionario))
                    .Select(f => new { f.IdFuncionario, f.Nombre })
                    .ToDictionaryAsync(f => f.IdFuncionario, f => f.Nombre, cancellationToken);
            }

            return sugerencias.Select(s => new NextAvailableSlot
            {
                Fecha = s.Fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                FechaLabel = FormatFechaLabel(s.Fecha),
                Hora = s.Hora.ToString("HH:mm", CultureInfo.InvariantCulture),
                HoraLabel = FormatHoraLabel(s.Hora),
                FuncionarioId = exponeFuncionario ? s.FuncionarioId : null,
                FuncionarioNombre = exponeFuncionario && nombres.TryGetValue(s.FuncionarioId, out var n) ? n : null
            }).ToList();
        }

        private static readonly CultureInfo CrCulture = CultureInfo.GetCultureInfo("es-CR");

        private static string FormatFechaLabel(DateOnly fecha)
        {
            var label = fecha.ToDateTime(TimeOnly.MinValue).ToString("dddd dd/MM", CrCulture);
            return label.Length > 0 ? char.ToUpper(label[0], CrCulture) + label[1..] : label;
        }

        private static string FormatHoraLabel(TimeOnly hora) =>
            hora.ToString("h:mm tt", CrCulture);

        public async Task<PublicBookingSubmitResult> SubmitAsync(
            PublicBookingTenantContext context,
            PublicBookingRequestInput input,
            CancellationToken cancellationToken = default)
        {
            var mensajeExito = string.IsNullOrWhiteSpace(context.MensajeConfirmacion)
                ? "Tu solicitud fue enviada. El negocio la revisará y te confirmará pronto."
                : context.MensajeConfirmacion!.Trim();

            // Honeypot: si un bot rellenó el campo oculto, se simula éxito sin crear nada.
            if (!string.IsNullOrWhiteSpace(input.Website))
            {
                return PublicBookingSubmitResult.Ok(mensajeExito);
            }

            // Idempotencia: si este token ya creó una solicitud (doble click, reintento, JS duplicado),
            // devolvemos éxito con la solicitud existente y NO creamos otra ni reenviamos notificación.
            var token = NormalizeToken(input.SubmissionToken);
            if (token is not null)
            {
                var existentePorToken = await _context.BookingRequests
                    .AsNoTracking()
                    .AnyAsync(r => r.PublicSubmissionToken == token, cancellationToken);

                if (existentePorToken)
                {
                    return PublicBookingSubmitResult.Ok(mensajeExito);
                }
            }

            var nombre = CollapseWhitespace(input.Nombre);
            var telefono = (input.Telefono ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(nombre))
            {
                return PublicBookingSubmitResult.Fail("Indicá tu nombre completo.");
            }

            if (nombre.Length > 100)
            {
                return PublicBookingSubmitResult.Fail("El nombre es demasiado largo.");
            }

            if (string.IsNullOrWhiteSpace(telefono) || telefono.Length < 6 || telefono.Length > 30)
            {
                return PublicBookingSubmitResult.Fail("Indicá un número de teléfono válido.");
            }

            if (!TryParseDate(input.Fecha, out var fecha) || !TryParseTime(input.Hora, out var hora))
            {
                return PublicBookingSubmitResult.Fail("Selecciona una fecha y hora válidas.");
            }

            var inicio = fecha.ToDateTime(hora);

            // Validaciones de rango (revalidadas en backend, no se confía en el frontend).
            var now = _businessDateTimeProvider.Now();
            if (inicio <= now)
            {
                return PublicBookingSubmitResult.Fail("No podés reservar en una fecha u hora pasada.");
            }

            if (inicio < now.AddMinutes(Math.Max(0, context.MinAdvanceMinutes)))
            {
                return PublicBookingSubmitResult.Fail("Ese horario es demasiado pronto. Elegí uno más adelante.");
            }

            var maxFecha = DateOnly.FromDateTime(_businessDateTimeProvider.Today()).AddDays(Math.Max(1, context.MaxDaysAhead));
            if (fecha > maxFecha)
            {
                return PublicBookingSubmitResult.Fail("Esa fecha está fuera del rango permitido.");
            }

            // Seguridad: no se puede reservar un servicio oculto/inactivo manipulando el request.
            if (!await _catalogService.IsServiceVisibleOnlineAsync(input.ServicioId, cancellationToken))
            {
                return PublicBookingSubmitResult.Fail("El servicio seleccionado no está disponible para reservas online.");
            }

            // Funcionario solicitado (respeta las reglas del tenant).
            var funcionarioSolicitado = ResolveFuncionarioFiltro(context, input.FuncionarioId);
            if (input.FuncionarioId.HasValue && input.FuncionarioId.Value > 0 && !context.PermiteElegirFuncionario)
            {
                // Se ignora la selección si el tenant no la permite.
                funcionarioSolicitado = null;
            }

            // Anti-spam: máximo de solicitudes Pending por teléfono por tenant. Va fuera de la
            // transacción a propósito: es una cuota, no una regla de integridad, y mantenerla
            // afuera reduce el alcance de los bloqueos del hold.
            var pendientesMismoTelefono = await _context.BookingRequests
                .AsNoTracking()
                .CountAsync(
                    r => r.TelefonoCliente == telefono && r.Estado == BookingRequestStates.Pending,
                    cancellationToken);

            if (pendientesMismoTelefono >= MaxPendingPerPhone)
            {
                return PublicBookingSubmitResult.Fail(
                    "Ya tenés varias solicitudes pendientes. Esperá a que el negocio te confirme.");
            }

            var ipHash = HashIp();
            var userAgent = ResolveUserAgent();

            // ── Revalidación final + creación del hold, a prueba de concurrencia ───────────────
            // Desde que esta solicitud queda Pending OCUPA el intervalo, así que "comprobar que
            // está libre" y "crear la solicitud" tienen que ser una sola operación atómica. Se usa
            // el mismo patrón que la creación de citas (CalendarCommandService): estrategia de
            // ejecución + transacción SERIALIZABLE. Las lecturas de ocupación toman range locks
            // sobre el rango leído, de modo que dos POST simultáneos por el mismo recurso e
            // intervalo NO pueden ambos ver "libre" y ambos insertar: uno gana y el otro se aborta.
            // La garantía vive en la base de datos, así que sigue valiendo con varias instancias
            // de la aplicación detrás del balanceador.
            BookingRequest? tracked = null;
            BookingRequest? creada = null;
            PublicBookingSubmitResult? resultado = null;

            var executionStrategy = _context.Database.CreateExecutionStrategy();

            try
            {
                await executionStrategy.ExecuteAsync(async () =>
                {
                    tracked = null;
                    creada = null;
                    resultado = null;

                    await using var transaction = await _context.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable,
                        cancellationToken);

                    // Idempotencia por token, ahora dentro de la transacción.
                    if (token is not null &&
                        await _context.BookingRequests.AsNoTracking()
                            .AnyAsync(r => r.PublicSubmissionToken == token, cancellationToken))
                    {
                        resultado = PublicBookingSubmitResult.Ok(mensajeExito);
                        return;
                    }

                    // Duplicado obvio: misma fecha/hora/servicio/teléfono aún pendiente. Se responde
                    // éxito (es la MISMA solicitud reenviada), no "horario ocupado".
                    var duplicada = await _context.BookingRequests
                        .AsNoTracking()
                        .AnyAsync(
                            r => r.TelefonoCliente == telefono &&
                                 r.ServicioId == input.ServicioId &&
                                 r.FechaHoraInicioSolicitada == inicio &&
                                 r.Estado == BookingRequestStates.Pending,
                            cancellationToken);

                    if (duplicada)
                    {
                        resultado = PublicBookingSubmitResult.Ok(mensajeExito);
                        return;
                    }

                    // Revalida disponibilidad real en backend y resuelve el recurso concreto. Con
                    // "cualquier profesional" devuelve el PRIMER funcionario libre: queda reservado
                    // ese y sólo ese, los demás siguen disponibles a esa misma hora.
                    var resolucion = await _availabilityService.ResolveSlotAsync(
                        input.ServicioId,
                        inicio,
                        funcionarioSolicitado,
                        cancellationToken);

                    if (!resolucion.Disponible || !resolucion.FuncionarioId.HasValue)
                    {
                        resultado = PublicBookingSubmitResult.Fail(
                            resolucion.Motivo ?? "Ese horario ya no está disponible. Probá con otro.");
                        return;
                    }

                    var clienteId = await TryMatchClienteAsync(telefono, cancellationToken);

                    var solicitud = new BookingRequest
                    {
                        ServicioId = input.ServicioId,
                        // Lo que pidió el cliente (null = "cualquiera")...
                        FuncionarioId = funcionarioSolicitado,
                        // ...y el recurso que el servidor reserva para sostener el hold.
                        FuncionarioAsignadoId = resolucion.FuncionarioId.Value,
                        ClienteId = clienteId,
                        NombreCliente = nombre,
                        TelefonoCliente = telefono,
                        CorreoCliente = null,
                        NotasCliente = null,
                        FechaHoraInicioSolicitada = inicio,
                        FechaHoraFinCalculada = inicio.AddMinutes(resolucion.DuracionMinutos),
                        DuracionMinutos = resolucion.DuracionMinutos,
                        Estado = BookingRequestStates.Pending,
                        Origen = BookingRequestOrigins.PublicLink,
                        AceptaWhatsApp = input.AceptaWhatsApp,
                        PublicSubmissionToken = token,
                        CreatedAtUtc = DateTime.UtcNow,
                        IpHash = ipHash,
                        UserAgent = userAgent
                    };

                    _context.BookingRequests.Add(solicitud);
                    tracked = solicitud;

                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    tracked = null;
                    creada = solicitud;
                    resultado = PublicBookingSubmitResult.Ok(mensajeExito);
                });
            }
            catch (Exception ex) when (ex is DbUpdateException or DbException)
            {
                if (tracked is not null)
                {
                    _context.Entry(tracked).State = EntityState.Detached;
                }

                // Carrera con el mismo token (índice único filtrado): es idempotente, éxito.
                if (token is not null &&
                    await _context.BookingRequests.AsNoTracking()
                        .AnyAsync(r => r.PublicSubmissionToken == token, cancellationToken))
                {
                    return PublicBookingSubmitResult.Ok(mensajeExito);
                }

                // Carrera con OTRA solicitud por el mismo intervalo: la base de datos abortó a
                // este perdedor (deadlock/serialización). No queda ningún hold a medias.
                _logger.LogWarning(
                    ex,
                    "No se pudo registrar la solicitud de reserva del {Inicio:yyyy-MM-dd HH:mm} (conflicto de concurrencia o error de base de datos).",
                    inicio);

                return PublicBookingSubmitResult.Fail(
                    "Ese horario acaba de ser tomado por otra solicitud. Elegí otro horario, por favor.");
            }

            if (creada is null)
            {
                return resultado ?? PublicBookingSubmitResult.Fail(
                    "No pudimos registrar tu solicitud en este momento. Intentá de nuevo.");
            }

            // Centro de Notificaciones: avisa al negocio que llegó una solicitud nueva.
            // Nunca debe romper el flujo público si la notificación falla.
            try
            {
                await _notificationService.CreateBookingRequestReceivedAsync(creada, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "No se pudo generar la notificación de la solicitud de reserva {RequestId}.",
                    creada.Id);
            }

            return resultado ?? PublicBookingSubmitResult.Ok(mensajeExito);
        }

        private static int? ResolveFuncionarioFiltro(PublicBookingTenantContext context, int? funcionarioId)
        {
            if (!context.PermiteElegirFuncionario)
            {
                return null;
            }

            return funcionarioId.HasValue && funcionarioId.Value > 0 ? funcionarioId : null;
        }

        /// <summary>
        /// Pista de a qué cliente podría corresponder la solicitud, calculada en el SERVIDOR al
        /// guardarla. No se expone nunca al formulario público (el visitante no puede averiguar si
        /// un teléfono está registrado) y no crea ningún cliente: una solicitud pendiente puede
        /// terminar rechazada y no debe ensuciar la base de Clientes.
        ///
        /// <para>
        /// Usa el resolver de identidad compartido, así que con duplicados históricos no elige a
        /// nadie: la ambigüedad la resuelve el administrador al confirmar.
        /// </para>
        /// </summary>
        private async Task<int?> TryMatchClienteAsync(string telefono, CancellationToken cancellationToken)
        {
            var resolucion = await _clienteIdentityService.ResolveAsync(
                nombre: null,
                telefono,
                cancellationToken);

            return resolucion.SingleMatch?.ClienteId;
        }

        private string? HashIp()
        {
            var ip = _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
            if (string.IsNullOrWhiteSpace(ip))
            {
                return null;
            }

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ip));
            return Convert.ToHexString(bytes)[..64].ToLowerInvariant();
        }

        private string? ResolveUserAgent()
        {
            var ua = _httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString();
            if (string.IsNullOrWhiteSpace(ua))
            {
                return null;
            }

            return ua.Length > 400 ? ua[..400] : ua;
        }

        private static bool TryParseDate(string? value, out DateOnly date) =>
            DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

        private static bool TryParseTime(string? value, out TimeOnly time) =>
            TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

        /// <summary>Normaliza el token de envío: solo alfanuméricos/guiones, máx. 64. Vacío → null.</summary>
        private static string? NormalizeToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var cleaned = new string(value.Trim().Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
            if (cleaned.Length == 0)
            {
                return null;
            }

            return cleaned.Length > 64 ? cleaned[..64] : cleaned;
        }

        private static string CollapseWhitespace(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return string.Join(
                ' ',
                value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
    }
}
