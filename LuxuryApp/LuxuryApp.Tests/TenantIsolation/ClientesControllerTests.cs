using LuxuryApp.Controllers.DataBase;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class ClientesControllerTests
    {
        [Fact]
        public async Task Create_ShouldPersistClientWithOptionalEmail_AndAllowSamePhoneAcrossTenants()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantB };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Clientes.Add(new ClientesModel
            {
                Nombre = "Cliente Tenant B",
                NumeroTelefono = "88889999",
                CorreoElectronico = "tenantb@test.local",
                FrecuenciaVisita = 30,
                FechaUltimaVisita = new DateTime(2026, 4, 1)
            });
            await context.SaveChangesAsync();

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            var controller = CreateController(context, tenantA);

            var result = await controller.Create(new ClientesModel
            {
                Nombre = "Cliente Tenant A",
                NumeroTelefono = "88889999",
                CorreoElectronico = string.Empty,
                FrecuenciaVisita = 15,
                FechaUltimaVisita = new DateTime(2026, 4, 13)
            });

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(ClientesController.Index), redirect.ActionName);

            var clienteActual = await context.Clientes.SingleAsync(c => c.Nombre == "Cliente Tenant A");
            Assert.Null(clienteActual.CorreoElectronico);
            Assert.False(clienteActual.AceptaMensajesWhatsApp);
            Assert.NotNull(clienteActual.WhatsAppConsentUpdatedAtUtc);
            Assert.Equal("ClienteForm", clienteActual.WhatsAppConsentSource);
            Assert.Equal("wa_optin_v1", clienteActual.WhatsAppConsentTextVersion);
            Assert.Equal("user-clientes", clienteActual.WhatsAppConsentCapturedByUserId);

            // Dar de alta un cliente NO es atenderlo: el alta no registra ninguna visita.
            // La visita la produce la atención real (un cobro o el cierre de la cita).
            Assert.Empty(await context.ClienteVisitas
                .AsNoTracking()
                .Where(v => v.ClienteId == clienteActual.Id)
                .ToListAsync());

            var totalConMismoTelefono = await context.Clientes
                .IgnoreQueryFilters()
                .CountAsync(c => c.NumeroTelefono == "88889999");

            Assert.Equal(2, totalConMismoTelefono);
        }

        [Fact]
        public async Task Create_ShouldPersistWhatsAppConsent_WhenChecked()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var controller = CreateController(context, tenantId);

            var result = await controller.Create(new ClientesModel
            {
                Nombre = "Cliente Opt In",
                NumeroTelefono = "75550000",
                CorreoElectronico = "optin@test.local",
                AceptaMensajesWhatsApp = true,
                FrecuenciaVisita = 20,
                FechaUltimaVisita = new DateTime(2026, 4, 18)
            });

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(ClientesController.Index), redirect.ActionName);

            var cliente = await context.Clientes.SingleAsync(c => c.NumeroTelefono == "75550000");
            Assert.True(cliente.AceptaMensajesWhatsApp);
            Assert.NotNull(cliente.WhatsAppConsentUpdatedAtUtc);
            Assert.Equal("ClienteForm", cliente.WhatsAppConsentSource);
            Assert.Equal("wa_optin_v1", cliente.WhatsAppConsentTextVersion);
            Assert.Equal("user-clientes", cliente.WhatsAppConsentCapturedByUserId);
        }

        [Fact]
        public async Task Create_Get_ShouldExposeTenantWhatsAppFlag_WhenFeatureIsDisabled()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var controller = CreateController(context, tenantId, tenantWhatsAppEnabled: false);

            var result = await controller.Create(CancellationToken.None);

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<ClientesModel>(view.Model);
            Assert.False((bool?)view.ViewData["TenantWhatsAppEnabled"] ?? true);
            Assert.False(model.AceptaMensajesWhatsApp);
        }

        [Fact]
        public async Task Create_ShouldForceConsentFalse_WhenTenantWhatsAppIsDisabled()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var controller = CreateController(context, tenantId, tenantWhatsAppEnabled: false);

            var result = await controller.Create(new ClientesModel
            {
                Nombre = "Cliente Sin Feature",
                NumeroTelefono = "76660000",
                CorreoElectronico = "nofeature@test.local",
                AceptaMensajesWhatsApp = true,
                FrecuenciaVisita = 20,
                FechaUltimaVisita = new DateTime(2026, 4, 20)
            });

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(ClientesController.Index), redirect.ActionName);

            var cliente = await context.Clientes.SingleAsync(c => c.NumeroTelefono == "76660000");
            Assert.False(cliente.AceptaMensajesWhatsApp);
            Assert.Null(cliente.WhatsAppConsentUpdatedAtUtc);
            Assert.Null(cliente.WhatsAppConsentSource);
            Assert.Null(cliente.WhatsAppConsentCapturedByUserId);
            Assert.Null(cliente.WhatsAppConsentTextVersion);
        }

        [Fact]
        public async Task Editar_ShouldAllowPhoneChange_AndUpdateRelatedVisitRows()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var cliente = new ClientesModel
            {
                Nombre = "Cliente Demo",
                NumeroTelefono = "70001111",
                CorreoElectronico = "demo@test.local",
                FrecuenciaVisita = 30,
                FechaUltimaVisita = new DateTime(2026, 4, 1)
            };

            context.Clientes.Add(cliente);
            await context.SaveChangesAsync();

            context.ClienteVisitas.Add(new ClienteVisitas
            {
                ClienteId = cliente.Id,
                NumeroTelefono = cliente.NumeroTelefono,
                FechaVisita = cliente.FechaUltimaVisita
            });

            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var controller = CreateController(context, tenantId);

            var result = await controller.Editar(new ClientesModel
            {
                Id = cliente.Id,
                Nombre = "Cliente Demo",
                NumeroTelefono = "79992222",
                CorreoElectronico = string.Empty,
                FrecuenciaVisita = 30,
                FechaUltimaVisita = new DateTime(2026, 4, 1)
            });

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(ClientesController.Buscar), redirect.ActionName);
            Assert.Equal("79992222", redirect.RouteValues?["criterio"]);

            var clienteActualizado = await context.Clientes.SingleAsync(c => c.Id == cliente.Id);
            Assert.Equal("79992222", clienteActualizado.NumeroTelefono);
            Assert.Null(clienteActualizado.CorreoElectronico);

            var visitas = await context.ClienteVisitas
                .Where(v => v.ClienteId == cliente.Id)
                .OrderBy(v => v.Id)
                .ToListAsync();

            Assert.All(visitas, visita => Assert.Equal("79992222", visita.NumeroTelefono));
        }

        [Fact]
        public async Task Editar_ShouldAllowRevokingWhatsAppConsent_AndRefreshAudit()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var cliente = new ClientesModel
            {
                Nombre = "Cliente Consentimiento",
                NumeroTelefono = "71112222",
                CorreoElectronico = "consent@test.local",
                AceptaMensajesWhatsApp = true,
                WhatsAppConsentUpdatedAtUtc = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc),
                WhatsAppConsentSource = "ClienteForm",
                WhatsAppConsentCapturedByUserId = "seed-user",
                WhatsAppConsentTextVersion = "wa_optin_v1",
                FrecuenciaVisita = 30,
                FechaUltimaVisita = new DateTime(2026, 4, 1)
            };

            context.Clientes.Add(cliente);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var controller = CreateController(context, tenantId);

            var result = await controller.Editar(new ClientesModel
            {
                Id = cliente.Id,
                Nombre = "Cliente Consentimiento",
                NumeroTelefono = "71112222",
                CorreoElectronico = "consent@test.local",
                AceptaMensajesWhatsApp = false,
                FrecuenciaVisita = 30,
                FechaUltimaVisita = new DateTime(2026, 4, 1)
            });

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(ClientesController.Buscar), redirect.ActionName);

            var clienteActualizado = await context.Clientes.SingleAsync(c => c.Id == cliente.Id);
            Assert.False(clienteActualizado.AceptaMensajesWhatsApp);
            Assert.NotNull(clienteActualizado.WhatsAppConsentUpdatedAtUtc);
            Assert.True(clienteActualizado.WhatsAppConsentUpdatedAtUtc > new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc));
            Assert.Equal("ClienteForm", clienteActualizado.WhatsAppConsentSource);
            Assert.Equal("wa_optin_v1", clienteActualizado.WhatsAppConsentTextVersion);
            Assert.Equal("user-clientes", clienteActualizado.WhatsAppConsentCapturedByUserId);
        }

        [Fact]
        public async Task Editar_Get_ShouldExposeTenantWhatsAppFlag_WhenFeatureIsDisabled()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var cliente = new ClientesModel
            {
                Nombre = "Cliente Editar",
                NumeroTelefono = "73334444",
                AceptaMensajesWhatsApp = true,
                FrecuenciaVisita = 30,
                FechaUltimaVisita = new DateTime(2026, 4, 1)
            };

            context.Clientes.Add(cliente);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var controller = CreateController(context, tenantId, tenantWhatsAppEnabled: false);

            var result = await controller.Editar(cliente.Id);

            var view = Assert.IsType<ViewResult>(result);
            Assert.False((bool?)view.ViewData["TenantWhatsAppEnabled"] ?? true);
            var model = Assert.IsType<ClientesModel>(view.Model);
            Assert.True(model.AceptaMensajesWhatsApp);
        }

        [Fact]
        public async Task Editar_ShouldPreserveExistingConsent_WhenTenantWhatsAppIsDisabled()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var originalAuditDate = new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);
            var cliente = new ClientesModel
            {
                Nombre = "Cliente Preservado",
                NumeroTelefono = "74446666",
                CorreoElectronico = "preservado@test.local",
                AceptaMensajesWhatsApp = true,
                WhatsAppConsentUpdatedAtUtc = originalAuditDate,
                WhatsAppConsentSource = "ClienteForm",
                WhatsAppConsentCapturedByUserId = "seed-user",
                WhatsAppConsentTextVersion = "wa_optin_v1",
                FrecuenciaVisita = 30,
                FechaUltimaVisita = new DateTime(2026, 4, 1)
            };

            context.Clientes.Add(cliente);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var controller = CreateController(context, tenantId, tenantWhatsAppEnabled: false);

            var result = await controller.Editar(new ClientesModel
            {
                Id = cliente.Id,
                Nombre = "Cliente Preservado",
                NumeroTelefono = "74446666",
                CorreoElectronico = "nuevo@test.local",
                AceptaMensajesWhatsApp = false,
                FrecuenciaVisita = 45,
                FechaUltimaVisita = new DateTime(2026, 4, 10)
            });

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(ClientesController.Buscar), redirect.ActionName);

            var clienteActualizado = await context.Clientes.SingleAsync(c => c.Id == cliente.Id);
            Assert.True(clienteActualizado.AceptaMensajesWhatsApp);
            Assert.Equal(originalAuditDate, clienteActualizado.WhatsAppConsentUpdatedAtUtc);
            Assert.Equal("ClienteForm", clienteActualizado.WhatsAppConsentSource);
            Assert.Equal("seed-user", clienteActualizado.WhatsAppConsentCapturedByUserId);
            Assert.Equal("wa_optin_v1", clienteActualizado.WhatsAppConsentTextVersion);
            Assert.Equal("nuevo@test.local", clienteActualizado.CorreoElectronico);

            // La frecuencia es DERIVADA del historial de citas: aunque el POST traiga 45, la
            // columna legacy no se toca. Nadie puede alterar las métricas CRM desde el formulario.
            Assert.Equal(30, clienteActualizado.FrecuenciaVisita);
            Assert.Equal(new DateTime(2026, 4, 1), clienteActualizado.FechaUltimaVisita);
        }

        [Fact]
        public async Task Index_ShouldReturnPagedOrderedProjection()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Clientes.AddRange(
                new ClientesModel
                {
                    Nombre = "Zoe",
                    NumeroTelefono = "1001",
                    FechaUltimaVisita = new DateTime(2026, 4, 1),
                    FrecuenciaVisita = 30
                },
                new ClientesModel
                {
                    Nombre = "Ana",
                    NumeroTelefono = "1002",
                    FechaUltimaVisita = new DateTime(2026, 4, 2),
                    FrecuenciaVisita = 30
                },
                new ClientesModel
                {
                    Nombre = "Luis",
                    NumeroTelefono = "1003",
                    FechaUltimaVisita = new DateTime(2026, 4, 3),
                    FrecuenciaVisita = 30
                });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var controller = CreateController(context, tenantId);

            var result = await controller.Index(pageNumber: 2, pageSize: 2);

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<ClientesIndexViewModel>(view.Model);

            Assert.Equal(3, model.TotalCount);
            Assert.Equal(2, model.PageNumber);
            Assert.Equal(2, model.PageSize);
            Assert.Equal(2, model.TotalPages);

            var cliente = Assert.Single(model.Clientes);
            Assert.Equal("Zoe", cliente.Nombre);
            Assert.Equal("1001", cliente.NumeroTelefono);
        }

        [Fact]
        public async Task RegistrarServicios_ShouldPersistDescriptionOnly()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var cliente = new ClientesModel
            {
                Nombre = "Cliente Servicios",
                NumeroTelefono = "74445555",
                FechaUltimaVisita = new DateTime(2026, 4, 10),
                FrecuenciaVisita = 20
            };

            context.Clientes.Add(cliente);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var controller = CreateController(context, tenantId);

            var result = await controller.RegistrarServicios(new ServicioRealizadoViewModel
            {
                ClienteId = cliente.Id,
                DescripcionServicios = "Corte premium y perfilado"
            });

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(ClientesController.Buscar), redirect.ActionName);
            Assert.Equal("74445555", redirect.RouteValues?["criterio"]);

            var clienteActualizado = await context.Clientes.SingleAsync(c => c.Id == cliente.Id);
            Assert.Equal("Corte premium y perfilado", clienteActualizado.DescripcionServiciosRealizados);
        }

        [Fact]
        public async Task Buscar_ShouldPrioritizeExactPhoneMatch_WhenCriteriaLooksLikePhone()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Clientes.AddRange(
                new ClientesModel
                {
                    Nombre = "Ana Uno",
                    NumeroTelefono = "88889999",
                    FechaUltimaVisita = new DateTime(2026, 4, 1),
                    FrecuenciaVisita = 30
                },
                new ClientesModel
                {
                    Nombre = "Ana Dos",
                    NumeroTelefono = "77776666",
                    FechaUltimaVisita = new DateTime(2026, 4, 2),
                    FrecuenciaVisita = 30
                });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var controller = CreateController(context, tenantId);

            var result = await controller.Buscar("8888-9999");

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<BuscarClienteViewModel>(view.Model);

            Assert.True(model.EsBusquedaTelefonica);
            Assert.Single(model.ClientesEncontrados);
            Assert.NotNull(model.ClienteSeleccionado);
            Assert.Equal("88889999", model.ClienteSeleccionado!.NumeroTelefono);
        }

        [Fact]
        public async Task Buscar_ShouldLimitNameResults_AndRequireRefinement()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var clientes = Enumerable.Range(1, 51)
                .Select(index => new ClientesModel
                {
                    Nombre = $"Ana {index:00}",
                    NumeroTelefono = $"8000{index:0000}",
                    FechaUltimaVisita = new DateTime(2026, 4, 1),
                    FrecuenciaVisita = 30
                });

            context.Clientes.AddRange(clientes);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var controller = CreateController(context, tenantId);

            var result = await controller.Buscar("Ana");

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<BuscarClienteViewModel>(view.Model);

            Assert.False(model.EsBusquedaTelefonica);
            Assert.True(model.ResultadosLimitados);
            Assert.Equal(50, model.ClientesEncontrados.Count);
            Assert.Contains("refina la búsqueda", model.Mensaje, StringComparison.OrdinalIgnoreCase);
            Assert.Null(model.ClienteSeleccionado);
        }

        [Fact]
        public async Task Autocompletado_ShouldReturnEmpty_WhenTermIsBelowMinimumLength()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var controller = CreateController(context, tenantId);

            var result = await controller.Autocompletado("An");

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
            Assert.Empty(payload);
        }

        [Fact]
        public async Task Autocompletado_ShouldReturnMatches_WhenTermMeetsMinimumLength()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Clientes.Add(new ClientesModel
            {
                Nombre = "Ana Rojas",
                NumeroTelefono = "88889999",
                FechaUltimaVisita = new DateTime(2026, 4, 1),
                FrecuenciaVisita = 30
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var controller = CreateController(context, tenantId);

            var result = await controller.Autocompletado("Ana");

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
            var serialized = System.Text.Json.JsonSerializer.Serialize(payload);

            Assert.Contains("Ana Rojas", serialized, StringComparison.Ordinal);
            Assert.Contains("88889999", serialized, StringComparison.Ordinal);
            Assert.Contains("aceptaMensajesWhatsApp", serialized, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Autocompletado_ShouldHideConsentState_WhenTenantWhatsAppIsDisabled()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Clientes.Add(new ClientesModel
            {
                Nombre = "Cliente Oculto",
                NumeroTelefono = "89997777",
                AceptaMensajesWhatsApp = true,
                FechaUltimaVisita = new DateTime(2026, 4, 1),
                FrecuenciaVisita = 30
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var controller = CreateController(context, tenantId, tenantWhatsAppEnabled: false);

            var result = await controller.Autocompletado("Cli");

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
            var serialized = System.Text.Json.JsonSerializer.Serialize(payload);

            Assert.Contains("\"aceptaMensajesWhatsApp\":false", serialized, StringComparison.OrdinalIgnoreCase);
        }

        // ── CREAR CLIENTE: frecuencia inicial ─────────────────────────────────

        [Fact]
        public async Task Create_Get_ShouldPrefillInitialFrequencyFromServer()
        {
            // El default no puede vivir solo en el value del HTML: el alta automática desde
            // calendario y reservas no pasa por esta vista.
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var controller = CreateController(context, tenantId);

            var result = await controller.Create(CancellationToken.None);

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<ClientesModel>(view.Model);
            Assert.Equal(ClienteDefaults.InitialVisitFrequencyDays, model.FrecuenciaVisita);
        }

        [Fact]
        public async Task Create_ShouldSucceedWithDefaultInitialFrequency()
        {
            // REGRESIÓN: al quitar el input de frecuencia, FrecuenciaVisita llegaba en 0, el
            // [Range(1, ...)] del modelo invalidaba el ModelState y crear clientes dejó de
            // funcionar mostrando un recuadro rojo sin texto.
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var controller = CreateController(context, tenantId);

            var result = await controller.Create(new ClientesModel
            {
                Nombre = "Cliente Default",
                NumeroTelefono = "70010001",
                FrecuenciaVisita = ClienteDefaults.InitialVisitFrequencyDays
            });

            Assert.IsType<RedirectToActionResult>(result);
            Assert.True(controller.ModelState.IsValid);

            var cliente = await context.Clientes.SingleAsync(c => c.NumeroTelefono == "70010001");
            Assert.Equal(ClienteDefaults.InitialVisitFrequencyDays, cliente.FrecuenciaVisita);
        }

        [Fact]
        public async Task Create_ShouldPersistCustomInitialFrequency()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var controller = CreateController(context, tenantId);

            var result = await controller.Create(new ClientesModel
            {
                Nombre = "Cliente Custom",
                NumeroTelefono = "70010002",
                FrecuenciaVisita = 21
            });

            Assert.IsType<RedirectToActionResult>(result);

            var cliente = await context.Clientes.SingleAsync(c => c.NumeroTelefono == "70010002");
            Assert.Equal(21, cliente.FrecuenciaVisita);
        }

        [Fact]
        public async Task Create_ShouldReportReadableError_WhenInitialFrequencyIsInvalid()
        {
            // Lo que importa no es solo que falle: es que el motivo llegue con texto.
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var controller = CreateController(context, tenantId);
            var cliente = new ClientesModel
            {
                Nombre = "Cliente Invalido",
                NumeroTelefono = "70010003",
                FrecuenciaVisita = 0
            };

            // El binder valida antes de entrar a la acción; en el test se reproduce igual.
            controller.ModelState.AddModelError(
                nameof(ClientesModel.FrecuenciaVisita),
                "La frecuencia de visita debe ser mayor a cero.");

            var result = await controller.Create(cliente);

            Assert.IsType<ViewResult>(result);
            Assert.False(controller.ModelState.IsValid);

            var mensajes = controller.ModelState
                .SelectMany(entry => entry.Value!.Errors)
                .Select(error => error.ErrorMessage)
                .ToList();

            Assert.NotEmpty(mensajes);
            Assert.All(mensajes, mensaje => Assert.False(string.IsNullOrWhiteSpace(mensaje)));
            Assert.DoesNotContain(await context.Clientes.ToListAsync(), c => c.NumeroTelefono == "70010003");
        }

        [Fact]
        public async Task Create_ShouldIgnorePostedDerivedLastVisit()
        {
            // FechaUltimaVisita es derivada: aunque el POST traiga una fecha, la columna legacy
            // se escribe desde el servidor y nunca desde el formulario.
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var controller = CreateController(context, tenantId);

            await controller.Create(new ClientesModel
            {
                Nombre = "Cliente Overpost",
                NumeroTelefono = "70010004",
                FrecuenciaVisita = 15,
                FechaUltimaVisita = new DateTime(2030, 1, 1)
            });

            var cliente = await context.Clientes.SingleAsync(c => c.NumeroTelefono == "70010004");
            Assert.NotEqual(new DateTime(2030, 1, 1), cliente.FechaUltimaVisita);
            Assert.Equal(ControllerTestSupport.BusinessDateTimeProvider.Today(), cliente.FechaUltimaVisita);
        }

        private static ClientesController CreateController(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId,
            bool tenantWhatsAppEnabled = true)
        {
            var controller = new ClientesController(
                context,
                ControllerTestSupport.BusinessDateTimeProvider,
                new FakeTenantWhatsAppFeatureService
                {
                    IsEnabled = tenantWhatsAppEnabled
                },
                ControllerTestSupport.CreateClienteIdentityService(context),
                ControllerTestSupport.CreateClienteVisitMetricsService(context),
                ControllerTestSupport.CreateClienteNotasService(context),
                NullLogger<ClientesController>.Instance);

            ControllerTestSupport.AttachHttpContext(
                controller,
                ControllerTestSupport.BuildTenantPrincipal("user-clientes", tenantId));

            return controller;
        }
    }
}
