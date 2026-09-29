using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Services.WhatsApp;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    /// <summary>
    /// "El negocio no contrató WhatsApp" y "el cliente no autorizó WhatsApp" son estados distintos.
    /// Confundirlos hacía que un tenant sin el complemento viera avisos de WhatsApp en cada acción
    /// y que se acusara al cliente de no autorizar algo que nunca se le ofreció.
    /// </summary>
    public class WhatsAppAddonVsConsentTests
    {
        // ── El motivo semántico: única lectura del ErrorCode ──────────────────────────────

        [Theory]
        [InlineData(WhatsAppErrorCodes.NoActiveWhatsAppAddon, WhatsAppNotificationReason.AddonInactive)]
        [InlineData(WhatsAppErrorCodes.SubscriptionRequired, WhatsAppNotificationReason.AddonInactive)]
        [InlineData(WhatsAppErrorCodes.ConsentMissing, WhatsAppNotificationReason.ConsentMissing)]
        [InlineData(WhatsAppErrorCodes.InvalidPhone, WhatsAppNotificationReason.CustomerPhoneMissing)]
        [InlineData(WhatsAppErrorCodes.NotConfigured, WhatsAppNotificationReason.NotConfigured)]
        [InlineData(WhatsAppErrorCodes.MonthlyLimitExceeded, WhatsAppNotificationReason.LimitReached)]
        public void ElMotivoDistingueComplementoDeConsentimiento(string errorCode, WhatsAppNotificationReason esperado)
        {
            Assert.Equal(esperado, WhatsAppNotificationReasons.FromErrorCode(errorCode));
        }

        [Fact]
        public void SoloElComplementoInactivoEsSilencioso()
        {
            Assert.True(WhatsAppNotificationReason.AddonInactive.IsSilent());
            Assert.False(WhatsAppNotificationReason.ConsentMissing.IsSilent());
            Assert.False(WhatsAppNotificationReason.NotConfigured.IsSilent());
            Assert.False(WhatsAppNotificationReason.ProviderFailed.IsSilent());
        }

        // ── Reservas: confirmar / rechazar sin complemento ────────────────────────────────

        [Fact]
        public async Task SinComplemento_ConfirmarNoMencionaWhatsApp()
        {
            using var fixture = await Fixture.CreateAsync(whatsAppAddonActivo: false);
            var solicitud = await fixture.SeedSolicitudPendienteAsync(nombreCliente: "Andrey Vargas Mora");

            // Lo que devuelve el motor real cuando el tenant no tiene el paquete.
            fixture.NotificationService.NextConfirmationResult = new WhatsAppConfirmationSendResult(
                WhatsAppConfirmationOutcome.Skipped,
                "No se envió la confirmación: se requiere el complemento de WhatsApp activo.",
                WhatsAppErrorCodes.NoActiveWhatsAppAddon,
                WhatsAppNotificationReason.AddonInactive);

            var result = await fixture.Service.ConfirmAsync(solicitud.Id, null, "user-1");

            Assert.True(result.Success);
            // Mensaje de éxito a secas, con el nombre de la persona de la reserva.
            Assert.Equal("Cita de Andrey agendada con éxito.", result.Message);
            Assert.DoesNotContain("WhatsApp", result.Message, StringComparison.OrdinalIgnoreCase);
            // El front solo avisa cuando hay estado de WhatsApp: null = silencio total.
            Assert.Null(result.WhatsAppStatus);
        }

        /// <summary>
        /// El motor evalúa el consentimiento ANTES que el complemento, así que un tenant sin
        /// WhatsApp recibe <c>ConsentMissing</c>, no <c>AddonInactive</c>. Ese era el defecto real:
        /// se acusaba al cliente de no autorizar algo que nunca se le ofreció.
        /// </summary>
        [Fact]
        public async Task SinComplemento_AunqueElMotorDigaConsentMissing_NoSeAcusaAlCliente()
        {
            using var fixture = await Fixture.CreateAsync(whatsAppAddonActivo: false);
            var solicitud = await fixture.SeedSolicitudPendienteAsync(aceptaWhatsApp: false, nombreCliente: "Andrey Vargas Mora");

            // Exactamente lo que devuelve el motor real en producción para este tenant.
            fixture.NotificationService.NextConfirmationResult = new WhatsAppConfirmationSendResult(
                WhatsAppConfirmationOutcome.Skipped,
                "No se envió la confirmación: el cliente no autorizó mensajes de WhatsApp.",
                WhatsAppErrorCodes.ConsentMissing,
                WhatsAppNotificationReason.ConsentMissing);

            var result = await fixture.Service.ConfirmAsync(solicitud.Id, null, "user-1");

            Assert.True(result.Success);
            Assert.Equal("Cita de Andrey agendada con éxito.", result.Message);
            Assert.DoesNotContain("WhatsApp", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("autoriz", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(result.WhatsAppStatus);
        }

        /// <summary>
        /// Sin complemento tampoco se habla de WhatsApp cuando el envío falla de verdad: para ese
        /// negocio el envío nunca debió existir.
        /// </summary>
        [Fact]
        public async Task SinComplemento_AunqueElEnvioFalle_NoSeMencionaWhatsApp()
        {
            using var fixture = await Fixture.CreateAsync(whatsAppAddonActivo: false);
            var solicitud = await fixture.SeedSolicitudPendienteAsync();

            fixture.NotificationService.NextConfirmationResult = new WhatsAppConfirmationSendResult(
                WhatsAppConfirmationOutcome.Failed,
                "No se pudo enviar la confirmación de WhatsApp.",
                WhatsAppErrorCodes.NotConfigured,
                WhatsAppNotificationReason.ProviderFailed);

            var result = await fixture.Service.ConfirmAsync(solicitud.Id, null, "user-1");

            Assert.True(result.Success);
            Assert.DoesNotContain("WhatsApp", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null(result.WhatsAppStatus);
        }

        [Fact]
        public async Task ConComplementoYErrorDeEnvio_NoSeConfundeConFaltaDeAutorizacion()
        {
            using var fixture = await Fixture.CreateAsync(whatsAppAddonActivo: true);
            var solicitud = await fixture.SeedSolicitudPendienteAsync(aceptaWhatsApp: true);

            fixture.NotificationService.NextConfirmationResult = new WhatsAppConfirmationSendResult(
                WhatsAppConfirmationOutcome.Failed,
                "Meta rechazó el mensaje.",
                WhatsAppErrorCodes.NotConfigured,
                WhatsAppNotificationReason.ProviderFailed);

            var result = await fixture.Service.ConfirmAsync(solicitud.Id, null, "user-1");

            Assert.True(result.Success);
            Assert.Equal("failed", result.WhatsAppStatus);
            Assert.DoesNotContain("autoriz", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task SinComplemento_RechazarNoMencionaWhatsApp()
        {
            using var fixture = await Fixture.CreateAsync(whatsAppAddonActivo: false);
            var solicitud = await fixture.SeedSolicitudPendienteAsync();

            var result = await fixture.Service.RejectAsync(solicitud.Id, "No hay espacio.", "user-1");

            Assert.True(result.Success);
            Assert.DoesNotContain("WhatsApp", result.Message, StringComparison.OrdinalIgnoreCase);

            var actualizada = await fixture.Context.BookingRequests.AsNoTracking().SingleAsync();
            Assert.Equal(BookingRequestStates.Rejected, actualizada.Estado);
        }

        [Fact]
        public async Task SinComplemento_LaPantallaDeReservasOfreceActivarlo()
        {
            using var fixture = await Fixture.CreateAsync(whatsAppAddonActivo: false);

            var page = await fixture.Service.BuildPageAsync(null, null);

            Assert.False(page.WhatsAppActivo);
        }

        [Fact]
        public async Task ConComplemento_LaPantallaNoOfreceActivarlo()
        {
            using var fixture = await Fixture.CreateAsync(whatsAppAddonActivo: true);

            var page = await fixture.Service.BuildPageAsync(null, null);

            Assert.True(page.WhatsAppActivo);
        }

        [Fact]
        public async Task ConComplementoYSinConsentimiento_SeInformaComoConsentMissing()
        {
            using var fixture = await Fixture.CreateAsync(whatsAppAddonActivo: true);
            var solicitud = await fixture.SeedSolicitudPendienteAsync(aceptaWhatsApp: false);

            fixture.NotificationService.NextConfirmationResult = new WhatsAppConfirmationSendResult(
                WhatsAppConfirmationOutcome.Skipped,
                "No se envió la confirmación: el cliente no autorizó mensajes de WhatsApp.",
                WhatsAppErrorCodes.ConsentMissing,
                WhatsAppNotificationReason.ConsentMissing);

            var result = await fixture.Service.ConfirmAsync(solicitud.Id, null, "user-1");

            Assert.True(result.Success);
            Assert.Equal("skipped", result.WhatsAppStatus);
            Assert.Contains("no autorizó notificaciones por WhatsApp", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ConComplementoYConsentimiento_SeInformaElEnvio()
        {
            using var fixture = await Fixture.CreateAsync(whatsAppAddonActivo: true);
            var solicitud = await fixture.SeedSolicitudPendienteAsync(aceptaWhatsApp: true);

            fixture.NotificationService.NextConfirmationResult = new WhatsAppConfirmationSendResult(
                WhatsAppConfirmationOutcome.Sent,
                "Confirmación de WhatsApp enviada.",
                Reason: WhatsAppNotificationReason.Eligible);

            var result = await fixture.Service.ConfirmAsync(solicitud.Id, null, "user-1");

            Assert.True(result.Success);
            Assert.Equal("sent", result.WhatsAppStatus);
            Assert.Contains("WhatsApp enviada", result.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ConComplemento_FalloDeMetaNoRevierteLaAprobacion()
        {
            using var fixture = await Fixture.CreateAsync(whatsAppAddonActivo: true);
            var solicitud = await fixture.SeedSolicitudPendienteAsync(aceptaWhatsApp: true);

            fixture.NotificationService.NextConfirmationResult = new WhatsAppConfirmationSendResult(
                WhatsAppConfirmationOutcome.Failed,
                "No se pudo enviar la confirmación de WhatsApp. Podés reintentarla.",
                "131047",
                WhatsAppNotificationReason.ProviderFailed);

            var result = await fixture.Service.ConfirmAsync(solicitud.Id, null, "user-1");

            Assert.True(result.Success);
            Assert.Equal("failed", result.WhatsAppStatus);

            var actualizada = await fixture.Context.BookingRequests.AsNoTracking().SingleAsync();
            Assert.Equal(BookingRequestStates.Confirmed, actualizada.Estado);
            Assert.NotNull(actualizada.ConvertedCitaId);
        }

        // ── Calendario: la MISMA regla, no un texto propio ────────────────────────────────

        [Fact]
        public async Task SinComplemento_ElCalendarioNoAcusaAlClienteDeNoAutorizar()
        {
            using var fixture = await Fixture.CreateAsync(whatsAppAddonActivo: false);
            var cita = await fixture.SeedCitaSinConsentimientoAsync();

            var detalle = await fixture.CreateCalendarQueryService().GetByIdAsync(cita.Id);

            Assert.NotNull(detalle);
            Assert.Equal(string.Empty, detalle!.WhatsAppConsentDisplay);
            Assert.Equal(string.Empty, detalle.WhatsAppStatusDisplay);
            Assert.False(detalle.CancelacionNotificaWhatsApp);
            Assert.Equal(string.Empty, detalle.CancelacionWhatsAppMensaje);
        }

        [Fact]
        public async Task ConComplemento_ElCalendarioSiInformaLaFaltaDeConsentimiento()
        {
            using var fixture = await Fixture.CreateAsync(whatsAppAddonActivo: true);
            var cita = await fixture.SeedCitaSinConsentimientoAsync();

            var detalle = await fixture.CreateCalendarQueryService().GetByIdAsync(cita.Id);

            Assert.NotNull(detalle);
            Assert.Contains("no autorizado", detalle!.WhatsAppConsentDisplay, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ElCalendarioYReservasComparteLaMismaCapability()
        {
            // Ambas pantallas preguntan a ITenantWhatsAppFeatureService; ninguna reimplementa la regla.
            using var fixture = await Fixture.CreateAsync(whatsAppAddonActivo: false);
            var cita = await fixture.SeedCitaSinConsentimientoAsync();

            var page = await fixture.Service.BuildPageAsync(null, null);
            var detalle = await fixture.CreateCalendarQueryService().GetByIdAsync(cita.Id);

            Assert.False(page.WhatsAppActivo);
            Assert.Equal(string.Empty, detalle!.WhatsAppConsentDisplay);
        }

        // ── Aislamiento multi-tenant ──────────────────────────────────────────────────────

        [Fact]
        public async Task SolicitudDeOtroTenant_NoSeConfirma()
        {
            using var fixture = await Fixture.CreateAsync(whatsAppAddonActivo: false);
            var solicitud = await fixture.SeedSolicitudPendienteAsync();

            fixture.TenantProvider.TenantId = Guid.NewGuid();
            fixture.Context.ChangeTracker.Clear();

            var result = await fixture.Service.ConfirmAsync(solicitud.Id, null, "otro-user");

            Assert.False(result.Success);
        }

        private sealed class Fixture : IDisposable
        {
            private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
            private readonly FakeTenantWhatsAppFeatureService _featureService;

            private Fixture(
                ApplicationDbContext context,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                TestTenantProvider tenantProvider,
                FakeTenantWhatsAppFeatureService featureService,
                NoOpCalendarWhatsAppNotificationService notificationService,
                BookingRequestService service,
                int servicioId,
                int funcionarioId)
            {
                Context = context;
                _connection = connection;
                TenantProvider = tenantProvider;
                _featureService = featureService;
                NotificationService = notificationService;
                Service = service;
                ServicioId = servicioId;
                FuncionarioId = funcionarioId;
            }

            private static DateTime Ahora => new(2026, 8, 27, 14, 0, 0);

            public ApplicationDbContext Context { get; }
            public TestTenantProvider TenantProvider { get; }
            public NoOpCalendarWhatsAppNotificationService NotificationService { get; }
            public BookingRequestService Service { get; }
            public int ServicioId { get; }
            public int FuncionarioId { get; }

            public static async Task<Fixture> CreateAsync(bool whatsAppAddonActivo)
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

                context.Tenants.Add(new Tenant { Id = tenantProvider.TenantId, Nombre = "Negocio", Activo = true });

                var puesto = new Puesto { NombrePuesto = "Barbería", Detalle = "-", Activo = true };
                context.Puestos.Add(puesto);
                await context.SaveChangesAsync();

                var funcionario = new Funcionario
                {
                    Nombre = "Andrea",
                    IdPuesto = puesto.IdPuesto,
                    ColorCalendario = "#123456",
                    PorcentajeGanancia = 40m,
                    PorcentajeProducto = 10m,
                    FechaIngreso = new DateTime(2026, 8, 1),
                    Activo = true
                };
                context.Funcionarios.Add(funcionario);

                var servicio = new Servicio { Nombre = "Corte", Precio = 8000m, DuracionMinutos = 30, Activo = true };
                context.Servicios.Add(servicio);
                await context.SaveChangesAsync();

                var featureService = new FakeTenantWhatsAppFeatureService
                {
                    IsEnabled = whatsAppAddonActivo,
                    HasAddon = whatsAppAddonActivo
                };
                var notificationService = new NoOpCalendarWhatsAppNotificationService();
                var reloj = new FixedBusinessDateTimeProvider(Ahora);

                var service = new BookingRequestService(
                    context,
                    ControllerTestSupport.CreateCalendarCommandService(context),
                    notificationService,
                    new SiempreDisponibleAvailabilityService(funcionario.IdFuncionario),
                    new SlugBookingSettingsService(),
                    reloj,
                    new HttpContextAccessor(),
                    featureService,
                    new RecordingBookingRejectionWhatsAppService(),
                    ControllerTestSupport.CreateClienteIdentityService(context),
                    NullLogger<BookingRequestService>.Instance);

                return new Fixture(
                    context,
                    connection,
                    tenantProvider,
                    featureService,
                    notificationService,
                    service,
                    servicio.Id,
                    funcionario.IdFuncionario);
            }

            /// <summary>El calendario se construye con la MISMA capability que usa Reservas.</summary>
            public ICalendarQueryService CreateCalendarQueryService() =>
                ControllerTestSupport.CreateCalendarQueryService(
                    Context,
                    cancellationNotificationService: null,
                    whatsAppFeatureService: _featureService);

            public async Task<BookingRequest> SeedSolicitudPendienteAsync(
                bool aceptaWhatsApp = true,
                string nombreCliente = "Cliente Online")
            {
                var solicitud = new BookingRequest
                {
                    ServicioId = ServicioId,
                    FuncionarioId = FuncionarioId,
                    FuncionarioAsignadoId = FuncionarioId,
                    NombreCliente = nombreCliente,
                    TelefonoCliente = "88889999",
                    FechaHoraInicioSolicitada = new DateTime(2026, 8, 28, 10, 0, 0),
                    FechaHoraFinCalculada = new DateTime(2026, 8, 28, 10, 30, 0),
                    DuracionMinutos = 30,
                    Estado = BookingRequestStates.Pending,
                    AceptaWhatsApp = aceptaWhatsApp,
                    CreatedAtUtc = DateTime.UtcNow
                };

                Context.BookingRequests.Add(solicitud);
                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
                return solicitud;
            }

            public async Task<Cita> SeedCitaSinConsentimientoAsync()
            {
                var cita = new Cita
                {
                    NombreCliente = "Cliente Online",
                    TelefonoCliente = "88889999",
                    FechaHoraCita = new DateTime(2026, 8, 28, 10, 0, 0),
                    Tipo = "CITA",
                    DuracionMinutos = 30,
                    FuncionarioId = FuncionarioId,
                    ServicioId = ServicioId,
                    WhatsAppConsentAtCreation = false
                };

                Context.Citas.Add(cita);
                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
                return cita;
            }

            public void Dispose()
            {
                Context.Dispose();
                _connection.Dispose();
            }
        }

        private sealed class SiempreDisponibleAvailabilityService : IBookingAvailabilityService
        {
            private readonly int _funcionarioId;

            public SiempreDisponibleAvailabilityService(int funcionarioId) => _funcionarioId = funcionarioId;

            public Task<SlotResolution> ResolveSlotAsync(
                int servicioId,
                DateTime inicio,
                int? funcionarioId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new SlotResolution
                {
                    Disponible = true,
                    FuncionarioId = funcionarioId ?? _funcionarioId,
                    DuracionMinutos = 30
                });

            public Task<IReadOnlyList<string>> GetAvailableSlotsAsync(
                int servicioId,
                DateOnly fecha,
                int? funcionarioId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<string>>([]);

            public Task<IReadOnlyList<AvailableSlotSuggestion>> GetNextAvailableSlotsAsync(
                int servicioId,
                DateOnly fromDate,
                int? funcionarioId,
                int maxSuggestions = 5,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<AvailableSlotSuggestion>>([]);
        }

        private sealed class SlugBookingSettingsService : IBookingSettingsService
        {
            public Task<BookingSettingsViewModel> BuildSettingsViewModelAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new BookingSettingsViewModel());

            public Task SaveSettingsAsync(BookingSettingsViewModel input, string? userId, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task<PublicBookingTenantContext?> ResolvePublicBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
                Task.FromResult<PublicBookingTenantContext?>(null);

            public Task<string?> GetCurrentSlugAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<string?>("negocio");
        }
    }
}
