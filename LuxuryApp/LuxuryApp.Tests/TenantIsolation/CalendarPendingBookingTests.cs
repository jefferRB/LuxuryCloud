using System.Reflection;
using LuxuryApp.Controllers.Calendar;
using LuxuryApp.Models.Asociados;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    /// <summary>
    /// El calendario muestra las solicitudes de reserva PENDIENTES sin crear citas falsas, y sus
    /// acciones Confirmar/Rechazar terminan en el MISMO servicio de aplicación que la pantalla de
    /// "Solicitudes de reserva".
    /// </summary>
    public class CalendarPendingBookingTests
    {
        private static readonly DateOnly Fecha = new(2026, 5, 27);

        // ── 15. El read model del calendario sólo devuelve pendientes del tenant correcto ──

        [Fact]
        public async Task PendingForCalendar_ReturnsOnlyCurrentTenantPendings()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantA);
            var servicioA = await SeedServicioAsync(context, "Corte A", 60);
            var funcionarioA = await SeedFuncionarioAsync(context, "Jamie A");
            await SeedPendingAsync(context, servicioA.Id, funcionarioA.IdFuncionario, At(12, 0), 60);

            tenantProvider.TenantId = tenantB;
            await EnsureTenantAsync(context, tenantB);
            var servicioB = await SeedServicioAsync(context, "Corte B", 60);
            var funcionarioB = await SeedFuncionarioAsync(context, "Drew B");
            await SeedPendingAsync(context, servicioB.Id, funcionarioB.IdFuncionario, At(15, 0), 60);

            var service = BuildRequestService(context);

            var deB = await service.GetPendingForCalendarAsync(Fecha);
            var soloB = Assert.Single(deB);
            Assert.Equal(funcionarioB.IdFuncionario, soloB.FuncionarioId);
            Assert.Equal(At(15, 0), soloB.FechaHoraInicio);

            tenantProvider.TenantId = tenantA;
            var deA = await service.GetPendingForCalendarAsync(Fecha);
            var soloA = Assert.Single(deA);
            Assert.Equal(funcionarioA.IdFuncionario, soloA.FuncionarioId);
            Assert.Equal("Jamie A", soloA.FuncionarioNombre);
            Assert.True(soloA.SolicitoCualquierFuncionario);
        }

        [Fact]
        public async Task PendingForCalendar_IgnoresProcessedRequestsAndOtherDays()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantProvider.TenantId);
            var servicio = await SeedServicioAsync(context, "Corte", 60);
            var funcionario = await SeedFuncionarioAsync(context, "Jamie");

            await SeedPendingAsync(context, servicio.Id, funcionario.IdFuncionario, At(12, 0), 60);
            await SeedPendingAsync(context, servicio.Id, funcionario.IdFuncionario, At(14, 0), 60,
                estado: BookingRequestStates.Rejected);
            await SeedPendingAsync(context, servicio.Id, funcionario.IdFuncionario, At(16, 0), 60,
                estado: BookingRequestStates.Confirmed);
            await SeedPendingAsync(context, servicio.Id, funcionario.IdFuncionario,
                Fecha.AddDays(1).ToDateTime(new TimeOnly(12, 0)), 60);

            var service = BuildRequestService(context);
            var pendientes = await service.GetPendingForCalendarAsync(Fecha);

            var unica = Assert.Single(pendientes);
            Assert.Equal(At(12, 0), unica.FechaHoraInicio);
        }

        // ── 16. Confirmar/Rechazar desde el calendario usan el mismo flujo de aplicación ──

        [Fact]
        public async Task ConfirmFromCalendar_DelegatesToTheSharedApplicationService()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var spy = new SpyBookingRequestService();
            var controller = CreateController(context, tenantId, spy);

            var result = await controller.ConfirmarSolicitud(42, clienteAccion: null, clienteId: null, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(1, spy.ConfirmCount);
            Assert.Equal(42, spy.LastConfirmedId);
            // El calendario nunca elige otro profesional a mano: respeta el que ya estaba reservado.
            Assert.Null(spy.LastFuncionarioOverride);
            Assert.Equal("calendar-user", spy.LastUserId);
            Assert.Contains("cita creada", ok.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task RejectFromCalendar_DelegatesToTheSharedApplicationService()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var spy = new SpyBookingRequestService();
            var controller = CreateController(context, tenantId, spy);

            var result = await controller.RechazarSolicitud(7, "No hay cupo", CancellationToken.None);

            Assert.IsType<OkObjectResult>(result);
            Assert.Equal(1, spy.RejectCount);
            Assert.Equal(7, spy.LastRejectedId);
            Assert.Equal("No hay cupo", spy.LastReason);
        }

        [Fact]
        public async Task ConfirmFromCalendar_ReturnsConflict_WhenRequestAlreadyProcessed()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var spy = new SpyBookingRequestService
            {
                ConfirmResult = BookingActionResult.Fail("Esta solicitud ya fue procesada.")
            };

            var controller = CreateController(context, tenantId, spy);

            var result = await controller.ConfirmarSolicitud(42, clienteAccion: null, clienteId: null, CancellationToken.None);

            // 409 para que el calendario refresque y muestre el estado real.
            var conflict = Assert.IsType<ConflictObjectResult>(result);
            Assert.Contains("procesada", conflict.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task PendingEndpoint_RejectsInvalidDate()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var controller = CreateController(context, tenantId, new SpyBookingRequestService());

            var result = await controller.GetSolicitudesPendientes("2026/05/27", CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Theory]
        [InlineData(nameof(CalendarController.ConfirmarSolicitud))]
        [InlineData(nameof(CalendarController.RechazarSolicitud))]
        public void BookingActions_RequireReservationsManageAndAntiForgery(string actionName)
        {
            var method = typeof(CalendarController)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Single(m => m.Name == actionName);

            Assert.NotEmpty(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true));

            var permisos = method
                .GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true)
                .Cast<RequirePermissionAttribute>()
                .Select(a => a.Permission)
                .ToList();

            Assert.Contains(AppPermissions.ReservationsManage, permisos);
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────

        private static DateTime At(int hora, int minuto) =>
            Fecha.ToDateTime(new TimeOnly(hora, minuto));

        private static CalendarController CreateController(
            ApplicationDbContext context,
            Guid tenantId,
            IBookingRequestService bookingRequestService)
        {
            var controller = new CalendarController(
                ControllerTestSupport.CreateCalendarCommandService(context),
                ControllerTestSupport.CreateCalendarQueryService(context),
                ControllerTestSupport.CreateControlCobrosQueryService(context),
                ControllerTestSupport.CreateCobroService(context),
                ControllerTestSupport.CreateComprobanteCobroService(),
                new FakeTenantWhatsAppFeatureService { IsEnabled = false },
                ControllerTestSupport.BusinessDateTimeProvider,
                ControllerTestSupport.CreateCobroFiscalPreviewService(
                    context, new TestTenantProvider { TenantId = tenantId }),
                ControllerTestSupport.CreateAvailabilityService(context),
                bookingRequestService,
                new TestAuthorizationService(),
                new NoOpAppointmentCancellationWhatsAppService());

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("calendar-user", tenantId));

            return controller;
        }

        private static BookingRequestService BuildRequestService(ApplicationDbContext context) =>
            new(
                context,
                ControllerTestSupport.CreateCalendarCommandService(context),
                new NoOpCalendarWhatsAppNotificationService(),
                ControllerTestSupport.CreateBookingAvailabilityService(
                    context, new FixedBusinessDateTimeProvider()),
                new StubBookingSettingsService(),
                new FixedBusinessDateTimeProvider(),
                new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                new FakeTenantWhatsAppFeatureService { IsEnabled = true },
                new RecordingBookingRejectionWhatsAppService(),
                ControllerTestSupport.CreateClienteIdentityService(context),
                NullLogger<BookingRequestService>.Instance);

        private static async Task SeedPendingAsync(
            ApplicationDbContext context,
            int servicioId,
            int funcionarioAsignadoId,
            DateTime inicio,
            int duracion,
            string estado = BookingRequestStates.Pending)
        {
            context.BookingRequests.Add(new BookingRequest
            {
                ServicioId = servicioId,
                FuncionarioId = null,
                FuncionarioAsignadoId = funcionarioAsignadoId,
                NombreCliente = "Cliente",
                TelefonoCliente = "88889999",
                FechaHoraInicioSolicitada = inicio,
                FechaHoraFinCalculada = inicio.AddMinutes(duracion),
                DuracionMinutos = duracion,
                Estado = estado,
                Origen = BookingRequestOrigins.PublicLink,
                CreatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        private static async Task<Servicio> SeedServicioAsync(
            ApplicationDbContext context, string nombre, int duracion)
        {
            var servicio = new Servicio
            {
                Nombre = nombre,
                Precio = 12000m,
                DuracionMinutos = duracion,
                Activo = true
            };
            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();
            return servicio;
        }

        private static async Task<Funcionario> SeedFuncionarioAsync(
            ApplicationDbContext context, string nombre)
        {
            var puesto = new Puesto
            {
                NombrePuesto = $"Puesto {Guid.NewGuid():N}",
                Detalle = "Reservas",
                Activo = true
            };
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

        private sealed class StubBookingSettingsService : IBookingSettingsService
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
    }
}
