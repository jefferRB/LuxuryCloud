using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class PlatformUserAdminServiceTests
    {
        private const string SuperAdminPassword = "Sup3r!Pass";
        private const string ValidReason = "Baja solicitada por el dueño del negocio.";

        [Fact]
        public async Task Deactivate_WithWrongPassword_DoesNotChangeStateAndAuditsFailure()
        {
            await using var fixture = await PlatformUserAdminFixture.CreateAsync();
            var target = await fixture.CreateUserAsync("user@tenant.com", fixture.TenantId);

            var result = await fixture.Service.DeactivateUserAsync(new DeactivatePlatformUserCommand
            {
                UserId = target.Id,
                ExpectedTenantId = fixture.TenantId,
                CurrentSuperAdminId = fixture.SuperAdminId,
                CurrentPassword = "incorrecta",
                ConfirmationEmail = "user@tenant.com",
                ConfirmationTenantName = fixture.TenantName,
                Reason = ValidReason
            });

            Assert.False(result.Success);

            var reloaded = await fixture.ReloadUserAsync(target.Id);
            Assert.True(reloaded.State);

            Assert.Equal(1, await fixture.CountAuditAsync(PlatformAuditActions.DangerousActionPasswordFailed));
            Assert.Equal(0, await fixture.CountAuditAsync(PlatformAuditActions.UserDeactivated));
        }

        [Fact]
        public async Task Deactivate_Self_IsBlocked()
        {
            await using var fixture = await PlatformUserAdminFixture.CreateAsync();

            var result = await fixture.Service.DeactivateUserAsync(new DeactivatePlatformUserCommand
            {
                UserId = fixture.SuperAdminId,
                ExpectedTenantId = fixture.TenantId,
                CurrentSuperAdminId = fixture.SuperAdminId,
                CurrentPassword = SuperAdminPassword,
                ConfirmationEmail = fixture.SuperAdminEmail,
                ConfirmationTenantName = fixture.TenantName,
                Reason = ValidReason
            });

            Assert.False(result.Success);
            var reloaded = await fixture.ReloadUserAsync(fixture.SuperAdminId);
            Assert.True(reloaded.State);
            Assert.Equal(1, await fixture.CountAuditAsync(PlatformAuditActions.DangerousActionBlocked));
        }

        [Fact]
        public async Task Deactivate_LastSuperAdmin_IsBlocked()
        {
            await using var fixture = await PlatformUserAdminFixture.CreateAsync();
            // Otro SuperAdmin, pero inactivo: no cuenta como respaldo.
            var inactiveSuper = await fixture.CreateUserAsync("super2@tenant.com", fixture.TenantId, isSuperAdmin: true, active: false);
            _ = inactiveSuper;

            var result = await fixture.Service.ValidateCanDeactivateAsync(
                fixture.SuperAdminId, "another-admin", fixture.TenantId);

            // El target sigue activo y es el único SuperAdmin activo.
            Assert.False(result.CanProceed);
        }

        [Fact]
        public async Task Deactivate_WithTenantMismatch_IsBlockedAsIdorAttempt()
        {
            await using var fixture = await PlatformUserAdminFixture.CreateAsync();
            var target = await fixture.CreateUserAsync("victim@tenant.com", fixture.TenantId);
            var otherTenantId = Guid.NewGuid();

            var result = await fixture.Service.DeactivateUserAsync(new DeactivatePlatformUserCommand
            {
                UserId = target.Id,
                ExpectedTenantId = otherTenantId, // manipulado
                CurrentSuperAdminId = fixture.SuperAdminId,
                CurrentPassword = SuperAdminPassword,
                ConfirmationEmail = "victim@tenant.com",
                ConfirmationTenantName = fixture.TenantName,
                Reason = ValidReason
            });

            Assert.False(result.Success);
            var reloaded = await fixture.ReloadUserAsync(target.Id);
            Assert.True(reloaded.State);
            Assert.Equal(1, await fixture.CountAuditAsync(PlatformAuditActions.DangerousActionBlocked));
        }

        [Fact]
        public async Task Deactivate_Valid_SetsStateFalseRotatesStampAndAudits()
        {
            await using var fixture = await PlatformUserAdminFixture.CreateAsync();
            var target = await fixture.CreateUserAsync("ok@tenant.com", fixture.TenantId);
            var stampBefore = (await fixture.ReloadUserAsync(target.Id)).SecurityStamp;

            var result = await fixture.Service.DeactivateUserAsync(new DeactivatePlatformUserCommand
            {
                UserId = target.Id,
                ExpectedTenantId = fixture.TenantId,
                CurrentSuperAdminId = fixture.SuperAdminId,
                CurrentPassword = SuperAdminPassword,
                ConfirmationEmail = "ok@tenant.com",
                ConfirmationTenantName = fixture.TenantName,
                Reason = ValidReason
            });

            Assert.True(result.Success);

            var reloaded = await fixture.ReloadUserAsync(target.Id);
            Assert.False(reloaded.State);
            Assert.NotEqual(stampBefore, reloaded.SecurityStamp);
            Assert.Equal(1, await fixture.CountAuditAsync(PlatformAuditActions.UserDeactivated));
        }

        [Fact]
        public async Task Deactivate_AlreadyInactive_IsIdempotent()
        {
            await using var fixture = await PlatformUserAdminFixture.CreateAsync();
            var target = await fixture.CreateUserAsync("already@tenant.com", fixture.TenantId, active: false);

            var result = await fixture.Service.DeactivateUserAsync(new DeactivatePlatformUserCommand
            {
                UserId = target.Id,
                ExpectedTenantId = fixture.TenantId,
                CurrentSuperAdminId = fixture.SuperAdminId,
                CurrentPassword = SuperAdminPassword,
                ConfirmationEmail = "already@tenant.com",
                ConfirmationTenantName = fixture.TenantName,
                Reason = ValidReason
            });

            Assert.True(result.Success);
            Assert.Equal(0, await fixture.CountAuditAsync(PlatformAuditActions.UserDeactivated));
        }

        [Fact]
        public async Task Reactivate_Valid_SetsStateTrueAndAudits()
        {
            await using var fixture = await PlatformUserAdminFixture.CreateAsync();
            var target = await fixture.CreateUserAsync("rev@tenant.com", fixture.TenantId, active: false);

            var result = await fixture.Service.ReactivateUserAsync(new DeactivatePlatformUserCommand
            {
                UserId = target.Id,
                ExpectedTenantId = fixture.TenantId,
                CurrentSuperAdminId = fixture.SuperAdminId,
                CurrentPassword = SuperAdminPassword,
                Reason = "El colaborador regresa."
            });

            Assert.True(result.Success);
            var reloaded = await fixture.ReloadUserAsync(target.Id);
            Assert.True(reloaded.State);
            Assert.Equal(1, await fixture.CountAuditAsync(PlatformAuditActions.UserReactivated));
        }

        private sealed class PlatformUserAdminFixture : IAsyncDisposable
        {
            private readonly ServiceProvider _provider;
            private readonly IServiceScope _scope;

            public string TenantName => "Negocio Demo";
            public Guid TenantId { get; private set; }
            public string SuperAdminId { get; private set; } = string.Empty;
            public string SuperAdminEmail => "superadmin@plataforma.com";
            public IPlatformUserAdminService Service { get; private set; } = default!;

            private ApplicationDbContext Context => _scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            private UserManager<AppUsuario> UserManager => _scope.ServiceProvider.GetRequiredService<UserManager<AppUsuario>>();

            private PlatformUserAdminFixture(ServiceProvider provider)
            {
                _provider = provider;
                _scope = provider.CreateScope();
            }

            public static async Task<PlatformUserAdminFixture> CreateAsync()
            {
                var connection = new SqliteConnection("DataSource=:memory:");
                await connection.OpenAsync();

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddOptions();
                services.AddHttpContextAccessor();
                services.AddDataProtection();
                services.AddSingleton(connection);
                services.AddSingleton<ITenantExecutionContextAccessor, TenantExecutionContextAccessor>();
                services.AddScoped<ITenantProvider, TenantProvider>();
                services.Configure<IdentityOptions>(options =>
                {
                    options.Password.RequiredLength = 6;
                    options.Password.RequireUppercase = true;
                    options.Password.RequireNonAlphanumeric = true;
                    options.Password.RequireDigit = true;
                    options.User.RequireUniqueEmail = false;
                });
                services.AddDbContext<ApplicationDbContext>((sp, options) =>
                    options.UseSqlite(sp.GetRequiredService<SqliteConnection>()));
                services
                    .AddIdentity<AppUsuario, IdentityRole>()
                    .AddEntityFrameworkStores<ApplicationDbContext>()
                    .AddDefaultTokenProviders();
                services.AddScoped<IPlatformAuditService, PlatformAuditService>();
                services.AddScoped<IPlatformUserAdminService, PlatformUserAdminService>();

                var provider = services.BuildServiceProvider();

                using (var setupScope = provider.CreateScope())
                {
                    var ctx = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await ctx.Database.EnsureCreatedAsync();
                }

                var fixture = new PlatformUserAdminFixture(provider);
                await fixture.SeedAsync();
                return fixture;
            }

            private async Task SeedAsync()
            {
                TenantId = Guid.NewGuid();
                Context.Tenants.Add(new Tenant { Id = TenantId, Nombre = TenantName, Activo = true });
                await Context.SaveChangesAsync();

                var roleManager = _scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                await roleManager.CreateAsync(new IdentityRole(AppRoles.Administrador));

                var superAdmin = new AppUsuario
                {
                    Id = Guid.NewGuid().ToString(),
                    UserName = SuperAdminEmail,
                    Email = SuperAdminEmail,
                    TenantId = TenantId,
                    State = true,
                    IsPlatformSuperAdmin = true,
                    EmailConfirmed = true
                };
                var created = await UserManager.CreateAsync(superAdmin, SuperAdminPassword);
                Assert.True(created.Succeeded);
                SuperAdminId = superAdmin.Id;

                Service = _scope.ServiceProvider.GetRequiredService<IPlatformUserAdminService>();
            }

            public async Task<AppUsuario> CreateUserAsync(
                string email,
                Guid tenantId,
                bool isSuperAdmin = false,
                bool active = true,
                bool isAdminRole = false)
            {
                var user = new AppUsuario
                {
                    Id = Guid.NewGuid().ToString(),
                    UserName = email,
                    Email = email,
                    TenantId = tenantId,
                    State = active,
                    IsPlatformSuperAdmin = isSuperAdmin,
                    EmailConfirmed = true
                };
                var created = await UserManager.CreateAsync(user, SuperAdminPassword);
                Assert.True(created.Succeeded);

                if (isAdminRole)
                {
                    await UserManager.AddToRoleAsync(user, AppRoles.Administrador);
                }

                return user;
            }

            public async Task<AppUsuario> ReloadUserAsync(string userId)
            {
                using var scope = _provider.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                return await ctx.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
            }

            public async Task<int> CountAuditAsync(string action)
            {
                using var scope = _provider.CreateScope();
                var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                return await ctx.PlatformAuditLogs.IgnoreQueryFilters().CountAsync(log => log.Action == action);
            }

            public async ValueTask DisposeAsync()
            {
                _scope.Dispose();
                await _provider.DisposeAsync();
            }
        }
    }
}
