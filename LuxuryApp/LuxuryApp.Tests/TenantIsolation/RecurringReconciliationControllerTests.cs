using System.Security.Claims;
using LuxuryApp.Controllers.Platform;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Payments;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class RecurringReconciliationControllerTests
    {
        [Fact]
        public async Task Index_ShouldForbidRegularUser()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var tenantId = await SeedPendingRecurringAsync(context);
            var controller = CreateController(context, userManager);
            ControllerTestSupport.AttachHttpContext(controller, BuildPrincipal("user-1", tenantId));

            var result = await controller.Index();

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task Index_ShouldForbidTenantAdministratorWithoutSuperAdminClaim()
        {
            // La rama Development+Administrador fue eliminada (S10): el rol de tenant
            // nunca fue una vía de acceso real porque la policy de clase ya bloqueaba.
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var tenantId = await SeedPendingRecurringAsync(context);
            var controller = CreateController(context, userManager);
            ControllerTestSupport.AttachHttpContext(controller, BuildPrincipal("admin-1", tenantId, role: "Administrador"));

            var result = await controller.Index();

            Assert.IsType<ForbidResult>(result);
        }

        [Fact]
        public async Task Index_ShouldAllowPlatformSuperAdmin()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;
            using var userManager = CreateUserManager(context);

            var tenantId = await SeedPendingRecurringAsync(context);
            var controller = CreateController(context, userManager);
            ControllerTestSupport.AttachHttpContext(controller, BuildPrincipal("superadmin-1", tenantId, isPlatformSuperAdmin: true));

            var result = await controller.Index();

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsAssignableFrom<LuxuryApp.Models.Platform.PlatformRecurringReconciliationPageViewModel>(view.Model);
            Assert.Single(model.Items);
        }

        private static RecurringReconciliationController CreateController(
            ApplicationDbContext context,
            UserManager<AppUsuario> userManager)
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            var repeatOptions = new TilopayRepeatOptions
            {
                Enabled = true,
                UseHostedLinks = true,
                EnableTestRecurringPlan = true,
                TestRecurring = new TilopayRepeatPlanOption
                {
                    TilopayPlanId = 7099,
                    Code = PlanCodes.TestRecurring,
                    MonthlyPrice = 1000,
                    Currency = "CRC",
                    MaxFuncionarios = 1,
                    CheckoutUrl = "https://tp.cr/l/test-link",
                    IsValidation = true
                }
            };

            var service = new SaaSPaymentService(
                context,
                new PaymentProviderResolver(Array.Empty<IPaymentProvider>()),
                new LuxuryApp.Services.SaaS.SuscripcionService(
                    context,
                    cache,
                    new LuxuryApp.Services.SaaS.TenantCommercialAccessCache(cache),
                    new FixedBusinessDateTimeProvider(),
                    Options.Create(repeatOptions),
                    NullLogger<LuxuryApp.Services.SaaS.SuscripcionService>.Instance),
                new TenantExecutionContextAccessor(),
                Options.Create(new OpcionesPago
                {
                    ProveedorPredeterminado = PaymentProviderType.Tilopay
                }),
                Options.Create(new OpcionesTilopay
                {
                    MerchantId = "merchant-1",
                    WebhookAccessToken = "token-seguro"
                }),
                Options.Create(repeatOptions),
                NullLogger<SaaSPaymentService>.Instance);

            return new RecurringReconciliationController(
                context,
                service,
                userManager,
                NullLogger<RecurringReconciliationController>.Instance);
        }

        private static async Task<Guid> SeedPendingRecurringAsync(ApplicationDbContext context)
        {
            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Pending",
                Activo = true
            });

            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.TestRecurring,
                Nombre = "Test recurrente",
                PrecioMensual = 1000,
                Moneda = "CRC",
                MaxFuncionarios = 1,
                Activo = true,
                EsPlanValidacion = true
            });

            context.Users.Add(new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                UserName = "owner@test.local",
                NormalizedUserName = "OWNER@TEST.LOCAL",
                Email = "owner@test.local",
                NormalizedEmail = "OWNER@TEST.LOCAL",
                TenantId = tenantId,
                State = true
            });

            context.PagosSuscripcion.Add(new PagoSuscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                Proveedor = PaymentProviderType.Tilopay,
                Estado = EstadoPagoProveedor.Pendiente,
                ReferenciaInterna = "LXA-TEST-RECON-1",
                ProviderReference = "LXA-TEST-RECON-1",
                TilopayRecurringPlanId = 7099,
                CorrelationToken = "CORR-RECON-1",
                ClienteEmail = "owner@test.local",
                Descripcion = "Pago recurrente pendiente",
                Monto = 1000m,
                Moneda = "CRC",
                FechaCreacionUtc = DateTime.UtcNow
            });

            await context.SaveChangesAsync();
            return tenantId;
        }

        private static ClaimsPrincipal BuildPrincipal(
            string userId,
            Guid tenantId,
            string? role = null,
            bool isPlatformSuperAdmin = false)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId),
                new(LuxuryApp.Services.Identity.CustomClaimTypes.UserId, userId),
                new(LuxuryApp.Services.Identity.CustomClaimTypes.TenantId, tenantId.ToString())
            };

            if (!string.IsNullOrWhiteSpace(role))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            if (isPlatformSuperAdmin)
            {
                claims.Add(new Claim(LuxuryApp.Services.Identity.CustomClaimTypes.PlatformSuperAdmin, bool.TrueString));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth", ClaimTypes.Name, ClaimTypes.Role));
        }

        private static (ApplicationDbContext Context, IDisposable Connection) CreateSystemContext()
        {
            var tenantProvider = new TestTenantProvider();
            return TestDbContextFactory.CreateSqliteContext(tenantProvider);
        }

        private static UserManager<AppUsuario> CreateUserManager(ApplicationDbContext context)
        {
            var store = new UserStore<AppUsuario>(context);

            return new UserManager<AppUsuario>(
                store,
                Options.Create(new IdentityOptions()),
                new PasswordHasher<AppUsuario>(),
                Array.Empty<IUserValidator<AppUsuario>>(),
                Array.Empty<IPasswordValidator<AppUsuario>>(),
                new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(),
                new ServiceCollection().BuildServiceProvider(),
                NullLogger<UserManager<AppUsuario>>.Instance);
        }
    }
}
