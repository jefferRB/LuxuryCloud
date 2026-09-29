using System.Text.Json;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Notifications;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.Notifications;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class BookingDuplicatePreventionTests
    {
        // ── Idempotencia del envío público por token ──

        [Fact]
        public async Task Submit_SameSubmissionToken_CreatesSingleBookingRequest()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await EnsureTenantAsync(context, tenantProvider.TenantId);
            await SeedSettingsAsync(context);
            var servicio = await SeedServicioAsync(context, "Corte y barba", 60);
            await SeedFuncionarioAsync(context, "Deyner");

            var service = BuildPublicService(context);
            var ctx = BuildContext(tenantProvider.TenantId);
            var input = new PublicBookingRequestInput
            {
                ServicioId = servicio.Id,
                Fecha = "2026-05-27",
                Hora = "10:00",
                Nombre = "Juan Perez",
                Telefono = "88887777",
                AceptaWhatsApp = true,
                SubmissionToken = "tok-abc-123"
            };

            var r1 = await service.SubmitAsync(ctx, input, default);
            var r2 = await service.SubmitAsync(ctx, input, default); // mismo token: idempotente

            Assert.True(r1.Success);
            Assert.True(r2.Success);
            Assert.Equal(1, await context.BookingRequests.CountAsync());
        }

        [Fact]
        public async Task BookingRequest_DuplicateTokenAtDbLevel_IsRejectedByUniqueIndex()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await EnsureTenantAsync(context, tenantProvider.TenantId);
            var servicio = await SeedServicioAsync(context, "Corte", 60);

            context.BookingRequests.Add(NewPendingRequest(servicio.Id, "88880000", token: "dup-token"));
            await context.SaveChangesAsync();

            context.BookingRequests.Add(NewPendingRequest(servicio.Id, "88881111", token: "dup-token"));

            // El índice único filtrado (TenantId + PublicSubmissionToken) debe impedir el duplicado.
            await Assert.ThrowsAnyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        // ── Idempotencia de la confirmación interna ──

        [Fact]
        public async Task Confirm_CalledTwice_CreatesSingleCitaAndSingleWhatsApp()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await EnsureTenantAsync(context, tenantProvider.TenantId);
            var servicio = await SeedServicioAsync(context, "Corte", 60);
            var funcionario = await SeedFuncionarioAsync(context, "Deyner");

            var solicitud = NewPendingRequest(servicio.Id, "88882222", token: null);
            context.BookingRequests.Add(solicitud);
            await context.SaveChangesAsync();

            var calendar = new CountingCalendarCommandService(context);
            var whatsApp = new CountingWhatsAppNotificationService();
            var service = new BookingRequestService(
                context,
                calendar,
                whatsApp,
                new AlwaysAvailableAvailabilityService(funcionario.IdFuncionario),
                new NoOpBookingSettingsService(),
                new FixedBusinessDateTimeProvider(),
                new HttpContextAccessor(),
                new FakeTenantWhatsAppFeatureService { IsEnabled = true },
                new RecordingBookingRejectionWhatsAppService(),
                ControllerTestSupport.CreateClienteIdentityService(context),
                NullLogger<BookingRequestService>.Instance);

            var r1 = await service.ConfirmAsync(solicitud.Id, null, "admin");
            var r2 = await service.ConfirmAsync(solicitud.Id, null, "admin"); // doble click / reintento

            Assert.True(r1.Success);
            Assert.NotNull(r1.CitaId);
            Assert.True(r2.Success);
            Assert.Contains("ya fue confirmada", r2.Message);

            Assert.Equal(1, calendar.CreateCount);        // una sola cita
            Assert.Equal(1, whatsApp.SendConfirmationCount); // un solo WhatsApp
            Assert.Equal(1, await context.BookingRequests.CountAsync(r => r.Estado == BookingRequestStates.Confirmed));
        }

        // ── helpers de armado ──

        private static PublicBookingService BuildPublicService(ApplicationDbContext context)
        {
            var catalog = new BookingCatalogService(context);
            var availability = ControllerTestSupport.CreateBookingAvailabilityService(context, new FixedBusinessDateTimeProvider(), catalog);
            return new PublicBookingService(
                context,
                new NoOpBookingSettingsService(),
                availability,
                catalog,
                new FixedBusinessDateTimeProvider(),
                new FakeTenantWhatsAppFeatureService { IsEnabled = true },
                new NoOpNotificationService(),
                new HttpContextAccessor(),
                ControllerTestSupport.CreateClienteIdentityService(context),
                NullLogger<PublicBookingService>.Instance);
        }

        private static PublicBookingTenantContext BuildContext(Guid tenantId) => new()
        {
            TenantId = tenantId,
            NombreNegocio = "Test",
            Slug = "test",
            PermiteElegirFuncionario = true,
            PermiteCualquierFuncionario = true,
            MostrarFotosFuncionarios = true,
            MinAdvanceMinutes = 0,
            MaxDaysAhead = 30
        };

        private static BookingRequest NewPendingRequest(int servicioId, string telefono, string? token) => new()
        {
            ServicioId = servicioId,
            FuncionarioId = null,
            NombreCliente = "Cliente",
            TelefonoCliente = telefono,
            FechaHoraInicioSolicitada = new DateTime(2026, 5, 27, 10, 0, 0),
            FechaHoraFinCalculada = new DateTime(2026, 5, 27, 11, 0, 0),
            DuracionMinutos = 60,
            Estado = BookingRequestStates.Pending,
            Origen = BookingRequestOrigins.PublicLink,
            AceptaWhatsApp = true,
            PublicSubmissionToken = token,
            CreatedAtUtc = DateTime.UtcNow
        };

        private static async Task SeedSettingsAsync(ApplicationDbContext context)
        {
            context.TenantBookingSettings.Add(new TenantBookingSettings
            {
                PublicBookingEnabled = true,
                PublicBookingSlug = $"slug-{Guid.NewGuid():N}"[..18],
                OpenTime = new TimeOnly(8, 0),
                CloseTime = new TimeOnly(18, 0),
                SlotIntervalMinutes = 30,
                PublicBookingMinAdvanceMinutes = 0,
                PublicBookingMaxDaysAhead = 30,
                WorkingDaysMask = 0b111_1111
            });
            await context.SaveChangesAsync();
        }

        private static async Task<Servicio> SeedServicioAsync(ApplicationDbContext context, string nombre, int duracion)
        {
            var servicio = new Servicio { Nombre = nombre, Precio = 6000m, DuracionMinutos = duracion, Activo = true };
            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();
            return servicio;
        }

        private static async Task<Funcionario> SeedFuncionarioAsync(ApplicationDbContext context, string nombre)
        {
            var puesto = new Puesto { NombrePuesto = $"Puesto {Guid.NewGuid():N}", Detalle = "R", Activo = true };
            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            var funcionario = new Funcionario
            {
                Nombre = nombre,
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#123456",
                PorcentajeGanancia = 40m,
                PorcentajeProducto = 10m,
                FechaIngreso = new DateTime(2026, 1, 1),
                Activo = true
            };
            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario;
        }

        private static async Task EnsureTenantAsync(ApplicationDbContext context, Guid tenantId)
        {
            if (!await context.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId))
            {
                context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Tenant Test", Activo = true });
                await context.SaveChangesAsync();
            }
        }

        // ── fakes ──

        private sealed class CountingCalendarCommandService : ICalendarCommandService
        {
            private readonly ApplicationDbContext _context;
            public int CreateCount { get; private set; }

            public CountingCalendarCommandService(ApplicationDbContext context) => _context = context;

            public async Task<CalendarAppointmentResponse> CreateAsync(CalendarUpsertRequest request, CancellationToken cancellationToken = default)
            {
                CreateCount++;
                // Persiste una cita real para que la FK ConvertedCitaId sea válida (como en producción).
                var cita = new Cita
                {
                    FuncionarioId = request.FuncionarioId,
                    FechaHoraCita = request.FechaHoraCita,
                    Tipo = "CITA",
                    DuracionMinutos = 60,
                    NombreCliente = request.NombreCliente ?? "Cliente"
                };
                _context.Citas.Add(cita);
                await _context.SaveChangesAsync(cancellationToken);
                return new CalendarAppointmentResponse { Id = cita.Id };
            }

            public Task<CalendarAppointmentResponse> UpdateAsync(int id, CalendarUpsertRequest request, CancellationToken cancellationToken = default) =>
                throw new NotImplementedException();
            public Task MoveAsync(int id, CalendarMoveRequest request, CancellationToken cancellationToken = default) =>
                throw new NotImplementedException();
            public Task ResizeDurationAsync(int id, int duracionMinutos, CancellationToken cancellationToken = default) =>
                throw new NotImplementedException();
            public Task DeleteAsync(int id, string? motivoCancelacion = null, CancellationToken cancellationToken = default) =>
                throw new NotImplementedException();
            public Task ProcessVisitsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private sealed class CountingWhatsAppNotificationService : ICalendarWhatsAppNotificationService
        {
            public int SendConfirmationCount { get; private set; }

            public Task<WhatsAppConfirmationSendResult> SendConfirmationNowAsync(int citaId, string source, CancellationToken cancellationToken = default)
            {
                SendConfirmationCount++;
                return Task.FromResult(new WhatsAppConfirmationSendResult(WhatsAppConfirmationOutcome.Sent, "ok"));
            }

            public Task SendAppointmentConfirmationAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task SendAppointmentReminderAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task QueueAppointmentConfirmationAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task QueueAppointmentReminderAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task QueueImmediateReminderOnCreateAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task ProcessInboundReplyAsync(JsonElement payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task ProcessStatusUpdateAsync(JsonElement payload, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task ScheduleDueRemindersAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task GenerateDailyBatchAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task ProcessPendingNotificationsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RescheduleConfirmationIfPendingAsync(int citaId, DateTime newFechaHoraCita, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task CancelPendingNotificationsAsync(int citaId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private sealed class AlwaysAvailableAvailabilityService : IBookingAvailabilityService
        {
            private readonly int _funcionarioId;
            public AlwaysAvailableAvailabilityService(int funcionarioId) => _funcionarioId = funcionarioId;

            public Task<SlotResolution> ResolveSlotAsync(int servicioId, DateTime inicio, int? funcionarioId, CancellationToken cancellationToken = default) =>
                Task.FromResult(new SlotResolution { Disponible = true, FuncionarioId = funcionarioId ?? _funcionarioId, DuracionMinutos = 60 });

            public Task<IReadOnlyList<string>> GetAvailableSlotsAsync(int servicioId, DateOnly fecha, int? funcionarioId, CancellationToken cancellationToken = default) =>
                Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());

            public Task<IReadOnlyList<AvailableSlotSuggestion>> GetNextAvailableSlotsAsync(int servicioId, DateOnly fromDate, int? funcionarioId, int maxSuggestions = 5, CancellationToken cancellationToken = default) =>
                Task.FromResult((IReadOnlyList<AvailableSlotSuggestion>)Array.Empty<AvailableSlotSuggestion>());
        }

        private sealed class NoOpBookingSettingsService : IBookingSettingsService
        {
            public Task<BookingSettingsViewModel> BuildSettingsViewModelAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new BookingSettingsViewModel());
            public Task SaveSettingsAsync(BookingSettingsViewModel input, string? userId, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
            public Task<PublicBookingTenantContext?> ResolvePublicBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
                Task.FromResult<PublicBookingTenantContext?>(null);
            public Task<string?> GetCurrentSlugAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<string?>(null);
        }

        private sealed class NoOpNotificationService : INotificationService
        {
            public Task<NotificationSummary> GetSummaryAsync(int limit = 15, CancellationToken cancellationToken = default) =>
                Task.FromResult(new NotificationSummary());
            public Task<int> MarkAllAsReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
            public Task<bool> MarkAsReadAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(true);
            public Task CreateBookingRequestReceivedAsync(BookingRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task CreateAppointmentCancelledViaWhatsAppAsync(Cita cita, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
