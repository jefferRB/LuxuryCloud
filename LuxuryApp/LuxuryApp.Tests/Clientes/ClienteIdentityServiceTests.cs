using LuxuryApp.Models.DataBase;
using LuxuryApp.Services.Clientes;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace LuxuryApp.Tests.Clientes
{
    /// <summary>
    /// Regla única de identidad de clientes. El teléfono normalizado identifica; el nombre no.
    /// </summary>
    public class ClienteIdentityServiceTests
    {
        [Theory]
        // Mismo número escrito de las formas en que la gente lo escribe de verdad.
        [InlineData("83334444")]
        [InlineData("8333-4444")]
        [InlineData("8333 4444")]
        [InlineData(" 8333.4444 ")]
        [InlineData("+506 8333-4444")]
        [InlineData("50683334444")]
        public async Task ResolveAsync_MismoTelefonoYMismoNombre_DevuelveCoincidenciaExacta(string telefonoEscrito)
        {
            using var fixture = await ClienteIdentityFixture.CreateAsync();
            var cliente = await fixture.SeedClienteAsync("Alex Example Rivera", "83334444");

            var resolucion = await fixture.Service.ResolveAsync("Alex Example Rivera", telefonoEscrito);

            Assert.Equal(ClienteIdentityStatus.ExistingExactMatch, resolucion.Status);
            Assert.Equal(cliente.Id, resolucion.SingleMatch?.ClienteId);
        }

        [Fact]
        public async Task ResolveAsync_MismoTelefonoNombreAbreviado_DevuelveCoincidenciaPorTelefono()
        {
            using var fixture = await ClienteIdentityFixture.CreateAsync();
            var cliente = await fixture.SeedClienteAsync("Alex Example Rivera", "8333-4444");

            var resolucion = await fixture.Service.ResolveAsync("Alex", "83334444");

            Assert.Equal(ClienteIdentityStatus.ExistingPhoneMatchWithDifferentName, resolucion.Status);
            Assert.Equal(cliente.Id, resolucion.SingleMatch?.ClienteId);
            // El dato canónico es el persistido: el nombre abreviado nunca reescribe el maestro.
            Assert.Equal("Alex Example Rivera", resolucion.SingleMatch?.Nombre);
        }

        [Fact]
        public async Task ResolveAsync_IgnoraTildesYMayusculasAlCompararNombres()
        {
            using var fixture = await ClienteIdentityFixture.CreateAsync();
            await fixture.SeedClienteAsync("José Pérez", "87770001");

            var resolucion = await fixture.Service.ResolveAsync("jose  perez", "8777-0001");

            Assert.Equal(ClienteIdentityStatus.ExistingExactMatch, resolucion.Status);
        }

        [Fact]
        public async Task ResolveAsync_MismoNombreTelefonoDistinto_NoAsumeQueEsLaMismaPersona()
        {
            using var fixture = await ClienteIdentityFixture.CreateAsync();
            await fixture.SeedClienteAsync("Maria Rodriguez", "88880000");

            var resolucion = await fixture.Service.ResolveAsync("Maria Rodriguez", "87651234");

            Assert.Equal(ClienteIdentityStatus.NotFound, resolucion.Status);
            Assert.Null(resolucion.SingleMatch);
        }

        [Fact]
        public async Task ResolveAsync_TelefonoNuevo_DevuelveNotFound()
        {
            using var fixture = await ClienteIdentityFixture.CreateAsync();
            await fixture.SeedClienteAsync("Cliente Existente", "88880000");

            var resolucion = await fixture.Service.ResolveAsync("Persona Nueva", "87001122");

            Assert.Equal(ClienteIdentityStatus.NotFound, resolucion.Status);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("sin numeros")]
        public async Task ResolveAsync_SinTelefonoUtilizable_DevuelveInsufficientData(string? telefono)
        {
            using var fixture = await ClienteIdentityFixture.CreateAsync();
            await fixture.SeedClienteAsync("Cliente Existente", "88880000");

            var resolucion = await fixture.Service.ResolveAsync("Cliente Existente", telefono);

            Assert.Equal(ClienteIdentityStatus.InsufficientData, resolucion.Status);
            Assert.Empty(resolucion.Matches);
        }

        [Fact]
        public async Task ResolveAsync_VariosClientesConElMismoTelefono_DevuelveAmbiguo()
        {
            using var fixture = await ClienteIdentityFixture.CreateAsync();
            var primero = await fixture.SeedClienteAsync("Ana Duplicada", "83334444");
            var segundo = await fixture.SeedClienteAsync("Ana D.", "8333-4444");

            var resolucion = await fixture.Service.ResolveAsync("Ana Duplicada", "83334444");

            Assert.Equal(ClienteIdentityStatus.AmbiguousPhoneMatch, resolucion.Status);
            Assert.Equal(2, resolucion.Matches.Count);
            // Jamás se elige uno arbitrariamente.
            Assert.Null(resolucion.SingleMatch);
            Assert.Contains(resolucion.Matches, m => m.ClienteId == primero.Id);
            Assert.Contains(resolucion.Matches, m => m.ClienteId == segundo.Id);
        }

        [Fact]
        public async Task ResolveAsync_NoVeClientesDeOtroTenant()
        {
            using var fixture = await ClienteIdentityFixture.CreateAsync();
            await fixture.SeedClienteAsync("Cliente Del Otro Negocio", "83334444");

            var otroTenant = fixture.SwitchToOtherTenant();

            var resolucion = await otroTenant.ResolveAsync("Cliente Del Otro Negocio", "83334444");

            Assert.Equal(ClienteIdentityStatus.NotFound, resolucion.Status);
        }

        [Fact]
        public async Task FindByIdAsync_NoDevuelveClientesDeOtroTenant()
        {
            using var fixture = await ClienteIdentityFixture.CreateAsync();
            var cliente = await fixture.SeedClienteAsync("Cliente Propio", "83334444");

            Assert.NotNull(await fixture.Service.FindByIdAsync(cliente.Id));

            var otroTenant = fixture.SwitchToOtherTenant();
            Assert.Null(await otroTenant.FindByIdAsync(cliente.Id));
        }

        [Fact]
        public async Task RegisterAsync_CreaClienteConLosMismosValoresQueElAltaManual()
        {
            using var fixture = await ClienteIdentityFixture.CreateAsync();

            var creado = await fixture.Service.RegisterAsync(new ClienteRegistrationRequest(
                "  Nuevo   Cliente ",
                " 8700-1122 ",
                AceptaMensajesWhatsApp: false,
                ConsentSource: null,
                ConsentCapturedAtUtc: null,
                ConsentCapturedByUserId: null));

            fixture.Context.ChangeTracker.Clear();
            var persistido = await fixture.Context.Clientes.AsNoTracking().SingleAsync(c => c.Id == creado.ClienteId);

            Assert.Equal("Nuevo   Cliente", persistido.Nombre);
            Assert.Equal("8700-1122", persistido.NumeroTelefono);
            Assert.Equal(15, persistido.FrecuenciaVisita);
            Assert.False(persistido.AceptaMensajesWhatsApp);
            Assert.Equal(fixture.TenantId, persistido.TenantId);

            // La visita la registra VisitasAutomaticasService cuando la cita termina, no el alta.
            Assert.Empty(await fixture.Context.ClienteVisitas.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task RegisterAsync_ConConsentimiento_GuardaLaAuditoriaDeWhatsApp()
        {
            using var fixture = await ClienteIdentityFixture.CreateAsync();
            var capturadoEn = new DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);

            var creado = await fixture.Service.RegisterAsync(new ClienteRegistrationRequest(
                "Cliente Autorizado",
                "87001122",
                AceptaMensajesWhatsApp: true,
                ConsentSource: "CitaManual",
                ConsentCapturedAtUtc: capturadoEn,
                ConsentCapturedByUserId: "user-1"));

            fixture.Context.ChangeTracker.Clear();
            var persistido = await fixture.Context.Clientes.AsNoTracking().SingleAsync(c => c.Id == creado.ClienteId);

            Assert.True(persistido.AceptaMensajesWhatsApp);
            Assert.Equal("CitaManual", persistido.WhatsAppConsentSource);
            Assert.Equal(capturadoEn, persistido.WhatsAppConsentUpdatedAtUtc);
            Assert.Equal("user-1", persistido.WhatsAppConsentCapturedByUserId);
            Assert.Equal("wa_optin_v1", persistido.WhatsAppConsentTextVersion);
        }

        [Fact]
        public async Task RegisterAsync_SinTelefono_NoCreaNada()
        {
            using var fixture = await ClienteIdentityFixture.CreateAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Service.RegisterAsync(new ClienteRegistrationRequest(
                    "Cliente Sin Telefono",
                    "   ",
                    AceptaMensajesWhatsApp: false,
                    ConsentSource: null,
                    ConsentCapturedAtUtc: null,
                    ConsentCapturedByUserId: null)));

            Assert.Empty(await fixture.Context.Clientes.AsNoTracking().ToListAsync());
        }

        private sealed class ClienteIdentityFixture : IDisposable
        {
            private ClienteIdentityFixture(
                ProyectoIdentity.Datos.ApplicationDbContext context,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                TestTenantProvider tenantProvider)
            {
                Context = context;
                Connection = connection;
                TenantProvider = tenantProvider;
                TenantId = tenantProvider.TenantId;
                Service = ControllerTestSupport.CreateClienteIdentityService(context);
            }

            public ProyectoIdentity.Datos.ApplicationDbContext Context { get; }
            public Microsoft.Data.Sqlite.SqliteConnection Connection { get; }
            public TestTenantProvider TenantProvider { get; }
            public Guid TenantId { get; }
            public IClienteIdentityService Service { get; }

            public static Task<ClienteIdentityFixture> CreateAsync()
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
                return Task.FromResult(new ClienteIdentityFixture(context, connection, tenantProvider));
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

            /// <summary>Mismo almacenamiento, otro negocio: comprueba el aislamiento real.</summary>
            public IClienteIdentityService SwitchToOtherTenant()
            {
                var otro = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var otroContexto = TestDbContextFactory.CreateSqliteContext(otro, Connection);
                return ControllerTestSupport.CreateClienteIdentityService(otroContexto);
            }

            public void Dispose()
            {
                Context.Dispose();
                Connection.Dispose();
            }
        }
    }
}
