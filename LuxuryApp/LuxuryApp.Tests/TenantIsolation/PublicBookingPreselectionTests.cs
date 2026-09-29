using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Notifications;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.BusinessTime;
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
    public class PublicBookingPreselectionTests
    {
        [Fact]
        public async Task BuildPageAsync_ValidServiceId_PreselectsService()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantId);
            var service = await SeedServiceAsync(context, "Corte", active: true);
            context.TenantBookingServiceSettings.Add(new TenantBookingServiceSetting
            {
                ServicioId = service.Id,
                IsVisibleOnline = true,
                ShowPrice = true
            });
            await context.SaveChangesAsync();

            var page = await BuildPublicBookingService(context)
                .BuildPageAsync(BuildContext(tenantId), service.Id);

            Assert.Equal(service.Id, page.PreselectedServiceId);
            Assert.Equal("Corte", page.PreselectedServiceName);
        }

        [Fact]
        public async Task BuildPageAsync_InvalidInactiveHiddenOrCrossTenantService_IgnoresPreselection()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantA);
            var visible = await SeedServiceAsync(context, "Visible", active: true);
            var hidden = await SeedServiceAsync(context, "Hidden", active: true);
            var inactive = await SeedServiceAsync(context, "Inactive", active: false);

            context.TenantBookingServiceSettings.AddRange(
                new TenantBookingServiceSetting { ServicioId = visible.Id, IsVisibleOnline = true },
                new TenantBookingServiceSetting { ServicioId = hidden.Id, IsVisibleOnline = false },
                new TenantBookingServiceSetting { ServicioId = inactive.Id, IsVisibleOnline = true });
            await context.SaveChangesAsync();

            tenantProvider.TenantId = tenantB;
            await EnsureTenantAsync(context, tenantB);
            var otherTenantService = await SeedServiceAsync(context, "Otro tenant", active: true);

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();
            var service = BuildPublicBookingService(context);
            var bookingContext = BuildContext(tenantA);

            var hiddenPage = await service.BuildPageAsync(bookingContext, hidden.Id);
            var inactivePage = await service.BuildPageAsync(bookingContext, inactive.Id);
            var crossTenantPage = await service.BuildPageAsync(bookingContext, otherTenantService.Id);
            var invalidPage = await service.BuildPageAsync(bookingContext, -99);
            var normalPage = await service.BuildPageAsync(bookingContext);

            Assert.Null(hiddenPage.PreselectedServiceId);
            Assert.Null(inactivePage.PreselectedServiceId);
            Assert.Null(crossTenantPage.PreselectedServiceId);
            Assert.Null(invalidPage.PreselectedServiceId);
            Assert.Null(normalPage.PreselectedServiceId);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task BuildPageAsync_ElCheckboxDeWhatsAppSigueALaCapabilityDelTenant(bool whatsAppActivo)
        {
            // Sin complemento el formulario público no ofrece la casilla; por eso el backend no
            // puede leer ese false como "el cliente dijo que no".
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await EnsureTenantAsync(context, tenantId);

            var page = await BuildPublicBookingService(context, whatsAppActivo)
                .BuildPageAsync(BuildContext(tenantId));

            Assert.Equal(whatsAppActivo, page.MostrarWhatsApp);
        }

        private static PublicBookingService BuildPublicBookingService(
            ApplicationDbContext context,
            bool whatsAppActivo = true)
        {
            var catalog = new BookingCatalogService(context);
            return new PublicBookingService(
                context,
                new NoOpBookingSettingsService(),
                ControllerTestSupport.CreateBookingAvailabilityService(context, new FixedBusinessDateTimeProvider(), catalog),
                catalog,
                new FixedBusinessDateTimeProvider(),
                new FakeTenantWhatsAppFeatureService { IsEnabled = whatsAppActivo },
                new NoOpNotificationService(),
                new HttpContextAccessor(),
                ControllerTestSupport.CreateClienteIdentityService(context),
                NullLogger<PublicBookingService>.Instance);
        }

        private static PublicBookingTenantContext BuildContext(Guid tenantId) => new()
        {
            TenantId = tenantId,
            NombreNegocio = "Tenant",
            Slug = "tenant",
            PermiteElegirFuncionario = false,
            PermiteCualquierFuncionario = true,
            MostrarFotosFuncionarios = false,
            MinAdvanceMinutes = 0,
            MaxDaysAhead = 30
        };

        private static async Task EnsureTenantAsync(ApplicationDbContext context, Guid tenantId)
        {
            if (!await context.Tenants.IgnoreQueryFilters().AnyAsync(tenant => tenant.Id == tenantId))
            {
                context.Tenants.Add(new Tenant { Id = tenantId, Nombre = $"Tenant {tenantId:N}", Activo = true });
                await context.SaveChangesAsync();
                context.ChangeTracker.Clear();
            }
        }

        private static async Task<Servicio> SeedServiceAsync(
            ApplicationDbContext context,
            string name,
            bool active)
        {
            var service = new Servicio
            {
                Nombre = name,
                DuracionMinutos = 30,
                Precio = 5000m,
                Activo = active
            };

            context.Servicios.Add(service);
            await context.SaveChangesAsync();
            return service;
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

            public Task<int> MarkAllAsReadAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(0);

            public Task<bool> MarkAsReadAsync(int id, CancellationToken cancellationToken = default) =>
                Task.FromResult(true);

            public Task CreateBookingRequestReceivedAsync(BookingRequest request, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task CreateAppointmentCancelledViaWhatsAppAsync(Cita cita, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }
    }
}
