using System.Net;
using System.Text.RegularExpressions;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Marketing;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.Layout;
using LuxuryApp.Services.Account;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Contracts;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Layout;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.PublicSite;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Security;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Antiforgery;
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
using ProyectoIdentity.Datos;
using System.Security.Claims;

namespace LuxuryApp.Tests.Identity
{
    /// <summary>
    /// Pruebas del pipeline HTTP COMPLETO con los controladores y vistas REALES
    /// (AccountsController, SeguridadController, vista Acceso), routing real, autenticación por
    /// cookie real, authorization real, antiforgery real, <see cref="LuxuryCookieValidation"/> y
    /// <c>SecurityStampValidator</c> reales. Cierran los últimos puntos previos a producción:
    /// cierre de sesión en todos los dispositivos con dos cookies, flujo 2FA end-to-end y el HTML
    /// del formulario de login. No se mockea el resultado principal (el pipeline de Identity).
    /// </summary>
    public sealed class SecurityPipelineHttpTests : IAsyncLifetime
    {
        private const string AuthCookieName = ".AspNetCore.Identity.Application";
        private const string Password = "Valid1!Passw0rd";
        private const string UserEmail = "owner@test.local";

        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        private readonly Guid _tenantId = Guid.NewGuid();
        private readonly Dictionary<HttpClient, CookieJarHandler> _jars = new();

        private IHost _host = null!;
        private TestServer _server = null!;

        public async Task InitializeAsync()
        {
            _connection.Open();

            _host = await new HostBuilder()
                .ConfigureWebHost(webHost =>
                {
                    webHost.UseTestServer();
                    // Carga los ApplicationParts (controladores + vistas Razor compiladas) del
                    // ensamblado real de la app, para renderizar la vista de login real.
                    webHost.UseSetting(WebHostDefaults.ApplicationKey, "LuxuryApp");
                    webHost.ConfigureServices(ConfigureServices);
                    webHost.Configure(Configure);
                })
                .StartAsync();

            _server = _host.GetTestServer();
        }

        public async Task DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
            await _connection.DisposeAsync();
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
            services.Configure<IdentityOptions>(options =>
            {
                options.Password.RequiredLength = 8;
                options.Password.RequireUppercase = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
            });
            services.Configure<OpcionesOnboardingTenant>(options =>
            {
                options.RegistrationRole = "Administrador";
                options.AddRegisteredRole = true;
                options.RegisteredRole = "Registrado";
                options.CreateInitialSubscription = false;
            });
            services.Configure<TilopayRepeatOptions>(_ => { });
            services.Configure<RegistrationSecurityOptions>(options =>
            {
                options.RequireEmailConfirmation = false;
                options.Turnstile.Enabled = false;
            });

            services
                .AddIdentity<AppUsuario, IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

            // Mismos componentes de seguridad reales que Program.cs.
            services.AddScoped<IUserClaimsPrincipalFactory<AppUsuario>, CustomClaimsPrincipalFactory>();
            services.AddScoped<TenantSessionSecurityValidator>();
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<AbsoluteSessionLifetimeEnforcer>();

            services.ConfigureApplicationCookie(options =>
            {
                // isDevelopment:false => Secure=Always (configuración equivalente a producción).
                AuthCookiePolicy.ConfigureApplicationCookie(options, isDevelopment: false);
                options.Events.OnValidatePrincipal = LuxuryCookieValidation.ValidatePrincipalAsync;
            });
            services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);

            // Dependencias de AccountsController.
            services.AddScoped<SuscripcionService>();
            services.AddScoped<ITenantCommercialAccessCache, TenantCommercialAccessCache>();
            services.AddScoped<ITenantCommercialAccessResolver, TenantCommercialAccessResolver>();
            services.AddScoped<IPromotionalCodeService, PromotionalCodeService>();
            services.AddScoped<IContractService, ContractService>();
            services.AddScoped<TenantProvisioningService>();
            services.AddScoped<ITenantDisplayNameService, TenantDisplayNameService>();
            services.AddScoped<IPublicSiteContentService, EmptyPublicSiteContentService>();
            services.AddScoped<LuxuryApp.Services.Asociados.IPostLoginDestinationService,
                LuxuryApp.Tests.Support.StubPostLoginDestinationService>();
            services.AddScoped<IAccountEmailService, NoopEmailService>();
            services.AddScoped<LuxuryApp.Services.Account.IAccountSettingsPageBuilder,
                LuxuryApp.Services.Account.AccountSettingsPageBuilder>();
            services.AddSingleton<RegistrationSecurityService>();
            services.AddHttpClient<TurnstileVerificationService>();

            // Dependencias de SeguridadController.
            services.AddScoped<IPlatformAuditService, NoopAuditService>();
            services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<PlatformSecurityOptions>>(
                new StaticOptionsMonitor<PlatformSecurityOptions>(new PlatformSecurityOptions
                {
                    Mfa = new PlatformSecurityOptions.MfaOptions { SuperAdminEnforcement = false }
                }));

            // El navbar público inyecta IPrivateNavigationService; para render anónimo no se llama.
            services.AddScoped<IPrivateNavigationService, EmptyPrivateNavigationService>();

            services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");

            var mvc = services.AddControllersWithViews(options =>
            {
                // Mismo gate global que Program.cs: todo exige autenticación salvo [AllowAnonymous].
                var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
                options.Filters.Add(new AuthorizeFilter(policy));
            });

            // Con TestServer el ensamblado de entrada es el de pruebas; registramos explícitamente
            // los ApplicationParts del ensamblado REAL: AssemblyPart para los controladores y
            // CompiledRazorAssemblyPart para las vistas Razor precompiladas de la vista de login.
            var appAssembly = typeof(LuxuryApp.Controllers.Identity.AccountsController).Assembly;
            mvc.ConfigureApplicationPartManager(apm =>
            {
                if (apm.ApplicationParts.All(p => p.Name != appAssembly.GetName().Name))
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
                endpoints.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");

                // Emite un token antiforgery real (mismo servicio que usa el tag helper),
                // ligado al principal actual, para poder ejecutar POST reales con antiforgery.
                endpoints.MapGet("/test-antiforgery", (HttpContext ctx, IAntiforgery antiforgery) =>
                {
                    var tokens = antiforgery.GetAndStoreTokens(ctx);
                    return Results.Text(tokens.RequestToken ?? string.Empty);
                });

                // Lee la marca de sesión del ticket REAL (post-backfill), autenticado por cookie.
                endpoints.MapGet("/test-session-marker", async ctx =>
                {
                    var auth = await ctx.AuthenticateAsync(IdentityConstants.ApplicationScheme);
                    string? marker = null;
                    auth.Properties?.Items.TryGetValue(
                        AbsoluteSessionLifetimeEnforcer.SessionStartedItemKey, out marker);
                    await ctx.Response.WriteAsync(marker ?? string.Empty);
                }).RequireAuthorization();

                // Devuelve los claims del principal autenticado real (para verificar que se conservan).
                endpoints.MapGet("/test-claims", async ctx =>
                {
                    var tenant = ctx.User.FindFirstValue(CustomClaimTypes.TenantId) ?? string.Empty;
                    var userId = ctx.User.FindFirstValue(CustomClaimTypes.UserId) ?? string.Empty;
                    var funcionario = ctx.User.FindFirstValue(CustomClaimTypes.FuncionarioId) ?? string.Empty;
                    await ctx.Response.WriteAsync($"{tenant}|{userId}|{funcionario}");
                }).RequireAuthorization();
            });
        }

        // ---------------------------------------------------------------------------------
        // 2. Cierre de sesión en todos los dispositivos (dos cookies reales)
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task CerrarSesionEnTodosLosDispositivos_ShouldInvalidateBothSessionsThroughRealPipeline()
        {
            await SeedUserAsync(twoFactor: false);
            var clientA = CreateClient();
            var clientB = CreateClient();

            await LoginWithPasswordAsync(clientA);
            await LoginWithPasswordAsync(clientB);

            // Ambos acceden a un endpoint protegido real.
            Assert.Equal(HttpStatusCode.OK, (await clientA.GetAsync("/test-claims")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await clientB.GetAsync("/test-claims")).StatusCode);

            var stampBefore = await GetSecurityStampAsync();

            // Cliente A ejecuta el cierre global vía HTTP real con antiforgery.
            var response = await PostFormAsync(
                clientA, "/Seguridad/CerrarSesionEnTodosLosDispositivos", new Dictionary<string, string>());

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Accounts/Acceso", response.Headers.Location!.ToString());

            // La cookie actual de A se borró.
            Assert.True(string.IsNullOrEmpty(Jar(clientA).GetCookie(AuthCookieName)),
                "El cierre global debe borrar la cookie actual del cliente A.");

            // El SecurityStamp cambió.
            var stampAfter = await GetSecurityStampAsync();
            Assert.NotEqual(stampBefore, stampAfter);

            // A ya no puede acceder.
            var aAfter = await clientA.GetAsync("/test-claims");
            Assert.Equal(HttpStatusCode.Redirect, aAfter.StatusCode);
            Assert.Contains("/Accounts/Acceso", aAfter.Headers.Location!.ToString());

            // B tenía cookie y cae en su siguiente request (misma cookie anterior), sin renovarse.
            var bBefore = Jar(clientB).GetCookie(AuthCookieName);
            Assert.False(string.IsNullOrEmpty(bBefore));
            var bAfter = await clientB.GetAsync("/test-claims");
            Assert.Equal(HttpStatusCode.Redirect, bAfter.StatusCode);
            Assert.Contains("/Accounts/Acceso", bAfter.Headers.Location!.ToString());
            Assert.True(string.IsNullOrEmpty(Jar(clientB).GetCookie(AuthCookieName)),
                "La cookie anterior de B no debe volver a emitirse como autenticada.");
        }

        [Fact]
        public async Task CerrarSesionEnTodosLosDispositivos_ShouldRejectGet()
        {
            await SeedUserAsync(twoFactor: false);
            var client = CreateClient();
            await LoginWithPasswordAsync(client);

            var response = await client.GetAsync("/Seguridad/CerrarSesionEnTodosLosDispositivos");

            // No existe acción GET: nunca 200 (no ejecuta la operación por GET).
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task CerrarSesionEnTodosLosDispositivos_ShouldRejectPostWithoutAntiforgery()
        {
            await SeedUserAsync(twoFactor: false);
            var client = CreateClient();
            await LoginWithPasswordAsync(client);
            var stampBefore = await GetSecurityStampAsync();

            var response = await client.PostAsync(
                "/Seguridad/CerrarSesionEnTodosLosDispositivos",
                new FormUrlEncodedContent(new Dictionary<string, string>()));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(stampBefore, await GetSecurityStampAsync()); // no ejecutó la revocación
        }

        [Fact]
        public async Task CerrarSesionEnTodosLosDispositivos_ShouldRejectUnauthenticated()
        {
            await SeedUserAsync(twoFactor: false);
            var client = CreateClient();

            var response = await PostFormAsync(
                client, "/Seguridad/CerrarSesionEnTodosLosDispositivos", new Dictionary<string, string>());

            // Sin sesión: authorization redirige al login; jamás ejecuta la operación.
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Accounts/Acceso", response.Headers.Location!.ToString());
        }

        // ---------------------------------------------------------------------------------
        // 3. Flujo completo de 2FA end-to-end
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task TwoFactorLogin_ShouldNotIssueAppCookieUntilTotpConfirmed_ThenPersist()
        {
            var key = await SeedUserAsync(twoFactor: true);
            var client = CreateClient();

            // Paso 1: contraseña válida + RememberMe. NO debe emitir la cookie de aplicación.
            var step1 = await PostFormAsync(client, "/Accounts/Acceso", new Dictionary<string, string>
            {
                ["Email"] = UserEmail,
                ["Password"] = Password,
                ["RememberMe"] = "true",
                ["returnurl"] = "/"
            });

            Assert.Equal(HttpStatusCode.Redirect, step1.StatusCode);
            Assert.Contains("/Accounts/VerificarCodigo", step1.Headers.Location!.ToString());
            Assert.True(string.IsNullOrEmpty(Jar(client).GetCookie(AuthCookieName)),
                "La cookie de aplicación no debe emitirse antes de completar el segundo factor.");

            // Paso 2: código TOTP válido calculado de forma determinista.
            var code = TotpTestHelper.ComputeCurrentCode(key!);
            var step2 = await PostFormAsync(client, "/Accounts/VerificarCodigo", new Dictionary<string, string>
            {
                ["Codigo"] = code,
                ["RememberMe"] = "true",
                ["ReturnUrl"] = "/"
            });

            Assert.Equal(HttpStatusCode.Redirect, step2.StatusCode);
            // Ahora sí existe la cookie de aplicación, persistente y segura.
            var setCookie = GetSetAuthCookieRaw(step2);
            Assert.NotNull(setCookie);
            Assert.Contains("expires=", setCookie!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrEmpty(Jar(client).GetCookie(AuthCookieName)));

            // Request autenticado posterior: la marca de 90 días se sembró una vez y es estable.
            var marker1 = await (await client.GetAsync("/test-session-marker")).Content.ReadAsStringAsync();
            Assert.False(string.IsNullOrWhiteSpace(marker1));
            var marker2 = await (await client.GetAsync("/test-session-marker")).Content.ReadAsStringAsync();
            Assert.Equal(marker1, marker2); // no se reemplaza en requests posteriores

            // Los claims se conservan tras la renovación por security stamp.
            var claims = await (await client.GetAsync("/test-claims")).Content.ReadAsStringAsync();
            Assert.StartsWith($"{_tenantId}|", claims);
        }

        [Fact]
        public async Task TwoFactorLogin_WithWrongCode_ShouldNotIssueAppCookie()
        {
            var key = await SeedUserAsync(twoFactor: true);
            var client = CreateClient();

            await PostFormAsync(client, "/Accounts/Acceso", new Dictionary<string, string>
            {
                ["Email"] = UserEmail,
                ["Password"] = Password,
                ["RememberMe"] = "true",
                ["returnurl"] = "/"
            });

            var wrongCode = TotpTestHelper.CorruptCode(TotpTestHelper.ComputeCurrentCode(key!));
            var response = await PostFormAsync(client, "/Accounts/VerificarCodigo", new Dictionary<string, string>
            {
                ["Codigo"] = wrongCode,
                ["RememberMe"] = "true",
                ["ReturnUrl"] = "/"
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // re-render con error, sin redirect
            Assert.True(string.IsNullOrEmpty(Jar(client).GetCookie(AuthCookieName)),
                "Un TOTP incorrecto no debe emitir la cookie de aplicación.");
        }

        // ---------------------------------------------------------------------------------
        // 4. HTML real del formulario de login
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task AccesoGet_ShouldRenderPasswordManagerFriendlyForm()
        {
            await SeedUserAsync(twoFactor: false);
            var client = CreateClient();

            var response = await client.GetAsync("/Accounts/Acceso");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            var form = ExtractLoginForm(html);

            Assert.Contains("autocomplete=\"on\"", form);
            Assert.Contains("autocomplete=\"username\"", form);
            Assert.Contains("autocomplete=\"current-password\"", form);
            Assert.DoesNotContain("autocomplete=\"off\"", form);

            // Campo password real, con name estable y sin value precargado.
            Assert.Matches(new Regex("<input[^>]*name=\"Password\"", RegexOptions.IgnoreCase), form);
            Assert.Contains("type=\"password\"", form);
            Assert.DoesNotContain("name=\"Password\" value=", form, StringComparison.OrdinalIgnoreCase);

            // Email con name estable.
            Assert.Matches(new Regex("<input[^>]*name=\"Email\"", RegexOptions.IgnoreCase), form);

            // Checkbox RememberMe marcado por defecto en GET limpio, con label asociado.
            var rememberInput = ExtractTag(form, "input", "name=\"RememberMe\"");
            Assert.Contains("type=\"checkbox\"", rememberInput);
            Assert.Contains("checked", rememberInput);
            Assert.Matches(new Regex("<label[^>]*for=\"RememberMe\"", RegexOptions.IgnoreCase), form);

            // Textos finales visibles (fragmentos ASCII estables para evitar fragilidad de encoding).
            Assert.Contains("Recordar este dispositivo y mantener mi", form);
            Assert.Contains("tu navegador puede ofrecer guardar tus credenciales", form);
            Assert.Contains("No uses esta", form);
            Assert.Contains("compartidos", form);

            // Antiforgery presente en el formulario.
            Assert.Contains("__RequestVerificationToken", form);
        }

        [Fact]
        public async Task AccesoPost_WithInvalidCredentials_ShouldRerenderWithoutLeakingPassword()
        {
            await SeedUserAsync(twoFactor: false);
            var client = CreateClient();
            const string secret = "Wrong9!Secret";

            var response = await PostFormAsync(client, "/Accounts/Acceso", new Dictionary<string, string>
            {
                ["Email"] = UserEmail,
                ["Password"] = secret,
                ["RememberMe"] = "true",
                ["returnurl"] = "/"
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // re-render de la vista con error
            var html = await response.Content.ReadAsStringAsync();

            // Mensaje genérico (sin enumeración) y contraseña jamás precargada en el HTML.
            Assert.Contains("No pudimos iniciar", html);
            Assert.DoesNotContain(secret, html);
            Assert.True(string.IsNullOrEmpty(Jar(client).GetCookie(AuthCookieName)));
        }

        // ---------------------------------------------------------------------------------
        // 5. Render real de las pantallas de configuracion unificadas
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Mi cuenta se RENDERIZA de verdad (no sólo compila): pipeline real, cookie real, vista
        /// real con sus cuatro parciales. Es lo que atrapa un modelo equivocado, un parcial que no
        /// existe o una referencia nula que el compilador no ve.
        /// </summary>
        [Fact]
        public async Task Cuenta_ShouldRenderUnifiedPageForAuthenticatedUser()
        {
            await SeedUserAsync(twoFactor: false);
            var client = CreateClient();
            await LoginWithPasswordAsync(client);

            var response = await client.GetAsync("/Accounts/Cuenta");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var html = await response.Content.ReadAsStringAsync();

            // Una sola card de módulo con el shell compartido.
            Assert.Contains("finance-page-shell", html, StringComparison.Ordinal);

            // Las secciones y sus anclas (las rutas legacy redirigen a estos fragmentos).
            Assert.Contains("id=\"perfil\"", html, StringComparison.Ordinal);
            Assert.Contains("id=\"seguridad\"", html, StringComparison.Ordinal);
            Assert.Contains("id=\"preferencias\"", html, StringComparison.Ordinal);

            // Seguridad integrada: estado del 2FA y sesiones, sin salir de la pantalla.
            Assert.Contains("Verificación en dos pasos", html, StringComparison.Ordinal);
            Assert.Contains("Cerrar sesión en todos los dispositivos", html, StringComparison.Ordinal);

            // Cada formulario lleva su antiforgery.
            Assert.Contains("__RequestVerificationToken", html, StringComparison.Ordinal);
        }

        /// <summary>
        /// El usuario del seed no es Administrador: la sección fiscal NO puede llegar al HTML.
        /// Mover la configuración de sitio no se la regala a nadie.
        /// </summary>
        [Fact]
        public async Task Cuenta_ShouldNotRenderFiscalSectionForNonAdministrador()
        {
            await SeedUserAsync(twoFactor: false);
            var client = CreateClient();
            await LoginWithPasswordAsync(client);

            var html = await (await client.GetAsync("/Accounts/Cuenta")).Content.ReadAsStringAsync();

            Assert.DoesNotContain("id=\"fiscal\"", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Configuración fiscal", html, StringComparison.Ordinal);
            Assert.DoesNotContain("TarifaIvaPorDefecto", html, StringComparison.Ordinal);
        }

        /// <summary>
        /// Ruta legacy de impuestos: sin pantalla propia redirige a Mi cuenta, no da 404.
        /// Acá el usuario no es Administrador, así que el gate de rol responde primero — que es
        /// exactamente lo que debe pasar: la autorización manda sobre la redirección.
        /// </summary>
        [Fact]
        public async Task ConfiguracionFiscal_LegacyRoute_ShouldNotReturnNotFound()
        {
            await SeedUserAsync(twoFactor: false);
            var client = CreateClient();
            await LoginWithPasswordAsync(client);

            var response = await client.GetAsync("/ConfiguracionFiscal");

            Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
            Assert.True(
                response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.Forbidden,
                $"Esperaba redirección o 403 por rol, llegó {response.StatusCode}.");
        }

        /// <summary>
        /// El enrolamiento de MFA sigue siendo una pantalla real (el portal de funcionarios y el
        /// enrolamiento obligatorio de superadmin llegan ahí directo) y ahora usa el shell común.
        /// El QR se dibuja en el navegador: la clave viaja una sola vez, en la URI otpauth.
        /// </summary>
        [Fact]
        public async Task Enrolar_ShouldRenderInsideSharedShell()
        {
            await SeedUserAsync(twoFactor: false);
            var client = CreateClient();
            await LoginWithPasswordAsync(client);

            var response = await client.GetAsync("/Seguridad/Enrolar");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var html = await response.Content.ReadAsStringAsync();

            Assert.Contains("finance-page-shell", html, StringComparison.Ordinal);
            Assert.Contains("Verificación en dos pasos", html, StringComparison.Ordinal);
            Assert.Contains("mfa-qr", html, StringComparison.Ordinal);
            Assert.Contains("Verificar y activar", html, StringComparison.Ordinal);

            // Sigue ofreciendo el cierre de sesión global, que es la única vía del portal
            // de funcionarios para esa acción.
            Assert.Contains("Cerrar sesión en todos los dispositivos", html, StringComparison.Ordinal);
        }

        /// <summary>
        /// Con el 2FA ya activo la pantalla NO vuelve a mostrar el secreto ni el QR: se enseñan
        /// una sola vez, durante el enrolamiento.
        /// </summary>
        [Fact]
        public async Task Enrolar_WithTwoFactorActive_ShouldNotExposeSecret()
        {
            var key = await SeedUserAsync(twoFactor: true);
            var client = CreateClient();

            // Con 2FA activo el login pide el código; se autentica el segundo factor.
            await PostFormAsync(client, "/Accounts/Acceso", new Dictionary<string, string>
            {
                ["Email"] = UserEmail,
                ["Password"] = Password,
                ["RememberMe"] = "true",
                ["returnurl"] = "/"
            });

            await PostFormAsync(client, "/Accounts/VerificarCodigo", new Dictionary<string, string>
            {
                ["Codigo"] = TotpTestHelper.ComputeCurrentCode(key!),
                ["RememberMe"] = "true",
                ["ReturnUrl"] = "/"
            });

            var html = await (await client.GetAsync("/Seguridad/Enrolar")).Content.ReadAsStringAsync();

            Assert.DoesNotContain("otpauth://", html, StringComparison.Ordinal);
            Assert.DoesNotContain("data-otpauth", html, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------------

        private async Task<string?> SeedUserAsync(bool twoFactor)
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await context.Database.EnsureCreatedAsync();

            if (!await context.Tenants.AnyAsync(t => t.Id == _tenantId))
            {
                context.Tenants.Add(new Tenant { Id = _tenantId, Nombre = "Tenant HTTP", Activo = true });
                await context.SaveChangesAsync();
            }

            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUsuario>>();
            var user = new AppUsuario
            {
                UserName = UserEmail,
                Email = UserEmail,
                TenantId = _tenantId,
                State = true
            };
            var result = await userManager.CreateAsync(user, Password);
            Assert.True(result.Succeeded, string.Join(",", result.Errors.Select(e => e.Description)));

            if (!twoFactor)
            {
                return null;
            }

            await userManager.ResetAuthenticatorKeyAsync(user);
            var key = await userManager.GetAuthenticatorKeyAsync(user);
            await userManager.SetTwoFactorEnabledAsync(user, true);
            return key;
        }

        private async Task<string?> GetSecurityStampAsync()
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await context.Users.SingleAsync(u => u.Email == UserEmail);
            return user.SecurityStamp;
        }

        private async Task LoginWithPasswordAsync(HttpClient client)
        {
            var response = await PostFormAsync(client, "/Accounts/Acceso", new Dictionary<string, string>
            {
                ["Email"] = UserEmail,
                ["Password"] = Password,
                ["RememberMe"] = "true",
                ["returnurl"] = "/"
            });
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.False(string.IsNullOrEmpty(Jar(client).GetCookie(AuthCookieName)),
                "El login con contraseña (sin 2FA) debe emitir la cookie de aplicación.");
        }

        private async Task<HttpResponseMessage> PostFormAsync(
            HttpClient client, string url, Dictionary<string, string> fields)
        {
            var token = await (await client.GetAsync("/test-antiforgery")).Content.ReadAsStringAsync();
            var payload = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
            return await client.PostAsync(url, new FormUrlEncodedContent(payload));
        }

        private HttpClient CreateClient()
        {
            var jar = new CookieJarHandler(_server.CreateHandler());
            var client = new HttpClient(jar) { BaseAddress = new Uri("http://localhost/") };
            _jars[client] = jar;
            return client;
        }

        private CookieJarHandler Jar(HttpClient client) => _jars[client];

        private static string? GetSetAuthCookieRaw(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                return null;
            }

            return setCookies.FirstOrDefault(c => c.StartsWith(AuthCookieName + "=", StringComparison.Ordinal));
        }

        private static string ExtractLoginForm(string html)
        {
            // El <form ... method="post"> que contiene el campo Password.
            var forms = Regex.Matches(html, "<form[\\s\\S]*?</form>", RegexOptions.IgnoreCase);
            foreach (Match form in forms)
            {
                if (form.Value.Contains("name=\"Password\"", StringComparison.OrdinalIgnoreCase))
                {
                    return form.Value;
                }
            }

            return html;
        }

        private static string ExtractTag(string html, string tag, string containing)
        {
            var matches = Regex.Matches(html, $"<{tag}[^>]*>", RegexOptions.IgnoreCase);
            foreach (Match m in matches)
            {
                if (m.Value.Contains(containing, StringComparison.OrdinalIgnoreCase))
                {
                    return m.Value;
                }
            }

            return string.Empty;
        }

        // --- Fakes periféricos (no tocan el pipeline de Identity) ---

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

        private sealed class EmptyPrivateNavigationService : IPrivateNavigationService
        {
            public Task<PrivateNavigationViewModel> BuildAsync(ClaimsPrincipal principal, CancellationToken ct = default) =>
                Task.FromResult(new PrivateNavigationViewModel());
        }

        private sealed class NoopEmailService : IAccountEmailService
        {
            public Task SendPasswordResetEmailAsync(string toEmail, string displayName, string resetLink, CancellationToken ct = default) =>
                Task.CompletedTask;
            public Task SendEmailConfirmationEmailAsync(string toEmail, string displayName, string confirmationLink, CancellationToken ct = default) =>
                Task.CompletedTask;
            public Task SendFuncionarioInvitationEmailAsync(string toEmail, string displayName, string setPasswordLink, string businessName, CancellationToken ct = default) =>
                Task.CompletedTask;
            public Task SendAccessInvitationEmailAsync(string toEmail, string displayName, string setPasswordLink, string businessName, string accessDescription, CancellationToken ct = default) =>
                Task.CompletedTask;
        }

        private sealed class NoopAuditService : IPlatformAuditService
        {
            public Task LogAsync(PlatformAuditEntry entry, CancellationToken ct = default) => Task.CompletedTask;
            public Task TryLogAsync(PlatformAuditEntry entry, CancellationToken ct = default) => Task.CompletedTask;
            public Task<IReadOnlyList<PlatformAuditLog>> GetRecentAsync(int take = 100, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());
            public Task<IReadOnlyList<PlatformAuditLog>> GetByTenantAsync(Guid tenantId, int take = 100, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());
            public Task<IReadOnlyList<PlatformAuditLog>> GetByUserAsync(string targetUserId, int take = 100, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());
            public Task<int> CountActorFailuresSinceAsync(string actorUserId, DateTime sinceUtc, CancellationToken ct = default) =>
                Task.FromResult(0);
        }
    }
}
