using System.Reflection;
using LuxuryApp.Controllers.Calendar;
using LuxuryApp.Controllers;
using LuxuryApp.Controllers.DataBase;
using LuxuryApp.Controllers.Finanzas;
using LuxuryApp.Controllers.Funcionarios;
using LuxuryApp.Controllers.Identity;
using LuxuryApp.Controllers.Platform;
using LuxuryApp.Controllers.Productos;
using LuxuryApp.Models.Asociados;
using LuxuryApp.Services.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class ControllerAuthorizationTests
    {
        /// <summary>
        /// Los módulos del negocio pasaron de "rol Administrador" a "permiso del catálogo".
        /// El administrador los sigue cumpliendo todos sin configurar nada (lo resuelve
        /// <c>PermissionAuthorizationHandler</c>), y un asociado solo entra donde se lo
        /// concedieron explícitamente.
        /// </summary>
        public static TheoryData<Type, string> ModulosProtegidosPorPermiso => new()
        {
            { typeof(CalendarController), AppPermissions.CalendarView },
            { typeof(ClientesController), AppPermissions.ClientsView },
            { typeof(CategoriasController), AppPermissions.ExpensesView },
            { typeof(CobrosController), AppPermissions.IncomeView },
            { typeof(DashboardController), AppPermissions.DashboardView },
            { typeof(EgresosController), AppPermissions.ExpensesView },
            { typeof(ServiciosController), AppPermissions.ServicesView },
            { typeof(FuncionariosController), AppPermissions.EmployeesView },
            { typeof(PuestosController), AppPermissions.EmployeesView },
            { typeof(InformacionController), AppPermissions.InformationView },
            { typeof(ProductosController), AppPermissions.ProductsView },
            { typeof(LuxuryApp.Controllers.Configuracion.PaginaPublicaController), AppPermissions.PublicWebsiteView },
            { typeof(LuxuryApp.Controllers.Asociados.AsociadosController), AppPermissions.AssociatesView },
            // Los estados de cuenta y la política de reparto son parte del módulo de Asociados.
            { typeof(LuxuryApp.Controllers.Inversionistas.InversionistasController), AppPermissions.AssociatesView }
        };

        [Theory]
        [MemberData(nameof(ModulosProtegidosPorPermiso))]
        public void ModulosDelNegocio_ShouldRequireCatalogPermission(Type controllerType, string permisoEsperado)
        {
            var permisos = controllerType
                .GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true)
                .OfType<RequirePermissionAttribute>()
                .Select(atributo => atributo.Permission)
                .ToArray();

            Assert.Contains(permisoEsperado, permisos);

            // Nunca anónimo, en ningún nivel.
            Assert.Empty(controllerType.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));
        }

        [Theory]
        [MemberData(nameof(ModulosProtegidosPorPermiso))]
        public void ModulosDelNegocio_EveryPostAction_ShouldBeProtected(Type controllerType, string permisoEsperado)
        {
            // Esconder un botón no protege nada: cada acción que ESCRIBE tiene que exigir un
            // permiso por sí misma, porque un formulario se puede reconstruir a mano.
            //
            // Una acción que acepta GET y POST a la vez es una consulta (buscadores que aceptan
            // ambos verbos): esas quedan cubiertas por el permiso de lectura del controlador.
            _ = permisoEsperado;

            var escriturasSinPermiso = controllerType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true).Any())
                .Where(method => !method.GetCustomAttributes(typeof(HttpGetAttribute), inherit: true).Any())
                .Where(method => !method
                    .GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true)
                    .OfType<RequirePermissionAttribute>()
                    .Any())
                .Select(method => $"{controllerType.Name}.{method.Name}")
                .ToArray();

            Assert.Empty(escriturasSinPermiso);
        }

        [Fact]
        public void PermissionCatalog_ShouldOnlyContainKnownKeys()
        {
            // Una clave fuera del catálogo no genera política (PermissionPolicyProvider devuelve
            // null) y la autorización falla cerrada. Esta prueba evita que un atributo se escriba
            // con una clave inventada y quede abriendo una pantalla sin querer.
            var atributos = typeof(AppPermissions).Assembly
                .GetTypes()
                .Where(tipo => typeof(Controller).IsAssignableFrom(tipo))
                .SelectMany(tipo => tipo
                    .GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true)
                    .OfType<RequirePermissionAttribute>()
                    .Concat(tipo
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .SelectMany(method => method
                            .GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true)
                            .OfType<RequirePermissionAttribute>())))
                .Select(atributo => atributo.Permission)
                .Distinct()
                .ToArray();

            Assert.NotEmpty(atributos);

            var desconocidos = atributos.Where(permiso => !AppPermissions.EsValido(permiso)).ToArray();
            Assert.Empty(desconocidos);
        }

        /// <summary>
        /// Módulos que siguen siendo exclusivos del dueño del negocio y NO se delegan por permiso:
        /// facturación, roles, impuestos, WhatsApp y configuración de agenda.
        /// </summary>
        [Theory]
        [InlineData(typeof(BillingController))]
        [InlineData(typeof(RolesController))]
        [InlineData(typeof(LuxuryApp.Controllers.Horarios.BloqueosRecurrentesController))]
        public void ModulosDelDueno_ShouldStillRequireAdministradorRole(Type controllerType)
        {
            var authorizeAttribute = controllerType
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .OfType<AuthorizeAttribute>()
                .FirstOrDefault();

            Assert.NotNull(authorizeAttribute);
            Assert.Equal(AppRoles.Administrador, authorizeAttribute!.Roles);
        }

        [Fact]
        public void PlatformController_ShouldRequirePlatformSuperAdminPolicy()
        {
            var authorizeAttribute = typeof(PlatformController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .OfType<AuthorizeAttribute>()
                .FirstOrDefault();

            Assert.NotNull(authorizeAttribute);
            Assert.Equal(PlatformAuthorizationPolicies.PlatformSuperAdmin, authorizeAttribute!.Policy);
        }

        [Fact]
        public void PlatformMonthlyReportsController_ShouldRequirePlatformSuperAdminPolicy()
        {
            var authorizeAttribute = typeof(PlatformMonthlyReportsController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .OfType<AuthorizeAttribute>()
                .FirstOrDefault();

            Assert.NotNull(authorizeAttribute);
            Assert.Null(authorizeAttribute!.Roles);
            Assert.Equal(PlatformAuthorizationPolicies.PlatformSuperAdmin, authorizeAttribute.Policy);
        }

        [Fact]
        public void RecurringReconciliationController_ShouldRequirePlatformSuperAdminPolicy()
        {
            var authorizeAttribute = typeof(RecurringReconciliationController)
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
                .OfType<AuthorizeAttribute>()
                .FirstOrDefault();

            Assert.NotNull(authorizeAttribute);
            Assert.Null(authorizeAttribute!.Roles);
            Assert.Equal(PlatformAuthorizationPolicies.PlatformSuperAdmin, authorizeAttribute.Policy);
        }
    }
}
