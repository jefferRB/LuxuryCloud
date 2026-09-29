using System.Security.Claims;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class TenantExecutionInfrastructureTests
    {
        [Fact]
        public async Task TenantExecutionService_ShouldExposeAmbientTenantToBackgroundScopes()
        {
            var connectionString = $"Data Source=file:tenant-execution-{Guid.NewGuid():N}?mode=memory&cache=shared";
            using var rootConnection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
            rootConnection.Open();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddHttpContextAccessor();
            services.AddSingleton<ITenantExecutionContextAccessor, TenantExecutionContextAccessor>();
            services.AddScoped<ITenantProvider, TenantProvider>();
            services.AddScoped<ApplicationDbContext>(serviceProvider =>
            {
                var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                    .UseSqlite(rootConnection)
                    .Options;

                return new ApplicationDbContext(
                    options,
                    serviceProvider.GetRequiredService<ITenantProvider>(),
                    NullLogger<ApplicationDbContext>.Instance);
            });
            services.AddSingleton<TenantExecutionService>();

            await using var provider = services.BuildServiceProvider();

            using (var seedScope = provider.CreateScope())
            {
                var context = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await context.Database.EnsureCreatedAsync();

                context.Tenants.AddRange(
                    new Tenant { Id = Guid.NewGuid(), Nombre = "Tenant A", Activo = true },
                    new Tenant { Id = Guid.NewGuid(), Nombre = "Tenant B", Activo = true });

                await context.SaveChangesAsync();
            }

            var seenTenants = new HashSet<Guid>();
            var executionService = provider.GetRequiredService<TenantExecutionService>();

            await executionService.RunForEachActiveTenantAsync(async (serviceProvider, tenantId, cancellationToken) =>
            {
                var tenantProvider = serviceProvider.GetRequiredService<ITenantProvider>();
                seenTenants.Add(tenantProvider.GetTenantId());
                await Task.CompletedTask;
            });

            Assert.Equal(2, seenTenants.Count);
        }

        [Fact]
        [Trait("Category", "SqlServer")]
        public async Task TenantSessionConnectionInterceptor_ShouldApplySessionContext()
        {
            var tenantProvider = new TestTenantProvider
            {
                TenantId = Guid.NewGuid()
            };

            var interceptor = new TenantSessionConnectionInterceptor(
                tenantProvider,
                NullLogger<TenantSessionConnectionInterceptor>.Instance);

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(Support.SqlServerTestConnection.Master)
                .AddInterceptors(interceptor)
                .Options;

            await using var context = new ApplicationDbContext(
                options,
                tenantProvider,
                NullLogger<ApplicationDbContext>.Instance);

            await context.Database.OpenConnectionAsync();
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)";

            var result = await command.ExecuteScalarAsync();

            Assert.Equal(tenantProvider.TenantId, (Guid)result!);
        }

        [Fact]
        [Trait("Category", "SqlServer")]
        public async Task TenantSessionConnectionInterceptor_ShouldClearSessionContextWhenTenantIsMissing()
        {
            var tenantProvider = new TestTenantProvider();

            var interceptor = new TenantSessionConnectionInterceptor(
                tenantProvider,
                NullLogger<TenantSessionConnectionInterceptor>.Instance);

            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlServer(Support.SqlServerTestConnection.Master)
                .AddInterceptors(interceptor)
                .Options;

            await using var context = new ApplicationDbContext(
                options,
                tenantProvider,
                NullLogger<ApplicationDbContext>.Instance);

            await context.Database.OpenConnectionAsync();
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT SESSION_CONTEXT(N'TenantId')";

            var result = await command.ExecuteScalarAsync();

            Assert.True(result is null or DBNull);
        }

        [Fact]
        public async Task SuscripcionMiddleware_ShouldRedirectUsersWithoutSubscription()
        {
            var tenantId = Guid.NewGuid();
            var httpContextAccessor = new HttpContextAccessor();
            var tenantProvider = new TenantProvider(httpContextAccessor, new TenantExecutionContextAccessor());
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider { TenantId = tenantId });

            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant sin suscripcion",
                Activo = true
            });

            context.Users.Add(new AppUsuario
            {
                Id = "middleware-user",
                UserName = "middleware-user@test.local",
                NormalizedUserName = "MIDDLEWARE-USER@TEST.LOCAL",
                Email = "middleware-user@test.local",
                NormalizedEmail = "MIDDLEWARE-USER@TEST.LOCAL",
                TenantId = tenantId,
                State = true,
                SecurityStamp = Guid.NewGuid().ToString("N")
            });

            await context.SaveChangesAsync();

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/Productos";
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "middleware-user"),
                    new Claim(CustomClaimTypes.UserId, "middleware-user"),
                    new Claim(CustomClaimTypes.TenantId, tenantId.ToString()),
                    new Claim(ClaimTypes.Name, "usuario@test.local")
                },
                authenticationType: "TestAuth"));

            httpContextAccessor.HttpContext = httpContext;

            var middleware = new SuscripcionMiddleware(
                _ => Task.CompletedTask,
                NullLogger<SuscripcionMiddleware>.Instance);

            await middleware.Invoke(httpContext, context, CreateResolver(context));

            Assert.Equal("/Billing/SinSuscripcion", httpContext.Response.Headers.Location.ToString());
            Assert.Equal(StatusCodes.Status302Found, httpContext.Response.StatusCode);
        }

        private static ITenantCommercialAccessResolver CreateResolver(ApplicationDbContext context)
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
    }
}
