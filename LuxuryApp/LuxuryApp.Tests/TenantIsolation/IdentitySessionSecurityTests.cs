using System.Security.Claims;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class IdentitySessionSecurityTests
    {
        [Fact]
        public async Task TenantSessionSecurityValidator_ShouldRejectSuspendedTenant()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid().ToString("N");

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant suspendido",
                Activo = false
            });

            context.Users.Add(new AppUsuario
            {
                Id = userId,
                UserName = "owner@test.local",
                NormalizedUserName = "OWNER@TEST.LOCAL",
                Email = "owner@test.local",
                NormalizedEmail = "OWNER@TEST.LOCAL",
                TenantId = tenantId,
                State = true,
                SecurityStamp = Guid.NewGuid().ToString("N")
            });

            await context.SaveChangesAsync();

            var principal = BuildPrincipal(userId, tenantId);
            var validator = new TenantSessionSecurityValidator(
                context,
                NullLogger<TenantSessionSecurityValidator>.Instance);

            var isValid = await validator.ValidateAsync(principal);

            Assert.False(isValid);
        }

        [Fact]
        public async Task TenantSessionSecurityValidator_ShouldRejectTenantClaimMismatch()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var actualTenantId = Guid.NewGuid();
            var staleTenantId = Guid.NewGuid();
            var userId = Guid.NewGuid().ToString("N");

            context.Tenants.Add(new Tenant
            {
                Id = actualTenantId,
                Nombre = "Tenant actual",
                Activo = true
            });

            context.Users.Add(new AppUsuario
            {
                Id = userId,
                UserName = "owner@test.local",
                NormalizedUserName = "OWNER@TEST.LOCAL",
                Email = "owner@test.local",
                NormalizedEmail = "OWNER@TEST.LOCAL",
                TenantId = actualTenantId,
                State = true,
                SecurityStamp = Guid.NewGuid().ToString("N")
            });

            await context.SaveChangesAsync();

            var principal = BuildPrincipal(userId, staleTenantId);
            var validator = new TenantSessionSecurityValidator(
                context,
                NullLogger<TenantSessionSecurityValidator>.Instance);

            var isValid = await validator.ValidateAsync(principal);

            Assert.False(isValid);
        }

        [Fact]
        public async Task TenantSessionSecurityValidator_ShouldRejectRevokedPlatformSuperAdminClaim()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid().ToString("N");

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant activo",
                Activo = true
            });

            context.Users.Add(new AppUsuario
            {
                Id = userId,
                UserName = "exadmin@test.local",
                NormalizedUserName = "EXADMIN@TEST.LOCAL",
                Email = "exadmin@test.local",
                NormalizedEmail = "EXADMIN@TEST.LOCAL",
                TenantId = tenantId,
                State = true,
                IsPlatformSuperAdmin = false,
                SecurityStamp = Guid.NewGuid().ToString("N")
            });

            await context.SaveChangesAsync();

            // Cookie viva emitida cuando el usuario aún era superadmin: el claim dice True,
            // la BD ya dice false. Debe expulsarse en el siguiente request, no al expirar.
            var principal = BuildPrincipal(userId, tenantId, isPlatformSuperAdmin: true);
            var validator = new TenantSessionSecurityValidator(
                context,
                NullLogger<TenantSessionSecurityValidator>.Instance);

            var isValid = await validator.ValidateAsync(principal);

            Assert.False(isValid);
        }

        [Fact]
        public async Task TenantSessionSecurityValidator_ShouldAllowCurrentPlatformSuperAdmin()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid().ToString("N");

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant plataforma",
                Activo = true
            });

            context.Users.Add(new AppUsuario
            {
                Id = userId,
                UserName = "superadmin@test.local",
                NormalizedUserName = "SUPERADMIN@TEST.LOCAL",
                Email = "superadmin@test.local",
                NormalizedEmail = "SUPERADMIN@TEST.LOCAL",
                TenantId = tenantId,
                State = true,
                IsPlatformSuperAdmin = true,
                SecurityStamp = Guid.NewGuid().ToString("N")
            });

            await context.SaveChangesAsync();

            var principal = BuildPrincipal(userId, tenantId, isPlatformSuperAdmin: true);
            var validator = new TenantSessionSecurityValidator(
                context,
                NullLogger<TenantSessionSecurityValidator>.Instance);

            var isValid = await validator.ValidateAsync(principal);

            Assert.True(isValid);
        }

        [Fact]
        public async Task TenantSessionSecurityValidator_ShouldTreatExpectedCancellationAsNonFailure()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid().ToString("N");

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant activo",
                Activo = true
            });

            context.Users.Add(new AppUsuario
            {
                Id = userId,
                UserName = "owner@test.local",
                NormalizedUserName = "OWNER@TEST.LOCAL",
                Email = "owner@test.local",
                NormalizedEmail = "OWNER@TEST.LOCAL",
                TenantId = tenantId,
                State = true,
                SecurityStamp = Guid.NewGuid().ToString("N")
            });

            await context.SaveChangesAsync();

            var principal = BuildPrincipal(userId, tenantId);
            var validator = new TenantSessionSecurityValidator(
                context,
                NullLogger<TenantSessionSecurityValidator>.Instance);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var isValid = await validator.ValidateAsync(principal, cancellation.Token);

            Assert.True(isValid);
        }

        [Fact]
        public async Task SuscripcionMiddleware_ShouldRejectSuspendedTenantWithActiveCookie()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var httpContextAccessor = new HttpContextAccessor();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant suspendido",
                Activo = false
            });

            var planId = Guid.NewGuid();

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Base",
                PrecioMensual = 10,
                Moneda = "CRC",
                Activo = true
            });

            context.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                Estado = EstadoSuscripcion.Activa,
                FechaInicio = DateTime.UtcNow
            });

            context.Users.Add(new AppUsuario
            {
                Id = "user-1",
                UserName = "user-1@test.local",
                NormalizedUserName = "USER-1@TEST.LOCAL",
                Email = "user-1@test.local",
                NormalizedEmail = "USER-1@TEST.LOCAL",
                TenantId = tenantId,
                State = true,
                SecurityStamp = Guid.NewGuid().ToString("N")
            });

            await context.SaveChangesAsync();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/Productos";
            httpContext.User = BuildPrincipal(userId: "user-1", tenantId);
            httpContext.RequestServices = new ServiceCollection()
                .AddSingleton<IAuthenticationService, FakeAuthenticationService>()
                .BuildServiceProvider();
            httpContextAccessor.HttpContext = httpContext;

            var middleware = new SuscripcionMiddleware(
                _ => Task.CompletedTask,
                NullLogger<SuscripcionMiddleware>.Instance);

            await middleware.Invoke(httpContext, context, CreateResolver(context));

            Assert.Equal("/Accounts/Acceso", httpContext.Response.Headers.Location.ToString());
            Assert.Equal(StatusCodes.Status302Found, httpContext.Response.StatusCode);
        }

        [Fact]
        public async Task SuscripcionMiddleware_ShouldRedirectNormalTenantWithoutCommercialAccessToSinSuscripcion()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant sin acceso",
                Activo = true
            });

            context.Users.Add(new AppUsuario
            {
                Id = "user-no-access",
                UserName = "user-no-access@test.local",
                NormalizedUserName = "USER-NO-ACCESS@TEST.LOCAL",
                Email = "user-no-access@test.local",
                NormalizedEmail = "USER-NO-ACCESS@TEST.LOCAL",
                TenantId = tenantId,
                State = true,
                SecurityStamp = Guid.NewGuid().ToString("N")
            });

            await context.SaveChangesAsync();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/Productos";
            httpContext.User = BuildPrincipal(userId: "user-no-access", tenantId);

            var middleware = new SuscripcionMiddleware(
                _ => Task.CompletedTask,
                NullLogger<SuscripcionMiddleware>.Instance);

            await middleware.Invoke(httpContext, context, CreateResolver(context));

            Assert.Equal("/Billing/SinSuscripcion", httpContext.Response.Headers.Location.ToString());
            Assert.Equal(StatusCodes.Status302Found, httpContext.Response.StatusCode);
        }

        [Fact]
        public async Task SuscripcionMiddleware_ShouldAllowExemptTenantWithForcedPlan()
        {
            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Full",
                PrecioMensual = 99,
                Moneda = "CRC",
                Activo = true
            });

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant exento",
                Activo = true,
                CommercialAccessMode = TenantCommercialAccessMode.Exempt,
                ForcedPlanId = planId
            });

            context.Users.Add(new AppUsuario
            {
                Id = "user-exempt",
                UserName = "user-exempt@test.local",
                NormalizedUserName = "USER-EXEMPT@TEST.LOCAL",
                Email = "user-exempt@test.local",
                NormalizedEmail = "USER-EXEMPT@TEST.LOCAL",
                TenantId = tenantId,
                State = true,
                SecurityStamp = Guid.NewGuid().ToString("N")
            });

            await context.SaveChangesAsync();

            var nextCalled = false;
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/Productos";
            httpContext.User = BuildPrincipal(userId: "user-exempt", tenantId);

            var middleware = new SuscripcionMiddleware(
                context =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                },
                NullLogger<SuscripcionMiddleware>.Instance);

            await middleware.Invoke(httpContext, context, CreateResolver(context));

            Assert.True(nextCalled);
            Assert.False(httpContext.Response.Headers.ContainsKey("Location"));
            Assert.True(httpContext.Items.ContainsKey("TenantCommercialAccess"));
        }

        [Fact]
        public async Task SuscripcionMiddleware_ShouldAllowPlatformSuperAdmin()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var nextCalled = false;
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/Productos";
            httpContext.User = BuildPrincipal(userId: "superadmin-user", tenantId, isPlatformSuperAdmin: true);

            var middleware = new SuscripcionMiddleware(
                context =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                },
                NullLogger<SuscripcionMiddleware>.Instance);

            await middleware.Invoke(httpContext, context, CreateResolver(context));

            Assert.True(nextCalled);
            Assert.False(httpContext.Response.Headers.ContainsKey("Location"));
        }

        [Theory]
        [InlineData("/privacidad")]
        [InlineData("/Contract")]
        [InlineData("/eliminacion-datos")]
        public async Task SuscripcionMiddleware_ShouldAllowPublicLegalRoutesWithoutCommercialGate(string path)
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant publico",
                Activo = true
            });

            context.Users.Add(new AppUsuario
            {
                Id = "user-public-privacy",
                UserName = "user-public-privacy@test.local",
                NormalizedUserName = "USER-PUBLIC-PRIVACY@TEST.LOCAL",
                Email = "user-public-privacy@test.local",
                NormalizedEmail = "USER-PUBLIC-PRIVACY@TEST.LOCAL",
                TenantId = tenantId,
                State = true,
                SecurityStamp = Guid.NewGuid().ToString("N")
            });

            await context.SaveChangesAsync();

            var nextCalled = false;
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = path;
            httpContext.User = BuildPrincipal(userId: "user-public-privacy", tenantId);

            var middleware = new SuscripcionMiddleware(
                _ =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                },
                NullLogger<SuscripcionMiddleware>.Instance);

            await middleware.Invoke(httpContext, context, CreateResolver(context));

            Assert.True(nextCalled);
            Assert.False(httpContext.Response.Headers.ContainsKey("Location"));
            Assert.False(httpContext.Items.ContainsKey("TenantCommercialAccess"));
        }

        private static ClaimsPrincipal BuildPrincipal(string userId, Guid tenantId, bool isPlatformSuperAdmin = false)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId),
                new(CustomClaimTypes.UserId, userId),
                new(CustomClaimTypes.TenantId, tenantId.ToString())
            };

            if (isPlatformSuperAdmin)
            {
                claims.Add(new Claim(CustomClaimTypes.PlatformSuperAdmin, bool.TrueString));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "TestAuth"));
        }

        private static ITenantCommercialAccessResolver CreateResolver(ProyectoIdentity.Datos.ApplicationDbContext context)
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            var accessCache = new TenantCommercialAccessCache(cache);
            var subscriptionService = new SuscripcionService(
                context,
                cache,
                accessCache,
                new FixedBusinessDateTimeProvider(),
                Options.Create(new TilopayRepeatOptions()),
                NullLogger<SuscripcionService>.Instance);

            return new TenantCommercialAccessResolver(
                context,
                cache,
                accessCache,
                subscriptionService,
                new FixedBusinessDateTimeProvider());
        }

        private sealed class FakeAuthenticationService : IAuthenticationService
        {
            public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
                Task.FromResult(AuthenticateResult.NoResult());

            public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
                Task.CompletedTask;

            public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
                Task.CompletedTask;

            public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
                Task.CompletedTask;

            public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
                Task.CompletedTask;
        }
    }
}
