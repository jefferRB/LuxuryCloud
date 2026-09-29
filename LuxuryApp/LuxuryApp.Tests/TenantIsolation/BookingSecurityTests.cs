using LuxuryApp.Controllers.Reservas;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Tests.Support;
using LuxuryApp.Models.Asociados;
using LuxuryApp.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    /// <summary>
    /// Auditoría de seguridad del módulo de Reservas Online públicas: autorización público/privado,
    /// antiforgery, aislamiento multi-tenant (IDOR de servicio/solicitud) y revalidación backend de
    /// día laboral y ventana horaria (no se confía en el frontend).
    /// </summary>
    public class BookingSecurityTests
    {
        // ──────────────────────── Autorización público/privado ────────────────────────

        [Fact]
        public void ReservasController_PrivateAdmin_RequiresReservationsPermission()
        {
            // La puerta ya no es el rol sino el permiso: el administrador lo cumple siempre y un
            // asociado solo si se lo concedieron. Sin permiso, la URL directa devuelve 403.
            var permiso = typeof(ReservasController)
                .GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true)
                .OfType<RequirePermissionAttribute>()
                .FirstOrDefault();

            Assert.NotNull(permiso);
            Assert.Equal(AppPermissions.ReservationsView, permiso!.Permission);

            // No debe llevar [AllowAnonymous] en ningún nivel.
            Assert.Empty(typeof(ReservasController)
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
        }

        [Fact]
        public void PublicReservasController_IsAnonymous_ButOnlyForRequestCreation()
        {
            Assert.NotEmpty(typeof(PublicReservasController)
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));

            // El controlador público no debe exponer acciones de administración.
            var actionNames = typeof(PublicReservasController)
                .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                .Select(m => m.Name)
                .ToArray();

            Assert.DoesNotContain("Confirmar", actionNames);
            Assert.DoesNotContain("Rechazar", actionNames);
            Assert.DoesNotContain("Configuracion", actionNames);
        }

        [Theory]
        [InlineData(nameof(ReservasController.Confirmar))]
        [InlineData(nameof(ReservasController.Rechazar))]
        public void PrivateMutatingActions_RequireAntiForgeryToken(string actionName)
        {
            var method = typeof(ReservasController).GetMethod(actionName);
            Assert.NotNull(method);

            var hasAntiForgery = method!
                .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true)
                .Any();

            Assert.True(hasAntiForgery, $"{actionName} debe exigir antiforgery token.");
        }

        [Fact]
        public void ConfiguracionPost_RequiresAntiForgeryToken()
        {
            var post = typeof(ReservasController)
                .GetMethods()
                .Single(m => m.Name == nameof(ReservasController.Configuracion)
                             && m.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true).Any());

            Assert.True(post.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true).Any());
        }

        [Fact]
        public void PublicSolicitar_RequiresAntiForgeryToken()
        {
            var post = typeof(PublicReservasController).GetMethod(nameof(PublicReservasController.Solicitar));
            Assert.NotNull(post);
            Assert.True(post!.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true).Any());
        }

        // ──────────────────────── IDOR / multi-tenant ────────────────────────

        [Fact]
        public async Task ResolveSlot_ServicioFromAnotherTenant_IsRejected()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            // Servicio y funcionario pertenecen al tenant A.
            var servicio = await SeedServicioAsync(context, 30);
            await SeedFuncionarioAsync(context);
            await SeedSettingsAsync(context, tenantProvider, workingMask: 0b1111111);

            // El atacante actúa como tenant B pero reenvía el ServicioId del tenant A.
            tenantProvider.TenantId = tenantB;
            context.ChangeTracker.Clear();

            var service = ControllerTestSupport.CreateBookingAvailabilityService(context, new FixedBusinessDateTimeProvider());
            var resolucion = await service.ResolveSlotAsync(
                servicio.Id,
                new DateTime(2026, 5, 27, 9, 0, 0),
                funcionarioId: null);

            Assert.False(resolucion.Disponible);
        }

        [Fact]
        public async Task Confirm_RequestFromAnotherTenant_IsNotFound()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var solicitud = await SeedBookingRequestAsync(context, BookingRequestStates.Pending);

            // El admin del tenant B intenta confirmar la solicitud del tenant A por id.
            tenantProvider.TenantId = tenantB;
            context.ChangeTracker.Clear();

            var service = CreateRequestService(context);
            var result = await service.ConfirmAsync(solicitud.Id, null, "user-b");

            Assert.False(result.Success);
            // La solicitud sigue Pending: no se procesó nada cross-tenant.
            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();
            var persisted = await context.BookingRequests.AsNoTracking().SingleAsync();
            Assert.Equal(BookingRequestStates.Pending, persisted.Estado);
        }

        // ──────────────────────── Guardas de estado (doble confirmación / carrera) ────────────────────────

        [Fact]
        public async Task Confirm_AlreadyRejectedRequest_IsBlocked()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var solicitud = await SeedBookingRequestAsync(context, BookingRequestStates.Rejected);
            var service = CreateRequestService(context);

            var result = await service.ConfirmAsync(solicitud.Id, null, "user");

            Assert.False(result.Success);
        }

        [Fact]
        public async Task Reject_AlreadyConfirmedRequest_IsBlocked()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var solicitud = await SeedBookingRequestAsync(context, BookingRequestStates.Confirmed);
            var service = CreateRequestService(context);

            var result = await service.RejectAsync(solicitud.Id, "motivo", "user");

            Assert.False(result.Success);
        }

        // ──────────────────────── Revalidación backend de día/hora ────────────────────────

        [Fact]
        public async Task ResolveSlot_NonWorkingDay_IsRejected()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var servicio = await SeedServicioAsync(context, 30);
            await SeedFuncionarioAsync(context);
            // Solo lunes laboral (bit 1). 2026-05-27 es miércoles.
            await SeedSettingsAsync(context, tenantProvider, workingMask: 0b0000010);

            var service = ControllerTestSupport.CreateBookingAvailabilityService(context, new FixedBusinessDateTimeProvider());
            var resolucion = await service.ResolveSlotAsync(
                servicio.Id,
                new DateTime(2026, 5, 27, 9, 0, 0),
                funcionarioId: null);

            Assert.False(resolucion.Disponible);
        }

        [Fact]
        public async Task ResolveSlot_OutsideBusinessHours_IsRejected()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var servicio = await SeedServicioAsync(context, 30);
            await SeedFuncionarioAsync(context);
            await SeedSettingsAsync(context, tenantProvider, workingMask: 0b1111111, open: new TimeOnly(8, 0), close: new TimeOnly(12, 0));

            var service = ControllerTestSupport.CreateBookingAvailabilityService(context, new FixedBusinessDateTimeProvider());
            // 13:00 está fuera de la jornada 08:00–12:00.
            var resolucion = await service.ResolveSlotAsync(
                servicio.Id,
                new DateTime(2026, 5, 27, 13, 0, 0),
                funcionarioId: null);

            Assert.False(resolucion.Disponible);
        }

        [Fact]
        public async Task ResolveSlot_ValidWorkingSlot_IsAvailable()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            var servicio = await SeedServicioAsync(context, 30);
            var funcionario = await SeedFuncionarioAsync(context);
            await SeedSettingsAsync(context, tenantProvider, workingMask: 0b1111111, open: new TimeOnly(8, 0), close: new TimeOnly(18, 0));

            var service = ControllerTestSupport.CreateBookingAvailabilityService(context, new FixedBusinessDateTimeProvider());
            var resolucion = await service.ResolveSlotAsync(
                servicio.Id,
                new DateTime(2026, 5, 27, 9, 0, 0),
                funcionarioId: null);

            Assert.True(resolucion.Disponible);
            Assert.Equal(funcionario.IdFuncionario, resolucion.FuncionarioId);
            Assert.Equal(30, resolucion.DuracionMinutos);
        }

        // ──────────────────────── Helpers ────────────────────────

        private static BookingRequestService CreateRequestService(ApplicationDbContext context) =>
            new(
                context,
                new ThrowingCalendarCommandService(),
                new NoOpCalendarWhatsAppNotificationService(),
                new ThrowingAvailabilityService(),
                new ThrowingSettingsService(),
                new FixedBusinessDateTimeProvider(),
                new HttpContextAccessor(),
                new FakeTenantWhatsAppFeatureService { IsEnabled = true },
                new RecordingBookingRejectionWhatsAppService(),
                ControllerTestSupport.CreateClienteIdentityService(context),
                NullLogger<BookingRequestService>.Instance);

        private static async Task<Servicio> SeedServicioAsync(ApplicationDbContext context, int duracionMinutos)
        {
            var servicio = new Servicio
            {
                Nombre = $"Servicio {Guid.NewGuid():N}",
                Precio = 25m,
                DuracionMinutos = duracionMinutos,
                Activo = true
            };
            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();
            return servicio;
        }

        private static async Task<Funcionario> SeedFuncionarioAsync(ApplicationDbContext context)
        {
            var puesto = new Puesto { NombrePuesto = $"Puesto {Guid.NewGuid():N}", Detalle = "Reservas", Activo = true };
            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            var funcionario = new Funcionario
            {
                Nombre = "Profesional",
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

        private static async Task SeedSettingsAsync(
            ApplicationDbContext context,
            TestTenantProvider tenantProvider,
            int workingMask,
            TimeOnly? open = null,
            TimeOnly? close = null)
        {
            await EnsureTenantAsync(context, tenantProvider.TenantId);

            context.TenantBookingSettings.Add(new TenantBookingSettings
            {
                PublicBookingEnabled = true,
                PublicBookingSlug = $"slug-{Guid.NewGuid():N}".Substring(0, 20),
                WorkingDaysMask = workingMask,
                OpenTime = open ?? new TimeOnly(8, 0),
                CloseTime = close ?? new TimeOnly(18, 0),
                SlotIntervalMinutes = 30,
                PublicBookingMaxDaysAhead = 30,
                PublicBookingMinAdvanceMinutes = 0
            });
            await context.SaveChangesAsync();
        }

        private static async Task EnsureTenantAsync(ApplicationDbContext context, Guid tenantId)
        {
            if (tenantId == Guid.Empty)
            {
                return;
            }

            if (await context.Tenants.IgnoreQueryFilters().AnyAsync(tenant => tenant.Id == tenantId))
            {
                return;
            }

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = $"Tenant {tenantId:N}",
                Activo = true
            });

            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }

        private static async Task<BookingRequest> SeedBookingRequestAsync(ApplicationDbContext context, string estado)
        {
            var servicio = await SeedServicioAsync(context, 30);
            var solicitud = new BookingRequest
            {
                ServicioId = servicio.Id,
                NombreCliente = "Cliente Test",
                TelefonoCliente = "88880000",
                FechaHoraInicioSolicitada = new DateTime(2026, 5, 27, 9, 0, 0),
                FechaHoraFinCalculada = new DateTime(2026, 5, 27, 9, 30, 0),
                DuracionMinutos = 30,
                Estado = estado,
                Origen = BookingRequestOrigins.PublicLink,
                CreatedAtUtc = DateTime.UtcNow
            };
            context.BookingRequests.Add(solicitud);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            return solicitud;
        }

        // Stubs que lanzan si se invocan: prueban que las guardas retornan ANTES de tocar
        // la disponibilidad real o la creación de la cita.
        private sealed class ThrowingAvailabilityService : IBookingAvailabilityService
        {
            public Task<IReadOnlyList<string>> GetAvailableSlotsAsync(int servicioId, DateOnly fecha, int? funcionarioId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("No debió calcularse disponibilidad.");

            public Task<SlotResolution> ResolveSlotAsync(int servicioId, DateTime inicio, int? funcionarioId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("No debió resolverse el slot.");

            public Task<IReadOnlyList<AvailableSlotSuggestion>> GetNextAvailableSlotsAsync(int servicioId, DateOnly fromDate, int? funcionarioId, int maxSuggestions = 5, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("No debió calcularse disponibilidad futura.");
        }

        private sealed class ThrowingCalendarCommandService : ICalendarCommandService
        {
            public Task<CalendarAppointmentResponse> CreateAsync(CalendarUpsertRequest request, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("No debió crearse cita.");

            public Task<CalendarAppointmentResponse> UpdateAsync(int id, CalendarUpsertRequest request, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();

            public Task MoveAsync(int id, CalendarMoveRequest request, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();

            public Task ResizeDurationAsync(int id, int duracionMinutos, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();

            public Task DeleteAsync(int id, string? motivoCancelacion = null, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();

            public Task ProcessVisitsAsync(CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();
        }

        private sealed class ThrowingSettingsService : IBookingSettingsService
        {
            public Task<BookingSettingsViewModel> BuildSettingsViewModelAsync(CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();

            public Task SaveSettingsAsync(BookingSettingsViewModel input, string? userId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();

            public Task<PublicBookingTenantContext?> ResolvePublicBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();

            public Task<string?> GetCurrentSlugAsync(CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();
        }
    }
}
