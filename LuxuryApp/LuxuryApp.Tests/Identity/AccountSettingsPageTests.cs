using LuxuryApp.Controllers.Configuracion;
using LuxuryApp.Models.Fiscal;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Account;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Services.Identity;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Identity
{
    /// <summary>
    /// Mi cuenta unificada: perfil, seguridad (2FA + sesiones) y, sólo para el administrador,
    /// la configuración fiscal que antes vivía en Opciones → Impuestos.
    ///
    /// <para>
    /// Lo que se prueba acá es la INTEGRACIÓN: que cada sección se arme con datos del usuario
    /// autenticado, que la sección fiscal no exista para quien no es administrador y que mover el
    /// formulario de IVA a esta pantalla no haya cambiado ni el guardado ni el aviso de impacto
    /// histórico. La criptografía del TOTP la cubre Identity y no se re-testea.
    /// </para>
    /// </summary>
    public class AccountSettingsPageTests
    {
        // ── El armado de la página ──

        [Fact]
        public async Task Build_ShouldUseAuthenticatedUserData()
        {
            using var fixture = await AccountPageFixture.CreateAsync();

            var page = await fixture.Builder.BuildAsync(fixture.AdminPrincipal);

            Assert.NotNull(page);
            Assert.Equal("dueno@test.local", page!.Profile.Email);
            Assert.Equal("Dueño", page.Profile.Name);
            Assert.Equal("+506 8888-8888", page.Profile.PhoneNumber);
            Assert.Equal("dueno@test.local", page.Security.Email);
        }

        [Fact]
        public async Task Build_ShouldReflectRealTwoFactorState()
        {
            using var fixture = await AccountPageFixture.CreateAsync();

            var sinMfa = await fixture.Builder.BuildAsync(fixture.AdminPrincipal);
            Assert.False(sinMfa!.Security.TwoFactorEnabled);

            await fixture.EnableTwoFactorAsync();

            var conMfa = await fixture.Builder.BuildAsync(fixture.AdminPrincipal);
            Assert.True(conMfa!.Security.TwoFactorEnabled);
        }

        /// <summary>
        /// El usuario corriente puede apagar su 2FA; el superadmin de plataforma no, cuando el
        /// enrolamiento obligatorio está encendido. Es la MISMA regla que aplica el endpoint
        /// (<see cref="MfaEnrollmentPolicy"/>): la pantalla no puede ofrecer un botón que el
        /// servidor va a rechazar.
        /// </summary>
        [Theory]
        [InlineData(false, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        public void MfaPolicy_ShouldMatchEndpointRule(
            bool esSuperAdmin,
            bool enforcementActivo,
            bool esperaPoderDeshabilitar)
        {
            var usuario = new AppUsuario { IsPlatformSuperAdmin = esSuperAdmin };
            var options = new PlatformSecurityOptions
            {
                Mfa = new PlatformSecurityOptions.MfaOptions { SuperAdminEnforcement = enforcementActivo }
            };

            Assert.Equal(esperaPoderDeshabilitar, MfaEnrollmentPolicy.PuedeDeshabilitar(usuario, options));
        }

        // ── Autorización de la sección fiscal ──

        [Fact]
        public async Task Build_ShouldIncludeFiscalSection_ForAdministrador()
        {
            using var fixture = await AccountPageFixture.CreateAsync();

            var page = await fixture.Builder.BuildAsync(fixture.AdminPrincipal);

            Assert.NotNull(page!.Fiscal);
            Assert.True(page.Fiscal!.PreciosIncluyenIva);
            Assert.Equal(13m, page.Fiscal.TarifaIvaPorDefecto);
        }

        /// <summary>
        /// Mover la configuración fiscal a Mi cuenta NO puede dársela a quien antes no la tenía.
        /// Para un usuario sin el rol Administrador la sección ni siquiera se construye, así que
        /// no llega al HTML: no depende de esconderla con CSS. El endpoint, además, sigue siendo
        /// <c>[Authorize(Roles="Administrador")]</c>.
        /// </summary>
        [Fact]
        public async Task Build_ShouldOmitFiscalSection_ForNonAdministrador()
        {
            using var fixture = await AccountPageFixture.CreateAsync();

            var page = await fixture.Builder.BuildAsync(fixture.FuncionarioPrincipal);

            Assert.NotNull(page);
            Assert.Null(page!.Fiscal);

            // El resto de la pantalla sí es suya.
            Assert.Equal("colaborador@test.local", page.Profile.Email);
        }

        [Fact]
        public void ConfiguracionFiscalController_ShouldStillRequireAdministrador()
        {
            var authorize = typeof(ConfiguracionFiscalController)
                .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), inherit: true)
                .OfType<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .SingleOrDefault();

            Assert.NotNull(authorize);
            Assert.Equal(AppRoles.Administrador, authorize!.Roles);
        }

        // ── Guardado fiscal: la lógica no cambió, sólo dónde se dibuja ──

        [Fact]
        public async Task GuardarFiscal_ShouldPersistAndRedirectToAccountSection()
        {
            using var fixture = await AccountPageFixture.CreateAsync();
            var controller = fixture.CreateFiscalController(cobrosLegacy: 0);

            var resultado = await controller.Index(
                new ConfiguracionFiscalViewModel { PreciosIncluyenIva = false, TarifaIvaPorDefecto = 4m },
                CancellationToken.None);

            // Post-Redirect-Get hacia la sección correcta de Mi cuenta.
            var redirect = Assert.IsType<RedirectToActionResult>(resultado);
            Assert.Equal("Cuenta", redirect.ActionName);
            Assert.Equal("Accounts", redirect.ControllerName);
            Assert.Equal(AccountSettingsSections.Fiscal, redirect.Fragment);

            var tenant = await fixture.Context.Tenants.AsNoTracking().SingleAsync(t => t.Id == fixture.TenantId);
            Assert.False(tenant.PreciosIncluyenIva);
            Assert.Equal(4m, tenant.TarifaIvaPorDefecto);
        }

        /// <summary>
        /// El aviso de impacto histórico sobrevivió a la mudanza: con cobros legacy el cambio NO
        /// se guarda hasta que el usuario confirma, y la pantalla que se repinta es Mi cuenta.
        /// </summary>
        [Fact]
        public async Task GuardarFiscal_ConCobrosLegacy_ExigeConfirmacionYNoGuarda()
        {
            using var fixture = await AccountPageFixture.CreateAsync();
            var controller = fixture.CreateFiscalController(cobrosLegacy: 3);

            var resultado = await controller.Index(
                new ConfiguracionFiscalViewModel { PreciosIncluyenIva = false, TarifaIvaPorDefecto = 4m },
                CancellationToken.None);

            var view = Assert.IsType<ViewResult>(resultado);
            Assert.Equal("~/Views/Accounts/Cuenta.cshtml", view.ViewName);
            Assert.False(controller.ModelState.IsValid);

            // El aviso viaja en ViewData, igual que antes, y dice cuántas transacciones hay en juego.
            var aviso = Assert.IsType<string>(controller.ViewData[AvisoImpactoHistorico.CampoConfirmacion]);
            Assert.Contains("3 cobros históricos", aviso);

            // La página se repinta completa y con lo que el usuario escribió.
            var page = Assert.IsType<AccountSettingsPageViewModel>(view.Model);
            Assert.NotNull(page.Fiscal);
            Assert.False(page.Fiscal!.PreciosIncluyenIva);
            Assert.Equal(AccountSettingsSections.Fiscal, page.FocusSection);

            // …y NADA se guardó.
            var tenant = await fixture.Context.Tenants.AsNoTracking().SingleAsync(t => t.Id == fixture.TenantId);
            Assert.True(tenant.PreciosIncluyenIva);
            Assert.Equal(13m, tenant.TarifaIvaPorDefecto);
        }

        [Fact]
        public async Task GuardarFiscal_Confirmado_Guarda()
        {
            using var fixture = await AccountPageFixture.CreateAsync();
            var controller = fixture.CreateFiscalController(cobrosLegacy: 3);

            var resultado = await controller.Index(
                new ConfiguracionFiscalViewModel { PreciosIncluyenIva = false, TarifaIvaPorDefecto = 4m },
                CancellationToken.None,
                confirmarImpactoHistorico: true);

            Assert.IsType<RedirectToActionResult>(resultado);

            var tenant = await fixture.Context.Tenants.AsNoTracking().SingleAsync(t => t.Id == fixture.TenantId);
            Assert.False(tenant.PreciosIncluyenIva);
        }

        /// <summary>
        /// El TenantId lo pone el servidor desde la identidad: no hay forma de que un formulario
        /// manipulado escriba la configuración de otro negocio. El ViewModel del POST no tiene
        /// siquiera una propiedad donde meterlo.
        /// </summary>
        [Fact]
        public void FiscalViewModel_ShouldNotBindTenantOrUser()
        {
            var propiedades = typeof(ConfiguracionFiscalViewModel)
                .GetProperties()
                .Select(propiedad => propiedad.Name)
                .OrderBy(nombre => nombre, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                new[]
                {
                    nameof(ConfiguracionFiscalViewModel.PreciosIncluyenIva),
                    nameof(ConfiguracionFiscalViewModel.TarifaIvaPorDefecto)
                },
                propiedades);
        }

        [Fact]
        public async Task GuardarFiscal_ShouldOnlyTouchItsOwnTenant()
        {
            using var fixture = await AccountPageFixture.CreateAsync();

            var otroTenantId = Guid.NewGuid();
            fixture.Context.Tenants.Add(new Tenant
            {
                Id = otroTenantId,
                Nombre = "Otro negocio",
                Activo = true,
                PreciosIncluyenIva = true,
                TarifaIvaPorDefecto = 13m
            });
            await fixture.Context.SaveChangesAsync();

            var controller = fixture.CreateFiscalController(cobrosLegacy: 0);
            await controller.Index(
                new ConfiguracionFiscalViewModel { PreciosIncluyenIva = false, TarifaIvaPorDefecto = 2m },
                CancellationToken.None);

            var ajeno = await fixture.Context.Tenants.AsNoTracking().SingleAsync(t => t.Id == otroTenantId);
            Assert.True(ajeno.PreciosIncluyenIva);
            Assert.Equal(13m, ajeno.TarifaIvaPorDefecto);
        }

        // ── Fixture ──

        private sealed class AccountPageFixture : IDisposable
        {
            private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
            private readonly TestTenantProvider _tenantProvider;

            private AccountPageFixture(
                ApplicationDbContext context,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                TestTenantProvider tenantProvider,
                UserManager<AppUsuario> userManager,
                Guid tenantId,
                string adminId,
                string funcionarioId)
            {
                Context = context;
                _connection = connection;
                _tenantProvider = tenantProvider;
                UserManager = userManager;
                TenantId = tenantId;
                AdminId = adminId;
                FuncionarioId = funcionarioId;
            }

            public ApplicationDbContext Context { get; }

            public UserManager<AppUsuario> UserManager { get; }

            public Guid TenantId { get; }

            public string AdminId { get; }

            public string FuncionarioId { get; }

            public IAccountSettingsPageBuilder Builder => new AccountSettingsPageBuilder(
                UserManager,
                Context,
                new StaticOptionsMonitor<PlatformSecurityOptions>(new PlatformSecurityOptions()));

            public System.Security.Claims.ClaimsPrincipal AdminPrincipal =>
                WithRole(ControllerTestSupport.BuildTenantPrincipal(AdminId, TenantId), AppRoles.Administrador);

            public System.Security.Claims.ClaimsPrincipal FuncionarioPrincipal =>
                WithRole(ControllerTestSupport.BuildTenantPrincipal(FuncionarioId, TenantId), AppRoles.Funcionario);

            public static async Task<AccountPageFixture> CreateAsync()
            {
                var tenantProvider = new TestTenantProvider();
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

                var tenantId = Guid.NewGuid();
                tenantProvider.TenantId = tenantId;

                context.Tenants.Add(new Tenant
                {
                    Id = tenantId,
                    Nombre = "Negocio de prueba",
                    Activo = true,
                    PreciosIncluyenIva = true,
                    TarifaIvaPorDefecto = 13m
                });

                var adminId = Guid.NewGuid().ToString("N");
                var funcionarioId = Guid.NewGuid().ToString("N");

                context.Users.Add(new AppUsuario
                {
                    Id = adminId,
                    UserName = "dueno@test.local",
                    NormalizedUserName = "DUENO@TEST.LOCAL",
                    Email = "dueno@test.local",
                    NormalizedEmail = "DUENO@TEST.LOCAL",
                    Name = "Dueño",
                    PhoneNumber = "+506 8888-8888",
                    TenantId = tenantId,
                    State = true,
                    SecurityStamp = Guid.NewGuid().ToString("N")
                });

                context.Users.Add(new AppUsuario
                {
                    Id = funcionarioId,
                    UserName = "colaborador@test.local",
                    NormalizedUserName = "COLABORADOR@TEST.LOCAL",
                    Email = "colaborador@test.local",
                    NormalizedEmail = "COLABORADOR@TEST.LOCAL",
                    Name = "Colaborador",
                    TenantId = tenantId,
                    State = true,
                    SecurityStamp = Guid.NewGuid().ToString("N")
                });

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

                return new AccountPageFixture(
                    context, connection, tenantProvider, userManager, tenantId, adminId, funcionarioId);
            }

            public async Task EnableTwoFactorAsync()
            {
                var usuario = await Context.Users.SingleAsync(user => user.Id == AdminId);
                usuario.TwoFactorEnabled = true;
                await Context.SaveChangesAsync();
            }

            public ConfiguracionFiscalController CreateFiscalController(int cobrosLegacy)
            {
                var controller = new ConfiguracionFiscalController(
                    Context,
                    _tenantProvider,
                    Builder,
                    NullLogger<ConfiguracionFiscalController>.Instance,
                    new FakeLegacyFinancialImpactService(cobrosLegacy));

                ControllerTestSupport.AttachHttpContext(controller, AdminPrincipal);
                return controller;
            }

            public void Dispose()
            {
                UserManager.Dispose();
                Context.Dispose();
                _connection.Dispose();
            }

            private static System.Security.Claims.ClaimsPrincipal WithRole(
                System.Security.Claims.ClaimsPrincipal principal,
                string role)
            {
                var identity = (System.Security.Claims.ClaimsIdentity)principal.Identity!;
                identity.AddClaim(new System.Security.Claims.Claim(identity.RoleClaimType, role));
                return principal;
            }
        }

        /// <summary>Cuenta fija de cobros legacy: acá se prueba el aviso, no el conteo.</summary>
        private sealed class FakeLegacyFinancialImpactService : ILegacyFinancialImpactService
        {
            private readonly int _cobros;

            public FakeLegacyFinancialImpactService(int cobros) => _cobros = cobros;

            public Task<int> ContarCobrosLegacyDeServicioAsync(int servicioId, CancellationToken cancellationToken = default)
                => Task.FromResult(_cobros);

            public Task<int> ContarCobrosLegacyDeProductoAsync(int productoId, CancellationToken cancellationToken = default)
                => Task.FromResult(_cobros);

            public Task<int> ContarCobrosLegacyDelNegocioAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(_cobros);

            public Task<int> ContarProduccionHistoricaDeColaboradorAsync(int funcionarioId, CancellationToken cancellationToken = default)
                => Task.FromResult(_cobros);
        }
    }
}
