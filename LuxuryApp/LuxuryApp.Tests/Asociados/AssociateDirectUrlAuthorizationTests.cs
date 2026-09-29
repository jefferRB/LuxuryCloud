using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using LuxuryApp.Models.Asociados;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Layout;
using LuxuryApp.Services.Asociados;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Layout;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Asociados
{
    /// <summary>
    /// Ataque por URL directa contra el pipeline HTTP REAL: routing, autenticación, autorización,
    /// el proveedor de políticas por permiso y el handler que consulta base de datos, todos los
    /// componentes de producción.
    ///
    /// <para>
    /// Es la prueba que importa del módulo: esconder un ítem del menú no protege nada. Alguien de
    /// Marketing puede escribir <c>/Cobros</c> en la barra de direcciones, y tiene que recibir un
    /// 403 igual que si nunca hubiera visto el enlace.
    /// </para>
    /// </summary>
    public sealed class AssociateDirectUrlAuthorizationTests : IAsyncLifetime
    {
        private const string HeaderUsuario = "X-Test-User";

        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        private readonly Guid _tenantId = Guid.NewGuid();

        private IHost _host = null!;
        private TestServer _server = null!;
        private string _marketingUserId = string.Empty;
        private int _marketingAssociateId;

        public async Task InitializeAsync()
        {
            _connection.Open();

            _host = await new HostBuilder()
                .ConfigureWebHost(webHost =>
                {
                    webHost.UseTestServer();
                    // Carga los controladores REALES del ensamblado de la aplicación.
                    webHost.UseSetting(WebHostDefaults.ApplicationKey, "LuxuryApp");
                    webHost.ConfigureServices(ConfigureServices);
                    webHost.Configure(Configure);
                })
                .StartAsync();

            _server = _host.GetTestServer();

            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await context.Database.EnsureCreatedAsync();

            _marketingUserId = await AssociateTestSupport.SeedTenantAndUserAsync(
                context,
                _tenantId,
                "andrea@test.local");

            var asociado = await AssociateTestSupport.SeedAssociateAsync(
                context,
                "Andrea",
                "andrea@test.local",
                activo: true,
                appUsuarioId: _marketingUserId,
                AssociateType.Marketing);

            _marketingAssociateId = asociado.Id;

            // Caso del enunciado: Marketing solo administra la página web.
            await AssociateTestSupport.GrantAsync(
                context,
                asociado.Id,
                AppPermissions.PublicWebsiteView,
                AppPermissions.PublicWebsiteManage);
        }

        public async Task DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
            await _connection.DisposeAsync();
        }

        [Theory]
        [InlineData("/Dashboard")]
        [InlineData("/Informacion")]
        [InlineData("/Clientes")]
        [InlineData("/Funcionarios")]
        [InlineData("/Cobros")]
        [InlineData("/Egresos")]
        [InlineData("/Productos")]
        [InlineData("/Servicios")]
        [InlineData("/Calendar")]
        [InlineData("/Reservas")]
        [InlineData("/Asociados")]
        [InlineData("/Inversionistas/Estados")]
        public async Task Marketing_EscribiendoLaUrlAMano_RecibeProhibido(string ruta)
        {
            var respuesta = await GetAsync(ruta, _marketingUserId);

            Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
        }

        [Theory]
        [InlineData("/Asociados/Crear")]
        [InlineData("/Asociados/Detalle/1")]
        public async Task Marketing_NoPuedeAbrirElModuloDeAsociados(string ruta)
        {
            var respuesta = await GetAsync(ruta, _marketingUserId);

            Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
        }

        [Fact]
        public async Task Marketing_SiPuedeAbrirLaAdministracionDeLaPaginaWeb()
        {
            var respuesta = await GetAsync("/Configuracion/PaginaPublica", _marketingUserId);

            // Sin ser administradora del negocio, Andrea abre la pantalla que sí le concedieron.
            Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        }

        [Fact]
        public async Task AccesoBloqueado_PierdeInclusoLoQueSiTeniaConcedido()
        {
            using (var scope = _host.Services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var cuenta = await context.Users.SingleAsync(user => user.Id == _marketingUserId);
                cuenta.State = false;
                await context.SaveChangesAsync();
            }

            var respuesta = await GetAsync("/Configuracion/PaginaPublica", _marketingUserId);

            Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
        }

        [Fact]
        public async Task AsociadoDesactivado_PierdeTodoAunqueLaCuentaSigaViva()
        {
            using (var scope = _host.Services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var asociado = await context.Associates.SingleAsync(current => current.Id == _marketingAssociateId);
                asociado.Activo = false;
                await context.SaveChangesAsync();
            }

            var respuesta = await GetAsync("/Configuracion/PaginaPublica", _marketingUserId);

            Assert.Equal(HttpStatusCode.Forbidden, respuesta.StatusCode);
        }

        [Fact]
        public async Task QuitarElPermiso_SurteEfectoEnElSiguienteRequest_SinCerrarSesion()
        {
            var antes = await GetAsync("/Configuracion/PaginaPublica", _marketingUserId);
            Assert.Equal(HttpStatusCode.OK, antes.StatusCode);

            using (var scope = _host.Services.CreateScope())
            {
                var permissionService = scope.ServiceProvider.GetRequiredService<IAssociatePermissionService>();
                await permissionService.GuardarAsync(_marketingAssociateId, [], actorUserId: "admin-1");
            }

            var despues = await GetAsync("/Configuracion/PaginaPublica", _marketingUserId);

            // La misma sesión, sin volver a iniciar sesión: el permiso ya no está.
            Assert.Equal(HttpStatusCode.Forbidden, despues.StatusCode);
        }

        [Fact]
        public async Task SinAutenticar_NoEsProhibidoSinoRedirigidoAlLogin()
        {
            var respuesta = await GetAsync("/Configuracion/PaginaPublica", usuario: null);

            Assert.Equal(HttpStatusCode.Redirect, respuesta.StatusCode);
        }

        // ─────────────── Infraestructura del host de prueba ───────────────

        private async Task<HttpResponseMessage> GetAsync(string ruta, string? usuario)
        {
            var client = _server.CreateClient();
            client.BaseAddress = new Uri("https://localhost");

            var request = new HttpRequestMessage(HttpMethod.Get, ruta);
            if (!string.IsNullOrEmpty(usuario))
            {
                request.Headers.Add(HeaderUsuario, usuario);
            }

            return await client.SendAsync(request);
        }

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(_connection);
            services.AddSingleton<ITenantProvider>(new TestTenantProvider { TenantId = _tenantId });
            services.AddDbContext<ApplicationDbContext>((sp, options) =>
                options.UseSqlite(sp.GetRequiredService<SqliteConnection>()));

            services.AddHttpContextAccessor();
            services.AddMemoryCache();
            services.AddSingleton<IBusinessDateTimeProvider>(new FixedBusinessDateTimeProvider());

            services
                .AddIdentity<AppUsuario, IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

            // Autenticación de prueba: reemplaza el login por cookie, pero TODO lo que viene
            // después (autorización, políticas, handler, base de datos) es el de producción.
            //
            // Los cuatro esquemas se fijan explícitamente porque AddIdentity ya dejó apuntando
            // Authenticate/Challenge a la cookie de Identity; sin esto el request llegaría sin
            // usuario y la prueba mediría un redirect al login en vez de un 403.
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = TestScheme.Nombre;
                    options.DefaultAuthenticateScheme = TestScheme.Nombre;
                    options.DefaultChallengeScheme = TestScheme.Nombre;
                    options.DefaultForbidScheme = TestScheme.Nombre;
                })
                .AddScheme<AuthenticationSchemeOptions, TestScheme>(TestScheme.Nombre, _ => { });

            services.AddAuthorization(options =>
            {
                options.AddPolicy(
                    AppAuthorizationPolicies.RequireTenantAdmin,
                    policy => policy.RequireRole(AppRoles.Administrador));
            });

            // Las piezas REALES de la autorización por permisos.
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
            services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
            services.AddScoped<IAssociatePermissionService, AssociatePermissionService>();
            services.AddScoped<IPlatformAuditService, FakePlatformAuditService>();

            // Dependencias mínimas de las pantallas que SÍ se abren en estas pruebas.
            services.AddScoped<IPrivateNavigationService, EmptyNavigationService>();
            services.AddScoped<LuxuryApp.Services.PublicPages.ITenantPublicPageSettingsService,
                StubPublicPageSettingsService>();
            services.AddScoped<LuxuryApp.Services.PublicImages.IPublicImageUploadService,
                StubPublicImageUploadService>();
            // La vista de Pagina publica arma el recortador desde el perfil real de cada imagen.
            services.AddSingleton<Microsoft.Extensions.Options.IOptions<LuxuryApp.Services.PublicImages.PublicImageOptions>>(
                Microsoft.Extensions.Options.Options.Create(new LuxuryApp.Services.PublicImages.PublicImageOptions()));
            services.AddSingleton<LuxuryApp.Services.PublicImages.IPublicImageProfileProvider,
                LuxuryApp.Services.PublicImages.PublicImageProfileProvider>();

            var mvc = services.AddControllersWithViews(options =>
            {
                // Mismo filtro global que Program.cs: todo exige autenticación por defecto.
                var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
                options.Filters.Add(new AuthorizeFilter(policy));
            });

            // Con TestServer el ensamblado de entrada es el de pruebas: hay que registrar los
            // ApplicationParts del ensamblado REAL para que existan los controladores y sus vistas.
            var appAssembly = typeof(LuxuryApp.Controllers.Finanzas.DashboardController).Assembly;
            mvc.ConfigureApplicationPartManager(apm =>
            {
                if (apm.ApplicationParts.All(part => part.Name != appAssembly.GetName().Name))
                {
                    apm.ApplicationParts.Add(
                        new Microsoft.AspNetCore.Mvc.ApplicationParts.AssemblyPart(appAssembly));
                }

                apm.ApplicationParts.Add(
                    new Microsoft.AspNetCore.Mvc.ApplicationParts.CompiledRazorAssemblyPart(appAssembly));
            });
        }

        private static void Configure(IApplicationBuilder app)
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllerRoute(
                    name: "default",
                    pattern: "{controller=Home}/{action=Index}/{id?}");
            });
        }

        /// <summary>Firma al usuario que venga en la cabecera, con el rol Asociado y su tenant.</summary>
        private sealed class TestScheme : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            public const string Nombre = "TestScheme";

            private readonly ApplicationDbContext _context;

            public TestScheme(
                IOptionsMonitor<AuthenticationSchemeOptions> options,
                ILoggerFactory logger,
                UrlEncoder encoder,
                ApplicationDbContext context)
                : base(options, logger, encoder)
            {
                _context = context;
            }

            protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                if (!Request.Headers.TryGetValue(HeaderUsuario, out var userId) ||
                    string.IsNullOrWhiteSpace(userId))
                {
                    return AuthenticateResult.NoResult();
                }

                var usuario = await _context.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(user => user.Id == userId.ToString());

                if (usuario is null)
                {
                    return AuthenticateResult.Fail("Usuario inexistente.");
                }

                var identity = new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, usuario.Id),
                        new Claim(CustomClaimTypes.UserId, usuario.Id),
                        new Claim(CustomClaimTypes.TenantId, usuario.TenantId.ToString()),
                        new Claim(ClaimTypes.Role, AppRoles.Asociado)
                    ],
                    Nombre,
                    ClaimTypes.Name,
                    ClaimTypes.Role);

                var principal = new ClaimsPrincipal(identity);
                return AuthenticateResult.Success(new AuthenticationTicket(principal, Nombre));
            }

            protected override Task HandleChallengeAsync(AuthenticationProperties properties)
            {
                // Igual que la cookie real: sin sesión se redirige al login, no se responde 403.
                Response.StatusCode = StatusCodes.Status302Found;
                Response.Headers.Location = "/Accounts/Acceso";
                return Task.CompletedTask;
            }

            protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
            {
                Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
        }

        private sealed class EmptyNavigationService : IPrivateNavigationService
        {
            public Task<PrivateNavigationViewModel> BuildAsync(
                ClaimsPrincipal principal,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new PrivateNavigationViewModel());
        }

        private sealed class StubPublicPageSettingsService
            : LuxuryApp.Services.PublicPages.ITenantPublicPageSettingsService
        {
            public Task<LuxuryApp.Models.PublicPages.EditTenantPublicPageViewModel> BuildForCurrentTenantAsync(
                HttpRequest? request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new LuxuryApp.Models.PublicPages.EditTenantPublicPageViewModel());

            public Task<LuxuryApp.Models.PublicPages.EditTenantPublicPageViewModel> PopulateReadOnlyFieldsAsync(
                LuxuryApp.Models.PublicPages.EditTenantPublicPageViewModel model,
                HttpRequest? request,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(model);

            public Task SaveForCurrentTenantAsync(
                LuxuryApp.Models.PublicPages.EditTenantPublicPageViewModel input,
                string? userId,
                CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task<bool> CanUsePublicLandingPageAsync(
                Guid tenantId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(true);
        }

        /// <summary>Estas pruebas nunca suben ni borran imágenes: solo miden la autorización.</summary>
        private sealed class StubPublicImageUploadService
            : LuxuryApp.Services.PublicImages.IPublicImageUploadService
        {
            public Task<LuxuryApp.Models.PublicPages.TenantPublicAsset> UploadPublicPageAssetAsync(
                LuxuryApp.Models.PublicPages.TenantPublicAssetType assetType,
                IFormFile? file,
                string? userId,
                CancellationToken cancellationToken = default,
                LuxuryApp.Services.PublicImages.PublicImageCropRequest? crop = null) =>
                throw new NotSupportedException();

            public Task<LuxuryApp.Models.PublicPages.TenantPublicAsset> UploadServiceAssetAsync(
                LuxuryApp.Models.PublicPages.TenantPublicAssetType assetType,
                int serviceId,
                IFormFile? file,
                string? userId,
                CancellationToken cancellationToken = default,
                LuxuryApp.Services.PublicImages.PublicImageCropRequest? crop = null) =>
                throw new NotSupportedException();

            public Task RemovePublicPageSingletonAsync(
                LuxuryApp.Models.PublicPages.TenantPublicAssetType assetType,
                string? userId,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task RemoveServiceMainImageAsync(
                int serviceId,
                string? userId,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task RemoveAssetAsync(
                Guid assetId,
                string? userId,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }
    }
}
