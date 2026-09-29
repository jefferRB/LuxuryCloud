using System.Security.Claims;
using LuxuryApp.Models.Asociados;
using LuxuryApp.Services.Identity;

namespace LuxuryApp.Services.Asociados
{
    /// <summary>
    /// Implementación del primer destino permitido.
    /// Ver <see cref="IPostLoginDestinationService"/> para el porqué.
    /// </summary>
    public sealed class PostLoginDestinationService : IPostLoginDestinationService
    {
        /// <summary>Dashboard del negocio: destino histórico de administradores.</summary>
        public const string Dashboard = "/Dashboard";

        /// <summary>Portal del funcionario.</summary>
        public const string PortalFuncionario = "/MiPortal";

        /// <summary>Página neutra para un asociado al que todavía no le concedieron nada.</summary>
        public const string SinPermisos = "/Home/SinPermisos";

        /// <summary>
        /// Orden de preferencia de destinos para un asociado. De lo más general (la foto del
        /// negocio) a lo más específico. Marketing, que solo tiene la página web, cae ahí.
        /// </summary>
        private static readonly IReadOnlyList<(string Permiso, string Ruta)> DestinosPorPermiso =
        [
            (AppPermissions.DashboardView, Dashboard),
            (AppPermissions.InformationView, "/Informacion"),
            (AppPermissions.CalendarView, "/Calendar"),
            (AppPermissions.ReservationsView, "/Reservas"),
            (AppPermissions.PublicWebsiteView, "/Configuracion/PaginaPublica"),
            (AppPermissions.ClientsView, "/Clientes"),
            (AppPermissions.IncomeView, "/Cobros"),
            (AppPermissions.ExpensesView, "/Egresos"),
            (AppPermissions.ProductsView, "/Productos"),
            (AppPermissions.ServicesView, "/Servicios"),
            (AppPermissions.EmployeesView, "/Funcionarios"),
            (AppPermissions.AssociatesView, "/Asociados")
        ];

        private readonly IAssociatePermissionService _permissionService;

        public PostLoginDestinationService(IAssociatePermissionService permissionService)
        {
            _permissionService = permissionService;
        }

        public async Task<string> ResolveAsync(
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default)
        {
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return Dashboard;
            }

            // Administrador y superadmin conservan exactamente el comportamiento de siempre.
            if (principal.HasClaim(CustomClaimTypes.PlatformSuperAdmin, bool.TrueString) ||
                principal.IsInRole(AppRoles.Administrador))
            {
                return Dashboard;
            }

            if (principal.IsInRole(AppRoles.Funcionario))
            {
                return PortalFuncionario;
            }

            if (!principal.IsInRole(AppRoles.Asociado))
            {
                return Dashboard;
            }

            var permisos = await _permissionService.ObtenerDelUsuarioActualAsync(cancellationToken);

            foreach (var (permiso, ruta) in DestinosPorPermiso)
            {
                if (permisos.Tiene(permiso))
                {
                    return ruta;
                }
            }

            // Cuenta creada pero sin nada concedido todavía: se le explica, no se le da un 403.
            return SinPermisos;
        }

        public async Task<bool> PuedeAbrirAsync(
            ClaimsPrincipal principal,
            string localPath,
            CancellationToken cancellationToken = default)
        {
            if (principal?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(localPath))
            {
                return false;
            }

            if (principal.HasClaim(CustomClaimTypes.PlatformSuperAdmin, bool.TrueString) ||
                principal.IsInRole(AppRoles.Administrador))
            {
                return true;
            }

            if (!principal.IsInRole(AppRoles.Asociado))
            {
                // Funcionarios y demás roles ya tienen su propio gate en AccountsController.
                return true;
            }

            // La raíz siempre es segura: HomeController reenvía al destino correcto.
            var ruta = localPath.Split('?')[0].TrimEnd('/');
            if (string.IsNullOrEmpty(ruta) || ruta == "/Home" || ruta.Equals(SinPermisos, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var permisos = await _permissionService.ObtenerDelUsuarioActualAsync(cancellationToken);

            foreach (var (permiso, destino) in DestinosPorPermiso)
            {
                if (ruta.StartsWith(destino, StringComparison.OrdinalIgnoreCase))
                {
                    return permisos.Tiene(permiso);
                }
            }

            // Ruta desconocida para el mapa de destinos: no se adivina. El asociado va a su
            // primer destino permitido y, si de verdad podía entrar, navega desde el menú.
            return false;
        }
    }
}
