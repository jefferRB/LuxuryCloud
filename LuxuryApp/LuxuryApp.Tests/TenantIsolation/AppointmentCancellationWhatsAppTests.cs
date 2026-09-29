using System.Net;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.PublicPages;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services;
using LuxuryApp.Services.Horarios;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.WhatsApp;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Tests.TenantIsolation
{
    /// <summary>
    /// Aviso de cancelacion por WhatsApp: SOLO para citas nacidas de una reserva online, una sola
    /// vez, con los parametros del template en el orden exacto, y sin que un fallo de Meta toque
    /// la cancelacion.
    /// </summary>
    public class AppointmentCancellationWhatsAppTests
    {
        [Fact]
        public async Task CancelarCitaDeReservaOnline_EnviaLaPlantillaUnaSolaVez()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fromOnlineBooking: true);

            await fixture.CommandService.DeleteAsync(cita.Id, "El profesional tuvo un imprevisto.");

            Assert.Equal(1, fixture.MetaClient.CancellationSendCount);
            Assert.Empty(await fixture.Context.Citas.AsNoTracking().ToListAsync());

            var log = await fixture.Context.WhatsAppMessageLogs
                .AsNoTracking()
                .SingleAsync(message => message.NotificationType == WhatsAppNotificationTypes.Cancellation);

            Assert.Equal(WhatsAppMessageDirections.Outbound, log.Direction);
            Assert.Equal(WhatsAppMessageStatuses.Sent, log.Status);
            Assert.Equal("luxurycloud_cancelacion_cita", log.TemplateName);
            Assert.NotNull(log.MetaMessageId);
            // La cita se elimino: la FK quedo en NULL pero la bitacora sobrevive.
            Assert.Null(log.CitaId);
        }

        [Fact]
        public async Task CancelarCitaManual_NoEnviaWhatsApp()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fromOnlineBooking: false);

            await fixture.CommandService.DeleteAsync(cita.Id, "Ya no puede venir.");

            Assert.Equal(0, fixture.MetaClient.CancellationSendCount);
            Assert.False(
                await fixture.Context.WhatsAppMessageLogs
                    .AsNoTracking()
                    .AnyAsync(message => message.NotificationType == WhatsAppNotificationTypes.Cancellation),
                "Una cita creada a mano en la agenda no debe generar aviso de cancelacion.");
        }

        [Fact]
        public async Task ParametrosDelTemplate_ViajanEnElOrdenExacto()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fromOnlineBooking: true);

            await fixture.CommandService.DeleteAsync(cita.Id, "  Cambio  de\nhorario  ");

            var parameters = Assert.Single(fixture.MetaClient.CancellationParameters);
            var ordered = parameters.ToOrderedBodyParameters();

            Assert.Equal(8, ordered.Count);
            Assert.Equal("Cliente Reserva", ordered[0]);              // {{1}} cliente
            Assert.Equal(Fixture.BusinessName, ordered[1]);           // {{2}} negocio
            Assert.Equal("Corte de cabello", ordered[2]);             // {{3}} servicio
            Assert.Equal("27/05/2026", ordered[3]);                   // {{4}} fecha
            Assert.Equal(
                new DateTime(2026, 5, 27, 10, 0, 0).ToString("hh:mm tt", System.Globalization.CultureInfo.GetCultureInfo("es-CR")),
                ordered[4]);                                          // {{5}} hora
            Assert.Equal("Cambio de horario", ordered[5]);            // {{6}} motivo, ya saneado
            Assert.Equal(Fixture.BusinessName, ordered[6]);           // {{7}} negocio otra vez
            Assert.Equal(Fixture.BusinessPublicPhone, ordered[7]);    // {{8}} telefono del NEGOCIO

            // {{2}} y {{7}} son el mismo negocio a proposito; {{8}} nunca es el numero central.
            Assert.Equal(ordered[1], ordered[6]);
        }

        [Fact]
        public async Task SinMotivo_UsaElTextoNeutroPorDefecto()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fromOnlineBooking: true);

            await fixture.CommandService.DeleteAsync(cita.Id, "   ");

            var parameters = Assert.Single(fixture.MetaClient.CancellationParameters);
            Assert.Equal(
                AppointmentCancellationWhatsAppService.DefaultCancellationReason,
                parameters.CancellationReason);
            Assert.Equal("Colaborador no disponible", parameters.CancellationReason);
        }

        [Fact]
        public async Task SiMetaFalla_LaCitaSigueCanceladaYElFalloQuedaRegistrado()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fromOnlineBooking: true);
            fixture.MetaClient.NextSendResult = MetaWhatsAppSendResult.Failed(
                "131047",
                "Re-engagement message.",
                HttpStatusCode.BadRequest);

            await fixture.CommandService.DeleteAsync(cita.Id, "Cierre por feriado.");

            Assert.Empty(await fixture.Context.Citas.AsNoTracking().ToListAsync());

            var log = await fixture.Context.WhatsAppMessageLogs
                .AsNoTracking()
                .SingleAsync(message => message.NotificationType == WhatsAppNotificationTypes.Cancellation);

            Assert.Equal(WhatsAppMessageStatuses.Failed, log.Status);
            Assert.Equal("131047", log.ErrorCode);
        }

        [Fact]
        public async Task Reejecucion_NoDuplicaElAviso()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fromOnlineBooking: true);

            // Primera pasada: se reserva la fila y se envia.
            var primera = await fixture.CancellationService.PrepareAsync(cita.Id, "Imprevisto.");
            Assert.NotNull(primera);
            await fixture.CancellationService.SendAsync(primera!);

            // Reejecucion del mismo flujo sobre la misma cita (reintento, doble click, retry del job).
            var segunda = await fixture.CancellationService.PrepareAsync(cita.Id, "Imprevisto.");

            Assert.Null(segunda);
            Assert.Equal(1, fixture.MetaClient.CancellationSendCount);
            Assert.Equal(
                1,
                await fixture.Context.WhatsAppMessageLogs
                    .AsNoTracking()
                    .CountAsync(message => message.NotificationType == WhatsAppNotificationTypes.Cancellation));
        }

        [Fact]
        public async Task SinConsentimientoDelCliente_NoEnviaYRegistraLaOmision()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fromOnlineBooking: true, whatsAppConsent: false);

            await fixture.CommandService.DeleteAsync(cita.Id, "Imprevisto.");

            Assert.Equal(0, fixture.MetaClient.CancellationSendCount);

            var log = await fixture.Context.WhatsAppMessageLogs
                .AsNoTracking()
                .SingleAsync(message => message.NotificationType == WhatsAppNotificationTypes.Cancellation);

            Assert.Equal(WhatsAppMessageStatuses.SkippedConsentMissing, log.Status);
            Assert.Equal(WhatsAppErrorCodes.ConsentMissing, log.ErrorCode);
        }

        [Fact]
        public async Task SinTelefonoPublicoDelNegocio_IgualEnviaConTextoDeRelleno()
        {
            // El aviso al cliente no depende de que el negocio haya cargado su pagina publica.
            using var fixture = await Fixture.CreateAsync(seedPublicPagePhone: false);
            var cita = await fixture.SeedCitaAsync(fromOnlineBooking: true);

            await fixture.CommandService.DeleteAsync(cita.Id, "Imprevisto.");

            Assert.Equal(1, fixture.MetaClient.CancellationSendCount);

            var parameters = Assert.Single(fixture.MetaClient.CancellationParameters);
            Assert.False(string.IsNullOrWhiteSpace(parameters.BusinessPhone));
        }

        [Fact]
        public async Task ClienteRegistradoQueAutorizoEnLaReserva_RecibeElAviso()
        {
            // Regresion de la causa real del no-envio: al confirmar la reserva de un cliente YA
            // REGISTRADO, ConfirmAsync guarda la cita con WhatsAppConsentAtCreation = false y delega
            // el consentimiento en Clientes.AceptaMensajesWhatsApp, campo que la reserva online nunca
            // escribe. La autorizacion explicita vive en BookingRequest.AceptaWhatsApp.
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(
                fromOnlineBooking: true,
                whatsAppConsent: true,
                comoClienteRegistrado: true,
                clienteAceptaMensajesWhatsApp: false);

            await fixture.CommandService.DeleteAsync(cita.Id, "Imprevisto.");

            Assert.Equal(1, fixture.MetaClient.CancellationSendCount);
        }

        [Fact]
        public async Task SinConsentimiento_ElPreviewIndicaContactoManualConElTelefonoDelCliente()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fromOnlineBooking: true, whatsAppConsent: false);

            var preview = await fixture.CancellationService.PreviewAsync(cita.Id);

            Assert.False(preview.NotificaraPorWhatsApp);
            Assert.Equal("88889999", preview.TelefonoCliente);
            Assert.Equal(
                "El cliente no autoriz\u00f3 notificaciones por WhatsApp. Contactalo manualmente al 88889999.",
                preview.Mensaje);
            Assert.Equal(
                "Sin autorizaci\u00f3n de WhatsApp. Contactar manualmente al 88889999.",
                preview.MensajeContactoManual);
        }

        [Fact]
        public async Task SinConsentimientoYSinTelefono_ElPreviewOmiteElNumero()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(
                fromOnlineBooking: true,
                whatsAppConsent: false,
                telefonoCliente: null);

            var preview = await fixture.CancellationService.PreviewAsync(cita.Id);

            Assert.False(preview.NotificaraPorWhatsApp);
            Assert.Null(preview.TelefonoCliente);
            Assert.Equal(
                "El cliente no autoriz\u00f3 notificaciones por WhatsApp. Contactalo manualmente.",
                preview.Mensaje);
        }

        [Fact]
        public async Task ConConsentimiento_ElPreviewAnunciaElEnvio()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fromOnlineBooking: true);

            var preview = await fixture.CancellationService.PreviewAsync(cita.Id);

            Assert.True(preview.NotificaraPorWhatsApp);
            Assert.Equal(
                "Se enviar\u00e1 una notificaci\u00f3n de cancelaci\u00f3n por WhatsApp al cliente.",
                preview.Mensaje);
            Assert.Equal(string.Empty, preview.MensajeContactoManual);
        }

        [Fact]
        public async Task CitaManual_ElPreviewNoAnunciaNada()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fromOnlineBooking: false);

            var preview = await fixture.CancellationService.PreviewAsync(cita.Id);

            Assert.False(preview.NotificaraPorWhatsApp);
            Assert.Equal(string.Empty, preview.Mensaje);
        }

        [Fact]
        public async Task MotivoEditado_ReemplazaAlPorDefecto()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fromOnlineBooking: true);

            await fixture.CommandService.DeleteAsync(cita.Id, "El local cierra por mantenimiento.");

            var parameters = Assert.Single(fixture.MetaClient.CancellationParameters);
            Assert.Equal("El local cierra por mantenimiento.", parameters.CancellationReason);
            Assert.NotEqual(
                AppointmentCancellationWhatsAppService.DefaultCancellationReason,
                parameters.CancellationReason);
        }

        [Fact]
        public async Task ReservaDeOtroTenant_NoPuedeApuntarAUnaCitaAjena()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fromOnlineBooking: false);

            // El guard de tenant del DbContext impide siquiera crear el vinculo cruzado. Por eso
            // BookingRequest.ConvertedCitaId sirve como fuente de verdad del origen: una reserva de
            // otro negocio nunca puede convertir una cita ajena en "reserva online".
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.SeedForeignBookingRequestPointingToAsync(cita.Id));

            Assert.Contains("otro tenant", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class Fixture : IDisposable
        {
            public const string BusinessName = "Barberia Prueba";
            public const string BusinessPublicPhone = "2222-3333";

            private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

            private Fixture(
                Guid tenantId,
                TestTenantProvider tenantProvider,
                ProyectoIdentity.Datos.ApplicationDbContext context,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                CapturingMetaWhatsAppClient metaClient,
                AppointmentCancellationWhatsAppService cancellationService,
                CalendarCommandService commandService)
            {
                TenantId = tenantId;
                TenantProvider = tenantProvider;
                Context = context;
                _connection = connection;
                MetaClient = metaClient;
                CancellationService = cancellationService;
                CommandService = commandService;
            }

            public static DateTime FixedNowLocal => new(2026, 5, 26, 10, 30, 0);

            public static DateTime FixedNowUtc =>
                new DateTimeOffset(FixedNowLocal, TimeSpan.FromHours(-6)).UtcDateTime;

            public Guid TenantId { get; }
            public TestTenantProvider TenantProvider { get; }
            public ProyectoIdentity.Datos.ApplicationDbContext Context { get; }
            public CapturingMetaWhatsAppClient MetaClient { get; }
            public AppointmentCancellationWhatsAppService CancellationService { get; }
            public CalendarCommandService CommandService { get; }

            public static async Task<Fixture> CreateAsync(bool seedPublicPagePhone = true)
            {
                var tenantId = Guid.NewGuid();
                var tenantProvider = new TestTenantProvider { TenantId = tenantId };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

                var options = new StaticOptionsMonitor<MetaWhatsAppOptions>(new MetaWhatsAppOptions { Enabled = true });
                var cache = new MemoryCache(new MemoryCacheOptions());
                var accessCache = new TenantCommercialAccessCache(cache);
                var businessDateTimeProvider = new FixedBusinessDateTimeProvider(FixedNowLocal);
                var subscriptionService = new SuscripcionService(
                    context,
                    cache,
                    accessCache,
                    businessDateTimeProvider,
                    Options.Create(new TilopayRepeatOptions()),
                    NullLogger<SuscripcionService>.Instance);
                var commercialAccessResolver = new TenantCommercialAccessResolver(
                    context,
                    cache,
                    accessCache,
                    subscriptionService,
                    businessDateTimeProvider);
                var settings = new TenantWhatsAppSettingsService(
                    context,
                    tenantProvider,
                    options,
                    subscriptionService,
                    businessDateTimeProvider,
                    commercialAccessResolver,
                    NullLogger<TenantWhatsAppSettingsService>.Instance);
                var tenantDisplayNameService = new TenantDisplayNameService(
                    context,
                    tenantProvider,
                    new HttpContextAccessor());
                var metaClient = new CapturingMetaWhatsAppClient();

                var cancellationNotifier = new WhatsAppCancellationNotifier(
                    context,
                    metaClient,
                    businessDateTimeProvider,
                    tenantDisplayNameService,
                    NullLogger<WhatsAppCancellationNotifier>.Instance);

                var cancellationService = new AppointmentCancellationWhatsAppService(
                    context,
                    metaClient,
                    options,
                    businessDateTimeProvider,
                    settings,
                    cancellationNotifier,
                    NullLogger<AppointmentCancellationWhatsAppService>.Instance);

                var commandService = new CalendarCommandService(
                    context,
                    new NoOpCalendarWhatsAppNotificationService(),
                    cancellationService,
                    new VisitasAutomaticasService(context, businessDateTimeProvider),
                    new FuncionarioAvailabilityService(context),
                    ControllerTestSupport.CreateClienteIdentityService(context),
                    NullLogger<CalendarCommandService>.Instance);

                context.Tenants.Add(new Tenant { Id = tenantId, Nombre = BusinessName });

                var basePlanId = Guid.NewGuid();
                context.Planes.Add(new Plan
                {
                    Id = basePlanId,
                    Codigo = PlanCodes.Basic,
                    Nombre = "Basico",
                    Moneda = "CRC",
                    PrecioMensual = 8000m,
                    MaxFuncionarios = 1,
                    Activo = true
                });
                context.Suscripciones.Add(new Suscripcion
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    PlanId = basePlanId,
                    CodigoPlan = PlanCodes.Basic,
                    Estado = EstadoSuscripcion.Activa,
                    Proveedor = PaymentProviderType.Tilopay,
                    FechaInicio = FixedNowUtc.AddDays(-3),
                    FechaFin = FixedNowUtc.AddDays(27),
                    FechaProximoCobroUtc = FixedNowUtc.AddDays(27),
                    FechaUltimaActualizacionUtc = FixedNowUtc
                });

                var addOnPlanId = Guid.NewGuid();
                context.Planes.Add(new Plan
                {
                    Id = addOnPlanId,
                    Codigo = PlanCodes.WhatsApp400,
                    Nombre = "WhatsApp 400",
                    Moneda = "CRC",
                    PrecioMensual = 6000m,
                    LimiteMensajesMensual = 400,
                    Activo = true
                });
                context.TenantSubscriptionAddons.Add(new TenantSubscriptionAddon
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    PlanId = addOnPlanId,
                    AddonCode = PlanCodes.WhatsApp400,
                    Estado = EstadoSuscripcion.Activa,
                    MonthlyMessageLimit = 400,
                    FechaInicio = FixedNowUtc.AddDays(-1),
                    FechaFin = FixedNowUtc.AddDays(29),
                    CreatedAtUtc = FixedNowUtc,
                    UpdatedAtUtc = FixedNowUtc
                });
                context.TenantWhatsAppSettings.Add(new TenantWhatsAppSettings
                {
                    TenantId = tenantId,
                    IsEnabled = true,
                    SendConfirmationOnCreate = true,
                    SendReminderThreeHoursBefore = true,
                    DailyMessageLimit = 30,
                    TimeZoneId = TenantWhatsAppSettings.DefaultTimeZoneId,
                    CreatedAtUtc = FixedNowUtc,
                    UpdatedAtUtc = FixedNowUtc
                });

                context.TenantPublicPages.Add(new TenantPublicPage
                {
                    TenantId = tenantId,
                    IsPublished = true,
                    Phone = seedPublicPagePhone ? BusinessPublicPhone : null
                });

                await context.SaveChangesAsync();

                return new Fixture(
                    tenantId,
                    tenantProvider,
                    context,
                    connection,
                    metaClient,
                    cancellationService,
                    commandService);
            }

            public async Task<Cita> SeedCitaAsync(
                bool fromOnlineBooking,
                bool whatsAppConsent = true,
                bool comoClienteRegistrado = false,
                bool clienteAceptaMensajesWhatsApp = false,
                string? telefonoCliente = "88889999")
            {
                var puesto = new Puesto
                {
                    NombrePuesto = $"Puesto {Guid.NewGuid():N}",
                    Detalle = "Cancelaciones",
                    Activo = true
                };
                Context.Puestos.Add(puesto);
                await Context.SaveChangesAsync();

                var funcionario = new Funcionario
                {
                    Nombre = "Andrea",
                    IdPuesto = puesto.IdPuesto,
                    ColorCalendario = "#123456",
                    PorcentajeGanancia = 40m,
                    PorcentajeProducto = 10m,
                    FechaIngreso = new DateTime(2026, 5, 1),
                    Activo = true
                };
                Context.Funcionarios.Add(funcionario);

                var servicio = new Servicio
                {
                    Nombre = "Corte de cabello",
                    Precio = 8000m,
                    DuracionMinutos = 30,
                    Activo = true
                };
                Context.Servicios.Add(servicio);
                await Context.SaveChangesAsync();

                // Cliente registrado: replica exactamente lo que deja ConfirmAsync (consentimiento
                // de la cita en false y delegado en Clientes.AceptaMensajesWhatsApp).
                LuxuryApp.Models.DataBase.ClientesModel? cliente = null;
                if (comoClienteRegistrado)
                {
                    cliente = new LuxuryApp.Models.DataBase.ClientesModel
                    {
                        Nombre = "Cliente Reserva",
                        NumeroTelefono = telefonoCliente ?? "88889999",
                        AceptaMensajesWhatsApp = clienteAceptaMensajesWhatsApp
                    };
                    Context.Clientes.Add(cliente);
                    await Context.SaveChangesAsync();
                }

                var cita = new Cita
                {
                    NombreCliente = "Cliente Reserva",
                    TelefonoCliente = telefonoCliente,
                    ClienteId = cliente?.Id,
                    FechaHoraCita = new DateTime(2026, 5, 27, 10, 0, 0),
                    Tipo = "CITA",
                    DuracionMinutos = 30,
                    FuncionarioId = funcionario.IdFuncionario,
                    ServicioId = servicio.Id,
                    WhatsAppConsentAtCreation = comoClienteRegistrado ? false : whatsAppConsent,
                    WhatsAppConsentSource = comoClienteRegistrado
                        ? WhatsAppConsentSources.ClienteRegistrado
                        : WhatsAppConsentSources.ClienteForm,
                    WhatsAppConsentCapturedAtUtc = FixedNowUtc
                };
                Context.Citas.Add(cita);
                await Context.SaveChangesAsync();

                if (fromOnlineBooking)
                {
                    Context.BookingRequests.Add(new BookingRequest
                    {
                        ServicioId = servicio.Id,
                        FuncionarioId = funcionario.IdFuncionario,
                        FuncionarioAsignadoId = funcionario.IdFuncionario,
                        NombreCliente = cita.NombreCliente!,
                        TelefonoCliente = telefonoCliente ?? "88889999",
                        FechaHoraInicioSolicitada = cita.FechaHoraCita,
                        FechaHoraFinCalculada = cita.FechaHoraCita.AddMinutes(30),
                        DuracionMinutos = 30,
                        Estado = BookingRequestStates.Confirmed,
                        AceptaWhatsApp = whatsAppConsent,
                        CreatedAtUtc = FixedNowUtc,
                        ConfirmedAtUtc = FixedNowUtc,
                        // Este es el vinculo real reserva -> cita: la unica fuente de verdad del origen.
                        ConvertedCitaId = cita.Id
                    });
                    await Context.SaveChangesAsync();
                }

                return cita;
            }

            /// <summary>
            /// Reserva de OTRO tenant apuntando al mismo id de cita. Sirve para comprobar que el
            /// origen se resuelve dentro del tenant y no por coincidencia de ids.
            /// </summary>
            public async Task SeedForeignBookingRequestPointingToAsync(int citaId)
            {
                var otherTenantId = Guid.NewGuid();
                Context.Tenants.Add(new Tenant { Id = otherTenantId, Nombre = "Otro negocio" });
                await Context.SaveChangesAsync();

                TenantProvider.TenantId = otherTenantId;

                var puesto = new Puesto { NombrePuesto = "Ajeno", Detalle = "Ajeno", Activo = true };
                Context.Puestos.Add(puesto);
                await Context.SaveChangesAsync();

                var funcionario = new Funcionario
                {
                    Nombre = "Ajeno",
                    IdPuesto = puesto.IdPuesto,
                    ColorCalendario = "#654321",
                    PorcentajeGanancia = 40m,
                    PorcentajeProducto = 10m,
                    FechaIngreso = new DateTime(2026, 5, 1),
                    Activo = true
                };
                Context.Funcionarios.Add(funcionario);

                var servicio = new Servicio { Nombre = "Ajeno", Precio = 5000m, DuracionMinutos = 30, Activo = true };
                Context.Servicios.Add(servicio);
                await Context.SaveChangesAsync();

                Context.BookingRequests.Add(new BookingRequest
                {
                    ServicioId = servicio.Id,
                    FuncionarioId = funcionario.IdFuncionario,
                    NombreCliente = "Cliente ajeno",
                    TelefonoCliente = "70000000",
                    FechaHoraInicioSolicitada = new DateTime(2026, 5, 27, 10, 0, 0),
                    FechaHoraFinCalculada = new DateTime(2026, 5, 27, 10, 30, 0),
                    DuracionMinutos = 30,
                    Estado = BookingRequestStates.Confirmed,
                    CreatedAtUtc = FixedNowUtc,
                    ConvertedCitaId = citaId
                });
                await Context.SaveChangesAsync();

                Context.ChangeTracker.Clear();
                TenantProvider.TenantId = TenantId;
            }

            public void Dispose()
            {
                Context.Dispose();
                _connection.Dispose();
            }
        }

        private sealed class CapturingMetaWhatsAppClient : IMetaWhatsAppClient
        {
            public int CancellationSendCount { get; private set; }

            public List<WhatsAppCancellationTemplateParameters> CancellationParameters { get; } = [];

            public MetaWhatsAppSendResult? NextSendResult { get; set; }

            public string? NormalizePhoneNumber(string? phoneNumber) =>
                string.IsNullOrWhiteSpace(phoneNumber)
                    ? null
                    : $"+506{new string(phoneNumber.Where(char.IsDigit).ToArray())}";

            public bool IsValidPhoneNumber(string? phoneNumber) => NormalizePhoneNumber(phoneNumber) is not null;

            public Task<MetaWhatsAppSendResult> SendConfirmationTemplateAsync(
                string recipientPhone,
                string customerName,
                string businessName,
                string appointmentDate,
                string appointmentTime,
                string professionalName,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(MetaWhatsAppSendResult.Succeeded("confirmation", HttpStatusCode.OK, null));

            public Task<MetaWhatsAppSendResult> SendReminderTemplateAsync(
                string recipientPhone,
                string customerName,
                string businessName,
                string appointmentTime,
                string professionalName,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(MetaWhatsAppSendResult.Succeeded("reminder", HttpStatusCode.OK, null));

            public Task<MetaWhatsAppSendResult> SendCancellationTemplateAsync(
                string recipientPhone,
                WhatsAppCancellationTemplateParameters parameters,
                CancellationToken cancellationToken = default)
            {
                CancellationSendCount++;
                CancellationParameters.Add(parameters);

                if (NextSendResult is not null)
                {
                    var configured = NextSendResult;
                    NextSendResult = null;
                    return Task.FromResult(configured);
                }

                return Task.FromResult(MetaWhatsAppSendResult.Succeeded(
                    $"cancellation-{CancellationSendCount}",
                    HttpStatusCode.OK,
                    null));
            }

            public Task<MetaWhatsAppSendResult> SendTextMessageAsync(
                string recipientPhone,
                string message,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(MetaWhatsAppSendResult.Succeeded("text", HttpStatusCode.OK, null));

            public Task<MetaWhatsAppConfigurationDiagnosticResult> TestConfigurationAsync(
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }
    }
}
