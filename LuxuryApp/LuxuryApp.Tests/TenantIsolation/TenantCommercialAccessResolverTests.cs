using System.Security.Claims;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class TenantCommercialAccessResolverTests
    {
        [Fact]
        public async Task PlatformSuperAdmin_ShouldAccessWithoutSubscription()
        {
            var (context, connection, resolver, _) = CreateServices();
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

            var user = new AppUsuario
            {
                Id = userId,
                UserName = "platform.owner@example.com",
                NormalizedUserName = "PLATFORM.OWNER@EXAMPLE.COM",
                Email = "platform.owner@example.com",
                NormalizedEmail = "PLATFORM.OWNER@EXAMPLE.COM",
                TenantId = tenantId,
                State = true,
                IsPlatformSuperAdmin = true,
                SecurityStamp = Guid.NewGuid().ToString("N")
            };

            context.Users.Add(user);
            await context.SaveChangesAsync();

            var validator = new TenantSessionSecurityValidator(
                context,
                NullLogger<TenantSessionSecurityValidator>.Instance);

            var principal = BuildPrincipal(userId, tenantId, isPlatformSuperAdmin: true);
            Assert.True(await validator.ValidateAsync(principal));

            var access = await resolver.ResolveAsync(tenantId, user);

            Assert.True(access.CanAccessApp);
            Assert.Equal(TenantCommercialAccessSource.PlatformSuperAdmin, access.AccessSource);
            Assert.False(access.RequiresBilling);
        }

        [Fact]
        public async Task ExemptTenant_ShouldAccessWithoutSubscription()
        {
            var (context, connection, resolver, _) = CreateServices();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

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
                Nombre = "Socio",
                Activo = true,
                CommercialAccessMode = TenantCommercialAccessMode.Exempt,
                ForcedPlanId = planId
            });

            await context.SaveChangesAsync();

            var access = await resolver.ResolveAsync(tenantId);

            Assert.True(access.CanAccessApp);
            Assert.Equal(TenantCommercialAccessSource.TenantExempt, access.AccessSource);
            Assert.Equal("Full", access.EffectivePlanName);
            Assert.False(access.RequiresBilling);
        }

        [Fact]
        public async Task NormalTenant_WithoutCommercialEntitlement_ShouldRequireSubscription()
        {
            var (context, connection, resolver, _) = CreateServices();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Normal",
                Activo = true
            });

            await context.SaveChangesAsync();

            var access = await resolver.ResolveAsync(tenantId);

            Assert.False(access.CanAccessApp);
            Assert.True(access.RequiresBilling);
            Assert.False(access.HasCommercialHistory);
        }

        [Fact]
        public async Task PromotionalCode_ShouldActivateTemporaryAccessWithoutCard()
        {
            var (context, connection, resolver, promoService) = CreateServices();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var user = CreateUser("promo-user", tenantId, "promo@test.local");

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
                Nombre = "Promo Tenant",
                Activo = true
            });

            context.Users.Add(user);
            context.PromotionalCodes.Add(new PromotionalCode
            {
                Id = Guid.NewGuid(),
                Codigo = "VIP30",
                Activo = true,
                DiasGratis = 30,
                PlanId = planId,
                MaxUsos = 1,
                SoloPrimerRegistro = true
            });

            await context.SaveChangesAsync();

            var redemption = await promoService.RedeemAsync("VIP30", tenantId, user);
            Assert.True(redemption.Succeeded);
            Assert.NotNull(redemption.AccessGrant);

            var access = await resolver.ResolveAsync(tenantId);

            Assert.True(access.CanAccessApp);
            Assert.Equal(TenantCommercialAccessSource.PromotionalGrant, access.AccessSource);
            Assert.False(access.RequiresBilling);
            Assert.Equal("Full", access.EffectivePlanName);
            Assert.True(access.AccessEndsUtc > DateTime.UtcNow.AddDays(29));
        }

        [Fact]
        public async Task ExpiredPromotionalCode_ShouldFail()
        {
            var (context, connection, _, promoService) = CreateServices();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var user = CreateUser("expired-user", tenantId, "expired@test.local");

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
                Nombre = "Expired Promo Tenant",
                Activo = true
            });

            context.Users.Add(user);
            context.PromotionalCodes.Add(new PromotionalCode
            {
                Id = Guid.NewGuid(),
                Codigo = "EXPIRADO",
                Activo = true,
                DiasGratis = 30,
                PlanId = planId,
                FechaExpiracionUtc = DateTime.UtcNow.AddDays(-1)
            });

            await context.SaveChangesAsync();

            var redemption = await promoService.RedeemAsync("EXPIRADO", tenantId, user);

            Assert.False(redemption.Succeeded);
            Assert.Contains("expir", redemption.Error!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task SingleUsePromotionalCode_ShouldNotBeReusable()
        {
            var (context, connection, _, promoService) = CreateServices();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var planId = Guid.NewGuid();
            var firstTenantId = Guid.NewGuid();
            var secondTenantId = Guid.NewGuid();

            context.Planes.Add(new Plan
            {
                Id = planId,
                Nombre = "Full",
                PrecioMensual = 99,
                Moneda = "CRC",
                Activo = true
            });

            context.Tenants.AddRange(
                new Tenant { Id = firstTenantId, Nombre = "Tenant A", Activo = true },
                new Tenant { Id = secondTenantId, Nombre = "Tenant B", Activo = true });

            var firstUser = CreateUser("first-user", firstTenantId, "first@test.local");
            var secondUser = CreateUser("second-user", secondTenantId, "second@test.local");

            context.Users.AddRange(firstUser, secondUser);
            context.PromotionalCodes.Add(new PromotionalCode
            {
                Id = Guid.NewGuid(),
                Codigo = "UNICO30",
                Activo = true,
                DiasGratis = 30,
                PlanId = planId,
                MaxUsos = 1
            });

            await context.SaveChangesAsync();

            var firstRedemption = await promoService.RedeemAsync("UNICO30", firstTenantId, firstUser);
            var secondRedemption = await promoService.RedeemAsync("UNICO30", secondTenantId, secondUser);

            Assert.True(firstRedemption.Succeeded);
            Assert.False(secondRedemption.Succeeded);
            Assert.Contains("uso", secondRedemption.Error!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task PromotionalCode_WithDifferentTargetEmail_ShouldFail()
        {
            var (context, connection, _, promoService) = CreateServices();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();
            var user = CreateUser("email-user", tenantId, "cliente@test.local");

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
                Nombre = "Email Tenant",
                Activo = true
            });

            context.Users.Add(user);
            context.PromotionalCodes.Add(new PromotionalCode
            {
                Id = Guid.NewGuid(),
                Codigo = "EMAIL30",
                Activo = true,
                DiasGratis = 30,
                PlanId = planId,
                EmailObjetivo = "otro@test.local"
            });

            await context.SaveChangesAsync();

            var redemption = await promoService.RedeemAsync("EMAIL30", tenantId, user);

            Assert.False(redemption.Succeeded);
            Assert.Contains("correo", redemption.Error!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ExpiredPromotionalGrant_ShouldReturnToSubscriptionRequirement()
        {
            var (context, connection, resolver, _) = CreateServices();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var planId = Guid.NewGuid();

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
                Nombre = "Expired Grant Tenant",
                Activo = true
            });

            context.TenantCommercialAccessGrants.Add(new TenantCommercialAccessGrant
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                FechaInicioUtc = DateTime.UtcNow.AddDays(-40),
                FechaFinUtc = DateTime.UtcNow.AddDays(-10),
                Activo = true
            });

            await context.SaveChangesAsync();

            var access = await resolver.ResolveAsync(tenantId);

            Assert.False(access.CanAccessApp);
            Assert.True(access.RequiresBilling);
            Assert.True(access.HasCommercialHistory);
        }

        private static (ProyectoIdentity.Datos.ApplicationDbContext Context, IDisposable Connection, ITenantCommercialAccessResolver Resolver, IPromotionalCodeService PromoService) CreateServices()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            var cache = new MemoryCache(new MemoryCacheOptions());
            var accessCache = new TenantCommercialAccessCache(cache);
            var businessNow = DateTime.SpecifyKind(
                DateTime.UtcNow.AddHours(-6).AddMinutes(1),
                DateTimeKind.Unspecified);
            var businessDateTimeProvider = new FixedBusinessDateTimeProvider(businessNow);
            var subscriptionService = new SuscripcionService(
                context,
                cache,
                accessCache,
                businessDateTimeProvider,
                Options.Create(new TilopayRepeatOptions()),
                NullLogger<SuscripcionService>.Instance);

            return (
                context,
                connection,
                new TenantCommercialAccessResolver(
                    context,
                    cache,
                    accessCache,
                    subscriptionService,
                    businessDateTimeProvider),
                new PromotionalCodeService(context, accessCache));
        }

        private static AppUsuario CreateUser(string id, Guid tenantId, string email) =>
            new()
            {
                Id = id,
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                TenantId = tenantId,
                State = true,
                SecurityStamp = Guid.NewGuid().ToString("N")
            };

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
    }
}
