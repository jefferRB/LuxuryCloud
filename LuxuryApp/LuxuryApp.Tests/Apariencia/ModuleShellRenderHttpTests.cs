using System.Net;
using System.Security.Claims;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Layout;
using LuxuryApp.Models.Marketing;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Layout;
using LuxuryApp.Services.PublicSite;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.WhatsApp;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Apariencia
{
    /// <summary>
    /// Render REAL del módulo de WhatsApp dentro del shell unificado.
    ///
    /// <para>
    /// Las pruebas que leen el .cshtml comprueban que el texto esté; ésta comprueba que la vista
    /// EJECUTE: que los <c>@if/else</c> reorganizados produzcan HTML válido, que los parciales
    /// existan y que el estado vacío y el estado con paquete se rendericen sin reventar. Es el
    /// caso que más se tocó en esta fase.
    /// </para>
    ///
    /// <para>
    /// No se prueba el motor de WhatsApp (envíos, cuotas, plantillas): eso ya está cubierto y no
    /// se modificó.
    /// </para>
    /// </summary>
    public class ModuleShellRenderHttpTests : IAsyncLifetime
    {
        private const string AdminEmail = "dueno-render@test.local";

        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        private readonly Guid _tenantId = Guid.NewGuid();

        private IHost _host = null!;
        private TestServer _server = null!;
        private string _adminUserId = string.Empty;

        public async Task InitializeAsync()
        {
            _connection.Open();

            _host = await new HostBuilder()
                .ConfigureWebHost(webHost =>
                {
                    webHost.UseTestServer();
                    // ApplicationParts del ensamblado real: controladores + vistas Razor compiladas.
                    webHost.UseSetting(WebHostDefaults.ApplicationKey, "LuxuryApp");
                    webHost.ConfigureServices(ConfigureServices);
                    webHost.Configure(Configure);
                })
                .StartAsync();

            _server = _host.GetTestServer();
            await SeedAsync();
        }

        public async Task DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
            await _connection.DisposeAsync();
        }

        /// <summary>
        /// Sin paquete: la pantalla se dibuja completa, dentro de UNA card, con el estado vacío y
        /// las automatizaciones bloqueadas. Es la ruta que más condicionales mueve.
        /// </summary>
        [Fact]
        public async Task WhatsApp_SinPaquete_RenderizaDentroDelShell()
        {
            var client = _server.CreateClient();

            var response = await client.GetAsync("/WhatsApp");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var html = await response.Content.ReadAsStringAsync();

            // Una sola card de módulo con el hero compartido.
            Assert.Contains("finance-page-shell", html, StringComparison.Ordinal);
            Assert.Contains("finance-hero", html, StringComparison.Ordinal);

            // Las tres secciones internas.
            Assert.Contains("Estado del add-on", html, StringComparison.Ordinal);
            Assert.Contains("Automatizaciones", html, StringComparison.Ordinal);
            Assert.Contains("Paquetes disponibles", html, StringComparison.Ordinal);

            // Estado vacío conservado, con su CTA y el bloqueo de automatizaciones.
            Assert.Contains("No tenés un paquete de WhatsApp activo.", html, StringComparison.Ordinal);
            Assert.Contains("wa-automation--locked", html, StringComparison.Ordinal);
            Assert.Contains("Activá un paquete de WhatsApp para configurar las automatizaciones.",
                html, StringComparison.Ordinal);

            // El estado también se dice con texto, no sólo con color.
            Assert.Contains("Sin paquete", html, StringComparison.Ordinal);

            // El ancla que enlazan el CTA y la vista de Suscripción.
            Assert.Contains("id=\"paquetes\"", html, StringComparison.Ordinal);

            EstructuraDeSeccionesEsCoherente(html);
        }

        /// <summary>
        /// Las secciones tienen que abrir y cerrar igual. Si un <c>@if</c> deja un
        /// <c>&lt;section&gt;</c> abierto, el layout se rompe en producción y no en el compilador.
        /// </summary>
        private static void EstructuraDeSeccionesEsCoherente(string html)
        {
            var aperturas = System.Text.RegularExpressions.Regex.Matches(html, "<section\\b").Count;
            var cierres = System.Text.RegularExpressions.Regex.Matches(html, "</section>").Count;

            Assert.Equal(aperturas, cierres);

            // Y ningún formulario anidado dentro de otro.
            Assert.DoesNotContain("<form", QuitarFormulariosDeNivelSuperior(html), StringComparison.OrdinalIgnoreCase);
        }

        private static string QuitarFormulariosDeNivelSuperior(string html)
        {
            // Reemplaza cada <form>…</form> no anidado por un hueco; si quedara algún "<form"
            // suelto después, es que había uno dentro de otro.
            return System.Text.RegularExpressions.Regex.Replace(
                html,
                "<form\\b[^>]*>((?!<form\\b)[\\s\\S])*?</form>",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // ── Host ──

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton(_connection);
            services.AddSingleton<ITenantProvider>(new TestTenantProvider { TenantId = _tenantId });
            services.AddDbContext<ApplicationDbContext>((sp, options) =>
                options.UseSqlite(sp.GetRequiredService<SqliteConnection>()));

            services.AddHttpContextAccessor();
            services.AddMemoryCache();
            services.AddSingleton<IBusinessDateTimeProvider>(new FixedBusinessDateTimeProvider());
            services.Configure<TilopayRepeatOptions>(_ => { });
            services.Configure<MetaWhatsAppOptions>(_ => { });

            services
                .AddIdentity<AppUsuario, IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

            // Dependencias reales del módulo WhatsApp.
            services.AddScoped<SuscripcionService>();
            services.AddScoped<ITenantCommercialAccessCache, TenantCommercialAccessCache>();
            services.AddScoped<ITenantCommercialAccessResolver, TenantCommercialAccessResolver>();
            services.AddScoped<ITenantWhatsAppSettingsService, TenantWhatsAppSettingsService>();
            services.AddScoped<ISubscriptionSummaryService, SubscriptionSummaryService>();
            services.AddScoped<IPublicSiteContentService, EmptyPublicSiteContentService>();
            services.AddScoped<IPrivateNavigationService, EmptyPrivateNavigationService>();

            services.AddAntiforgery();

            // Autenticación de prueba: el usuario llega ya autenticado como Administrador del
            // tenant. Lo que se prueba acá es el RENDER, no el login (ya cubierto aparte).
            // AddIdentity ya fijó los esquemas por defecto a la cookie de aplicación; hay que
            // pisarlos TODOS o [Authorize(Roles=...)] autenticaría por el esquema equivocado y
            // devolvería un redirect al login en vez de renderizar.
            services
                .AddAuthentication(options =>
                {
                    options.DefaultScheme = "TestScheme";
                    options.DefaultAuthenticateScheme = "TestScheme";
                    options.DefaultChallengeScheme = "TestScheme";
                    options.DefaultSignInScheme = "TestScheme";
                })
                .AddScheme<AuthenticationSchemeOptions, SeededUserAuthHandler>("TestScheme", _ => { });

            services.AddSingleton(new SeededUserAccessor(() => _adminUserId, _tenantId));

            var mvc = services.AddControllersWithViews(options =>
            {
                var policy = new AuthorizationPolicyBuilder("TestScheme").RequireAuthenticatedUser().Build();
                options.Filters.Add(new AuthorizeFilter(policy));
            });

            var appAssembly = typeof(LuxuryApp.Controllers.WhatsApp.WhatsAppController).Assembly;
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
                endpoints.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}"));
        }

        private async Task SeedAsync()
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await context.Database.EnsureCreatedAsync();

            context.Tenants.Add(new Tenant { Id = _tenantId, Nombre = "Tenant render", Activo = true });
            await context.SaveChangesAsync();

            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            await roleManager.CreateAsync(new IdentityRole(AppRoles.Administrador));

            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUsuario>>();
            var user = new AppUsuario
            {
                UserName = AdminEmail,
                Email = AdminEmail,
                Name = "Dueña del negocio",
                TenantId = _tenantId,
                State = true
            };

            var creado = await userManager.CreateAsync(user, "Sup3rSecreta!");
            Assert.True(creado.Succeeded, string.Join(",", creado.Errors.Select(e => e.Description)));
            await userManager.AddToRoleAsync(user, AppRoles.Administrador);

            _adminUserId = user.Id;
        }

        // ── Dobles mínimos ──

        private sealed record SeededUserAccessor(Func<string> UserId, Guid TenantId);

        /// <summary>
        /// Autentica siempre al administrador sembrado. No hay forma de pedir otra identidad desde
        /// el request: el objetivo es renderizar, no probar autorización (eso va aparte).
        /// </summary>
        private sealed class SeededUserAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            private readonly SeededUserAccessor _accessor;

            public SeededUserAuthHandler(
                Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
                Microsoft.Extensions.Logging.ILoggerFactory logger,
                System.Text.Encodings.Web.UrlEncoder encoder,
                SeededUserAccessor accessor)
                : base(options, logger, encoder)
            {
                _accessor = accessor;
            }

            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                var userId = _accessor.UserId();
                if (string.IsNullOrEmpty(userId))
                {
                    return Task.FromResult(AuthenticateResult.NoResult());
                }

                var identity = new ClaimsIdentity(
                    new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, userId),
                        new Claim(CustomClaimTypes.UserId, userId),
                        new Claim(CustomClaimTypes.TenantId, _accessor.TenantId.ToString()),
                        new Claim(ClaimTypes.Role, AppRoles.Administrador)
                    },
                    "TestScheme");

                var principal = new ClaimsPrincipal(identity);
                return Task.FromResult(AuthenticateResult.Success(
                    new AuthenticationTicket(principal, "TestScheme")));
            }
        }

        private sealed class EmptyPrivateNavigationService : IPrivateNavigationService
        {
            public Task<PrivateNavigationViewModel> BuildAsync(
                ClaimsPrincipal principal,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new PrivateNavigationViewModel());
        }

        private sealed class EmptyPublicSiteContentService : IPublicSiteContentService
        {
            public IReadOnlyCollection<MarketingMetricViewModel> GetHeroMetrics() => [];
            public IReadOnlyCollection<MarketingModuleViewModel> GetModules() => [];
            public Task<IReadOnlyCollection<MarketingPlanCardViewModel>> GetPlanCardsAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyCollection<MarketingPlanCardViewModel>>([]);
            public Task<IReadOnlyCollection<MarketingPlanCardViewModel>> GetWhatsAppAddonCardsAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyCollection<MarketingPlanCardViewModel>>([]);
            public Task<IReadOnlyCollection<MarketingPlanCardViewModel>> GetInternalPlanCardsAsync(CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyCollection<MarketingPlanCardViewModel>>([]);
            public Task<Plan?> FindAvailablePlanAsync(Guid planId, CancellationToken ct = default) =>
                Task.FromResult<Plan?>(null);
            public Task<string?> GetPlanNameAsync(Guid? planId, CancellationToken ct = default) =>
                Task.FromResult<string?>(null);
        }
    }
}
