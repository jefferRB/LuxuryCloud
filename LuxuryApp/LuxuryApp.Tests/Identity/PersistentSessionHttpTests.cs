using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Identity
{
    /// <summary>
    /// Pruebas del pipeline HTTP real: levantan un <see cref="TestServer"/> con Identity, la
    /// cookie configurada por <see cref="AuthCookiePolicy"/> y el evento real
    /// <see cref="LuxuryCookieValidation.ValidatePrincipalAsync"/>. Verifican comportamiento del
    /// framework que no se puede confirmar con pruebas unitarias del enforcer:
    /// persistencia real de la cookie, que el backfill no convierta una cookie de sesión en
    /// persistente, que la marca de 90 días sobrevive a las renovaciones y que el tope y la
    /// revocación por security stamp / tenant realmente rechazan el request.
    /// </summary>
    public sealed class PersistentSessionHttpTests : IAsyncLifetime
    {
        private const string AuthCookieName = ".AspNetCore.Identity.Application";
        private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private readonly SqliteConnection _connection = new("DataSource=:memory:");
        private readonly FixedTimeProvider _clock = new(Origin);
        private readonly Guid _tenantId = Guid.NewGuid();
        private const string UserEmail = "owner@test.local";

        private IHost _host = null!;
        private TestServer _server = null!;

        public async Task InitializeAsync()
        {
            _connection.Open();

            _host = await new HostBuilder()
                .ConfigureWebHost(webHost =>
                {
                    webHost.UseTestServer();
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

        private void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<ITenantProvider>(new TestTenantProvider { TenantId = _tenantId });
            services.AddSingleton(_connection);
            services.AddDbContext<ApplicationDbContext>((sp, options) =>
                options.UseSqlite(sp.GetRequiredService<SqliteConnection>()));

            services
                .AddIdentity<AppUsuario, IdentityRole>()
                .AddEntityFrameworkStores<ApplicationDbContext>()
                .AddDefaultTokenProviders();

            // Mismos componentes reales que en Program.cs.
            services.AddScoped<IUserClaimsPrincipalFactory<AppUsuario>, CustomClaimsPrincipalFactory>();
            services.AddScoped<TenantSessionSecurityValidator>();
            // El framework (cookie handler + security stamp validator) usa el reloj real; solo
            // el enforcer del tope de 90 días usa el reloj controlado, para poder "viajar" en el
            // tiempo sin romper el chequeo de intervalo del stamp (que compara con IssuedUtc real).
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(new AbsoluteSessionLifetimeEnforcer(_clock));

            services.ConfigureApplicationCookie(options =>
            {
                AuthCookiePolicy.ConfigureApplicationCookie(options, isDevelopment: false);
                options.Events.OnValidatePrincipal = LuxuryCookieValidation.ValidatePrincipalAsync;
            });

            // Revocación inmediata entre dispositivos, igual que en producción.
            services.Configure<SecurityStampValidatorOptions>(options =>
                options.ValidationInterval = TimeSpan.Zero);

            services.AddRouting();
            services.AddAuthorization();
        }

        private static void Configure(IApplicationBuilder app)
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapPost("/test-login", async context =>
                {
                    var email = context.Request.Query["email"].ToString();
                    var persist = context.Request.Query["persist"].ToString() == "true";
                    var userManager = context.RequestServices.GetRequiredService<UserManager<AppUsuario>>();
                    var signInManager = context.RequestServices.GetRequiredService<SignInManager<AppUsuario>>();

                    var user = await userManager.FindByEmailAsync(email);
                    if (user is null)
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    await signInManager.SignInAsync(user, persist);
                    context.Response.StatusCode = StatusCodes.Status200OK;
                });

                endpoints.MapGet("/whoami", async context =>
                {
                    var auth = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
                    string? marker = null;
                    auth.Properties?.Items.TryGetValue(
                        AbsoluteSessionLifetimeEnforcer.SessionStartedItemKey, out marker);

                    await context.Response.WriteAsJsonAsync(new WhoAmIResponse(
                        context.User.FindFirstValue(CustomClaimTypes.TenantId),
                        context.User.FindFirstValue(CustomClaimTypes.UserId),
                        marker));
                }).RequireAuthorization();
            });
        }

        private async Task SeedAsync()
        {
            using var scope = _host.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await context.Database.EnsureCreatedAsync();

            context.Tenants.Add(new Tenant { Id = _tenantId, Nombre = "Tenant HTTP", Activo = true });

            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUsuario>>();
            var user = new AppUsuario
            {
                UserName = UserEmail,
                Email = UserEmail,
                TenantId = _tenantId,
                State = true
            };

            await context.SaveChangesAsync();
            var result = await userManager.CreateAsync(user, "Valid1!Passw0rd");
            Assert.True(result.Succeeded, string.Join(",", result.Errors.Select(e => e.Description)));
        }

        // --- Fase 5.1 y 5.2: persistencia real de la cookie ---

        [Fact]
        public async Task PersistentLogin_ShouldIssueSecureExpiringHttpOnlyCookie()
        {
            var client = _server.CreateClient();

            var loginCookie = await LoginAsync(client, persist: true);

            Assert.True(loginCookie.HasExpiry, "La cookie persistente debe incluir Expires/Max-Age.");
            Assert.True(loginCookie.HttpOnly);
            Assert.True(loginCookie.Secure);
            Assert.Equal("lax", loginCookie.SameSite);
        }

        [Fact]
        public async Task NonPersistentLogin_ShouldIssueSessionCookieWithoutExpiry()
        {
            var client = _server.CreateClient();

            var loginCookie = await LoginAsync(client, persist: false);

            Assert.False(loginCookie.HasExpiry, "La cookie de sesión NO debe incluir Expires/Max-Age.");
            Assert.True(loginCookie.HttpOnly);
        }

        [Fact]
        public async Task Backfill_ShouldNotConvertSessionCookieIntoPersistent()
        {
            var client = _server.CreateClient();
            var cookie = await LoginAsync(client, persist: false);

            // Primer request autenticado: el backfill siembra la marca y renueva la cookie.
            var (response, renewed) = await WhoAmIAsync(client, cookie);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<WhoAmIResponse>();

            Assert.NotNull(body!.Marker); // la marca quedó sembrada
            Assert.NotNull(renewed);       // hubo renovación (ShouldRenew)
            Assert.False(renewed!.HasExpiry, "El backfill NO debe convertir una cookie de sesión en persistente.");
        }

        // --- Fase 5.3 y 6: tope absoluto de 90 días y marca inmutable ---

        [Fact]
        public async Task AbsoluteLimit_ShouldRejectAfter90DaysWithoutResettingOriginalStart()
        {
            var client = _server.CreateClient();
            var cookie = await LoginAsync(client, persist: true);

            // Request 1 (t = Origin): backfill siembra la marca = Origin.
            (var r1, cookie) = await FollowAsync(client, cookie);
            Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
            var body1 = await r1.Content.ReadFromJsonAsync<WhoAmIResponse>();
            Assert.NotNull(body1!.Marker);

            // Request 2 (t = +40 días): si la marca se reiniciara aquí, la sesión NO expiraría luego.
            _clock.Advance(TimeSpan.FromDays(40));
            (var r2, cookie) = await FollowAsync(client, cookie);
            Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
            var body2 = await r2.Content.ReadFromJsonAsync<WhoAmIResponse>();
            Assert.Equal(body1.Marker, body2!.Marker); // misma marca original: sliding/stamp no la reemplazan

            // Request 3 (t = +91 días desde el origen): supera el tope absoluto.
            _clock.Advance(TimeSpan.FromDays(51));
            var (r3, _) = await WhoAmIAsync(client, cookie);

            Assert.Equal(HttpStatusCode.Redirect, r3.StatusCode);
            Assert.Contains("/Accounts/Acceso", r3.Headers.Location!.ToString());
            // La cookie se borra y NO se emite una nueva cookie autenticada.
            var deletion = ReadAuthCookie(r3);
            Assert.True(deletion is null || deletion.IsDeletion,
                "Una sesión vencida por el tope de 90 días debe borrar la cookie, no re-emitirla.");
        }

        // --- Fase 5.4: revocación por security stamp ---

        [Fact]
        public async Task SecurityStampChange_ShouldRejectPreviouslyValidCookie()
        {
            var client = _server.CreateClient();
            var cookie = await LoginAsync(client, persist: true);

            (var ok, cookie) = await FollowAsync(client, cookie);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

            // Operación administrativa: rota el security stamp (equivale a "cerrar sesión en todos").
            using (var scope = _host.Services.CreateScope())
            {
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUsuario>>();
                var user = await userManager.FindByEmailAsync(UserEmail);
                await userManager.UpdateSecurityStampAsync(user!);
            }

            var (rejected, _) = await WhoAmIAsync(client, cookie);
            Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
            Assert.Contains("/Accounts/Acceso", rejected.Headers.Location!.ToString());
        }

        // --- Fase 5.6: aislamiento multi-tenant ---

        [Fact]
        public async Task SuspendedTenant_ShouldRejectPreviouslyValidCookie()
        {
            var client = _server.CreateClient();
            var cookie = await LoginAsync(client, persist: true);

            (var ok, cookie) = await FollowAsync(client, cookie);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            var body = await ok.Content.ReadFromJsonAsync<WhoAmIResponse>();
            Assert.Equal(_tenantId.ToString(), body!.Tenant); // el claim de tenant se conserva

            using (var scope = _host.Services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var tenant = await context.Tenants.SingleAsync(t => t.Id == _tenantId);
                tenant.Activo = false;
                await context.SaveChangesAsync();
            }

            var (rejected, _) = await WhoAmIAsync(client, cookie);
            Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
            Assert.Contains("/Accounts/Acceso", rejected.Headers.Location!.ToString());
        }

        [Fact]
        public async Task DisabledUser_ShouldRejectPreviouslyValidCookie()
        {
            var client = _server.CreateClient();
            var cookie = await LoginAsync(client, persist: true);

            (var ok, cookie) = await FollowAsync(client, cookie);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

            using (var scope = _host.Services.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var user = await context.Users.SingleAsync(u => u.Email == UserEmail);
                user.State = false;
                await context.SaveChangesAsync();
            }

            var (rejected, _) = await WhoAmIAsync(client, cookie);
            Assert.Equal(HttpStatusCode.Redirect, rejected.StatusCode);
            Assert.Contains("/Accounts/Acceso", rejected.Headers.Location!.ToString());
        }

        // --- Helpers ---

        private static async Task<AuthCookie> LoginAsync(HttpClient client, bool persist)
        {
            var response = await client.PostAsync(
                $"/test-login?email={UserEmail}&persist={(persist ? "true" : "false")}",
                content: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var cookie = ReadAuthCookie(response);
            Assert.NotNull(cookie);
            Assert.False(cookie!.IsDeletion);
            return cookie;
        }

        // Envía /whoami sin seguir el redirect y devuelve la cookie renovada (si la hubo).
        private static async Task<(HttpResponseMessage Response, AuthCookie? Renewed)> WhoAmIAsync(
            HttpClient client, AuthCookie cookie)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/whoami");
            request.Headers.Add("Cookie", cookie.ToRequestHeader());
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead);
            return (response, ReadAuthCookie(response));
        }

        // Igual que WhoAmIAsync pero exige 200 y devuelve la cookie vigente (renovada o la previa).
        private static async Task<(HttpResponseMessage Response, AuthCookie Cookie)> FollowAsync(
            HttpClient client, AuthCookie cookie)
        {
            var (response, renewed) = await WhoAmIAsync(client, cookie);
            var current = renewed is not null && !renewed.IsDeletion ? renewed : cookie;
            return (response, current);
        }

        private static AuthCookie? ReadAuthCookie(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                return null;
            }

            foreach (var raw in setCookies)
            {
                if (raw.StartsWith(AuthCookieName + "=", StringComparison.Ordinal))
                {
                    return AuthCookie.Parse(raw);
                }
            }

            return null;
        }

        private sealed record WhoAmIResponse(string? Tenant, string? UserId, string? Marker);

        private sealed class AuthCookie
        {
            public required string Name { get; init; }
            public required string Value { get; init; }
            public bool HttpOnly { get; init; }
            public bool Secure { get; init; }
            public string? SameSite { get; init; }
            public bool HasExpiry { get; init; }

            // Una cookie con valor vacío es una orden de borrado emitida por SignOut.
            public bool IsDeletion => string.IsNullOrEmpty(Value);

            public string ToRequestHeader() => $"{Name}={Value}";

            public static AuthCookie Parse(string setCookie)
            {
                var segments = setCookie.Split(';');
                var first = segments[0];
                var eq = first.IndexOf('=');
                var name = first[..eq].Trim();
                var value = first[(eq + 1)..].Trim();

                var httpOnly = false;
                var secure = false;
                string? sameSite = null;
                var hasExpiry = false;

                foreach (var attr in segments.Skip(1))
                {
                    var trimmed = attr.Trim();
                    if (trimmed.Equals("httponly", StringComparison.OrdinalIgnoreCase))
                    {
                        httpOnly = true;
                    }
                    else if (trimmed.Equals("secure", StringComparison.OrdinalIgnoreCase))
                    {
                        secure = true;
                    }
                    else if (trimmed.StartsWith("samesite=", StringComparison.OrdinalIgnoreCase))
                    {
                        sameSite = trimmed["samesite=".Length..].ToLowerInvariant();
                    }
                    else if (trimmed.StartsWith("expires=", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.StartsWith("max-age=", StringComparison.OrdinalIgnoreCase))
                    {
                        hasExpiry = true;
                    }
                }

                return new AuthCookie
                {
                    Name = name,
                    Value = value,
                    HttpOnly = httpOnly,
                    Secure = secure,
                    SameSite = sameSite,
                    HasExpiry = hasExpiry
                };
            }
        }
    }
}
