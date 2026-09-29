using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.PublicPages;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.PublicImages;
using LuxuryApp.Services.PublicPages;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class TenantPublicPageQueryServiceTests
    {
        [Fact]
        public async Task GetBySlug_ActiveTenantPublishedPage_ReturnsLandingWithBookingCta()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await SeedTenantAsync(context, tenantId, "Barberia Luxury");
            await SeedBookingAndPageAsync(
                context,
                slug: "barberia-luxury",
                page =>
                {
                    page.HeroEyebrow = "Estudio premium";
                    page.BusinessHours = "Lun a Sab: 9 a.m. - 7 p.m.";
                    page.WhatsAppPhone = "+506 8888-7777";
                    page.WazeUrl = "https://waze.com/ul?ll=9.9,-84.1&navigate=yes";
                });

            tenantProvider.TenantId = Guid.Empty;
            context.ChangeTracker.Clear();

            var service = CreateQueryService(context);
            var model = await service.GetBySlugAsync("barberia-luxury");

            Assert.NotNull(model);
            Assert.Equal("Barberia Luxury", model!.BusinessName);
            Assert.Equal("https://public.test/reservar/barberia-luxury", model.BookingUrl);
            Assert.Equal("https://public.test/sitio/barberia-luxury", model.PublicSiteUrl);
            Assert.Equal("https://wa.me/50688887777", model.WhatsAppUrl);
            Assert.Equal("Estudio premium", model.HeroEyebrow);
            Assert.Equal("Lun a Sab: 9 a.m. - 7 p.m.", model.BusinessHours);
            Assert.Equal("https://public.test/sitio/barberia-luxury/go/waze", model.WazeActionUrl);
            // Sin asset de ubicacion configurado no debe haber imagen (evita bloque vacio).
            Assert.Null(model.LocationImage);
        }

        [Fact]
        public async Task GetBySlug_MissingOrUnpublishedPage_ReturnsNull()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await SeedTenantAsync(context, tenantId, "Tenant A");
            await SeedBookingAndPageAsync(context, "tenant-a", page => page.IsPublished = false);

            tenantProvider.TenantId = Guid.Empty;
            context.ChangeTracker.Clear();

            var service = CreateQueryService(context);

            Assert.Null(await service.GetBySlugAsync("no-existe"));
            Assert.Null(await service.GetBySlugAsync("tenant-a"));
        }

        [Fact]
        public async Task GetBySlug_InactiveTenant_ReturnsNull()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await SeedTenantAsync(context, tenantId, "Tenant Inactivo", active: false);
            await SeedBookingAndPageAsync(context, "tenant-inactivo");

            tenantProvider.TenantId = Guid.Empty;
            context.ChangeTracker.Clear();

            var service = CreateQueryService(context);

            Assert.Null(await service.GetBySlugAsync("tenant-inactivo"));
        }

        [Fact]
        public async Task GetBySlug_DoesNotLeakServicesAcrossTenants()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await SeedTenantAsync(context, tenantA, "Tenant A");
            await SeedBookingAndPageAsync(context, "tenant-a");
            await SeedServiceAsync(context, "Servicio A", 30, 5000m);

            tenantProvider.TenantId = tenantB;
            await SeedTenantAsync(context, tenantB, "Tenant B");
            await SeedBookingAndPageAsync(context, "tenant-b");
            await SeedServiceAsync(context, "Servicio B", 45, 9000m);

            tenantProvider.TenantId = Guid.Empty;
            context.ChangeTracker.Clear();

            var service = CreateQueryService(context);
            var model = await service.GetBySlugAsync("tenant-b");

            Assert.NotNull(model);
            var only = Assert.Single(model!.Services);
            Assert.Equal("Servicio B", only.Name);
            Assert.DoesNotContain(model.Services, serviceCard => serviceCard.Name == "Servicio A");
        }

        [Fact]
        public async Task GetBySlug_Services_AreActiveVisibleAndOrdered()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await SeedTenantAsync(context, tenantId, "Tenant Servicios");
            await SeedBookingAndPageAsync(context, "tenant-servicios");

            var a = await SeedServiceAsync(context, "A interno", 30, 5000m);
            var b = await SeedServiceAsync(context, "B interno", 60, 7000m);
            var hidden = await SeedServiceAsync(context, "Oculto", 20, 2000m);
            var inactive = await SeedServiceAsync(context, "Inactivo", 20, 2000m, active: false);

            context.TenantBookingServiceSettings.AddRange(
                new TenantBookingServiceSetting
                {
                    ServicioId = a.Id,
                    IsVisibleOnline = true,
                    PublicName = "Servicio A",
                    PublicDescription = "Descripcion A",
                    DisplayOrder = 2,
                    ShowPrice = true
                },
                new TenantBookingServiceSetting
                {
                    ServicioId = b.Id,
                    IsVisibleOnline = true,
                    PublicName = "Servicio B",
                    DisplayOrder = 1,
                    ShowPrice = true
                },
                new TenantBookingServiceSetting
                {
                    ServicioId = hidden.Id,
                    IsVisibleOnline = false,
                    DisplayOrder = 3,
                    ShowPrice = true
                },
                new TenantBookingServiceSetting
                {
                    ServicioId = inactive.Id,
                    IsVisibleOnline = true,
                    DisplayOrder = 4,
                    ShowPrice = true
                });
            await context.SaveChangesAsync();

            tenantProvider.TenantId = Guid.Empty;
            context.ChangeTracker.Clear();

            var model = await CreateQueryService(context).GetBySlugAsync("tenant-servicios");

            Assert.NotNull(model);
            Assert.Equal(new[] { "Servicio B", "Servicio A" }, model!.Services.Select(service => service.Name));
            Assert.All(model.Services, service => Assert.NotEqual("Oculto", service.Name));
            Assert.Equal(7000m, model.Services[0].Price);
            Assert.Equal("Descripcion A", model.Services[1].Description);
        }

        [Fact]
        public async Task GetBySlug_RespectsDisplayFlags()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await SeedTenantAsync(context, tenantId, "Tenant Flags");
            await SeedBookingAndPageAsync(
                context,
                "tenant-flags",
                page =>
                {
                    page.ShowPrices = false;
                    page.ShowTeam = false;
                    page.ShowWhatsAppButton = false;
                    page.WhatsAppPhone = "8888-7777";
                });

            var servicio = await SeedServiceAsync(context, "Servicio visible", 30, 5000m);
            context.TenantBookingServiceSettings.Add(new TenantBookingServiceSetting
            {
                ServicioId = servicio.Id,
                IsVisibleOnline = true,
                ShowPrice = true
            });
            await SeedFuncionarioAsync(context, "Profesional");
            await context.SaveChangesAsync();

            tenantProvider.TenantId = Guid.Empty;
            context.ChangeTracker.Clear();

            var model = await CreateQueryService(context).GetBySlugAsync("tenant-flags");

            Assert.NotNull(model);
            Assert.Null(Assert.Single(model!.Services).Price);
            Assert.Empty(model.TeamMembers);
            Assert.Null(model.WhatsAppUrl);
        }

        [Fact]
        public async Task GetBySlug_LoadsOnlyActivePublicAssets()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await SeedTenantAsync(context, tenantId, "Tenant Assets");
            await SeedBookingAndPageAsync(context, "tenant-assets");
            var service = await SeedServiceAsync(context, "Corte", 30, 5000m);
            var page = await context.TenantPublicPages.SingleAsync();

            context.TenantPublicAssets.AddRange(
                new TenantPublicAsset
                {
                    TenantPublicPageId = page.Id,
                    AssetType = TenantPublicAssetType.Logo,
                    StorageKey = $"tenants/{tenantId:N}/public-page/logo/{Guid.NewGuid():N}.webp",
                    PublicUrl = "https://media.test/logo.webp",
                    ContentType = "image/webp",
                    SizeBytes = 100,
                    Width = 128,
                    Height = 128,
                    IsActive = true
                },
                new TenantPublicAsset
                {
                    TenantPublicPageId = page.Id,
                    AssetType = TenantPublicAssetType.Cover,
                    StorageKey = $"tenants/{tenantId:N}/public-page/cover/{Guid.NewGuid():N}.webp",
                    PublicUrl = "https://media.test/cover.webp",
                    ContentType = "image/webp",
                    SizeBytes = 100,
                    Width = 1200,
                    Height = 800,
                    IsActive = true
                },
                new TenantPublicAsset
                {
                    TenantPublicPageId = page.Id,
                    AssetType = TenantPublicAssetType.BusinessGallery,
                    StorageKey = $"tenants/{tenantId:N}/public-page/gallery/{Guid.NewGuid():N}.webp",
                    PublicUrl = "https://media.test/gallery.webp",
                    ContentType = "image/webp",
                    SizeBytes = 100,
                    Width = 800,
                    Height = 600,
                    IsActive = true
                },
                new TenantPublicAsset
                {
                    TenantPublicPageId = page.Id,
                    AssetType = TenantPublicAssetType.BusinessGallery,
                    StorageKey = $"tenants/{tenantId:N}/public-page/gallery/{Guid.NewGuid():N}.webp",
                    PublicUrl = "https://media.test/inactive.webp",
                    ContentType = "image/webp",
                    SizeBytes = 100,
                    Width = 800,
                    Height = 600,
                    IsActive = false,
                    DeletedAtUtc = DateTime.UtcNow
                },
                new TenantPublicAsset
                {
                    ServicioId = service.Id,
                    AssetType = TenantPublicAssetType.ServiceMain,
                    StorageKey = $"tenants/{tenantId:N}/services/{service.Id}/main/{Guid.NewGuid():N}.webp",
                    PublicUrl = "https://media.test/service.webp",
                    ContentType = "image/webp",
                    SizeBytes = 100,
                    Width = 800,
                    Height = 600,
                    IsActive = true
                },
                new TenantPublicAsset
                {
                    TenantPublicPageId = page.Id,
                    AssetType = TenantPublicAssetType.Location,
                    StorageKey = $"tenants/{tenantId:N}/public-page/location/{Guid.NewGuid():N}.webp",
                    PublicUrl = "https://media.test/location.webp",
                    ContentType = "image/webp",
                    SizeBytes = 100,
                    Width = 1200,
                    Height = 900,
                    IsActive = true
                });
            await context.SaveChangesAsync();

            tenantProvider.TenantId = Guid.Empty;
            context.ChangeTracker.Clear();

            var model = await CreateQueryService(context).GetBySlugAsync("tenant-assets");

            Assert.NotNull(model);
            Assert.Equal("https://media.test/logo.webp", model!.LogoImage?.Url);
            Assert.Equal("https://media.test/cover.webp", model.CoverImage?.Url);
            Assert.Equal("https://media.test/gallery.webp", Assert.Single(model.BusinessGallery).Url);
            Assert.Equal("https://media.test/service.webp", Assert.Single(model.Services).MainImage?.Url);
            Assert.Equal("https://media.test/location.webp", model.LocationImage?.Url);
            Assert.DoesNotContain(model.BusinessGallery, image => image.Url.Contains("inactive", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task GetBySlug_ServiceCta_UsesTrackedActionAndPreselectedBookingUrl()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var _ = context;
            using var __ = connection;

            await SeedTenantAsync(context, tenantId, "Tenant CTA");
            await SeedBookingAndPageAsync(context, "tenant-cta");
            var servicio = await SeedServiceAsync(context, "Corte", 30, 5000m);

            context.TenantBookingServiceSettings.Add(new TenantBookingServiceSetting
            {
                ServicioId = servicio.Id,
                IsVisibleOnline = true,
                ShowPrice = true
            });
            await context.SaveChangesAsync();

            tenantProvider.TenantId = Guid.Empty;
            context.ChangeTracker.Clear();

            var model = await CreateQueryService(context).GetBySlugAsync("tenant-cta");

            Assert.NotNull(model);
            Assert.Equal("https://public.test/sitio/tenant-cta/go/reservar", model!.ReserveActionUrl);

            var card = Assert.Single(model.Services);
            Assert.Equal(servicio.Id, card.ServiceId);
            Assert.Equal($"https://public.test/reservar/tenant-cta?servicioId={servicio.Id}", card.BookingUrl);
            Assert.Equal(
                $"https://public.test/sitio/tenant-cta/go/servicio/{servicio.Id}/reservar",
                card.ReserveActionUrl);
        }

        private static TenantPublicPageQueryService CreateQueryService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider? clock = null)
        {
            var http = new DefaultHttpContext();
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("public.test");
            var accessor = new HttpContextAccessor { HttpContext = http };

            return new TenantPublicPageQueryService(
                context,
                accessor,
                new PublicUrlValidationService(),
                new TenantDisplayNameService(context, new TestTenantProvider(), accessor),
                new BusinessScheduleService(),
                clock ?? new FixedBusinessDateTimeProvider());
        }

        private static async Task SeedTenantAsync(
            ApplicationDbContext context,
            Guid tenantId,
            string name,
            bool active = true)
        {
            context.Tenants.Add(new Tenant { Id = tenantId, Nombre = name, Activo = active });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }

        private static async Task SeedBookingAndPageAsync(
            ApplicationDbContext context,
            string slug,
            Action<TenantPublicPage>? configurePage = null)
        {
            context.TenantBookingSettings.Add(new TenantBookingSettings
            {
                PublicBookingEnabled = true,
                PublicBookingSlug = slug
            });

            var page = new TenantPublicPage
            {
                IsPublished = true,
                HeroTitle = "Agenda tu cita",
                Description = "Reserva online.",
                ShowServices = true,
                ShowPrices = true,
                ShowLocation = true,
                ShowWhatsAppButton = true
            };
            configurePage?.Invoke(page);
            context.TenantPublicPages.Add(page);

            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }

        private static async Task<Servicio> SeedServiceAsync(
            ApplicationDbContext context,
            string name,
            int duration,
            decimal price,
            bool active = true)
        {
            var service = new Servicio
            {
                Nombre = name,
                DuracionMinutos = duration,
                Precio = price,
                Activo = active
            };
            context.Servicios.Add(service);
            await context.SaveChangesAsync();
            return service;
        }

        private static async Task<Funcionario> SeedFuncionarioAsync(ApplicationDbContext context, string name)
        {
            var puesto = new Puesto
            {
                NombrePuesto = $"Puesto {Guid.NewGuid():N}",
                Detalle = "Publico",
                Activo = true
            };
            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            var funcionario = new Funcionario
            {
                Nombre = name,
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#047857",
                PorcentajeGanancia = 40m,
                PorcentajeProducto = 10m,
                FechaIngreso = new DateTime(2026, 1, 1),
                Activo = true
            };
            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario;
        }
    }
}
