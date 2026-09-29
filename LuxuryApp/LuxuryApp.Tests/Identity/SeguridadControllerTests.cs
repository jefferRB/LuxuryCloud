using System.Security.Cryptography;
using System.Text.Json;
using LuxuryApp.Controllers.Identity;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Platform;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Identity
{
    public class SeguridadControllerTests
    {
        [Fact]
        public async Task Enrolar_ShouldGenerateSharedKeyAndOtpauthUri()
        {
            var fixture = await CreateFixtureAsync(isSuperAdmin: true, enforcement: true);
            using var disposable = fixture;

            var result = await fixture.Controller.Enrolar();

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<EnrolarMfaViewModel>(view.Model);
            Assert.False(model.TwoFactorActivo);
            Assert.NotEmpty(model.ClaveFormateada);
            Assert.StartsWith("otpauth://totp/LuxuryCloud:", model.OtpauthUri);
            Assert.Contains("issuer=LuxuryCloud", model.OtpauthUri);
        }

        [Fact]
        public async Task Confirmar_WithValidTotp_ShouldEnableTwoFactorAndGenerateRecoveryCodes()
        {
            var fixture = await CreateFixtureAsync(isSuperAdmin: true, enforcement: true);
            using var disposable = fixture;

            // El GET genera la clave; el código se calcula igual que lo haría la app del teléfono.
            await fixture.Controller.Enrolar();
            var clave = await fixture.UserManager.GetAuthenticatorKeyAsync(fixture.Usuario);
            var codigo = TotpTestHelper.ComputeCurrentCode(clave!);

            var result = await fixture.Controller.Confirmar(new EnrolarMfaViewModel { Codigo = codigo });

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(SeguridadController.CodigosRecuperacion), redirect.ActionName);

            var usuarioActualizado = await fixture.UserManager.FindByIdAsync(fixture.Usuario.Id);
            Assert.True(usuarioActualizado!.TwoFactorEnabled);

            var serialized = Assert.IsType<string>(fixture.Controller.TempData["MfaRecoveryCodes"]);
            var codigos = JsonSerializer.Deserialize<string[]>(serialized);
            Assert.NotNull(codigos);
            Assert.Equal(8, codigos!.Length);

            Assert.Contains(fixture.AuditService.Entries, entry => entry.Action == PlatformAuditActions.MfaEnabled);
        }

        [Fact]
        public async Task Confirmar_WithInvalidCode_ShouldNotEnableTwoFactor()
        {
            var fixture = await CreateFixtureAsync(isSuperAdmin: true, enforcement: true);
            using var disposable = fixture;

            await fixture.Controller.Enrolar();
            var clave = await fixture.UserManager.GetAuthenticatorKeyAsync(fixture.Usuario);
            var codigoValido = TotpTestHelper.ComputeCurrentCode(clave!);
            var codigoInvalido = TotpTestHelper.CorruptCode(codigoValido);

            var result = await fixture.Controller.Confirmar(new EnrolarMfaViewModel { Codigo = codigoInvalido });

            var view = Assert.IsType<ViewResult>(result);
            Assert.Equal(nameof(SeguridadController.Enrolar), view.ViewName);

            var usuarioActualizado = await fixture.UserManager.FindByIdAsync(fixture.Usuario.Id);
            Assert.False(usuarioActualizado!.TwoFactorEnabled);
            Assert.Empty(fixture.AuditService.Entries);
        }

        [Fact]
        public async Task Deshabilitar_SuperAdminWithEnforcement_ShouldBeForbidden()
        {
            var fixture = await CreateFixtureAsync(isSuperAdmin: true, enforcement: true, twoFactorEnabled: true);
            using var disposable = fixture;

            var result = await fixture.Controller.Deshabilitar();

            Assert.IsType<ForbidResult>(result);
            var usuarioActualizado = await fixture.UserManager.FindByIdAsync(fixture.Usuario.Id);
            Assert.True(usuarioActualizado!.TwoFactorEnabled);
        }

        [Fact]
        public async Task Deshabilitar_RegularUser_ShouldDisableWithoutPlatformAudit()
        {
            var fixture = await CreateFixtureAsync(isSuperAdmin: false, enforcement: true, twoFactorEnabled: true);
            using var disposable = fixture;

            var result = await fixture.Controller.Deshabilitar();

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal(nameof(SeguridadController.Enrolar), redirect.ActionName);

            var usuarioActualizado = await fixture.UserManager.FindByIdAsync(fixture.Usuario.Id);
            Assert.False(usuarioActualizado!.TwoFactorEnabled);
            Assert.Empty(fixture.AuditService.Entries);
        }

        [Fact]
        public async Task CerrarSesionEnTodosLosDispositivos_ShouldRotateSecurityStampAndRedirectToLogin()
        {
            var fixture = await CreateFixtureAsync(isSuperAdmin: false, enforcement: false);
            using var disposable = fixture;

            var stampBefore = (await fixture.UserManager.FindByIdAsync(fixture.Usuario.Id))!.SecurityStamp;

            var result = await fixture.Controller.CerrarSesionEnTodosLosDispositivos();

            var redirect = Assert.IsType<RedirectToActionResult>(result);
            Assert.Equal("Acceso", redirect.ActionName);
            Assert.Equal("Accounts", redirect.ControllerName);

            var stampAfter = (await fixture.UserManager.FindByIdAsync(fixture.Usuario.Id))!.SecurityStamp;
            Assert.NotEqual(stampBefore, stampAfter);
            Assert.False(string.IsNullOrWhiteSpace(stampAfter));
            Assert.NotNull(fixture.Controller.TempData["SeguridadMensaje"]);
        }

        private static async Task<SeguridadFixture> CreateFixtureAsync(
            bool isSuperAdmin,
            bool enforcement,
            bool twoFactorEnabled = false)
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid().ToString("N");

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant seguridad",
                Activo = true
            });

            var usuario = new AppUsuario
            {
                Id = userId,
                UserName = "seguridad@test.local",
                NormalizedUserName = "SEGURIDAD@TEST.LOCAL",
                Email = "seguridad@test.local",
                NormalizedEmail = "SEGURIDAD@TEST.LOCAL",
                TenantId = tenantId,
                State = true,
                IsPlatformSuperAdmin = isSuperAdmin,
                TwoFactorEnabled = twoFactorEnabled,
                SecurityStamp = Guid.NewGuid().ToString("N")
            };

            context.Users.Add(usuario);
            await context.SaveChangesAsync();

            var userManager = new UserManager<AppUsuario>(
                new UserStore<AppUsuario>(context),
                Options.Create(new IdentityOptions()),
                new PasswordHasher<AppUsuario>(),
                Enumerable.Empty<IUserValidator<AppUsuario>>(),
                Enumerable.Empty<IPasswordValidator<AppUsuario>>(),
                new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(),
                services: null!,
                NullLogger<UserManager<AppUsuario>>.Instance);

            userManager.RegisterTokenProvider(
                TokenOptions.DefaultAuthenticatorProvider,
                new AuthenticatorTokenProvider<AppUsuario>());

            var auditService = new FakeAuditService();
            var securityOptions = new PlatformSecurityOptions
            {
                Mfa = new PlatformSecurityOptions.MfaOptions { SuperAdminEnforcement = enforcement }
            };

            var controller = new SeguridadController(
                userManager,
                CreateSignInManager(userManager),
                auditService,
                new StaticOptionsMonitor<PlatformSecurityOptions>(securityOptions),
                NullLogger<SeguridadController>.Instance);

            var principal = ControllerTestSupport.BuildTenantPrincipal(userId, tenantId, isSuperAdmin);
            ControllerTestSupport.AttachHttpContext(controller, principal);

            return new SeguridadFixture(context, connection, userManager, controller, usuario, auditService);
        }

        private static SignInManager<AppUsuario> CreateSignInManager(UserManager<AppUsuario> userManager) =>
            new NoRefreshSignInManager(
                userManager,
                new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                new UserClaimsPrincipalFactory<AppUsuario>(userManager, Options.Create(new IdentityOptions())),
                Options.Create(new IdentityOptions()),
                NullLogger<SignInManager<AppUsuario>>.Instance,
                new NoSchemesProvider(),
                new DefaultUserConfirmation<AppUsuario>());

        /// <summary>RefreshSignInAsync requiere el pipeline de cookies real; en pruebas es no-op.</summary>
        private sealed class NoRefreshSignInManager : SignInManager<AppUsuario>
        {
            public NoRefreshSignInManager(
                UserManager<AppUsuario> userManager,
                Microsoft.AspNetCore.Http.IHttpContextAccessor contextAccessor,
                IUserClaimsPrincipalFactory<AppUsuario> claimsFactory,
                IOptions<IdentityOptions> optionsAccessor,
                Microsoft.Extensions.Logging.ILogger<SignInManager<AppUsuario>> logger,
                Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider schemes,
                IUserConfirmation<AppUsuario> confirmation)
                : base(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
            {
            }

            public override Task RefreshSignInAsync(AppUsuario user) => Task.CompletedTask;

            // SignOutAsync real requiere IAuthenticationService en el pipeline; en pruebas es no-op.
            public override Task SignOutAsync() => Task.CompletedTask;
        }

        private sealed class NoSchemesProvider : Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider
        {
            public void AddScheme(Microsoft.AspNetCore.Authentication.AuthenticationScheme scheme)
            {
            }

            public void RemoveScheme(string name)
            {
            }

            public Task<IEnumerable<Microsoft.AspNetCore.Authentication.AuthenticationScheme>> GetAllSchemesAsync() =>
                Task.FromResult(Enumerable.Empty<Microsoft.AspNetCore.Authentication.AuthenticationScheme>());

            public Task<Microsoft.AspNetCore.Authentication.AuthenticationScheme?> GetSchemeAsync(string name) =>
                Task.FromResult<Microsoft.AspNetCore.Authentication.AuthenticationScheme?>(null);

            public Task<IEnumerable<Microsoft.AspNetCore.Authentication.AuthenticationScheme>> GetRequestHandlerSchemesAsync() =>
                Task.FromResult(Enumerable.Empty<Microsoft.AspNetCore.Authentication.AuthenticationScheme>());

            public Task<Microsoft.AspNetCore.Authentication.AuthenticationScheme?> GetDefaultAuthenticateSchemeAsync() =>
                Task.FromResult<Microsoft.AspNetCore.Authentication.AuthenticationScheme?>(null);

            public Task<Microsoft.AspNetCore.Authentication.AuthenticationScheme?> GetDefaultChallengeSchemeAsync() =>
                Task.FromResult<Microsoft.AspNetCore.Authentication.AuthenticationScheme?>(null);

            public Task<Microsoft.AspNetCore.Authentication.AuthenticationScheme?> GetDefaultForbidSchemeAsync() =>
                Task.FromResult<Microsoft.AspNetCore.Authentication.AuthenticationScheme?>(null);

            public Task<Microsoft.AspNetCore.Authentication.AuthenticationScheme?> GetDefaultSignInSchemeAsync() =>
                Task.FromResult<Microsoft.AspNetCore.Authentication.AuthenticationScheme?>(null);

            public Task<Microsoft.AspNetCore.Authentication.AuthenticationScheme?> GetDefaultSignOutSchemeAsync() =>
                Task.FromResult<Microsoft.AspNetCore.Authentication.AuthenticationScheme?>(null);
        }

        private sealed class SeguridadFixture : IDisposable
        {
            public SeguridadFixture(
                ApplicationDbContext context,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                UserManager<AppUsuario> userManager,
                SeguridadController controller,
                AppUsuario usuario,
                FakeAuditService auditService)
            {
                Context = context;
                Connection = connection;
                UserManager = userManager;
                Controller = controller;
                Usuario = usuario;
                AuditService = auditService;
            }

            public ApplicationDbContext Context { get; }
            public Microsoft.Data.Sqlite.SqliteConnection Connection { get; }
            public UserManager<AppUsuario> UserManager { get; }
            public SeguridadController Controller { get; }
            public AppUsuario Usuario { get; }
            public FakeAuditService AuditService { get; }

            public void Dispose()
            {
                UserManager.Dispose();
                Context.Dispose();
                Connection.Dispose();
            }
        }

        private sealed class FakeAuditService : IPlatformAuditService
        {
            public List<PlatformAuditEntry> Entries { get; } = new();

            public Task LogAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default)
            {
                Entries.Add(entry);
                return Task.CompletedTask;
            }

            public Task TryLogAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default) =>
                LogAsync(entry, cancellationToken);

            public Task<IReadOnlyList<PlatformAuditLog>> GetRecentAsync(int take = 100, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());

            public Task<IReadOnlyList<PlatformAuditLog>> GetByTenantAsync(Guid tenantId, int take = 100, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());

            public Task<IReadOnlyList<PlatformAuditLog>> GetByUserAsync(string targetUserId, int take = 100, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());

            public Task<int> CountActorFailuresSinceAsync(string actorUserId, DateTime sinceUtc, CancellationToken cancellationToken = default) =>
                Task.FromResult(0);
        }
    }

    /// <summary>
    /// TOTP de referencia (RFC 6238, paso de 30 s, 6 dígitos, HMAC-SHA1): calcula el mismo
    /// código que generaría la aplicación del teléfono a partir de la clave Base32 de Identity.
    /// </summary>
    internal static class TotpTestHelper
    {
        public static string ComputeCurrentCode(string base32Key)
        {
            var keyBytes = Base32Decode(base32Key);
            var unixTimestamp = (long)Math.Round((DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds);
            var timestep = (ulong)(unixTimestamp / 30);

            var timestepBytes = BitConverter.GetBytes(timestep);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(timestepBytes);
            }

            using var hmac = new HMACSHA1(keyBytes);
            var hash = hmac.ComputeHash(timestepBytes);

            var offset = hash[^1] & 0xf;
            var binaryCode = ((hash[offset] & 0x7f) << 24)
                | ((hash[offset + 1] & 0xff) << 16)
                | ((hash[offset + 2] & 0xff) << 8)
                | (hash[offset + 3] & 0xff);

            return (binaryCode % 1_000_000).ToString("D6");
        }

        /// <summary>Altera el último dígito para garantizar un código inválido.</summary>
        public static string CorruptCode(string code)
        {
            var lastDigit = (code[^1] - '0' + 5) % 10;
            return code[..^1] + (char)('0' + lastDigit);
        }

        private static byte[] Base32Decode(string input)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            input = input.Trim().TrimEnd('=').ToUpperInvariant();

            var bits = 0;
            var value = 0;
            var output = new List<byte>();

            foreach (var character in input)
            {
                value = (value << 5) | alphabet.IndexOf(character);
                bits += 5;

                if (bits >= 8)
                {
                    output.Add((byte)((value >> (bits - 8)) & 0xff));
                    bits -= 8;
                }
            }

            return output.ToArray();
        }
    }
}
