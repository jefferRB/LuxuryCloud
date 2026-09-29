using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.Clientes;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.Clientes
{
    /// <summary>
    /// Resolución del Cliente al CONFIRMAR una reserva online. El cliente nunca nace al recibir la
    /// solicitud (una pendiente puede terminar rechazada): nace, como mucho, al confirmarla.
    /// </summary>
    public class BookingClienteResolutionTests
    {
        [Fact]
        public async Task ConfirmAsync_ClienteExactoExistente_VinculaSinPreguntar()
        {
            using var fixture = await BookingFixture.CreateAsync();
            var cliente = await fixture.SeedClienteAsync("Alex Example Rivera", "83334444");
            var solicitudId = await fixture.SeedSolicitudAsync("Alex Example Rivera", "83334444");

            var preview = await fixture.Service.PreviewClienteAsync(solicitudId);

            Assert.NotNull(preview);
            Assert.Equal(ClienteIdentityStatus.ExistingExactMatch, preview!.Status);
            Assert.True(preview.PuedeConfirmarDirecto);

            var resultado = await fixture.Service.ConfirmAsync(solicitudId, null, "admin");

            Assert.True(resultado.Success);
            fixture.Context.ChangeTracker.Clear();

            var cita = await fixture.Context.Citas.AsNoTracking().SingleAsync();
            Assert.Equal(cliente.Id, cita.ClienteId);
            Assert.Single(await fixture.Context.Clientes.AsNoTracking().ToListAsync());

            var solicitud = await fixture.Context.BookingRequests.AsNoTracking().SingleAsync();
            Assert.Equal(cliente.Id, solicitud.ClienteId);
        }

        [Fact]
        public async Task PreviewClienteAsync_ClienteNuevo_PideDecision()
        {
            using var fixture = await BookingFixture.CreateAsync();
            var solicitudId = await fixture.SeedSolicitudAsync("Persona Nueva", "87001122");

            var preview = await fixture.Service.PreviewClienteAsync(solicitudId);

            Assert.NotNull(preview);
            Assert.Equal(ClienteIdentityStatus.NotFound, preview!.Status);
            Assert.False(preview.PuedeConfirmarDirecto);
        }

        [Fact]
        public async Task PreviewClienteAsync_MismoTelefonoOtroNombre_PideDecisionDeVinculacion()
        {
            using var fixture = await BookingFixture.CreateAsync();
            await fixture.SeedClienteAsync("Alex Example Rivera", "83334444");
            var solicitudId = await fixture.SeedSolicitudAsync("Alex", "8333-4444");

            var preview = await fixture.Service.PreviewClienteAsync(solicitudId);

            Assert.NotNull(preview);
            Assert.Equal(ClienteIdentityStatus.ExistingPhoneMatchWithDifferentName, preview!.Status);
            Assert.False(preview.PuedeConfirmarDirecto);
            Assert.Equal("Alex Example Rivera", Assert.Single(preview.Matches).Nombre);
        }

        [Fact]
        public async Task ConfirmAsync_ConfirmarYRegistrar_CreaClienteYLoRelacionaConLaCita()
        {
            using var fixture = await BookingFixture.CreateAsync();
            var solicitudId = await fixture.SeedSolicitudAsync("Persona Nueva", "8700-1122", aceptaWhatsApp: true);

            var resultado = await fixture.Service.ConfirmAsync(
                solicitudId,
                null,
                "admin",
                new BookingClienteChoice(BookingClienteDecision.Registrar));

            Assert.True(resultado.Success);
            fixture.Context.ChangeTracker.Clear();

            var cliente = await fixture.Context.Clientes.AsNoTracking().SingleAsync();
            Assert.Equal("Persona Nueva", cliente.Nombre);
            Assert.Equal("8700-1122", cliente.NumeroTelefono);
            // El cliente autorizó WhatsApp en el formulario público: se conserva en su ficha.
            Assert.True(cliente.AceptaMensajesWhatsApp);

            var cita = await fixture.Context.Citas.AsNoTracking().SingleAsync();
            Assert.Equal(cliente.Id, cita.ClienteId);

            var solicitud = await fixture.Context.BookingRequests.AsNoTracking().SingleAsync();
            Assert.Equal(cliente.Id, solicitud.ClienteId);
            Assert.Equal(cita.Id, solicitud.ConvertedCitaId);
        }

        [Fact]
        public async Task ConfirmAsync_ConfirmarSinRegistrar_CreaLaCitaSinCliente()
        {
            using var fixture = await BookingFixture.CreateAsync();
            var solicitudId = await fixture.SeedSolicitudAsync("Persona Nueva", "87001122");

            var resultado = await fixture.Service.ConfirmAsync(
                solicitudId,
                null,
                "admin",
                new BookingClienteChoice(BookingClienteDecision.SinVincular));

            Assert.True(resultado.Success);
            fixture.Context.ChangeTracker.Clear();

            Assert.Empty(await fixture.Context.Clientes.AsNoTracking().ToListAsync());

            var cita = await fixture.Context.Citas.AsNoTracking().SingleAsync();
            Assert.Null(cita.ClienteId);
            Assert.Equal("Persona Nueva", cita.NombreCliente);

            var solicitud = await fixture.Context.BookingRequests.AsNoTracking().SingleAsync();
            Assert.Equal(BookingRequestStates.Confirmed, solicitud.Estado);
            Assert.Null(solicitud.ClienteId);
        }

        [Fact]
        public async Task ConfirmAsync_SinVincularConTelefonoConocido_NoVinculaNiSobrescribeElCliente()
        {
            using var fixture = await BookingFixture.CreateAsync();
            await fixture.SeedClienteAsync("Alex Example Rivera", "83334444");
            var solicitudId = await fixture.SeedSolicitudAsync("Alex", "83334444");

            await fixture.Service.ConfirmAsync(
                solicitudId,
                null,
                "admin",
                new BookingClienteChoice(BookingClienteDecision.SinVincular));

            fixture.Context.ChangeTracker.Clear();

            var cita = await fixture.Context.Citas.AsNoTracking().SingleAsync();
            Assert.Null(cita.ClienteId);
            Assert.Equal("Alex", cita.NombreCliente);

            // El nombre maestro del cliente NO se toca por lo que escribieron en la reserva.
            var cliente = await fixture.Context.Clientes.AsNoTracking().SingleAsync();
            Assert.Equal("Alex Example Rivera", cliente.Nombre);
        }

        [Fact]
        public async Task ConfirmAsync_Vincular_AsociaLaCitaAlClienteElegido()
        {
            using var fixture = await BookingFixture.CreateAsync();
            var cliente = await fixture.SeedClienteAsync("Alex Example Rivera", "8333-4444");
            var solicitudId = await fixture.SeedSolicitudAsync("Alex", "83334444");

            var resultado = await fixture.Service.ConfirmAsync(
                solicitudId,
                null,
                "admin",
                new BookingClienteChoice(BookingClienteDecision.Vincular, cliente.Id));

            Assert.True(resultado.Success);
            fixture.Context.ChangeTracker.Clear();

            var cita = await fixture.Context.Citas.AsNoTracking().SingleAsync();
            Assert.Equal(cliente.Id, cita.ClienteId);
            Assert.Equal("Alex Example Rivera", cita.NombreCliente);

            var clientePersistido = await fixture.Context.Clientes.AsNoTracking().SingleAsync();
            Assert.Equal("Alex Example Rivera", clientePersistido.Nombre);
        }

        [Fact]
        public async Task ConfirmAsync_VincularUnClienteQueNoCoincide_RechazaYDejaLaSolicitudPendiente()
        {
            using var fixture = await BookingFixture.CreateAsync();
            var otroCliente = await fixture.SeedClienteAsync("Otra Persona", "88880000");
            var solicitudId = await fixture.SeedSolicitudAsync("Persona Nueva", "87001122");

            var resultado = await fixture.Service.ConfirmAsync(
                solicitudId,
                null,
                "admin",
                new BookingClienteChoice(BookingClienteDecision.Vincular, otroCliente.Id));

            Assert.False(resultado.Success);
            fixture.Context.ChangeTracker.Clear();

            // Nada se confirmó: la decisión se valida ANTES del claim.
            var solicitud = await fixture.Context.BookingRequests.AsNoTracking().SingleAsync();
            Assert.Equal(BookingRequestStates.Pending, solicitud.Estado);
            Assert.Empty(await fixture.Context.Citas.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task ConfirmAsync_TelefonoDuplicado_NoVinculaAlAzar()
        {
            using var fixture = await BookingFixture.CreateAsync();
            await fixture.SeedClienteAsync("Ana Duplicada", "83334444");
            await fixture.SeedClienteAsync("Ana D.", "8333-4444");
            var solicitudId = await fixture.SeedSolicitudAsync("Ana", "83334444");

            var preview = await fixture.Service.PreviewClienteAsync(solicitudId);
            Assert.Equal(ClienteIdentityStatus.AmbiguousPhoneMatch, preview!.Status);
            Assert.False(preview.PuedeConfirmarDirecto);
            Assert.Equal(2, preview.Matches.Count);

            // Confirmar sin decidir no elige a ninguno.
            await fixture.Service.ConfirmAsync(solicitudId, null, "admin");

            fixture.Context.ChangeTracker.Clear();
            var cita = await fixture.Context.Citas.AsNoTracking().SingleAsync();
            Assert.Null(cita.ClienteId);
            Assert.Equal(2, await fixture.Context.Clientes.AsNoTracking().CountAsync());
        }

        [Fact]
        public async Task ConfirmAsync_RegistrarCuandoElTelefonoYaExiste_ReutilizaYNoDuplica()
        {
            using var fixture = await BookingFixture.CreateAsync();
            var cliente = await fixture.SeedClienteAsync("Alex Example Rivera", "83334444");
            var solicitudId = await fixture.SeedSolicitudAsync("Alex", "8333-4444");

            // El administrador pidió "registrar" con una pantalla desactualizada.
            await fixture.Service.ConfirmAsync(
                solicitudId,
                null,
                "admin",
                new BookingClienteChoice(BookingClienteDecision.Registrar));

            fixture.Context.ChangeTracker.Clear();

            Assert.Single(await fixture.Context.Clientes.AsNoTracking().ToListAsync());
            var cita = await fixture.Context.Citas.AsNoTracking().SingleAsync();
            Assert.Equal(cliente.Id, cita.ClienteId);
        }

        [Fact]
        public async Task PreviewClienteAsync_SolicitudDeOtroTenant_NoDevuelveNada()
        {
            using var fixture = await BookingFixture.CreateAsync();
            var solicitudId = await fixture.SeedSolicitudAsync("Persona Nueva", "87001122");

            var otroProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var otroContexto = TestDbContextFactory.CreateSqliteContext(otroProvider, fixture.Connection);
            var otroServicio = BookingFixture.BuildService(otroContexto);

            Assert.Null(await otroServicio.PreviewClienteAsync(solicitudId));
        }

        internal sealed class BookingFixture : IDisposable
        {
            private BookingFixture(
                ProyectoIdentity.Datos.ApplicationDbContext context,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                TestTenantProvider tenantProvider,
                int funcionarioId,
                int servicioId)
            {
                Context = context;
                Connection = connection;
                TenantProvider = tenantProvider;
                TenantId = tenantProvider.TenantId;
                FuncionarioId = funcionarioId;
                ServicioId = servicioId;
                Service = BuildService(context, funcionarioId);
            }

            public ProyectoIdentity.Datos.ApplicationDbContext Context { get; }
            public Microsoft.Data.Sqlite.SqliteConnection Connection { get; }
            public TestTenantProvider TenantProvider { get; }
            public Guid TenantId { get; }
            public int FuncionarioId { get; }
            public int ServicioId { get; }
            public BookingRequestService Service { get; }

            /// <summary>
            /// Se construye con el MISMO CalendarCommandService de producción: confirmar una
            /// reserva y crear una cita desde el calendario pasan por el mismo flujo.
            /// </summary>
            public static BookingRequestService BuildService(
                ProyectoIdentity.Datos.ApplicationDbContext context,
                int funcionarioId = 0) =>
                new(
                    context,
                    ControllerTestSupport.CreateCalendarCommandService(context),
                    new NoOpCalendarWhatsAppNotificationService(),
                    new SiempreDisponible(funcionarioId),
                    new StubSettings(),
                    ControllerTestSupport.BusinessDateTimeProvider,
                    new HttpContextAccessor(),
                    new FakeTenantWhatsAppFeatureService { IsEnabled = false },
                    new NoOpRejectionService(),
                    ControllerTestSupport.CreateClienteIdentityService(context),
                    NullLogger<BookingRequestService>.Instance);

            public static async Task<BookingFixture> CreateAsync()
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

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
                    Nombre = "Ana",
                    IdPuesto = puesto.IdPuesto,
                    Activo = true,
                    ColorCalendario = "#123456",
                    PorcentajeGanancia = 40m,
                    PorcentajeProducto = 10m,
                    FechaIngreso = new DateTime(2026, 4, 1)
                };
                context.Funcionarios.Add(funcionario);

                var servicio = new Servicio
                {
                    Nombre = "Corte",
                    DuracionMinutos = 60,
                    Precio = 10000,
                    Activo = true
                };
                context.Servicios.Add(servicio);
                await context.SaveChangesAsync();
                context.ChangeTracker.Clear();

                return new BookingFixture(
                    context,
                    connection,
                    tenantProvider,
                    funcionario.IdFuncionario,
                    servicio.Id);
            }

            public async Task<ClientesModel> SeedClienteAsync(string nombre, string telefono)
            {
                var cliente = new ClientesModel
                {
                    Nombre = nombre,
                    NumeroTelefono = telefono,
                    FrecuenciaVisita = 15,
                    FechaUltimaVisita = new DateTime(2026, 9, 1)
                };

                Context.Clientes.Add(cliente);
                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
                return cliente;
            }

            public async Task<int> SeedSolicitudAsync(string nombre, string telefono, bool aceptaWhatsApp = false)
            {
                var inicio = new DateTime(2026, 9, 18, 10, 0, 0);
                var solicitud = new BookingRequest
                {
                    ServicioId = ServicioId,
                    FuncionarioAsignadoId = FuncionarioId,
                    NombreCliente = nombre,
                    TelefonoCliente = telefono,
                    FechaHoraInicioSolicitada = inicio,
                    FechaHoraFinCalculada = inicio.AddHours(1),
                    DuracionMinutos = 60,
                    Estado = BookingRequestStates.Pending,
                    Origen = BookingRequestOrigins.PublicLink,
                    AceptaWhatsApp = aceptaWhatsApp,
                    CreatedAtUtc = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc)
                };

                Context.BookingRequests.Add(solicitud);
                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
                return solicitud.Id;
            }

            public void Dispose()
            {
                Context.Dispose();
                Connection.Dispose();
            }
        }

        private sealed class SiempreDisponible : IBookingAvailabilityService
        {
            private readonly int _funcionarioId;

            public SiempreDisponible(int funcionarioId) => _funcionarioId = funcionarioId;

            public Task<SlotResolution> ResolveSlotAsync(int servicioId, DateTime inicio, int? funcionarioId, CancellationToken cancellationToken = default) =>
                Task.FromResult(new SlotResolution
                {
                    Disponible = true,
                    FuncionarioId = funcionarioId ?? _funcionarioId,
                    DuracionMinutos = 60
                });

            public Task<IReadOnlyList<string>> GetAvailableSlotsAsync(int servicioId, DateOnly fecha, int? funcionarioId, CancellationToken cancellationToken = default) =>
                Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());

            public Task<IReadOnlyList<AvailableSlotSuggestion>> GetNextAvailableSlotsAsync(int servicioId, DateOnly fromDate, int? funcionarioId, int maxSuggestions = 5, CancellationToken cancellationToken = default) =>
                Task.FromResult((IReadOnlyList<AvailableSlotSuggestion>)Array.Empty<AvailableSlotSuggestion>());
        }

        private sealed class StubSettings : IBookingSettingsService
        {
            public Task<BookingSettingsViewModel> BuildSettingsViewModelAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new BookingSettingsViewModel());

            public Task SaveSettingsAsync(BookingSettingsViewModel model, string? userId, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task<PublicBookingTenantContext?> ResolvePublicBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
                Task.FromResult<PublicBookingTenantContext?>(null);

            public Task<string?> GetCurrentSlugAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<string?>("negocio");
        }

        private sealed class NoOpRejectionService : IBookingRejectionWhatsAppService
        {
            public Task<LuxuryApp.Services.WhatsApp.WhatsAppNotificationReason> NotifyRejectionAsync(
                int bookingRequestId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(LuxuryApp.Services.WhatsApp.WhatsAppNotificationReason.AddonInactive);
        }
    }
}
