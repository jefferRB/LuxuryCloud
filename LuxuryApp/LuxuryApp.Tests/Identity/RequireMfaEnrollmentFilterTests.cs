using System.Security.Claims;
using LuxuryApp.Filters;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Identity;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Identity
{
    public class RequireMfaEnrollmentFilterTests
    {
        [Fact]
        public async Task SuperAdminWithoutTotp_ShouldRedirectToEnrollment()
        {
            var (context, connection, tenantId, userId) = await SeedAsync(twoFactorEnabled: false, isSuperAdmin: true);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var principal = ControllerTestSupport.BuildTenantPrincipal(userId, tenantId, isPlatformSuperAdmin: true);
            var (result, nextCalled) = await RunFilterAsync(context, enforcement: true, principal, excludedAction: false);

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Enrolar", redirect.ActionName);
            Assert.Equal("Seguridad", redirect.ControllerName);
            Assert.False(nextCalled);
        }

        [Fact]
        public async Task SuperAdminWithoutTotp_ShouldPassOnExcludedAction()
        {
            var (context, connection, tenantId, userId) = await SeedAsync(twoFactorEnabled: false, isSuperAdmin: true);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var principal = ControllerTestSupport.BuildTenantPrincipal(userId, tenantId, isPlatformSuperAdmin: true);
            var (result, nextCalled) = await RunFilterAsync(context, enforcement: true, principal, excludedAction: true);

            Assert.Null(result);
            Assert.True(nextCalled);
        }

        [Fact]
        public async Task SuperAdminWithoutTotp_ShouldPassWhenEnforcementDisabled()
        {
            var (context, connection, tenantId, userId) = await SeedAsync(twoFactorEnabled: false, isSuperAdmin: true);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var principal = ControllerTestSupport.BuildTenantPrincipal(userId, tenantId, isPlatformSuperAdmin: true);
            var (result, nextCalled) = await RunFilterAsync(context, enforcement: false, principal, excludedAction: false);

            Assert.Null(result);
            Assert.True(nextCalled);
        }

        [Fact]
        public async Task RegularUser_ShouldNeverBeGated()
        {
            var (context, connection, tenantId, userId) = await SeedAsync(twoFactorEnabled: false, isSuperAdmin: false);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var principal = ControllerTestSupport.BuildTenantPrincipal(userId, tenantId, isPlatformSuperAdmin: false);
            var (result, nextCalled) = await RunFilterAsync(context, enforcement: true, principal, excludedAction: false);

            Assert.Null(result);
            Assert.True(nextCalled);
        }

        [Fact]
        public async Task SuperAdminWithTotp_ShouldPass()
        {
            var (context, connection, tenantId, userId) = await SeedAsync(twoFactorEnabled: true, isSuperAdmin: true);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var principal = ControllerTestSupport.BuildTenantPrincipal(userId, tenantId, isPlatformSuperAdmin: true);
            var (result, nextCalled) = await RunFilterAsync(context, enforcement: true, principal, excludedAction: false);

            Assert.Null(result);
            Assert.True(nextCalled);
        }

        [Fact]
        public async Task AnonymousRequest_ShouldPass()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
            var (result, nextCalled) = await RunFilterAsync(context, enforcement: true, anonymous, excludedAction: false);

            Assert.Null(result);
            Assert.True(nextCalled);
        }

        private static async Task<(ApplicationDbContext Context, Microsoft.Data.Sqlite.SqliteConnection Connection, Guid TenantId, string UserId)> SeedAsync(
            bool twoFactorEnabled,
            bool isSuperAdmin)
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid().ToString("N");

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant MFA",
                Activo = true
            });

            context.Users.Add(new AppUsuario
            {
                Id = userId,
                UserName = "mfa@test.local",
                NormalizedUserName = "MFA@TEST.LOCAL",
                Email = "mfa@test.local",
                NormalizedEmail = "MFA@TEST.LOCAL",
                TenantId = tenantId,
                State = true,
                IsPlatformSuperAdmin = isSuperAdmin,
                TwoFactorEnabled = twoFactorEnabled,
                SecurityStamp = Guid.NewGuid().ToString("N")
            });

            await context.SaveChangesAsync();
            return (context, connection, tenantId, userId);
        }

        private static async Task<(IActionResult? Result, bool NextCalled)> RunFilterAsync(
            ApplicationDbContext context,
            bool enforcement,
            ClaimsPrincipal principal,
            bool excludedAction)
        {
            var options = new PlatformSecurityOptions
            {
                Mfa = new PlatformSecurityOptions.MfaOptions { SuperAdminEnforcement = enforcement }
            };

            var filter = new RequireMfaEnrollmentFilter(
                context,
                new StaticOptionsMonitor<PlatformSecurityOptions>(options),
                NullLogger<RequireMfaEnrollmentFilter>.Instance);

            var httpContext = new DefaultHttpContext { User = principal };

            var endpointMetadata = new List<object>();
            if (excludedAction)
            {
                endpointMetadata.Add(new AllowWithoutMfaEnrollmentAttribute());
            }

            var actionContext = new ActionContext(
                httpContext,
                new RouteData(),
                new ActionDescriptor { EndpointMetadata = endpointMetadata });

            var executingContext = new ActionExecutingContext(
                actionContext,
                new List<IFilterMetadata>(),
                new Dictionary<string, object?>(),
                controller: new object());

            var nextCalled = false;
            Task<ActionExecutedContext> Next()
            {
                nextCalled = true;
                return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object()));
            }

            await filter.OnActionExecutionAsync(executingContext, Next);
            return (executingContext.Result, nextCalled);
        }
    }
}
