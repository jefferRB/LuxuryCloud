using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Layout;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Tests.Identity
{
    public class PrivateNavigationSeguridadTests
    {
        [Fact]
        public async Task BuildAsync_ShouldOfferCuentaWithoutDuplicatingItsSectionsInTheMenu()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid().ToString("N");

            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant nav",
                Activo = true
            });

            context.Users.Add(new AppUsuario
            {
                Id = userId,
                UserName = "nav@test.local",
                NormalizedUserName = "NAV@TEST.LOCAL",
                Email = "nav@test.local",
                NormalizedEmail = "NAV@TEST.LOCAL",
                TenantId = tenantId,
                State = true,
                IsPlatformSuperAdmin = false,
                SecurityStamp = Guid.NewGuid().ToString("N")
            });

            await context.SaveChangesAsync();

            using var userManager = new UserManager<AppUsuario>(
                new UserStore<AppUsuario>(context),
                Options.Create(new IdentityOptions()),
                new PasswordHasher<AppUsuario>(),
                Enumerable.Empty<IUserValidator<AppUsuario>>(),
                Enumerable.Empty<IPasswordValidator<AppUsuario>>(),
                new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(),
                services: null!,
                NullLogger<UserManager<AppUsuario>>.Instance);

            var service = new PrivateNavigationService(
                userManager,
                new HttpContextAccessor(),
                new FakeCommercialAccessResolver(),
                new NoAssociatePermissionService());

            var principal = ControllerTestSupport.BuildTenantPrincipal(userId, tenantId);

            var navigation = await service.BuildAsync(principal);

            // Cuenta sigue disponible para cualquier autenticado, sin importar rol.
            Assert.Contains(
                navigation.SecondaryItems,
                item => item.Controller == "Accounts" && item.Action == "Cuenta");

            // La doble autenticación dejó de tener entrada propia: es una sección DENTRO de
            // Mi cuenta. /Seguridad/Enrolar sigue existiendo (el portal de funcionarios y el
            // enrolamiento obligatorio de superadmin llegan ahí directo), pero no por el menú.
            Assert.DoesNotContain(
                navigation.SecondaryItems,
                item => item.Controller == "Seguridad");

            // Impuestos y Bloqueos de horario tampoco: el primero vive en Mi cuenta y el
            // segundo se administra desde el Calendario y la configuración de Reservas.
            Assert.DoesNotContain(
                navigation.SecondaryItems,
                item => item.Controller == "ConfiguracionFiscal");

            Assert.DoesNotContain(
                navigation.SecondaryItems,
                item => item.Controller == "BloqueosRecurrentes");
        }
    }
}
