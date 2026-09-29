using System.Net;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Layout;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Layout;
using LuxuryApp.Services.PublicSite;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProyectoIdentity.Datos;
using System.Security.Claims;

namespace LuxuryApp.Tests.TenantIsolation
{
    /// <summary>
    /// Renderiza la landing pública REAL (HomeController.Index + Home/Index.cshtml +
    /// PublicSiteContentService real + ISubscriptionPricingCatalog real) por HTTP. Verifica que
    /// la sección de precios usa el calculador comercial (LC_M_), no planes legacy/TEST, deriva
    /// el "Desde ₡8.000" de LC_M_01, degrada sin catálogo y nunca expone TEST/₡100/Básico.
    /// </summary>
    public sealed class LandingPageHttpTests
    {
        [Fact]
        public async Task Get_Landing_WithCatalog_Returns200_SingleH1_AndStartingPriceFromLcM01()
        {
            await using var harness = await LandingHarness.CreateAsync(withCatalog: true, seedNoise: true);

            var response = await harness.Client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var html = await response.Content.ReadAsStringAsync();

            Assert.Equal(1, CountOccurrences(html, "<h1"));
            Assert.Contains("Desde", html);
            Assert.Contains("8.000", html); // LC_M_01, precio de entrada
            Assert.Contains("Calcular mi plan completo", html);
            Assert.Contains("/Billing/Planes", html);
        }

        [Fact]
        public async Task Get_Landing_DoesNotExposeTestOrLegacyPlans()
        {
            await using var harness = await LandingHarness.CreateAsync(withCatalog: true, seedNoise: true);

            var html = await (await harness.Client.GetAsync("/")).Content.ReadAsStringAsync();

            // Regla comercial crítica de NO exposición pública:
            Assert.DoesNotContain("LuxuryCloud Test Producción", html);
            Assert.DoesNotContain("₡100", html);
            Assert.DoesNotContain("Básico", html);
            Assert.DoesNotContain("lp-pricing-card", html); // ya no existen las cards legacy/TEST
            // El precio en JSON-LD proviene de LC_M_01 (8000), nunca del TEST de 100.
            Assert.Contains("\"price\":\"8000\"", html);
            Assert.DoesNotContain("\"price\":\"100\"", html);
        }

        [Fact]
        public async Task Get_Landing_RendersSingularForOneWorker_AndCarriesPluralData()
        {
            await using var harness = await LandingHarness.CreateAsync(withCatalog: true, seedNoise: false);

            var html = await (await harness.Client.GetAsync("/")).Content.ReadAsStringAsync();

            Assert.Contains("1 integrante incluido", html);       // estado inicial servidor (singular)
            Assert.Contains("3 integrantes incluidos", html);     // dato para el JS (plural correcto)
            // El plan de 1 integrante nunca se pluraliza (evita "1 integrantes incluidos").
            Assert.DoesNotContain("\"workersLabel\":\"1 integrantes incluidos\"", html);
            Assert.DoesNotContain("Hasta 1 funcionarios", html);
        }

        [Fact]
        public async Task Get_Landing_WhatsAppFrom_ShownFromAddonCatalog_SeparateFromBase()
        {
            await using var harness = await LandingHarness.CreateAsync(withCatalog: true, seedNoise: true);

            var html = await (await harness.Client.GetAsync("/")).Content.ReadAsStringAsync();

            Assert.Contains("Ver opciones de WhatsApp", html);
            Assert.Contains("/Billing/Planes#whatsapp", html);
            // "Desde ₡6.000" del add-on y "Desde ₡8.000" del plan base son bloques separados.
            Assert.Contains("Desde <strong>₡6.000", html);
            Assert.Contains("Desde <strong>₡8.000", html);
        }

        [Fact]
        public async Task Get_Landing_WithoutCatalog_ShowsUnavailableMessage_AndNoInventedPrice()
        {
            await using var harness = await LandingHarness.CreateAsync(withCatalog: false, seedNoise: true);

            var response = await harness.Client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var html = await response.Content.ReadAsStringAsync();

            Assert.Equal(1, CountOccurrences(html, "<h1"));
            Assert.Contains("Los precios no están disponibles temporalmente.", html);
            Assert.Contains("/Billing/Planes", html);
            Assert.DoesNotContain("Desde <strong>", html); // sin "Desde ₡X"
            Assert.DoesNotContain("8.000", html);
            Assert.DoesNotContain("₡100", html);
            Assert.DoesNotContain("\"offers\"", html); // JSON-LD sin precio inventado
        }

        [Fact]
        public async Task Get_Landing_UsesRealRoutesForKeyCtas()
        {
            await using var harness = await LandingHarness.CreateAsync(withCatalog: true, seedNoise: false);

            var html = await (await harness.Client.GetAsync("/")).Content.ReadAsStringAsync();

            Assert.Contains("/Accounts/Registro", html);
            Assert.Contains("/Accounts/Acceso", html);
            Assert.Contains("/Billing/Planes", html);
        }

        [Fact]
        public async Task Get_Landing_DoesNotRenderVideoPlayer_WhenNoVideoConfigured()
        {
            await using var harness = await LandingHarness.CreateAsync(withCatalog: true, seedNoise: false);

            var html = await (await harness.Client.GetAsync("/")).Content.ReadAsStringAsync();

            Assert.DoesNotContain("<video", html);
            Assert.DoesNotContain("<iframe", html);
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        private sealed class LandingHarness : IAsyncDisposable
        {
            private readonly IHost _host;
            private readonly SqliteConnection _connection;

            private LandingHarness(IHost host, SqliteConnection connection, HttpClient client)
            {
                _host = host;
                _connection = connection;
                Client = client;
            }

            public HttpClient Client { get; }

            public static async Task<LandingHarness> CreateAsync(bool withCatalog, bool seedNoise)
            {
                var connection = new SqliteConnection("DataSource=:memory:");
                connection.Open();

                var host = await new HostBuilder()
                    .ConfigureWebHost(webHost =>
                    {
                        webHost.UseTestServer();
                        webHost.UseSetting(WebHostDefaults.ApplicationKey, "LuxuryApp");
                        webHost.ConfigureServices(services => ConfigureServices(services, connection, withCatalog));
                        webHost.Configure(Configure);
                    })
                    .StartAsync();

                using (var scope = host.Services.CreateScope())
                {
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await context.Database.EnsureCreatedAsync();

                    if (seedNoise)
                    {
                        // Ruido comercial que NO debe aparecer en la landing.
                        context.Planes.AddRange(
                            NewPlan(PlanCodes.Basic, "Básico", 8000m, maxFuncionarios: 1),
                            NewPlan(PlanCodes.Pro, "Pro", 20000m, maxFuncionarios: 3),
                            NewPlan(PlanCodes.TestProdBasic100, "LuxuryCloud Test Producción", 100m, maxFuncionarios: 1),
                            NewPlan(PlanCodes.WhatsApp400, "WhatsApp 400", 6000m, monthlyMessageLimit: 400));
                        await context.SaveChangesAsync();
                    }
                }

                var client = host.GetTestServer().CreateClient();
                client.BaseAddress = new Uri("http://localhost/");

                return new LandingHarness(host, connection, client);
            }

            private static Plan NewPlan(string code, string name, decimal price, int? maxFuncionarios = null, int? monthlyMessageLimit = null) =>
                new()
                {
                    Id = Guid.NewGuid(),
                    Codigo = code,
                    Nombre = name,
                    PrecioMensual = price,
                    Moneda = "CRC",
                    MaxFuncionarios = maxFuncionarios,
                    LimiteMensajesMensual = monthlyMessageLimit,
                    Activo = true
                };

            private static void ConfigureServices(IServiceCollection services, SqliteConnection connection, bool withCatalog)
            {
                services.AddSingleton(connection);
                services.AddSingleton<ITenantProvider>(new TestTenantProvider());
                services.AddDbContext<ApplicationDbContext>((sp, options) =>
                    options.UseSqlite(sp.GetRequiredService<SqliteConnection>()));

                services.AddHttpContextAccessor();
                services.AddMemoryCache();
                services.Configure<OpcionesPago>(options => options.EnableValidationPlans = false);
                services.Configure<OpcionesTilopay>(_ => { });
                services.Configure<TilopayRepeatOptions>(options =>
                {
                    if (withCatalog)
                    {
                        options.Enabled = true;
                        options.UseHostedLinks = true;
                        options.UseRecurringCheckoutForPublicPlans = true;
                        options.Calculator = BuildCalculatorConfig();
                    }
                });

                services.AddSingleton<ISubscriptionPricingCatalog, SubscriptionPricingCatalog>();

                services
                    .AddIdentity<AppUsuario, IdentityRole>()
                    .AddEntityFrameworkStores<ApplicationDbContext>()
                    .AddDefaultTokenProviders();

                services.AddScoped<IPublicSiteContentService, PublicSiteContentService>();
                services.AddScoped<LuxuryApp.Services.Asociados.IPostLoginDestinationService,
                    LuxuryApp.Tests.Support.StubPostLoginDestinationService>();
                services.AddScoped<IPrivateNavigationService, EmptyPrivateNavigationService>();

                var mvc = services.AddControllersWithViews(options =>
                {
                    var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
                    options.Filters.Add(new AuthorizeFilter(policy));
                });

                var appAssembly = typeof(LuxuryApp.Controllers.HomeController).Assembly;
                mvc.ConfigureApplicationPartManager(apm =>
                {
                    if (apm.ApplicationParts.All(p => p.Name != appAssembly.GetName().Name))
                    {
                        apm.ApplicationParts.Add(new AssemblyPart(appAssembly));
                    }

                    apm.ApplicationParts.Add(new CompiledRazorAssemblyPart(appAssembly));
                });
            }

            private static List<TilopayRepeatPlanOption> BuildCalculatorConfig()
            {
                var monthly = new[] { 8000m, 15000m, 20000m, 25000m, 30000m, 35000m, 40000m, 45000m, 50000m, 55000m, 60000m };
                var annual = new[] { 81600m, 153000m, 204000m, 255000m, 306000m, 336000m, 360000m, 378000m, 390000m, 429000m, 468000m };
                var annualEq = new[] { 6800m, 12750m, 17000m, 21250m, 25500m, 28000m, 30000m, 31500m, 32500m, 35750m, 39000m };

                var list = new List<TilopayRepeatPlanOption>();
                for (var w = 1; w <= 11; w++)
                {
                    list.Add(new TilopayRepeatPlanOption
                    {
                        Code = $"LC_M_{w:D2}",
                        TilopayPlanId = 6100 + w,
                        BillingCycle = BillingCycle.Monthly,
                        MonthlyPrice = monthly[w - 1],
                        MonthlyEquivalentAmount = monthly[w - 1],
                        Currency = "CRC",
                        MaxFuncionarios = w,
                        CheckoutUrl = $"https://tp.cr/l/m{w}",
                        IsPublic = true,
                        UsesRecurringCheckout = true
                    });
                    list.Add(new TilopayRepeatPlanOption
                    {
                        Code = $"LC_A_{w:D2}",
                        TilopayPlanId = 6200 + w,
                        BillingCycle = BillingCycle.Annual,
                        MonthlyPrice = annual[w - 1],
                        MonthlyEquivalentAmount = annualEq[w - 1],
                        Currency = "CRC",
                        MaxFuncionarios = w,
                        CheckoutUrl = $"https://tp.cr/l/a{w}",
                        IsPublic = true,
                        UsesRecurringCheckout = true
                    });
                }

                return list;
            }

            private static void Configure(IApplicationBuilder app)
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
                });
            }

            public async ValueTask DisposeAsync()
            {
                Client.Dispose();
                await _host.StopAsync();
                _host.Dispose();
                await _connection.DisposeAsync();
            }
        }

        private sealed class EmptyPrivateNavigationService : IPrivateNavigationService
        {
            public Task<PrivateNavigationViewModel> BuildAsync(ClaimsPrincipal principal, CancellationToken ct = default) =>
                Task.FromResult(new PrivateNavigationViewModel());
        }
    }
}
