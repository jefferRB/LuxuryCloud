using LuxuryApp.Models.Asociados;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Asociados
{
    /// <summary>
    /// Resolución de permisos de un asociado. Es la pieza que decide si una URL se abre o
    /// devuelve 403, así que se prueba contra base de datos real (SQLite en memoria) y no
    /// contra dobles.
    /// </summary>
    public sealed class AssociatePermissionServiceTests : IDisposable
    {
        private readonly Guid _tenantId = Guid.NewGuid();
        private readonly TestTenantProvider _tenantProvider;
        private readonly ApplicationDbContext _context;
        private readonly SqliteConnection _connection;
        private readonly FakePlatformAuditService _audit = new();

        public AssociatePermissionServiceTests()
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
        public async Task SinConcesiones_NoTieneNingunPermiso()
        {
            var (asociado, accessor) = await SeedAsociadoConCuentaAsync("Andrea", "andrea@test.local");

            var service = AssociateTestSupport.CreatePermissionService(_context, accessor, _audit);
            var permisos = await service.ObtenerDelUsuarioActualAsync();

            Assert.False(permisos.TieneAlguno);
            Assert.False(permisos.Tiene(AppPermissions.DashboardView));
            Assert.False(permisos.Tiene(AppPermissions.PublicWebsiteManage));
            Assert.NotEqual(0, asociado.Id);
        }

        [Fact]
        public async Task Manage_ImplicaView_DelMismoModulo()
        {
            var (asociado, accessor) = await SeedAsociadoConCuentaAsync("Andrea", "andrea@test.local");
            await AssociateTestSupport.GrantAsync(_context, asociado.Id, AppPermissions.PublicWebsiteManage);

            var service = AssociateTestSupport.CreatePermissionService(_context, accessor, _audit);
            var permisos = await service.ObtenerDelUsuarioActualAsync();

            Assert.True(permisos.Tiene(AppPermissions.PublicWebsiteManage));
            Assert.True(permisos.Tiene(AppPermissions.PublicWebsiteView));

            // Y nada más: administrar la web no abre las finanzas.
            Assert.False(permisos.Tiene(AppPermissions.DashboardView));
            Assert.False(permisos.Tiene(AppPermissions.IncomeView));
            Assert.False(permisos.Tiene(AppPermissions.AssociatesView));
        }

        [Fact]
        public async Task CuentaBloqueada_PierdeTodosLosPermisos()
        {
            var (asociado, accessor) = await SeedAsociadoConCuentaAsync("Andrea", "andrea@test.local");
            await AssociateTestSupport.GrantAsync(_context, asociado.Id, AppPermissions.PublicWebsiteManage);

            var cuenta = _context.Users.Single(user => user.Id == asociado.AppUsuarioId);
            cuenta.State = false;
            await _context.SaveChangesAsync();

            var service = AssociateTestSupport.CreatePermissionService(_context, accessor, _audit);
            var permisos = await service.ObtenerDelUsuarioActualAsync();

            // Bloquear el acceso NO obliga a desmarcar permisos uno por uno: el conjunto queda vacío.
            Assert.False(permisos.TieneAlguno);
        }

        [Fact]
        public async Task AsociadoInactivo_PierdeTodosLosPermisos()
        {
            var (asociado, accessor) = await SeedAsociadoConCuentaAsync("Andrea", "andrea@test.local");
            await AssociateTestSupport.GrantAsync(_context, asociado.Id, AppPermissions.PublicWebsiteManage);

            var seguimiento = _context.Associates.Single(current => current.Id == asociado.Id);
            seguimiento.Activo = false;
            await _context.SaveChangesAsync();

            var service = AssociateTestSupport.CreatePermissionService(_context, accessor, _audit);
            var permisos = await service.ObtenerDelUsuarioActualAsync();

            Assert.False(permisos.TieneAlguno);

            // Pero las concesiones siguen guardadas: reactivarlo lo devuelve tal cual estaba.
            Assert.NotEmpty(_context.AssociatePermissions.Where(permiso => permiso.AssociateId == asociado.Id));
        }

        [Fact]
        public async Task CambiarPermisos_AplicaSinCerrarSesion()
        {
            var (asociado, accessor) = await SeedAsociadoConCuentaAsync("Andrea", "andrea@test.local");
            await AssociateTestSupport.GrantAsync(_context, asociado.Id, AppPermissions.PublicWebsiteManage);

            // Request 1: tiene la web.
            var primerRequest = AssociateTestSupport.CreatePermissionService(_context, accessor, _audit);
            Assert.True((await primerRequest.ObtenerDelUsuarioActualAsync()).Tiene(AppPermissions.PublicWebsiteManage));

            // El administrador se los quita y le da otro.
            var guardado = await primerRequest.GuardarAsync(
                asociado.Id,
                [AppPermissions.CalendarView],
                actorUserId: "admin-1");

            Assert.True(guardado);

            // Request 2 (nuevo HttpContext = nuevo caché por request): ya no puede entrar a la web.
            var segundoRequest = AssociateTestSupport.CreatePermissionService(
                _context,
                NuevoAccessor(asociado.AppUsuarioId!),
                _audit);

            var permisos = await segundoRequest.ObtenerDelUsuarioActualAsync();

            Assert.False(permisos.Tiene(AppPermissions.PublicWebsiteManage));
            Assert.False(permisos.Tiene(AppPermissions.PublicWebsiteView));
            Assert.True(permisos.Tiene(AppPermissions.CalendarView));
        }

        [Fact]
        public async Task Guardar_IgnoraClavesFueraDelCatalogo()
        {
            var (asociado, accessor) = await SeedAsociadoConCuentaAsync("Andrea", "andrea@test.local");

            var service = AssociateTestSupport.CreatePermissionService(_context, accessor, _audit);
            await service.GuardarAsync(
                asociado.Id,
                ["Permiso.Inventado", AppPermissions.CalendarView],
                actorUserId: "admin-1");

            var permisos = await service.ObtenerDeAsociadoAsync(asociado.Id);

            Assert.True(permisos.Tiene(AppPermissions.CalendarView));
            Assert.DoesNotContain("Permiso.Inventado", permisos.Concedidos);
        }

        [Fact]
        public async Task Asociados_NoSeVenEntreTenants()
        {
            var (asociadoA, _) = await SeedAsociadoConCuentaAsync("Andrea A", "a@test.local");
            await AssociateTestSupport.GrantAsync(_context, asociadoA.Id, AppPermissions.PublicWebsiteManage);

            // Otro negocio intenta guardar permisos sobre el asociado del primero.
            var otroTenant = Guid.NewGuid();
            _tenantProvider.TenantId = otroTenant;

            var service = AssociateTestSupport.CreatePermissionService(
                _context,
                NuevoAccessor(asociadoA.AppUsuarioId!, otroTenant),
                _audit);

            var guardado = await service.GuardarAsync(
                asociadoA.Id,
                [AppPermissions.IncomeManage],
                actorUserId: "intruso");

            Assert.False(guardado);

            // Y el usuario del tenant A tampoco resuelve permisos dentro del tenant B.
            var permisos = await service.ObtenerDelUsuarioActualAsync();
            Assert.False(permisos.TieneAlguno);

            _tenantProvider.TenantId = _tenantId;
            var sigueIntacto = await AssociateTestSupport
                .CreatePermissionService(_context, NuevoAccessor(asociadoA.AppUsuarioId!), _audit)
                .ObtenerDeAsociadoAsync(asociadoA.Id);

            Assert.True(sigueIntacto.Tiene(AppPermissions.PublicWebsiteManage));
        }

        // ─────────────── Helpers ───────────────

        private async Task<(Associate Asociado, IHttpContextAccessor Accessor)> SeedAsociadoConCuentaAsync(
            string nombre,
            string email)
        {
            var userId = await AssociateTestSupport.SeedTenantAndUserAsync(_context, _tenantId, email);

            var asociado = await AssociateTestSupport.SeedAssociateAsync(
                _context,
                nombre,
                email,
                activo: true,
                appUsuarioId: userId,
                AssociateType.Marketing);

            return (asociado, NuevoAccessor(userId));
        }

        private IHttpContextAccessor NuevoAccessor(string userId, Guid? tenantId = null)
        {
            return TestHttpContextAccessor.For(
                AssociateTestSupport.BuildAssociatePrincipal(userId, tenantId ?? _tenantId));
        }
    }
}
