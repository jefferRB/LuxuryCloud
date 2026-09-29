using LuxuryApp.Controllers.Calendar;
using LuxuryApp.Controllers.Reservas;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Tests.Clientes
{
    /// <summary>
    /// Confirmar una reserva desde el Calendario y desde el módulo de Reservas tiene que terminar
    /// en EXACTAMENTE el mismo flujo de aplicación, con la misma decisión sobre el cliente. Si
    /// alguna de las dos pantallas reimplementara la regla, este test se cae.
    /// </summary>
    public class BookingConfirmationSharedFlowTests
    {
        [Theory]
        [InlineData("registrar", null, BookingClienteDecision.Registrar)]
        [InlineData("sinvincular", null, BookingClienteDecision.SinVincular)]
        [InlineData("vincular", 77, BookingClienteDecision.Vincular)]
        [InlineData(null, null, BookingClienteDecision.Automatico)]
        // Valor desconocido/manipulado: allowlist, cae al comportamiento seguro por defecto.
        [InlineData("borrar-todo", null, BookingClienteDecision.Automatico)]
        public async Task ConfirmarDesdeAmbasPantallas_UsaElMismoServicioYLaMismaDecision(
            string? clienteAccion,
            int? clienteId,
            BookingClienteDecision decisionEsperada)
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var spyCalendario = new SpyBookingRequestService();
            var spyReservas = new SpyBookingRequestService();

            var calendario = BuildCalendarController(context, tenantId, spyCalendario);
            var reservas = BuildReservasController(tenantId, spyReservas);

            var desdeCalendario = await calendario.ConfirmarSolicitud(
                42, clienteAccion, clienteId, CancellationToken.None);
            var desdeReservas = await reservas.Confirmar(
                42, null, clienteAccion, clienteId, CancellationToken.None);

            Assert.IsType<OkObjectResult>(desdeCalendario);
            Assert.IsType<OkObjectResult>(desdeReservas);

            // Las dos pantallas delegan: ninguna crea la cita ni resuelve el cliente por su cuenta.
            Assert.Equal(1, spyCalendario.ConfirmCount);
            Assert.Equal(1, spyReservas.ConfirmCount);

            Assert.Equal(decisionEsperada, spyCalendario.LastClienteChoice?.Decision);
            Assert.Equal(decisionEsperada, spyReservas.LastClienteChoice?.Decision);

            if (decisionEsperada == BookingClienteDecision.Vincular)
            {
                Assert.Equal(clienteId, spyCalendario.LastClienteChoice?.ClienteId);
                Assert.Equal(clienteId, spyReservas.LastClienteChoice?.ClienteId);
            }
        }

        [Fact]
        public async Task ConsultaPreviaDelCliente_TambienSaleDelMismoServicioEnAmbasPantallas()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var spyCalendario = new SpyBookingRequestService();
            var spyReservas = new SpyBookingRequestService();

            await BuildCalendarController(context, tenantId, spyCalendario)
                .ClientePrevioSolicitud(42, CancellationToken.None);
            await BuildReservasController(tenantId, spyReservas)
                .ClientePrevio(42, CancellationToken.None);

            Assert.Equal(1, spyCalendario.PreviewCount);
            Assert.Equal(42, spyCalendario.LastPreviewId);
            Assert.Equal(1, spyReservas.PreviewCount);
            Assert.Equal(42, spyReservas.LastPreviewId);
        }

        private static CalendarController BuildCalendarController(
            ProyectoIdentity.Datos.ApplicationDbContext context,
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

        private static ReservasController BuildReservasController(
            Guid tenantId,
            IBookingRequestService bookingRequestService)
        {
            var controller = ControllerTestSupport.CreateReservasController(
                bookingRequestService,
                new StubSettingsService(),
                new StubBookingCatalogService());

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("reservas-user", tenantId));

            return controller;
        }

        private sealed class StubSettingsService : IBookingSettingsService
        {
            public Task<LuxuryApp.Models.Reservas.BookingSettingsViewModel> BuildSettingsViewModelAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new LuxuryApp.Models.Reservas.BookingSettingsViewModel());

            public Task SaveSettingsAsync(
                LuxuryApp.Models.Reservas.BookingSettingsViewModel model,
                string? userId,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task<PublicBookingTenantContext?> ResolvePublicBySlugAsync(
                string slug,
                CancellationToken cancellationToken = default) =>
                Task.FromResult<PublicBookingTenantContext?>(null);

            public Task<string?> GetCurrentSlugAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<string?>("negocio");
        }

        private sealed class StubBookingCatalogService : IBookingCatalogService
        {
            public Task<IReadOnlyList<LuxuryApp.Models.Reservas.PublicBookingServiceOption>> GetPublicServicesAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult((IReadOnlyList<LuxuryApp.Models.Reservas.PublicBookingServiceOption>)
                    Array.Empty<LuxuryApp.Models.Reservas.PublicBookingServiceOption>());

            public Task<IReadOnlyList<int>> GetCompatibleFuncionarioIdsAsync(
                int servicioId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult((IReadOnlyList<int>)Array.Empty<int>());

            public Task<bool> IsServiceVisibleOnlineAsync(int servicioId, CancellationToken cancellationToken = default) =>
                Task.FromResult(false);

            public Task<LuxuryApp.Models.Reservas.BookingCatalogViewModel> BuildManagementAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new LuxuryApp.Models.Reservas.BookingCatalogViewModel());

            public Task SaveAsync(
                LuxuryApp.Models.Reservas.BookingCatalogSaveInput input,
                string? userId,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }
    }
}
