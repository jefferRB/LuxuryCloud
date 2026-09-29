using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Legal;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Contracts;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class TenantProvisioningServiceTests
    {
        [Fact]
        public async Task RegisterAsync_ShouldRollbackTenantWhenUserCreationFails()
        {
            await using var provider = await CreateServiceProviderAsync();

            using (var scope = provider.CreateScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();

                var result = await service.RegisterAsync(new TenantRegistrationRequest
                {
                    Name = "Tenant fallido",
                    Email = "rollback@test.local",
                    PhoneNumber = "88888888",
                    Password = "x",
                    AcceptCurrentContract = true,
                    SubmittedContractDocumentId = ContractDocumentSeedData.InitialDocumentId,
                    ContractIpAddress = "203.0.113.10",
                    ContractUserAgent = "TenantProvisioningServiceTests/rollback"
                });

                Assert.False(result.Succeeded);
            }

            using (var assertScope = provider.CreateScope())
            {
                var dbContext = assertScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                Assert.Equal(0, await dbContext.Tenants.CountAsync());
                Assert.Equal(0, await dbContext.Users.CountAsync());
                Assert.Equal(0, await dbContext.ContractAcceptanceRecords.CountAsync());
            }
        }

        [Fact]
        public async Task RegisterAsync_ShouldPersistContractAcceptanceEvidenceOnSuccessfulProvisioning()
        {
            await using var provider = await CreateServiceProviderAsync();

            TenantProvisioningResult result;

            using (var scope = provider.CreateScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();

                result = await service.RegisterAsync(new TenantRegistrationRequest
                {
                    Name = "Luxury Tenant",
                    Email = "owner-contract@test.local",
                    PhoneNumber = "88888888",
                    Password = "Valid1!",
                    AcceptCurrentContract = true,
                    SubmittedContractDocumentId = ContractDocumentSeedData.InitialDocumentId,
                    ContractIpAddress = "198.51.100.25",
                    ContractUserAgent = "TenantProvisioningServiceTests/success"
                });
            }

            Assert.True(result.Succeeded);
            Assert.NotNull(result.User);

            using (var assertScope = provider.CreateScope())
            {
                var dbContext = assertScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var acceptance = await dbContext.ContractAcceptanceRecords.SingleAsync();
                var activeDocument = await dbContext.ContractDocuments.SingleAsync(document => document.IsActive);

                Assert.Equal(result.User!.Id, acceptance.UserId);
                Assert.Equal(activeDocument.Id, acceptance.ContractDocumentId);
                Assert.Equal(activeDocument.VersionNumber, acceptance.ContractVersion);
                Assert.Equal(activeDocument.ContentHash, acceptance.AcceptedContentHash);
                Assert.Equal(ContractAcceptanceSources.Register, acceptance.AcceptanceSource);
                Assert.Equal("198.51.100.25", acceptance.IpAddress);
                Assert.Equal("TenantProvisioningServiceTests/success", acceptance.UserAgent);
                Assert.NotEqual(default, acceptance.AcceptedAtUtc);
            }
        }

        [Fact]
        public async Task RegisterAsync_ShouldReturnContractValidationErrorWhenAcceptedButSubmittedDocumentIsMissing()
        {
            await using var provider = await CreateServiceProviderAsync();

            using var scope = provider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();

            var result = await service.RegisterAsync(new TenantRegistrationRequest
            {
                Name = "Luxury Tenant",
                Email = "missing-contract@test.local",
                PhoneNumber = "88888888",
                Password = "Valid1!",
                AcceptCurrentContract = true,
                SubmittedContractDocumentId = null,
                ContractIpAddress = "198.51.100.26",
                ContractUserAgent = "TenantProvisioningServiceTests/missing-contract"
            });

            Assert.False(result.Succeeded);
            var error = Assert.Single(result.Errors);
            Assert.Equal("No fue posible validar el contrato vigente. Recarga la página e intenta de nuevo.", error);
            Assert.DoesNotContain("Debes aceptar el contrato para crear tu cuenta.", result.Errors);
        }

        [Fact]
        public async Task RegisterAsync_ShouldReturnContractChangedErrorWhenSubmittedDocumentDoesNotMatchActiveContract()
        {
            await using var provider = await CreateServiceProviderAsync();

            using var scope = provider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();

            var result = await service.RegisterAsync(new TenantRegistrationRequest
            {
                Name = "Luxury Tenant",
                Email = "changed-contract@test.local",
                PhoneNumber = "88888888",
                Password = "Valid1!",
                AcceptCurrentContract = true,
                SubmittedContractDocumentId = Guid.NewGuid(),
                ContractIpAddress = "198.51.100.27",
                ContractUserAgent = "TenantProvisioningServiceTests/changed-contract"
            });

            Assert.False(result.Succeeded);
            var error = Assert.Single(result.Errors);
            Assert.Equal("El contrato vigente cambió. Recarga la página e intenta de nuevo.", error);
        }

        [Fact]
        public async Task RegisterAsync_WithValidBusinessCode_ShouldCreateTenantGrantAndMarkCodeUsed()
        {
            await using var provider = await CreateServiceProviderAsync();
            var planId = await SeedBusinessPlanAndCodeAsync(provider, "BUSINESS15-TEST-0001", diasGratis: 15, maxUsos: 1);

            TenantProvisioningResult result;
            using (var scope = provider.CreateScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();
                result = await service.RegisterAsync(new TenantRegistrationRequest
                {
                    Name = "QA Trial Business",
                    Email = "qa.trial.business.0001@luxurycloud.test",
                    PhoneNumber = "88880001",
                    Password = "Valid1!",
                    AccessCode = "BUSINESS15-TEST-0001",
                    AcceptCurrentContract = true,
                    SubmittedContractDocumentId = ContractDocumentSeedData.InitialDocumentId,
                    ContractIpAddress = "203.0.113.20",
                    ContractUserAgent = "TenantProvisioningServiceTests/business-code"
                });
            }

            Assert.True(result.Succeeded);
            Assert.True(result.PromotionalAccessApplied);
            // Nota: el acceso activo por grant lo cubre TenantCommercialAccessResolverTests con reloj
            // alineado; aquí el harness usa un reloj fijo distinto a DateTime.UtcNow del grant.

            using var assertScope = provider.CreateScope();
            var db = assertScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            Assert.Equal(1, await db.Tenants.CountAsync());
            Assert.Equal(1, await db.Users.CountAsync());

            var grant = await db.TenantCommercialAccessGrants.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(result.TenantId, grant.TenantId);
            Assert.Equal(planId, grant.PlanId);
            Assert.True(grant.Activo);
            Assert.Equal(TenantCommercialAccessGrantSource.PromotionalCode, grant.Source);
            Assert.InRange((grant.FechaFinUtc - grant.FechaInicioUtc).TotalDays, 14.99, 15.01);

            var code = await db.PromotionalCodes.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(1, code.UsosActuales);

            var redemption = await db.PromotionalCodeRedemptions.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(result.TenantId, redemption.TenantId);
        }

        [Fact]
        public async Task RegisterAsync_ReusingSingleUseCode_ShouldFailAndNotCreateSecondTenant()
        {
            await using var provider = await CreateServiceProviderAsync();
            await SeedBusinessPlanAndCodeAsync(provider, "BUSINESS15-TEST-0002", diasGratis: 15, maxUsos: 1);

            using (var scope = provider.CreateScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();
                var first = await service.RegisterAsync(BuildRequest("qa.trial.business.0002a@luxurycloud.test", "BUSINESS15-TEST-0002"));
                Assert.True(first.Succeeded);
            }

            TenantProvisioningResult second;
            using (var scope = provider.CreateScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();
                second = await service.RegisterAsync(BuildRequest("qa.trial.business.0002b@luxurycloud.test", "BUSINESS15-TEST-0002"));
            }

            Assert.False(second.Succeeded);

            using var assertScope = provider.CreateScope();
            var db = assertScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            // Solo el primer registro debe existir; el segundo se revierte por completo.
            Assert.Equal(1, await db.Tenants.CountAsync());
            Assert.Equal(1, await db.Users.CountAsync());
            var code = await db.PromotionalCodes.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(1, code.UsosActuales);
        }

        [Fact]
        public async Task RegisterAsync_WithInvalidCode_ShouldFailWithoutCreatingTenantOrUser()
        {
            await using var provider = await CreateServiceProviderAsync();
            await SeedBusinessPlanAndCodeAsync(provider, "BUSINESS15-TEST-0003", diasGratis: 15, maxUsos: 1);

            TenantProvisioningResult result;
            using (var scope = provider.CreateScope())
            {
                var service = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();
                result = await service.RegisterAsync(BuildRequest("qa.trial.business.0003@luxurycloud.test", "NOPE-INVALID-CODE"));
            }

            Assert.False(result.Succeeded);

            using var assertScope = provider.CreateScope();
            var db = assertScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(0, await db.Tenants.CountAsync());
            Assert.Equal(0, await db.Users.CountAsync());
            Assert.Equal(0, await db.TenantCommercialAccessGrants.IgnoreQueryFilters().CountAsync());
        }

        private static TenantRegistrationRequest BuildRequest(string email, string accessCode) =>
            new()
            {
                Name = "QA Trial Business",
                Email = email,
                PhoneNumber = "88880000",
                Password = "Valid1!",
                AccessCode = accessCode,
                AcceptCurrentContract = true,
                SubmittedContractDocumentId = ContractDocumentSeedData.InitialDocumentId,
                ContractIpAddress = "203.0.113.21",
                ContractUserAgent = "TenantProvisioningServiceTests/code"
            };

        private static async Task<Guid> SeedBusinessPlanAndCodeAsync(
            ServiceProvider provider,
            string code,
            int diasGratis,
            int maxUsos)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var planId = Guid.NewGuid();
            db.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.Business,
                Nombre = "Business",
                Moneda = "CRC",
                PrecioMensual = 15000,
                Activo = true
            });

            db.PromotionalCodes.Add(new PromotionalCode
            {
                Id = Guid.NewGuid(),
                Codigo = code,
                Activo = true,
                TipoBeneficio = PromotionalBenefitType.FreeAccessDays,
                DiasGratis = diasGratis,
                PlanId = planId,
                MaxUsos = maxUsos,
                SoloPrimerRegistro = true
            });

            await db.SaveChangesAsync();
            return planId;
        }

        private static async Task<ServiceProvider> CreateServiceProviderAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpContextAccessor();
            services.AddMemoryCache();
            services.AddSingleton(connection);
            services.AddSingleton<TestWebHostEnvironment>();
            services.AddSingleton<IWebHostEnvironment>(serviceProvider => serviceProvider.GetRequiredService<TestWebHostEnvironment>());
            services.AddSingleton<IHostEnvironment>(serviceProvider => serviceProvider.GetRequiredService<TestWebHostEnvironment>());
            services.AddSingleton<ITenantExecutionContextAccessor, TenantExecutionContextAccessor>();
            services.AddScoped<ITenantProvider, TenantProvider>();
            services.AddSingleton<ITenantCommercialAccessCache, TenantCommercialAccessCache>();
            services.Configure<IdentityOptions>(_ => { });
            services.Configure<TilopayRepeatOptions>(_ => { });
            services.Configure<OpcionesOnboardingTenant>(options =>
            {
                options.RegistrationRole = "Administrador";
                options.AddRegisteredRole = true;
                options.RegisteredRole = "Registrado";
                options.CreateInitialSubscription = false;
            });
            services.AddDbContext<ApplicationDbContext>((serviceProvider, options) =>
            {
                options.UseSqlite(serviceProvider.GetRequiredService<SqliteConnection>());
            });
            services
                .AddIdentity<AppUsuario, IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();
            services.AddSingleton<Services.BusinessTime.IBusinessDateTimeProvider>(_ => new FixedBusinessDateTimeProvider());
            services.AddScoped<SuscripcionService>();
            services.AddScoped<ITenantCommercialAccessResolver, TenantCommercialAccessResolver>();
            services.AddScoped<IPromotionalCodeService, PromotionalCodeService>();
            services.AddScoped<IContractService, ContractService>();
            services.AddScoped<TenantProvisioningService>();

            var provider = services.BuildServiceProvider();

            using (var setupScope = provider.CreateScope())
            {
                var dbContext = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await dbContext.Database.EnsureCreatedAsync();
            }

            return provider;
        }
    }
}
