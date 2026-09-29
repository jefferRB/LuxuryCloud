using System.Security.Claims;
using LuxuryApp.Models.Asociados;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.Asociados;
using LuxuryApp.Services.Inversionistas;
using LuxuryApp.Services.Tenant;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Support
{
    /// <summary>
    /// Armado del módulo de Asociados sobre SQLite en memoria, con las implementaciones REALES de
    /// permisos, participación y cálculo de ganancia. Nada de dobles donde hay dinero o
    /// autorización: si esas reglas cambian, las pruebas lo detectan.
    /// </summary>
    internal static class AssociateTestSupport
    {
        public static AssociatePermissionService CreatePermissionService(
            ApplicationDbContext context,
            IHttpContextAccessor httpContextAccessor,
            FakePlatformAuditService audit) =>
            new(
                context,
                httpContextAccessor,
                ControllerTestSupport.BusinessDateTimeProvider,
                audit,
                NullLogger<AssociatePermissionService>.Instance);

        public static AssociateProfitAllocationService CreateAllocationService(
            ApplicationDbContext context,
            ITenantProvider tenantProvider,
            FakePlatformAuditService audit) =>
            new(
                context,
                InvestorTestSupport.CreateCalculationService(context, tenantProvider),
                ControllerTestSupport.BusinessDateTimeProvider);

        public static AssociateService CreateAssociateService(
            ApplicationDbContext context,
            IHttpContextAccessor httpContextAccessor,
            FakePlatformAuditService audit,
            IAssociateAccessService accessService,
            ITenantProvider tenantProvider,
            IInvestorService? investorService = null)
        {
            investorService ??= InvestorTestSupport.CreateInvestorService(context, audit);

            return new AssociateService(
                context,
                investorService,
                InvestorTestSupport.CreateCycleService(context, tenantProvider, audit, investorService),
                CreatePermissionService(context, httpContextAccessor, audit),
                accessService,
                ControllerTestSupport.BusinessDateTimeProvider,
                audit,
                NullLogger<AssociateService>.Instance);
        }

        /// <summary>UserManager real sobre el DbContext de prueba (sin validadores externos).</summary>
        public static UserManager<AppUsuario> CreateUserManager(ApplicationDbContext context) =>
            new(
                new UserStore<AppUsuario>(context),
                Options.Create(new IdentityOptions()),
                new PasswordHasher<AppUsuario>(),
                Array.Empty<IUserValidator<AppUsuario>>(),
                Array.Empty<IPasswordValidator<AppUsuario>>(),
                new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(),
                new ServiceCollection().BuildServiceProvider(),
                NullLogger<UserManager<AppUsuario>>.Instance);

        // ─────────────── Semillas ───────────────

        /// <summary>
        /// Tenant + cuenta Identity del asociado. Hace falta porque <c>AppUsuario.TenantId</c>
        /// tiene clave foránea hacia Tenants: sin el negocio, la cuenta no se puede insertar.
        /// </summary>
        public static async Task<string> SeedTenantAndUserAsync(
            ApplicationDbContext context,
            Guid tenantId,
            string email,
            bool cuentaActiva = true)
        {
            if (!await context.Tenants.AnyAsync(tenant => tenant.Id == tenantId))
            {
                context.Tenants.Add(new LuxuryApp.Models.SaaS.Tenant
                {
                    Id = tenantId,
                    Nombre = "Negocio de prueba",
                    Activo = true
                });

                await context.SaveChangesAsync();
            }

            var userId = Guid.NewGuid().ToString();

            context.Users.Add(new AppUsuario
            {
                Id = userId,
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                State = cuentaActiva,
                TenantId = tenantId,
                SecurityStamp = Guid.NewGuid().ToString()
            });

            await context.SaveChangesAsync();
            return userId;
        }

        /// <summary>
        /// Crea un asociado directamente en base de datos. <paramref name="appUsuarioId"/> simula
        /// que tiene cuenta de acceso sin pasar por Identity.
        /// </summary>
        public static async Task<Associate> SeedAssociateAsync(
            ApplicationDbContext context,
            string nombre,
            string? email = null,
            bool activo = true,
            string? appUsuarioId = null,
            params AssociateType[] tipos)
        {
            var associate = new Associate
            {
                Nombre = nombre,
                Email = email,
                Activo = activo,
                AppUsuarioId = appUsuarioId,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            context.Associates.Add(associate);
            await context.SaveChangesAsync();

            foreach (var tipo in tipos)
            {
                context.AssociateTypes.Add(new AssociateTypeAssignment
                {
                    AssociateId = associate.Id,
                    Tipo = tipo,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }

            if (tipos.Length > 0)
            {
                await context.SaveChangesAsync();
            }

            return associate;
        }

        public static async Task GrantAsync(
            ApplicationDbContext context,
            int associateId,
            params string[] permisos)
        {
            foreach (var permiso in permisos)
            {
                context.AssociatePermissions.Add(new AssociatePermission
                {
                    AssociateId = associateId,
                    Permiso = permiso,
                    Permitido = true,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }

            await context.SaveChangesAsync();
        }

        /// <summary>Principal de un asociado autenticado, con su rol y su tenant.</summary>
        public static ClaimsPrincipal BuildAssociatePrincipal(string userId, Guid tenantId)
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.NameIdentifier, userId),
                    new Claim(LuxuryApp.Services.Identity.CustomClaimTypes.UserId, userId),
                    new Claim(LuxuryApp.Services.Identity.CustomClaimTypes.TenantId, tenantId.ToString()),
                    new Claim(ClaimTypes.Role, LuxuryApp.Services.Identity.AppRoles.Asociado)
                ],
                authenticationType: "TestAuth",
                nameType: ClaimTypes.Name,
                roleType: ClaimTypes.Role);

            return new ClaimsPrincipal(identity);
        }
    }

    /// <summary>
    /// Accessor de prueba con una propiedad normal.
    ///
    /// <para>
    /// A propósito NO se usa <c>HttpContextAccessor</c>: el real guarda el contexto en un
    /// <c>AsyncLocal</c>, y una asignación hecha dentro de un método async no sobrevive al
    /// retorno al método que lo llamó. En producción no es problema (ASP.NET lo fija al inicio
    /// del request), pero en una prueba haría que el usuario "desaparezca" a mitad del arreglo.
    /// </para>
    /// </summary>
    internal sealed class TestHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }

        public static TestHttpContextAccessor For(ClaimsPrincipal user) =>
            new() { HttpContext = new DefaultHttpContext { User = user } };

        public static TestHttpContextAccessor Anonymous() =>
            new() { HttpContext = new DefaultHttpContext() };
    }

    /// <summary>Permisos siempre vacíos: para pruebas que no van del módulo de asociados.</summary>
    internal sealed class NoAssociatePermissionService : IAssociatePermissionService
    {
        public Task<AssociatePermissionSet> ObtenerDelUsuarioActualAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(AssociatePermissionSet.Ninguno);

        public Task<AssociatePermissionSet> ObtenerDeAsociadoAsync(int associateId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AssociatePermissionSet.Ninguno);

        public Task<bool> GuardarAsync(
            int associateId,
            IEnumerable<string> permisosConcedidos,
            string? actorUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task LimpiarAsync(int associateId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Sin participaciones: el KPI del dashboard no existe.</summary>
    internal sealed class NoAssociateProfitAllocationService : IAssociateProfitAllocationService
    {
        public Task<AssociateAllocationKpiViewModel?> BuildMonthlyKpiAsync(
            int mes,
            int anio,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AssociateAllocationKpiViewModel?>(null);
    }

    /// <summary>Destino fijo: las pruebas de Home no dependen del catálogo de permisos.</summary>
    internal sealed class StubPostLoginDestinationService : IPostLoginDestinationService
    {
        public string Destino { get; set; } = "/Dashboard";

        public Task<string> ResolveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) =>
            Task.FromResult(Destino);

        public Task<bool> PuedeAbrirAsync(
            ClaimsPrincipal principal,
            string localPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
