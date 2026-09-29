using ClosedXML.Excel;
using LuxuryApp.Controllers;
using LuxuryApp.Controllers.DataBase;
using LuxuryApp.Controllers.Finanzas;
using LuxuryApp.Controllers.Funcionarios;
using LuxuryApp.Controllers.Identity;
using LuxuryApp.Models.Common;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services;
using Microsoft.AspNetCore.Http;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class EndpointBindingSecurityTests
    {
        [Fact]
        public async Task CategoriaCreate_ShouldIgnoreTenantIdOverpostingAndForceInternalState()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            tenantProvider.TenantId = tenantA;

            var controller = new CategoriasController(context, NullLogger<CategoriasController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };

            var maliciousPayload = new Categoria
            {
                TenantId = tenantB,
                Nombre = "Categoria segura",
                Detalle = "Payload manipulado",
                Activo = false
            };

            var result = await controller.Create(maliciousPayload);

            Assert.IsType<OkResult>(result);

            var persisted = context.Categorias.Single();
            Assert.Equal(tenantA, persisted.TenantId);
            Assert.True(persisted.Activo);
        }

        [Fact]
        public async Task SecondaryJsonEndpoint_ShouldNotExposeOtherTenantRecords()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();

            tenantProvider.TenantId = tenantB;
            context.Servicios.Add(new Servicio
            {
                Nombre = "Servicio privado",
                Precio = 50,
                DuracionMinutos = 60,
                Activo = true
            });
            await context.SaveChangesAsync();

            var foreignServiceId = context.Servicios.Single().Id;

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            var controller = new ServiciosController(context, NullLogger<ServiciosController>.Instance, new LuxuryApp.Services.Finanzas.LegacyFinancialImpactService(context));
            var result = await controller.ObtenerPrecio(foreignServiceId);

            Assert.IsType<JsonResult>(result);
            Assert.Null(result.Value);
        }

        [Fact]
        public async Task ClientesAutocomplete_ShouldOnlyReturnCurrentTenantMatches()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();

            tenantProvider.TenantId = tenantB;
            context.Clientes.Add(new ClientesModel
            {
                Nombre = "Cliente Privado",
                NumeroTelefono = "2222",
                CorreoElectronico = "private@test.local",
                FechaUltimaVisita = DateTime.UtcNow,
                FrecuenciaVisita = 30
            });
            await context.SaveChangesAsync();

            tenantProvider.TenantId = tenantA;
            context.Clientes.Add(new ClientesModel
            {
                Nombre = "Cliente Publico",
                NumeroTelefono = "1111",
                CorreoElectronico = "public@test.local",
                FechaUltimaVisita = DateTime.UtcNow,
                FrecuenciaVisita = 30
            });
            await context.SaveChangesAsync();

            context.ChangeTracker.Clear();

            var controller = new ClientesController(
                context,
                ControllerTestSupport.BusinessDateTimeProvider,
                new FakeTenantWhatsAppFeatureService
                {
                    IsEnabled = true
                },
                ControllerTestSupport.CreateClienteIdentityService(context),
                ControllerTestSupport.CreateClienteVisitMetricsService(context),
                ControllerTestSupport.CreateClienteNotasService(context),
                NullLogger<ClientesController>.Instance);
            var result = await controller.Autocompletado("Cliente");

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
            var serialized = System.Text.Json.JsonSerializer.Serialize(payload);

            Assert.Contains("Cliente Publico", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("Cliente Privado", serialized, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ClientesAutocomplete_ByPhoneFragment_ShouldOnlyReturnCurrentTenantMatches()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();

            tenantProvider.TenantId = tenantB;
            context.Clientes.Add(new ClientesModel
            {
                Nombre = "Cliente Privado Telefono",
                NumeroTelefono = "55119999",
                CorreoElectronico = "private-phone@test.local",
                FechaUltimaVisita = DateTime.UtcNow,
                FrecuenciaVisita = 30
            });
            await context.SaveChangesAsync();

            tenantProvider.TenantId = tenantA;
            context.Clientes.Add(new ClientesModel
            {
                Nombre = "Cliente Visible Telefono",
                NumeroTelefono = "55110000",
                CorreoElectronico = "public-phone@test.local",
                FechaUltimaVisita = DateTime.UtcNow,
                FrecuenciaVisita = 30
            });
            await context.SaveChangesAsync();

            context.ChangeTracker.Clear();

            var controller = new ClientesController(
                context,
                ControllerTestSupport.BusinessDateTimeProvider,
                new FakeTenantWhatsAppFeatureService
                {
                    IsEnabled = true
                },
                ControllerTestSupport.CreateClienteIdentityService(context),
                ControllerTestSupport.CreateClienteVisitMetricsService(context),
                ControllerTestSupport.CreateClienteNotasService(context),
                NullLogger<ClientesController>.Instance);
            var result = await controller.Autocompletado("5511");

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = Assert.IsAssignableFrom<IEnumerable<object>>(ok.Value);
            var serialized = System.Text.Json.JsonSerializer.Serialize(payload);

            Assert.Contains("Cliente Visible Telefono", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("Cliente Privado Telefono", serialized, StringComparison.Ordinal);
            Assert.Contains("55110000", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("55119999", serialized, StringComparison.Ordinal);
        }

        [Fact]
        public async Task FuncionariosGetActivos_ShouldOnlyReturnCurrentTenantRecords()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();

            tenantProvider.TenantId = tenantB;
            context.Puestos.Add(new Puesto
            {
                NombrePuesto = "Puesto Privado",
                Activo = true
            });
            await context.SaveChangesAsync();
            var foreignPuestoId = context.Puestos.Single().IdPuesto;

            context.Funcionarios.Add(new Funcionario
            {
                Nombre = "Funcionario Privado",
                IdPuesto = foreignPuestoId,
                ColorCalendario = "#111111",
                PorcentajeGanancia = 10,
                PorcentajeProducto = 10,
                FechaIngreso = DateTime.UtcNow,
                Activo = true
            });
            await context.SaveChangesAsync();

            tenantProvider.TenantId = tenantA;
            context.Puestos.Add(new Puesto
            {
                NombrePuesto = "Puesto Visible",
                Activo = true
            });
            await context.SaveChangesAsync();
            var currentPuestoId = context.Puestos
                .Single(puesto => puesto.NombrePuesto == "Puesto Visible").IdPuesto;

            context.Funcionarios.Add(new Funcionario
            {
                Nombre = "Funcionario Visible",
                IdPuesto = currentPuestoId,
                ColorCalendario = "#222222",
                PorcentajeGanancia = 15,
                PorcentajeProducto = 15,
                FechaIngreso = DateTime.UtcNow,
                Activo = true
            });
            await context.SaveChangesAsync();

            context.ChangeTracker.Clear();

            var controller = new FuncionariosController(
                context,
                ControllerTestSupport.CreateLiquidacionSemanalService(context, tenantProvider),
                ControllerTestSupport.CreateFuncionarioPortalAccessService(),
                ControllerTestSupport.CreateFuncionarioPortalPermissionService(),
                ControllerTestSupport.CreateAccountEmailService(),
                ControllerTestSupport.BusinessDateTimeProvider,
                ControllerTestSupport.CreateTenantDisplayNameService(),
                ControllerTestSupport.CreateFuncionarioPhotoStorageService(),
                tenantProvider,
                NullLogger<FuncionariosController>.Instance,
                new LuxuryApp.Services.Finanzas.LegacyFinancialImpactService(context));
            var result = await controller.GetActivos();

            var json = Assert.IsType<JsonResult>(result);
            var serialized = System.Text.Json.JsonSerializer.Serialize(json.Value);

            Assert.Contains("Funcionario Visible", serialized, StringComparison.Ordinal);
            Assert.DoesNotContain("Funcionario Privado", serialized, StringComparison.Ordinal);
        }

        [Fact]
        public async Task CobrosExportarExcel_ShouldExcludeForeignTenantRows()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();

            tenantProvider.TenantId = tenantB;
            context.Puestos.Add(new Puesto
            {
                NombrePuesto = "Puesto Privado",
                Activo = true
            });
            await context.SaveChangesAsync();
            var foreignPuestoId = context.Puestos.Single().IdPuesto;

            var foreignFuncionario = new Funcionario
            {
                Nombre = "Funcionario Privado",
                IdPuesto = foreignPuestoId,
                ColorCalendario = "#111111",
                PorcentajeGanancia = 10,
                PorcentajeProducto = 10,
                FechaIngreso = DateTime.UtcNow,
                Activo = true
            };
            context.Funcionarios.Add(foreignFuncionario);
            await context.SaveChangesAsync();

            context.Cobros.Add(new Cobro
            {
                NombreCliente = "Cliente Privado",
                FuncionarioId = foreignFuncionario.IdFuncionario,
                FechaCobro = ControllerTestSupport.BusinessDateTimeProvider.Today(),
                Monto = 1250,
                MetodoPago = "EFECTIVO"
            });
            await context.SaveChangesAsync();

            tenantProvider.TenantId = tenantA;
            context.Puestos.Add(new Puesto
            {
                NombrePuesto = "Puesto Visible",
                Activo = true
            });
            await context.SaveChangesAsync();
            var currentPuestoId = context.Puestos
                .Single(puesto => puesto.NombrePuesto == "Puesto Visible").IdPuesto;

            var currentFuncionario = new Funcionario
            {
                Nombre = "Funcionario Visible",
                IdPuesto = currentPuestoId,
                ColorCalendario = "#222222",
                PorcentajeGanancia = 12,
                PorcentajeProducto = 12,
                FechaIngreso = DateTime.UtcNow,
                Activo = true
            };
            context.Funcionarios.Add(currentFuncionario);
            await context.SaveChangesAsync();

            context.Cobros.Add(new Cobro
            {
                NombreCliente = "Cliente Visible",
                FuncionarioId = currentFuncionario.IdFuncionario,
                FechaCobro = ControllerTestSupport.BusinessDateTimeProvider.Today(),
                Monto = 1500,
                MetodoPago = "SINPE"
            });
            await context.SaveChangesAsync();

            context.ChangeTracker.Clear();

            var controller = new CobrosController(
                ControllerTestSupport.CreateCobroService(context),
                ControllerTestSupport.CreateCobroQueryService(context, tenantProvider),
                ControllerTestSupport.CreateComprobanteCobroService(),
                ControllerTestSupport.BusinessDateTimeProvider,
                ControllerTestSupport.CreateTenantDisplayNameService());
            ControllerTestSupport.AttachHttpContext(controller);
            var result = await controller.ExportarExcel(new CobroFiltroViewModel { VistaTiempo = "todo" });

            var file = Assert.IsType<FileContentResult>(result);
            using var workbook = new XLWorkbook(new MemoryStream(file.FileContents));
            var text = string.Join(
                "|",
                workbook.Worksheets.SelectMany(sheet => sheet.CellsUsed().Select(cell => cell.GetString())));

            Assert.Contains("Cliente Visible", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Cliente Privado", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task EgresosExportarExcel_ShouldExcludeForeignTenantRows()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();

            tenantProvider.TenantId = tenantB;
            context.Categorias.Add(new Categoria
            {
                Nombre = "Categoria Privada",
                Detalle = "B",
                Activo = true
            });
            await context.SaveChangesAsync();
            var foreignCategoryId = context.Categorias.Single().Id;

            context.Egresos.Add(new Egreso
            {
                CategoriaId = foreignCategoryId,
                FechaEgreso = ControllerTestSupport.BusinessDateTimeProvider.Today(),
                Detalle = "Egreso Privado",
                Monto = 500,
                MetodoPago = "EFECTIVO"
            });
            await context.SaveChangesAsync();

            tenantProvider.TenantId = tenantA;
            context.Categorias.Add(new Categoria
            {
                Nombre = "Categoria Visible",
                Detalle = "A",
                Activo = true
            });
            await context.SaveChangesAsync();
            var currentCategoryId = context.Categorias
                .Single(category => category.Nombre == "Categoria Visible").Id;

            context.Egresos.Add(new Egreso
            {
                CategoriaId = currentCategoryId,
                FechaEgreso = ControllerTestSupport.BusinessDateTimeProvider.Today(),
                Detalle = "Egreso Visible",
                Monto = 750,
                MetodoPago = "TARJETA"
            });
            await context.SaveChangesAsync();

            context.ChangeTracker.Clear();

            var controller = new EgresosController(
                ControllerTestSupport.CreateEgresoService(context),
                ControllerTestSupport.CreateEgresoQueryService(context),
                ControllerTestSupport.BusinessDateTimeProvider,
                ControllerTestSupport.CreateTenantDisplayNameService());
            ControllerTestSupport.AttachHttpContext(controller);
            var result = await controller.ExportarExcel(new EgresoFiltroViewModel { VistaTiempo = "dia" });

            var file = Assert.IsType<FileContentResult>(result);
            using var workbook = new XLWorkbook(new MemoryStream(file.FileContents));
            var text = string.Join(
                "|",
                workbook.Worksheets.SelectMany(sheet => sheet.CellsUsed().Select(cell => cell.GetString())));

            Assert.Contains("Egreso Visible", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Egreso Privado", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task SubscriptionHistory_ShouldBeFilteredByCurrentTenant()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            tenantProvider.TenantId = Guid.Empty;

            var tenantA = new Tenant { Id = Guid.NewGuid(), Nombre = "Tenant A", Activo = true };
            var tenantB = new Tenant { Id = Guid.NewGuid(), Nombre = "Tenant B", Activo = true };
            var plan = new Plan
            {
                Id = Guid.NewGuid(),
                Nombre = "Plan Test",
                PrecioMensual = 10,
                Moneda = "CRC",
                Activo = true
            };

            context.Tenants.AddRange(tenantA, tenantB);
            context.Planes.Add(plan);
            await context.SaveChangesAsync();

            tenantProvider.TenantId = tenantA.Id;
            var subscriptionA = new Suscripcion
            {
                PlanId = plan.Id,
                Estado = EstadoSuscripcion.Activa,
                Proveedor = PaymentProviderType.Tilopay,
                ProviderReference = "LXA-AA1101-ABCDEF0001",
                FechaInicio = DateTime.UtcNow
            };
            context.Suscripciones.Add(subscriptionA);
            await context.SaveChangesAsync();

            context.HistorialSuscripciones.Add(new HistorialSuscripcion
            {
                Id = Guid.NewGuid(),
                SuscripcionId = subscriptionA.Id,
                PlanIdNuevo = plan.Id,
                FechaCambio = DateTime.UtcNow,
                Proveedor = PaymentProviderType.Tilopay,
                Motivo = "Tenant A"
            });
            await context.SaveChangesAsync();

            tenantProvider.TenantId = tenantB.Id;
            var subscriptionB = new Suscripcion
            {
                PlanId = plan.Id,
                Estado = EstadoSuscripcion.Activa,
                Proveedor = PaymentProviderType.Tilopay,
                ProviderReference = "LXA-BB2202-ABCDEF0002",
                FechaInicio = DateTime.UtcNow
            };
            context.Suscripciones.Add(subscriptionB);
            await context.SaveChangesAsync();

            context.HistorialSuscripciones.Add(new HistorialSuscripcion
            {
                Id = Guid.NewGuid(),
                SuscripcionId = subscriptionB.Id,
                PlanIdNuevo = plan.Id,
                FechaCambio = DateTime.UtcNow,
                Proveedor = PaymentProviderType.Tilopay,
                Motivo = "Tenant B"
            });
            await context.SaveChangesAsync();

            context.ChangeTracker.Clear();

            tenantProvider.TenantId = tenantA.Id;
            var tenantAHistories = await context.HistorialSuscripciones
                .Include(historial => historial.Suscripcion)
                .ToListAsync();

            Assert.Single(tenantAHistories);
            Assert.Equal("Tenant A", tenantAHistories[0].Motivo);
            Assert.Equal(tenantA.Id, tenantAHistories[0].Suscripcion?.TenantId);
        }

        [Fact]
        public void TenantEntityProperties_ShouldBeBindNever()
        {
            var tenantEntityTypes = typeof(ProyectoIdentity.Datos.ApplicationDbContext).Assembly
                .GetTypes()
                .Where(type => typeof(ITenantEntity).IsAssignableFrom(type) && type.IsClass)
                .ToArray();

            var violations = tenantEntityTypes
                .Where(type => type.GetProperty(nameof(ITenantEntity.TenantId))?
                    .GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ModelBinding.BindNeverAttribute), inherit: true)
                    .Any() != true)
                .Select(type => type.FullName)
                .OrderBy(name => name)
                .ToArray();

            Assert.True(
                violations.Length == 0,
                "TenantId sin BindNever en: " + string.Join(", ", violations));
        }

        [Fact]
        public void MutatingControllerActions_ShouldRequireAntiforgeryOrExplicitIgnore()
        {
            var mutatingHttpAttributes = new[]
            {
                typeof(HttpPostAttribute),
                typeof(HttpPutAttribute),
                typeof(HttpPatchAttribute),
                typeof(HttpDeleteAttribute)
            };

            var protectionAttributes = new[]
            {
                typeof(ValidateAntiForgeryTokenAttribute),
                typeof(AutoValidateAntiforgeryTokenAttribute),
                typeof(IgnoreAntiforgeryTokenAttribute)
            };

            var controllerTypes = typeof(BillingController).Assembly
                .GetTypes()
                .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract);

            var violations = controllerTypes
                .SelectMany(type => type.GetMethods()
                    .Where(method => method.DeclaringType == type)
                    .Where(method => method.GetCustomAttributes(inherit: true)
                        .Any(attribute => mutatingHttpAttributes.Contains(attribute.GetType())))
                    .Select(method => new
                    {
                        Type = type,
                        Method = method,
                        Attributes = type.GetCustomAttributes(inherit: true)
                            .Concat(method.GetCustomAttributes(inherit: true))
                            .ToArray()
                    }))
                .Where(item => !item.Attributes.Any(attribute =>
                    protectionAttributes.Contains(attribute.GetType())))
                .Select(item => $"{item.Type.FullName}.{item.Method.Name}")
                .OrderBy(name => name)
                .ToArray();

            Assert.True(
                violations.Length == 0,
                "Acciones mutadoras sin antiforgery o exclusión explícita: " + string.Join(", ", violations));
        }

        [Fact]
        public void IgnoreQueryFilters_ShouldOnlyExistInApprovedInfrastructureFiles()
        {
            var repoRoot = TestProjectPaths.RepositoryRoot;
            var allowedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine("Controllers", "Billing", "BillingController.cs"),
                Path.Combine("Controllers", "Platform", "PlatformController.cs"),
                Path.Combine("Controllers", "Platform", "RecurringReconciliationController.cs"),
                Path.Combine("Services", "Payments", "SaaSPaymentService.cs"),
                Path.Combine("Services", "Stripe", "SuscripcionService.cs"),
                Path.Combine("Services", "SaaS", "PromotionalCodeService.cs"),
                Path.Combine("Services", "SaaS", "TenantCommercialAccessResolver.cs"),

                // Resumen de suscripcion: filtra siempre por TenantId explicito recibido del caller.
                Path.Combine("Services", "SaaS", "SubscriptionSummaryService.cs"),
                // Cambio de plan: lee/escribe el intento filtrando por TenantId explicito (corre
                // tambien desde el webhook, sin contexto de tenant del request).
                Path.Combine("Services", "SaaS", "PlanChangeService.cs"),
                // Decisión de cambio de plan: SOLO lectura, filtra suscripción/intents por TenantId
                // explícito recibido del caller autenticado.
                Path.Combine("Services", "SaaS", "PlanChangeDecisionService.cs"),
                // Modulo WhatsApp: filtra por user.TenantId del usuario autenticado.
                Path.Combine("Controllers", "WhatsApp", "WhatsAppController.cs"),
                // Comprobante publico: acceso por token secreto (capability), ruta sin contexto de tenant.
                Path.Combine("Services", "Comprobantes", "ComprobanteCobroService.cs"),
                // Reservas publicas: resolucion por slug publico antes de tener contexto de tenant +
                // verificacion de unicidad de slug (TenantId != actual).
                Path.Combine("Services", "Reservas", "BookingSettingsService.cs"),
                Path.Combine("Services", "Tenant", "TenantDisplayNameService.cs"),
                // Pagina publica del tenant: resuelve por slug publico antes de tener
                // contexto de tenant y luego proyecta solo datos publicos.
                Path.Combine("Services", "PublicPages", "TenantPublicPageQueryService.cs"),
                Path.Combine("Services", "PublicPages", "TenantPublicPageSettingsService.cs"),
                Path.Combine("Services", "PublicPages", "TenantPublicPageAnalyticsService.cs"),
                Path.Combine("Services", "PublicPages", "TenantPublicPageRedirectService.cs"),
                // Expiracion soft de registros pendientes: escaneo cross-tenant deliberado,
                // gateado por config y limitado a PendingVerification sin pago/verificacion/actividad.
                Path.Combine("Services", "Tenant", "PendingTenantExpirationService.cs"),
                // Consola de plataforma (SuperAdmin): agregados/lecturas cross-tenant deliberados,
                // gateados por la politica PlatformSuperAdmin en los controllers Platform.
                Path.Combine("Controllers", "Platform", "PlatformUsersController.cs"),
                // Gestión del ciclo de vida del suscriptor (SuperAdmin): lee estado local↔proveedor
                // cross-tenant en solo lectura; las acciones delegan en ProviderSubscriptionManager.
                Path.Combine("Controllers", "Platform", "PlatformProviderSubscriptionController.cs"),
                Path.Combine("Services", "Platform", "PlatformTenantProfileService.cs"),
                Path.Combine("Services", "Platform", "PlatformMetricsService.cs"),
                Path.Combine("Services", "Platform", "PlatformWhatsAppStatusService.cs"),
                Path.Combine("Services", "Platform", "PlatformAuditService.cs"),
                // Consola de resumen mensual (SuperAdmin): agregados cross-tenant deliberados.
                Path.Combine("Services", "Platform", "PlatformMonthlyReportService.cs"),
                // Snapshot comercial (SuperAdmin/worker): agregado mensual cross-tenant deliberado.
                Path.Combine("Services", "Platform", "PlatformCommercialSnapshotService.cs"),
                // Reconciliación automática de Billing: corre en background sin contexto de
                // tenant (worker diario) y debe ver pagos/suscripciones de todos los tenants.
                Path.Combine("Services", "Billing", "BillingReconciliationService.cs"),
                // Health check de Billing (SuperAdmin): agregados cross-tenant deliberados,
                // gateado por PlatformSuperAdmin en PlatformBillingHealthController.
                Path.Combine("Services", "Billing", "BillingHealthService.cs"),
                // Mission Control (SuperAdmin): señales/colas cross-tenant deliberadas,
                // gateado por PlatformSuperAdmin en PlatformController.
                Path.Combine("Services", "Platform", "PlatformMissionControlService.cs"),
                // Resolución de id_suscriptor: corre desde webhook/worker sin contexto de tenant;
                // filtra por TenantId explícito y escribe bajo BeginScope(tenantId) para RLS.
                Path.Combine("Services", "Billing", "SubscriberResolutionService.cs"),
                // Gestión del suscriptor del proveedor (cancel/pause/reactivate/upgrade): corre
                // desde webhook/plataforma sin contexto de tenant; filtra por TenantId explícito.
                Path.Combine("Services", "Billing", "ProviderSubscriptionManager.cs"),
                // Gestión de la cancelación saliente del suscriptor del ADD-ON de WhatsApp (Strategy B
                // / cascada): corre desde webhook/reconciliación/plataforma sin contexto de tenant;
                // filtra por TenantId explícito y escribe bajo BeginScope(tenantId) para RLS.
                Path.Combine("Services", "Billing", "AddonSubscriptionManager.cs"),
                // Aplicación tardía del cambio de plan: corre desde el webhook y desde el worker de
                // reconciliación (sin contexto de tenant). Resuelve el tenant a partir del pago,
                // filtra por TenantId explícito y escribe bajo BeginScope(tenantId) para RLS.
                Path.Combine("Services", "Billing", "PlanChangeLateApplicationService.cs"),
                // Sincronización del expire del proveedor: corre desde la reconciliación (worker sin
                // contexto de tenant), escanea cross-tenant en solo lectura y escribe cada
                // suscripción bajo BeginScope(tenantId) para RLS.
                Path.Combine("Services", "Billing", "ProviderExpirySyncService.cs"),
                // Recuperación de pago: se engancha en el webhook (sin contexto de tenant), resuelve
                // el tenant por email+plan, filtra por TenantId explícito y escribe el incidente bajo
                // BeginScope(tenantId) para RLS.
                Path.Combine("Services", "Billing", "PaymentRecoveryService.cs"),
                // Notificaciones de recuperación: corren desde el worker (sin contexto de tenant),
                // escanean incidentes cross-tenant en solo lectura y escriben los marcadores de cada
                // incidente bajo BeginScope(tenantId) para RLS.
                Path.Combine("Services", "Billing", "PaymentRecoveryNotificationService.cs"),
                // Actualización de método de pago: lee la suscripción por TenantId explícito (lo llama
                // el admin del tenant y también Platform/SuperAdmin, sin contexto de tenant fijo).
                Path.Combine("Services", "Billing", "PaymentMethodUpdateService.cs"),
                // Auditoría SOLO LECTURA del estado real de add-ons en TiloPay: se dispara desde el
                // webhook (sin contexto de tenant) y desde la reconciliación (worker cross-tenant).
                // Filtra por TenantId explícito y escribe el snapshot/incidente bajo
                // BeginScope(tenantId) para RLS. Nunca cancela ni cobra nada.
                Path.Combine("Services", "Billing", "AddonProviderAuditService.cs")
            };

            var targetRoots = new[]
            {
                Path.Combine(repoRoot, "Controllers"),
                Path.Combine(repoRoot, "Datos"),
                Path.Combine(repoRoot, "Services"),
                Path.Combine(repoRoot, "Workers")
            };

            var violations = targetRoots
                .SelectMany(root => Directory.Exists(root)
                    ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                    : Enumerable.Empty<string>())
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                               !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Where(file => File.ReadAllText(file).Contains(".IgnoreQueryFilters()", StringComparison.Ordinal))
                .Select(file => Path.GetRelativePath(repoRoot, file))
                .Where(relativePath => !allowedFiles.Contains(relativePath))
                .OrderBy(path => path)
                .ToArray();

            Assert.True(
                violations.Length == 0,
                "Uso inesperado de IgnoreQueryFilters en: " + string.Join(", ", violations));
        }
    }
}
