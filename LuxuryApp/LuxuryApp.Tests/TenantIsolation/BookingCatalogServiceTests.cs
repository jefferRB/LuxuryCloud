using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Reservas;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class BookingCatalogServiceTests
    {
        [Fact]
        public async Task GetPublicServices_WithoutConfiguration_ShouldFallBackToAllActiveServices()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await EnsureTenantAsync(context, tenantProvider.TenantId);

            await SeedServicioAsync(context, "Corte", 30, 5000m);
            await SeedServicioAsync(context, "Barba", 60, 7000m);

            var service = new BookingCatalogService(context);
            var servicios = await service.GetPublicServicesAsync();

            // Sin configuración: se muestran todos y sin precio (ShowPrice off por defecto).
            Assert.Equal(2, servicios.Count);
            Assert.All(servicios, s => Assert.Null(s.Precio));
        }

        [Fact]
        public async Task GetPublicServices_WithConfiguration_ShouldOnlyShowVisible_AndUseTheRealServiceName()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await EnsureTenantAsync(context, tenantProvider.TenantId);

            var visible = await SeedServicioAsync(context, "Corte interno", 30, 5000m);
            var oculto = await SeedServicioAsync(context, "Servicio interno", 30, 9000m);

            context.TenantBookingServiceSettings.Add(new TenantBookingServiceSetting
            {
                ServicioId = visible.Id,
                IsVisibleOnline = true,
                // Valor histórico: la columna sigue existiendo, pero ya no decide lo que ve el
                // cliente. El nombre público es el del servicio real, sin copia que se desincronice.
                PublicName = "Corte premium",
                ShowPrice = true,
                DisplayOrder = 1
            });
            context.TenantBookingServiceSettings.Add(new TenantBookingServiceSetting
            {
                ServicioId = oculto.Id,
                IsVisibleOnline = false
            });
            await context.SaveChangesAsync();

            var service = new BookingCatalogService(context);
            var servicios = await service.GetPublicServicesAsync();

            var only = Assert.Single(servicios);
            Assert.Equal("Corte interno", only.Nombre); // nombre del Servicio, no el PublicName viejo
            Assert.Equal(5000m, only.Precio);           // ShowPrice → precio visible
        }

        [Fact]
        public async Task GetCompatibleFuncionarioIds_WithoutAssignment_ShouldReturnAllActive()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await EnsureTenantAsync(context, tenantProvider.TenantId);

            var servicio = await SeedServicioAsync(context, "Corte", 30, 5000m);
            var f1 = await SeedFuncionarioAsync(context, "Deyner");
            var f2 = await SeedFuncionarioAsync(context, "Jordan");

            var service = new BookingCatalogService(context);
            var compatibles = await service.GetCompatibleFuncionarioIdsAsync(servicio.Id);

            Assert.Equal(2, compatibles.Count);
            Assert.Contains(f1.IdFuncionario, compatibles);
            Assert.Contains(f2.IdFuncionario, compatibles);
        }

        [Fact]
        public async Task GetCompatibleFuncionarioIds_WithAssignment_ShouldReturnOnlyEnabledActive()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await EnsureTenantAsync(context, tenantProvider.TenantId);

            var servicio = await SeedServicioAsync(context, "Corte", 30, 5000m);
            var f1 = await SeedFuncionarioAsync(context, "Deyner");
            var f2 = await SeedFuncionarioAsync(context, "Jordan");

            context.TenantBookingFuncionarioServices.Add(new TenantBookingFuncionarioService
            {
                ServicioId = servicio.Id,
                FuncionarioId = f1.IdFuncionario,
                IsEnabled = true
            });
            await context.SaveChangesAsync();

            var service = new BookingCatalogService(context);
            var compatibles = await service.GetCompatibleFuncionarioIdsAsync(servicio.Id);

            var only = Assert.Single(compatibles);
            Assert.Equal(f1.IdFuncionario, only);
        }

        [Fact]
        public async Task IsServiceVisibleOnline_Respects_Configuration_And_ActiveState()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await EnsureTenantAsync(context, tenantProvider.TenantId);

            var visible = await SeedServicioAsync(context, "Corte", 30, 5000m);
            var oculto = await SeedServicioAsync(context, "Interno", 30, 5000m);

            // Sin configuración: cualquier servicio activo es reservable (compatibilidad).
            var service = new BookingCatalogService(context);
            Assert.True(await service.IsServiceVisibleOnlineAsync(visible.Id));

            context.TenantBookingServiceSettings.Add(new TenantBookingServiceSetting { ServicioId = visible.Id, IsVisibleOnline = true });
            context.TenantBookingServiceSettings.Add(new TenantBookingServiceSetting { ServicioId = oculto.Id, IsVisibleOnline = false });
            await context.SaveChangesAsync();

            Assert.True(await service.IsServiceVisibleOnlineAsync(visible.Id));
            Assert.False(await service.IsServiceVisibleOnlineAsync(oculto.Id));
            Assert.False(await service.IsServiceVisibleOnlineAsync(999999)); // inexistente
        }

        [Fact]
        public async Task Save_ShouldUpsertSettingsAndSyncFuncionarioAssignments()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await EnsureTenantAsync(context, tenantProvider.TenantId);

            var servicio = await SeedServicioAsync(context, "Corte", 30, 5000m);
            var f1 = await SeedFuncionarioAsync(context, "Deyner");
            var f2 = await SeedFuncionarioAsync(context, "Jordan");

            var service = new BookingCatalogService(context);
            await service.SaveAsync(new BookingCatalogSaveInput
            {
                Servicios =
                {
                    new BookingCatalogServiceSaveItem
                    {
                        ServicioId = servicio.Id,
                        IsVisibleOnline = true,
                        ShowPrice = true,
                        FuncionarioIds = new List<int> { f1.IdFuncionario, f2.IdFuncionario }
                    }
                }
            }, "user-1");

            // Cambiar: quitar f2, dejar solo f1.
            await service.SaveAsync(new BookingCatalogSaveInput
            {
                Servicios =
                {
                    new BookingCatalogServiceSaveItem
                    {
                        ServicioId = servicio.Id,
                        IsVisibleOnline = true,
                        ShowPrice = true,
                        FuncionarioIds = new List<int> { f1.IdFuncionario }
                    }
                }
            }, "user-1");

            var compatibles = await service.GetCompatibleFuncionarioIdsAsync(servicio.Id);
            var only = Assert.Single(compatibles);
            Assert.Equal(f1.IdFuncionario, only);

            // Solo debe existir un setting (upsert, no duplicado).
            Assert.Equal(1, await context.TenantBookingServiceSettings.CountAsync(s => s.ServicioId == servicio.Id));
        }

        /// <summary>
        /// El orden público es ALFABÉTICO por el nombre del servicio, e ignora el DisplayOrder
        /// heredado: el negocio ya no administra un campo de orden que se desincronizaba.
        /// </summary>
        [Fact]
        public async Task GetPublicServices_OrdersAlphabetically_IgnoringLegacyDisplayOrder()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await EnsureTenantAsync(context, tenantProvider.TenantId);

            var corte = await SeedServicioAsync(context, "Corte", 30, 1000m);
            var alisado = await SeedServicioAsync(context, "Alisado", 30, 1000m);
            var barba = await SeedServicioAsync(context, "Barba", 30, 1000m);

            // DisplayOrder heredado que contradice el alfabético: debe ser ignorado.
            context.TenantBookingServiceSettings.AddRange(
                new TenantBookingServiceSetting { ServicioId = corte.Id, IsVisibleOnline = true, DisplayOrder = 1 },
                new TenantBookingServiceSetting { ServicioId = alisado.Id, IsVisibleOnline = true, DisplayOrder = 2 },
                new TenantBookingServiceSetting { ServicioId = barba.Id, IsVisibleOnline = true, DisplayOrder = 3 });
            await context.SaveChangesAsync();

            var servicios = await new BookingCatalogService(context).GetPublicServicesAsync();

            Assert.Equal(new[] { alisado.Id, barba.Id, corte.Id }, servicios.Select(s => s.Id).ToArray());
        }

        /// <summary>
        /// Guardar desde el panel simplificado NO borra los valores históricos de las columnas que
        /// salieron de la interfaz. Simplificar la experiencia no es migrar destructivamente.
        /// </summary>
        [Fact]
        public async Task Save_DoesNotWipeLegacyPublicNameCategoryOrDisplayOrder()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;
            await EnsureTenantAsync(context, tenantProvider.TenantId);

            var servicio = await SeedServicioAsync(context, "Corte", 30, 5000m);

            context.TenantBookingServiceSettings.Add(new TenantBookingServiceSetting
            {
                ServicioId = servicio.Id,
                IsVisibleOnline = true,
                PublicName = "Corte premium",
                Category = "Cabello",
                DisplayOrder = 7
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            await new BookingCatalogService(context).SaveAsync(new BookingCatalogSaveInput
            {
                Servicios =
                {
                    new BookingCatalogServiceSaveItem
                    {
                        ServicioId = servicio.Id,
                        IsVisibleOnline = false,
                        ShowPrice = true
                    }
                }
            }, "user-1");

            context.ChangeTracker.Clear();
            var guardado = await context.TenantBookingServiceSettings
                .SingleAsync(setting => setting.ServicioId == servicio.Id);

            Assert.False(guardado.IsVisibleOnline);   // lo que sí administra el panel
            Assert.True(guardado.ShowPrice);
            Assert.Equal("Corte premium", guardado.PublicName);  // intacto
            Assert.Equal("Cabello", guardado.Category);          // intacto
            Assert.Equal(7, guardado.DisplayOrder);              // intacto
        }

        // ── helpers ──

        private static async Task<Servicio> SeedServicioAsync(ApplicationDbContext context, string nombre, int duracion, decimal precio)
        {
            var servicio = new Servicio { Nombre = nombre, Precio = precio, DuracionMinutos = duracion, Activo = true };
            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();
            return servicio;
        }

        private static async Task<Funcionario> SeedFuncionarioAsync(ApplicationDbContext context, string nombre)
        {
            var puesto = new Puesto { NombrePuesto = $"Puesto {Guid.NewGuid():N}", Detalle = "Reservas", Activo = true };
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
    }
}
