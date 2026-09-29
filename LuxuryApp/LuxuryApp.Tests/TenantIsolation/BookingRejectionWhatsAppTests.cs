using System.Globalization;
using System.Net;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.PublicPages;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.WhatsApp;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    /// <summary>
    /// Rechazar una solicitud de reserva online avisa al cliente con el MISMO template aprobado que
    /// la cancelación de una cita. El rechazo es la operación principal: WhatsApp nunca lo revierte.
    /// </summary>
    public class BookingRejectionWhatsAppTests
    {
        private const string MotivoPorDefecto = "El colaborador no está disponible.";

        // ── 1. Camino feliz ───────────────────────────────────────────────────────────────

        [Fact]
        public async Task ConComplementoYConsentimiento_RechazaYEnviaElTemplateUnaSolaVez()
        {
            using var fixture = await Fixture.CreateAsync();
            var solicitud = await fixture.SeedSolicitudPendienteAsync();

            var result = await fixture.Service.RejectAsync(solicitud.Id, null, "user-1");

            Assert.True(result.Success);

            var actualizada = await fixture.Context.BookingRequests.AsNoTracking().SingleAsync();
            Assert.Equal(BookingRequestStates.Rejected, actualizada.Estado);

            Assert.Equal(1, fixture.MetaClient.CancellationSendCount);

            var log = await fixture.Context.WhatsAppMessageLogs.AsNoTracking().SingleAsync();
            Assert.Equal(WhatsAppMessageDirections.Outbound, log.Direction);
            Assert.Equal(WhatsAppNotificationTypes.Cancellation, log.NotificationType);
            Assert.Equal("luxurycloud_cancelacion_cita", log.TemplateName);
            Assert.Equal(WhatsAppMessageStatuses.Sent, log.Status);
            // Nunca hubo cita: la bitácora queda sin CitaId.
            Assert.Null(log.CitaId);
        }

        // ── 2. Parámetros exactos del template ────────────────────────────────────────────

        [Fact]
        public async Task ParametrosDelTemplate_ViajanEnElOrdenExacto()
        {
            using var fixture = await Fixture.CreateAsync();
            var solicitud = await fixture.SeedSolicitudPendienteAsync();

            await fixture.Service.RejectAsync(solicitud.Id, null, "user-1");

            var ordered = Assert.Single(fixture.MetaClient.CancellationParameters).ToOrderedBodyParameters();

            Assert.Equal(8, ordered.Count);
            Assert.Equal("Cliente Online", ordered[0]);              // {{1}} cliente
            Assert.Equal(Fixture.BusinessName, ordered[1]);          // {{2}} negocio
            Assert.Equal("Corte de cabello", ordered[2]);            // {{3}} servicio solicitado
            Assert.Equal("28/08/2026", ordered[3]);                  // {{4}} fecha solicitada
            Assert.Equal(
                Fixture.FechaSolicitada.ToString("hh:mm tt", CultureInfo.GetCultureInfo("es-CR")),
                ordered[4]);                                         // {{5}} hora solicitada
            Assert.Equal(MotivoPorDefecto, ordered[5]);              // {{6}} motivo del rechazo
            Assert.Equal(Fixture.BusinessName, ordered[6]);          // {{7}} negocio otra vez
            Assert.Equal(Fixture.BusinessPublicPhone, ordered[7]);   // {{8}} teléfono público del negocio

            Assert.Equal(ordered[1], ordered[6]);
        }

        // ── 3 y 4. Motivo ─────────────────────────────────────────────────────────────────

        [Fact]
        public async Task MotivoPersonalizado_LlegaComoVariableSeis()
        {
            using var fixture = await Fixture.CreateAsync();
            var solicitud = await fixture.SeedSolicitudPendienteAsync();

            await fixture.Service.RejectAsync(
                solicitud.Id,
                "No contamos con disponibilidad para ese horario.",
                "user-1");

            var parameters = Assert.Single(fixture.MetaClient.CancellationParameters);
            Assert.Equal("No contamos con disponibilidad para ese horario.", parameters.CancellationReason);

            var actualizada = await fixture.Context.BookingRequests.AsNoTracking().SingleAsync();
            Assert.Equal("No contamos con disponibilidad para ese horario.", actualizada.RejectedReason);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\n  ")]
        public async Task MotivoVacioManipuladoEnElRequest_CaeAlValorPorDefecto(string? motivo)
        {
            // La garantía es del backend: no se confía en el required del formulario.
            using var fixture = await Fixture.CreateAsync();
            var solicitud = await fixture.SeedSolicitudPendienteAsync();

            await fixture.Service.RejectAsync(solicitud.Id, motivo, "user-1");

            var parameters = Assert.Single(fixture.MetaClient.CancellationParameters);
            Assert.Equal(MotivoPorDefecto, parameters.CancellationReason);

            var actualizada = await fixture.Context.BookingRequests.AsNoTracking().SingleAsync();
            Assert.Equal(MotivoPorDefecto, actualizada.RejectedReason);
        }

        [Fact]
        public async Task MotivoLarguisimo_SeRecortaAlLimiteExistente()
        {
            using var fixture = await Fixture.CreateAsync();
            var solicitud = await fixture.SeedSolicitudPendienteAsync();

            await fixture.Service.RejectAsync(solicitud.Id, new string('x', 500), "user-1");

            var actualizada = await fixture.Context.BookingRequests.AsNoTracking().SingleAsync();
            Assert.Equal(BookingRejectionDefaults.MotivoMaxLength, actualizada.RejectedReason!.Length);
        }

        // ── 5 y 6. Complemento y consentimiento ───────────────────────────────────────────

        [Fact]
        public async Task SinComplemento_RechazaEnSilencioSinTocarMeta()
        {
            using var fixture = await Fixture.CreateAsync(conComplemento: false);
            var solicitud = await fixture.SeedSolicitudPendienteAsync();

            var result = await fixture.Service.RejectAsync(solicitud.Id, null, "user-1");

            Assert.True(result.Success);
            Assert.DoesNotContain("WhatsApp", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, fixture.MetaClient.CancellationSendCount);

            var actualizada = await fixture.Context.BookingRequests.AsNoTracking().SingleAsync();
            Assert.Equal(BookingRequestStates.Rejected, actualizada.Estado);

            // Silencio total: ni siquiera se registra una omisión que el negocio pueda leer.
            Assert.Empty(await fixture.Context.WhatsAppMessageLogs.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task SinConsentimiento_RechazaYRegistraConsentMissingSinTocarMeta()
        {
            using var fixture = await Fixture.CreateAsync();
            var solicitud = await fixture.SeedSolicitudPendienteAsync(aceptaWhatsApp: false);

            var result = await fixture.Service.RejectAsync(solicitud.Id, null, "user-1");

            Assert.True(result.Success);
            Assert.Equal(0, fixture.MetaClient.CancellationSendCount);

            var actualizada = await fixture.Context.BookingRequests.AsNoTracking().SingleAsync();
            Assert.Equal(BookingRequestStates.Rejected, actualizada.Estado);

            var log = await fixture.Context.WhatsAppMessageLogs.AsNoTracking().SingleAsync();
            Assert.Equal(WhatsAppMessageStatuses.SkippedConsentMissing, log.Status);
            Assert.Equal(WhatsAppErrorCodes.ConsentMissing, log.ErrorCode);
        }

        [Fact]
        public async Task SinConsentimiento_ElMotivoSemanticoEsConsentMissing()
        {
            using var fixture = await Fixture.CreateAsync();
            var solicitud = await fixture.SeedSolicitudPendienteAsync(aceptaWhatsApp: false);
            await fixture.Service.RejectAsync(solicitud.Id, null, "user-1");

            // Se vuelve a preguntar al servicio de aviso: el rechazo ya ocurrió y la razón es estable.
            var razon = await fixture.RejectionService.NotifyRejectionAsync(solicitud.Id);

            Assert.Equal(WhatsAppNotificationReason.ConsentMissing, razon);
        }

        [Fact]
        public async Task SinComplemento_ElMotivoSemanticoEsAddonInactiveYEsSilencioso()
        {
            using var fixture = await Fixture.CreateAsync(conComplemento: false);
            var solicitud = await fixture.SeedSolicitudPendienteAsync();
            await fixture.Service.RejectAsync(solicitud.Id, null, "user-1");

            var razon = await fixture.RejectionService.NotifyRejectionAsync(solicitud.Id);

            Assert.Equal(WhatsAppNotificationReason.AddonInactive, razon);
            Assert.True(razon.IsSilent());
        }

        // ── 7. Fallo de Meta ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task SiMetaFalla_LaSolicitudSigueRechazadaYElFalloQuedaRegistrado()
        {
            using var fixture = await Fixture.CreateAsync();
            var solicitud = await fixture.SeedSolicitudPendienteAsync();
            fixture.MetaClient.NextSendResult = MetaWhatsAppSendResult.Failed(
                "131047",
                "Re-engagement message.",
                HttpStatusCode.BadRequest);

            var result = await fixture.Service.RejectAsync(solicitud.Id, null, "user-1");

            Assert.True(result.Success);

            var actualizada = await fixture.Context.BookingRequests.AsNoTracking().SingleAsync();
            Assert.Equal(BookingRequestStates.Rejected, actualizada.Estado);

            var log = await fixture.Context.WhatsAppMessageLogs.AsNoTracking().SingleAsync();
            Assert.Equal(WhatsAppMessageStatuses.Failed, log.Status);
            Assert.Equal("131047", log.ErrorCode);
        }

        // ── 8. Idempotencia ───────────────────────────────────────────────────────────────

        [Fact]
        public async Task RechazarDosVeces_NoMandaUnSegundoWhatsApp()
        {
            using var fixture = await Fixture.CreateAsync();
            var solicitud = await fixture.SeedSolicitudPendienteAsync();

            var primero = await fixture.Service.RejectAsync(solicitud.Id, null, "user-1");
            var segundo = await fixture.Service.RejectAsync(solicitud.Id, null, "user-1");

            Assert.True(primero.Success);
            Assert.False(segundo.Success);

            Assert.Equal(1, fixture.MetaClient.CancellationSendCount);
            Assert.Single(await fixture.Context.WhatsAppMessageLogs.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task SolicitudYaConfirmada_NoSeRechazaNiAvisa()
        {
            using var fixture = await Fixture.CreateAsync();
            var solicitud = await fixture.SeedSolicitudPendienteAsync(estado: BookingRequestStates.Confirmed);

            var result = await fixture.Service.RejectAsync(solicitud.Id, null, "user-1");

            Assert.False(result.Success);
            Assert.Equal(0, fixture.MetaClient.CancellationSendCount);
        }

        // ── 9. Aislamiento multi-tenant ───────────────────────────────────────────────────

        [Fact]
        public async Task SolicitudDeOtroTenant_NoSeRechazaNiAvisa()
        {
            using var fixture = await Fixture.CreateAsync();
            var solicitud = await fixture.SeedSolicitudPendienteAsync();

            fixture.TenantProvider.TenantId = Guid.NewGuid();
            fixture.Context.ChangeTracker.Clear();

            var result = await fixture.Service.RejectAsync(solicitud.Id, null, "intruso");

            Assert.False(result.Success);
            Assert.Equal(0, fixture.MetaClient.CancellationSendCount);
        }

        [Fact]
        public async Task ElNombreYTelefonoDelTemplateSalenDelTenantDueño()
        {
            // Otro negocio con nombre y teléfono distintos no puede filtrarse al mensaje.
            using var fixture = await Fixture.CreateAsync();
            await fixture.SeedOtroTenantConPaginaPublicaAsync();
            var solicitud = await fixture.SeedSolicitudPendienteAsync();

            await fixture.Service.RejectAsync(solicitud.Id, null, "user-1");

            var parameters = Assert.Single(fixture.MetaClient.CancellationParameters);
            Assert.Equal(Fixture.BusinessName, parameters.BusinessName);
            Assert.Equal(Fixture.BusinessPublicPhone, parameters.BusinessPhone);
            Assert.DoesNotContain("Ajeno", parameters.BusinessName, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual("9999-9999", parameters.BusinessPhone);
        }

        // ── 10. Copy del modal ────────────────────────────────────────────────────────────

        [Fact]
        public void ElModalDeRechazo_NoDiceOpcionalNiNiegaElAviso()
        {
            var vista = LeerVista("Views/Reservas/Index.cshtml");

            Assert.DoesNotContain("Motivo (opcional)", vista, StringComparison.Ordinal);
            Assert.DoesNotContain("no se envía aviso de cancelación al cliente", vista, StringComparison.Ordinal);
            Assert.DoesNotContain("todavía no es una cita", vista, StringComparison.Ordinal);
        }

        [Fact]
        public void ElModalDeRechazo_PrecargaElMotivoYAdaptaElCopyAlComplemento()
        {
            var vista = LeerVista("Views/Reservas/Index.cshtml");

            Assert.Contains("BookingRejectionDefaults.MotivoPorDefecto", vista, StringComparison.Ordinal);
            Assert.Contains(
                "El cliente recibirá el aviso por WhatsApp si autorizó las notificaciones.",
                vista,
                StringComparison.Ordinal);
            Assert.Contains("Quedará registrado para tu control.", vista, StringComparison.Ordinal);
            Assert.Contains("Model.WhatsAppActivo", vista, StringComparison.Ordinal);
        }

        [Fact]
        public void ElValorPorDefectoEsElTextoPedido()
        {
            Assert.Equal(MotivoPorDefecto, BookingRejectionDefaults.MotivoPorDefecto);
            Assert.Equal(MotivoPorDefecto, BookingRejectionDefaults.NormalizarMotivo(null));
            Assert.Equal(MotivoPorDefecto, BookingRejectionDefaults.NormalizarMotivo("  "));
            Assert.Equal("Otro motivo.", BookingRejectionDefaults.NormalizarMotivo("  Otro motivo.  "));
        }

        private static string LeerVista(string rutaRelativa)
        {
            var directorio = new DirectoryInfo(AppContext.BaseDirectory);
            while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, rutaRelativa)))
            {
                directorio = directorio.Parent;
            }

            Assert.NotNull(directorio);
            return File.ReadAllText(Path.Combine(directorio!.FullName, rutaRelativa));
        }

        private sealed class Fixture : IDisposable
        {
            public const string BusinessName = "Barberia Prueba";
            public const string BusinessPublicPhone = "2222-3333";

            public static readonly DateTime FechaSolicitada = new(2026, 8, 28, 10, 0, 0);

            private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

            private Fixture(
                Guid tenantId,
                TestTenantProvider tenantProvider,
                ApplicationDbContext context,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                CapturingMetaWhatsAppClient metaClient,
                IBookingRejectionWhatsAppService rejectionService,
                BookingRequestService service,
                int servicioId,
                int funcionarioId)
            {
                TenantId = tenantId;
                TenantProvider = tenantProvider;
                Context = context;
                _connection = connection;
                MetaClient = metaClient;
                RejectionService = rejectionService;
                Service = service;
                ServicioId = servicioId;
                FuncionarioId = funcionarioId;
            }

            private static DateTime AhoraLocal => new(2026, 8, 27, 14, 0, 0);

            private static DateTime AhoraUtc =>
                new DateTimeOffset(AhoraLocal, TimeSpan.FromHours(-6)).UtcDateTime;

            public Guid TenantId { get; }
            public TestTenantProvider TenantProvider { get; }
            public ApplicationDbContext Context { get; }
            public CapturingMetaWhatsAppClient MetaClient { get; }
            public IBookingRejectionWhatsAppService RejectionService { get; }
            public BookingRequestService Service { get; }
            public int ServicioId { get; }
            public int FuncionarioId { get; }

            public static async Task<Fixture> CreateAsync(bool conComplemento = true)
            {
                var tenantId = Guid.NewGuid();
                var tenantProvider = new TestTenantProvider { TenantId = tenantId };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

                var options = new StaticOptionsMonitor<MetaWhatsAppOptions>(new MetaWhatsAppOptions { Enabled = true });
                var cache = new MemoryCache(new MemoryCacheOptions());
                var accessCache = new TenantCommercialAccessCache(cache);
                var reloj = new FixedBusinessDateTimeProvider(AhoraLocal);
                var subscriptionService = new SuscripcionService(
                    context,
                    cache,
                    accessCache,
                    reloj,
                    Options.Create(new TilopayRepeatOptions()),
                    NullLogger<SuscripcionService>.Instance);
                var commercialAccessResolver = new TenantCommercialAccessResolver(
                    context,
                    cache,
                    accessCache,
                    subscriptionService,
                    reloj);
                var settings = new TenantWhatsAppSettingsService(
                    context,
                    tenantProvider,
                    options,
                    subscriptionService,
                    reloj,
                    commercialAccessResolver,
                    NullLogger<TenantWhatsAppSettingsService>.Instance);
                var metaClient = new CapturingMetaWhatsAppClient();

                var notifier = new WhatsAppCancellationNotifier(
                    context,
                    metaClient,
                    reloj,
                    new TenantDisplayNameService(context, tenantProvider, new HttpContextAccessor()),
                    NullLogger<WhatsAppCancellationNotifier>.Instance);

                var rejectionService = new BookingRejectionWhatsAppService(
                    context,
                    metaClient,
                    options,
                    reloj,
                    settings,
                    notifier,
                    NullLogger<BookingRejectionWhatsAppService>.Instance);

                context.Tenants.Add(new Tenant { Id = tenantId, Nombre = BusinessName, Activo = true });

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
                    FechaInicio = AhoraUtc.AddDays(-3),
                    FechaFin = AhoraUtc.AddDays(27),
                    FechaProximoCobroUtc = AhoraUtc.AddDays(27),
                    FechaUltimaActualizacionUtc = AhoraUtc
                });

                if (conComplemento)
                {
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
                        FechaInicio = AhoraUtc.AddDays(-1),
                        FechaFin = AhoraUtc.AddDays(29),
                        CreatedAtUtc = AhoraUtc,
                        UpdatedAtUtc = AhoraUtc
                    });
                    context.TenantWhatsAppSettings.Add(new TenantWhatsAppSettings
                    {
                        TenantId = tenantId,
                        IsEnabled = true,
                        SendConfirmationOnCreate = true,
                        SendReminderThreeHoursBefore = true,
                        DailyMessageLimit = 30,
                        TimeZoneId = TenantWhatsAppSettings.DefaultTimeZoneId,
                        CreatedAtUtc = AhoraUtc,
                        UpdatedAtUtc = AhoraUtc
                    });
                }

                context.TenantPublicPages.Add(new TenantPublicPage
                {
                    TenantId = tenantId,
                    IsPublished = true,
                    Phone = BusinessPublicPhone
                });

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

                var servicio = new Servicio
                {
                    Nombre = "Corte de cabello",
                    Precio = 8000m,
                    DuracionMinutos = 30,
                    Activo = true
                };
                context.Servicios.Add(servicio);
                await context.SaveChangesAsync();

                var service = new BookingRequestService(
                    context,
                    ControllerTestSupport.CreateCalendarCommandService(context),
                    new NoOpCalendarWhatsAppNotificationService(),
                    new SinDisponibilidadService(),
                    new SlugSettingsService(),
                    reloj,
                    new HttpContextAccessor(),
                    new FakeTenantWhatsAppFeatureService { IsEnabled = conComplemento, HasAddon = conComplemento },
                    rejectionService,
                    ControllerTestSupport.CreateClienteIdentityService(context),
                    NullLogger<BookingRequestService>.Instance);

                return new Fixture(
                    tenantId,
                    tenantProvider,
                    context,
                    connection,
                    metaClient,
                    rejectionService,
                    service,
                    servicio.Id,
                    funcionario.IdFuncionario);
            }

            public async Task<BookingRequest> SeedSolicitudPendienteAsync(
                bool aceptaWhatsApp = true,
                string? estado = null)
            {
                var solicitud = new BookingRequest
                {
                    ServicioId = ServicioId,
                    FuncionarioId = FuncionarioId,
                    FuncionarioAsignadoId = FuncionarioId,
                    NombreCliente = "Cliente Online",
                    TelefonoCliente = "88889999",
                    FechaHoraInicioSolicitada = FechaSolicitada,
                    FechaHoraFinCalculada = FechaSolicitada.AddMinutes(30),
                    DuracionMinutos = 30,
                    Estado = estado ?? BookingRequestStates.Pending,
                    AceptaWhatsApp = aceptaWhatsApp,
                    CreatedAtUtc = AhoraUtc
                };

                Context.BookingRequests.Add(solicitud);
                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
                return solicitud;
            }

            /// <summary>Otro negocio con página pública propia, para probar el aislamiento.</summary>
            public async Task SeedOtroTenantConPaginaPublicaAsync()
            {
                var otroTenantId = Guid.NewGuid();
                Context.Tenants.Add(new Tenant { Id = otroTenantId, Nombre = "Negocio Ajeno", Activo = true });
                await Context.SaveChangesAsync();

                TenantProvider.TenantId = otroTenantId;
                Context.TenantPublicPages.Add(new TenantPublicPage
                {
                    TenantId = otroTenantId,
                    IsPublished = true,
                    Phone = "9999-9999"
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

        /// <summary>Rechazar no toca disponibilidad; basta con un stub que nunca se use.</summary>
        private sealed class SinDisponibilidadService : IBookingAvailabilityService
        {
            public Task<IReadOnlyList<string>> GetAvailableSlotsAsync(
                int servicioId,
                DateOnly fecha,
                int? funcionarioId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<string>>([]);

            public Task<SlotResolution> ResolveSlotAsync(
                int servicioId,
                DateTime inicio,
                int? funcionarioId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(SlotResolution.NoDisponible("No aplica en estos tests."));

            public Task<IReadOnlyList<AvailableSlotSuggestion>> GetNextAvailableSlotsAsync(
                int servicioId,
                DateOnly fromDate,
                int? funcionarioId,
                int maxSuggestions = 5,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<AvailableSlotSuggestion>>([]);
        }

        private sealed class SlugSettingsService : IBookingSettingsService
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
                    $"rejection-{CancellationSendCount}",
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
