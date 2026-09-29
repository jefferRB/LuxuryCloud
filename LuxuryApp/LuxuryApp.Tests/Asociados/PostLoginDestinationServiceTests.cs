using System.Security.Claims;
using LuxuryApp.Models.Asociados;
using LuxuryApp.Services.Asociados;
using LuxuryApp.Services.Identity;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Asociados
{
    /// <summary>
    /// Primer destino después del login. Existe para que nadie caiga en un "Acceso denegado"
    /// apenas entra: alguien de Marketing no tiene Dashboard, así que su lugar es la página web.
    /// </summary>
    public sealed class PostLoginDestinationServiceTests : IDisposable
    {
        private readonly Guid _tenantId = Guid.NewGuid();
        private readonly TestTenantProvider _tenantProvider;
        private readonly ApplicationDbContext _context;
        private readonly SqliteConnection _connection;
        private readonly FakePlatformAuditService _audit = new();

        public PostLoginDestinationServiceTests()
        {
            _tenantProvider = new TestTenantProvider { TenantId = _tenantId };
            (_context, _connection) = TestDbContextFactory.CreateSqliteContext(_tenantProvider);
        }

        public void Dispose()
        {
            _context.Dispose();
            _connection.Dispose();
        }

        [Fact]
        public async Task Administrador_VaAlDashboardComoSiempre()
        {
            var service = await CreateServiceAsync("admin-1");
            var principal = BuildPrincipal("admin-1", AppRoles.Administrador);

            Assert.Equal("/Dashboard", await service.ResolveAsync(principal));
        }

        [Fact]
        public async Task Funcionario_VaASuPortal()
        {
            var service = await CreateServiceAsync("func-1");
            var principal = BuildPrincipal("func-1", AppRoles.Funcionario);

            Assert.Equal("/MiPortal", await service.ResolveAsync(principal));
        }

        [Fact]
        public async Task MarketingSoloConPaginaWeb_VaALaPaginaWeb()
        {
            var userId = await SeedAsociadoAsync("Andrea", AppPermissions.PublicWebsiteManage);
            var service = await CreateServiceAsync(userId);

            var destino = await service.ResolveAsync(BuildPrincipal(userId, AppRoles.Asociado));

            Assert.Equal("/Configuracion/PaginaPublica", destino);
        }

        [Fact]
        public async Task AsociadoConDashboard_VaAlDashboard()
        {
            var userId = await SeedAsociadoAsync("Jairo", AppPermissions.DashboardView);
            var service = await CreateServiceAsync(userId);

            Assert.Equal("/Dashboard", await service.ResolveAsync(BuildPrincipal(userId, AppRoles.Asociado)));
        }

        [Fact]
        public async Task AsociadoSinPermisos_VaAUnaPantallaQueLoExplica()
        {
            var userId = await SeedAsociadoAsync("Recién creado");
            var service = await CreateServiceAsync(userId);

            var destino = await service.ResolveAsync(BuildPrincipal(userId, AppRoles.Asociado));

            // Nunca un 403 seco en el primer segundo dentro del producto.
            Assert.Equal("/Home/SinPermisos", destino);
        }

        [Fact]
        public async Task ReturnUrl_HaciaUnModuloProhibido_NoSeRespeta()
        {
            var userId = await SeedAsociadoAsync("Andrea", AppPermissions.PublicWebsiteManage);
            var service = await CreateServiceAsync(userId);
            var principal = BuildPrincipal(userId, AppRoles.Asociado);

            Assert.False(await service.PuedeAbrirAsync(principal, "/Cobros"));
            Assert.False(await service.PuedeAbrirAsync(principal, "/Asociados"));
            Assert.True(await service.PuedeAbrirAsync(principal, "/Configuracion/PaginaPublica"));
        }

        // ─────────────── Helpers ───────────────

        private async Task<string> SeedAsociadoAsync(string nombre, params string[] permisos)
        {
            var email = $"{Guid.NewGuid():N}@test.local";
            var userId = await AssociateTestSupport.SeedTenantAndUserAsync(_context, _tenantId, email);

            var asociado = await AssociateTestSupport.SeedAssociateAsync(
                _context, nombre, email, activo: true, appUsuarioId: userId, AssociateType.Marketing);

            if (permisos.Length > 0)
            {
                await AssociateTestSupport.GrantAsync(_context, asociado.Id, permisos);
            }

            return userId;
        }

        private Task<PostLoginDestinationService> CreateServiceAsync(string userId)
        {
            var accessor = TestHttpContextAccessor.For(
                AssociateTestSupport.BuildAssociatePrincipal(userId, _tenantId));

            return Task.FromResult(new PostLoginDestinationService(
                AssociateTestSupport.CreatePermissionService(_context, accessor, _audit)));
        }

        private ClaimsPrincipal BuildPrincipal(string userId, string rol) =>
            new(new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId),
                    new Claim(CustomClaimTypes.UserId, userId),
                    new Claim(CustomClaimTypes.TenantId, _tenantId.ToString()),
                    new Claim(ClaimTypes.Role, rol)
                ],
                authenticationType: "TestAuth",
                nameType: ClaimTypes.Name,
                roleType: ClaimTypes.Role));
    }
}
