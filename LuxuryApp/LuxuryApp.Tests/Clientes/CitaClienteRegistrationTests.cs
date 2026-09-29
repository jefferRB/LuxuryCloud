using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.Clientes;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace LuxuryApp.Tests.Clientes
{
    /// <summary>
    /// Registro y vinculación de clientes desde la creación de citas. El backend es autoritativo:
    /// lo que manda el navegador es una intención, la identidad se resuelve al guardar.
    /// </summary>
    public class CitaClienteRegistrationTests
    {
        private static readonly DateTime Horario = new(2026, 9, 17, 10, 0, 0);

        [Fact]
        public async Task CreateAsync_ClienteExistentePorTelefono_VinculaSinCrearOtroCliente()
        {
            using var fixture = await CitaFixture.CreateAsync();
            var cliente = await fixture.SeedClienteAsync("Alex Example Rivera", "8333-4444");

            // El formulario no mandó ClienteId: solo el nombre abreviado y el teléfono.
            await fixture.Service.CreateAsync(BuildRequest(
                fixture,
                nombre: "Alex",
                telefono: "83334444",
                linkMode: ClienteLinkMode.Registrar));

            fixture.Context.ChangeTracker.Clear();

            Assert.Single(await fixture.Context.Clientes.AsNoTracking().ToListAsync());

            var cita = await fixture.Context.Citas.AsNoTracking().SingleAsync();
            Assert.Equal(cliente.Id, cita.ClienteId);
            // La cita toma los datos canónicos del cliente; el nombre abreviado no pisa el maestro.
            Assert.Equal("Alex Example Rivera", cita.NombreCliente);
            Assert.Equal("8333-4444", cita.TelefonoCliente);

            var clientePersistido = await fixture.Context.Clientes.AsNoTracking().SingleAsync();
            Assert.Equal("Alex Example Rivera", clientePersistido.Nombre);
        }

        [Fact]
        public async Task CreateAsync_ClienteNuevoConRegistro_CreaClienteYLoVincula()
        {
            using var fixture = await CitaFixture.CreateAsync();

            await fixture.Service.CreateAsync(BuildRequest(
                fixture,
                nombre: "Cliente Nuevo",
                telefono: "8700-1122",
                linkMode: ClienteLinkMode.Registrar));

            fixture.Context.ChangeTracker.Clear();

            var cliente = await fixture.Context.Clientes.AsNoTracking().SingleAsync();
            var cita = await fixture.Context.Citas.AsNoTracking().SingleAsync();

            Assert.Equal("Cliente Nuevo", cliente.Nombre);
            Assert.Equal("8700-1122", cliente.NumeroTelefono);
            Assert.Equal(cliente.Id, cita.ClienteId);
            Assert.Equal(fixture.TenantId, cliente.TenantId);
        }

        [Fact]
        public async Task CreateAsync_ClienteNuevoSinRegistro_NoCreaCliente()
        {
            using var fixture = await CitaFixture.CreateAsync();

            await fixture.Service.CreateAsync(BuildRequest(
                fixture,
                nombre: "Cliente Nuevo",
                telefono: "87001122",
                linkMode: ClienteLinkMode.Automatico));

            fixture.Context.ChangeTracker.Clear();

            Assert.Empty(await fixture.Context.Clientes.AsNoTracking().ToListAsync());

            var cita = await fixture.Context.Citas.AsNoTracking().SingleAsync();
            Assert.Null(cita.ClienteId);
            Assert.Equal("Cliente Nuevo", cita.NombreCliente);
            Assert.Equal("87001122", cita.TelefonoCliente);
        }

        [Fact]
        public async Task CreateAsync_SoloNombreSinTelefono_CreaLaCitaYNoRegistraNada()
        {
            using var fixture = await CitaFixture.CreateAsync();

            await fixture.Service.CreateAsync(BuildRequest(
                fixture,
                nombre: "Cliente Sin Telefono",
                telefono: null,
                // Aunque el formulario pidiera registrar, sin teléfono no hay identidad que crear.
                linkMode: ClienteLinkMode.Registrar));

            fixture.Context.ChangeTracker.Clear();

            Assert.Empty(await fixture.Context.Clientes.AsNoTracking().ToListAsync());

            var cita = await fixture.Context.Citas.AsNoTracking().SingleAsync();
            Assert.Null(cita.ClienteId);
            Assert.Equal("Cliente Sin Telefono", cita.NombreCliente);
        }

        [Fact]
        public async Task CreateAsync_SinVincular_NoVinculaAunqueElTelefonoCoincida()
        {
            using var fixture = await CitaFixture.CreateAsync();
            await fixture.SeedClienteAsync("Cliente Registrado", "83334444");

            await fixture.Service.CreateAsync(BuildRequest(
                fixture,
                nombre: "Otra Persona",
                telefono: "83334444",
                linkMode: ClienteLinkMode.SinVincular));

            fixture.Context.ChangeTracker.Clear();

            Assert.Single(await fixture.Context.Clientes.AsNoTracking().ToListAsync());

            var cita = await fixture.Context.Citas.AsNoTracking().SingleAsync();
            Assert.Null(cita.ClienteId);
            Assert.Equal("Otra Persona", cita.NombreCliente);
        }

        [Fact]
        public async Task CreateAsync_TelefonoDuplicado_NoEligeUnClienteAlAzar()
        {
            using var fixture = await CitaFixture.CreateAsync();
            await fixture.SeedClienteAsync("Ana Duplicada", "83334444");
            await fixture.SeedClienteAsync("Ana D.", "8333-4444");

            await fixture.Service.CreateAsync(BuildRequest(
                fixture,
                nombre: "Ana",
                telefono: "83334444",
                linkMode: ClienteLinkMode.Registrar));

            fixture.Context.ChangeTracker.Clear();

            // Ni se elige uno, ni se crea un tercero.
            Assert.Equal(2, await fixture.Context.Clientes.AsNoTracking().CountAsync());

            var cita = await fixture.Context.Citas.AsNoTracking().SingleAsync();
            Assert.Null(cita.ClienteId);
        }

        [Fact]
        public async Task CreateAsync_ClienteCreadoEntreLaComprobacionYElGuardado_NoDuplica()
        {
            using var fixture = await CitaFixture.CreateAsync();

            // 1. El formulario comprueba: el teléfono NO existe.
            var resolucionInicial = await fixture.ClienteIdentityService.ResolveAsync("Cliente Nuevo", "87001122");
            Assert.Equal(ClienteIdentityStatus.NotFound, resolucionInicial.Status);

            // 2. Otra petición (otro contexto, misma base) registra ese mismo teléfono.
            var otroContexto = TestDbContextFactory.CreateSqliteContext(fixture.TenantProvider, fixture.Connection);
            await ControllerTestSupport.CreateClienteIdentityService(otroContexto).RegisterAsync(
                new ClienteRegistrationRequest(
                    "Cliente Nuevo",
                    "8700-1122",
                    AceptaMensajesWhatsApp: false,
                    ConsentSource: null,
                    ConsentCapturedAtUtc: null,
                    ConsentCapturedByUserId: null));

            // 3. La petición original guarda con la casilla marcada, basada en una lectura vieja.
            await fixture.Service.CreateAsync(BuildRequest(
                fixture,
                nombre: "Cliente Nuevo",
                telefono: "87001122",
                linkMode: ClienteLinkMode.Registrar));

            fixture.Context.ChangeTracker.Clear();

            // El backend re-resolvió al guardar: reutiliza el cliente en vez de duplicarlo.
            var cliente = await fixture.Context.Clientes.AsNoTracking().SingleAsync();
            var cita = await fixture.Context.Citas.AsNoTracking().SingleAsync();
            Assert.Equal(cliente.Id, cita.ClienteId);
        }

        [Fact]
        public async Task CreateAsync_RegistrandoClienteConConsentimiento_ConservaElPermisoDeWhatsApp()
        {
            using var fixture = await CitaFixture.CreateAsync();

            await fixture.Service.CreateAsync(new CalendarUpsertRequest
            {
                Tipo = "CITA",
                NombreCliente = "Cliente Autorizado",
                TelefonoCliente = "87001122",
                ClienteLinkMode = ClienteLinkMode.Registrar,
                ServicioId = fixture.ServicioId,
                FuncionarioId = fixture.FuncionarioId,
                FechaHoraCita = Horario,
                WhatsAppConsentAtCreation = true,
                WhatsAppConsentSource = "CitaManual",
                WhatsAppConsentCapturedAtUtc = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc),
                WhatsAppConsentCapturedByUserId = "user-1"
            });

            fixture.Context.ChangeTracker.Clear();

            // El consentimiento no se pierde al pasar de "cita suelta" a "cliente registrado":
            // con ClienteId, la política de envío solo mira el flag del cliente.
            var cliente = await fixture.Context.Clientes.AsNoTracking().SingleAsync();
            Assert.True(cliente.AceptaMensajesWhatsApp);

            var cita = await fixture.Context.Citas.AsNoTracking().SingleAsync();
            Assert.Equal(cliente.Id, cita.ClienteId);
            Assert.True(cita.WhatsAppConsentAtCreation);
        }

        [Fact]
        public async Task CreateAsync_DescansoNoTocaClientes()
        {
            using var fixture = await CitaFixture.CreateAsync();

            await fixture.Service.CreateAsync(new CalendarUpsertRequest
            {
                Tipo = "DESCANSO",
                FechaHoraCita = Horario,
                FuncionarioId = fixture.FuncionarioId,
                DuracionMinutos = 30,
                NombreCliente = "Cliente Nuevo",
                TelefonoCliente = "87001122",
                ClienteLinkMode = ClienteLinkMode.Registrar
            });

            fixture.Context.ChangeTracker.Clear();

            Assert.Empty(await fixture.Context.Clientes.AsNoTracking().ToListAsync());
            var cita = await fixture.Context.Citas.AsNoTracking().SingleAsync();
            Assert.Equal("DESCANSO", cita.NombreCliente);
            Assert.Null(cita.ClienteId);
        }

        [Fact]
        public async Task CreateAsync_ClienteIdDeOtroTenant_NoSeVincula()
        {
            using var fixture = await CitaFixture.CreateAsync();

            var otroProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var otroContexto = TestDbContextFactory.CreateSqliteContext(otroProvider, fixture.Connection);
            var ajeno = new ClientesModel
            {
                Nombre = "Cliente Ajeno",
                NumeroTelefono = "83334444",
                FrecuenciaVisita = 15,
                FechaUltimaVisita = new DateTime(2026, 9, 1)
            };
            otroContexto.Clientes.Add(ajeno);
            await otroContexto.SaveChangesAsync();

            var error = await Assert.ThrowsAsync<CalendarValidationException>(() =>
                fixture.Service.CreateAsync(new CalendarUpsertRequest
                {
                    Tipo = "CITA",
                    NombreCliente = "Cliente Ajeno",
                    TelefonoCliente = "83334444",
                    ClienteId = ajeno.Id,
                    ServicioId = fixture.ServicioId,
                    FuncionarioId = fixture.FuncionarioId,
                    FechaHoraCita = Horario
                }));

            Assert.Contains("no pertenece", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await fixture.Context.Citas.AsNoTracking().ToListAsync());
        }

        private static CalendarUpsertRequest BuildRequest(
            CitaFixture fixture,
            string? nombre,
            string? telefono,
            ClienteLinkMode linkMode) =>
            new()
            {
                Tipo = "CITA",
                NombreCliente = nombre,
                TelefonoCliente = telefono,
                ClienteLinkMode = linkMode,
                ServicioId = fixture.ServicioId,
                FuncionarioId = fixture.FuncionarioId,
                FechaHoraCita = Horario
            };

        internal sealed class CitaFixture : IDisposable
        {
            private CitaFixture(
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
                ClienteIdentityService = ControllerTestSupport.CreateClienteIdentityService(context);
                Service = ControllerTestSupport.CreateCalendarCommandService(
                    context,
                    clienteIdentityService: ClienteIdentityService);
            }

            public ProyectoIdentity.Datos.ApplicationDbContext Context { get; }
            public Microsoft.Data.Sqlite.SqliteConnection Connection { get; }
            public TestTenantProvider TenantProvider { get; }
            public Guid TenantId { get; }
            public int FuncionarioId { get; }
            public int ServicioId { get; }
            public IClienteIdentityService ClienteIdentityService { get; }
            public ICalendarCommandService Service { get; }

            public static async Task<CitaFixture> CreateAsync()
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

                var puesto = new Puesto
                {
                    NombrePuesto = $"Puesto {Guid.NewGuid():N}",
                    Detalle = "Calendario",
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
                    DuracionMinutos = 30,
                    Precio = 10000,
                    Activo = true
                };
                context.Servicios.Add(servicio);
                await context.SaveChangesAsync();
                context.ChangeTracker.Clear();

                return new CitaFixture(
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

            public void Dispose()
            {
                Context.Dispose();
                Connection.Dispose();
            }
        }
    }
}
