using LuxuryApp.Models.Asociados;
using LuxuryApp.Services.Asociados;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Services.Identity
{
    /// <summary>Exige un permiso concreto del catálogo (<see cref="AppPermissions"/>).</summary>
    public sealed class PermissionRequirement : IAuthorizationRequirement
    {
        public PermissionRequirement(string permission)
        {
            Permission = permission;
        }

        public string Permission { get; }
    }

    /// <summary>
    /// Protege una acción o controlador exigiendo un permiso del catálogo.
    ///
    /// <para>
    /// Es la ÚNICA forma correcta de autorizar un módulo del negocio: esconder el elemento del
    /// menú no autoriza nada. Un usuario sin el permiso recibe 403 aunque escriba la URL a mano.
    /// </para>
    ///
    /// <para>
    /// Se puede combinar: <c>[RequirePermission(View)]</c> en el controlador y
    /// <c>[RequirePermission(Manage)]</c> en las acciones que escriben. ASP.NET Core exige que
    /// TODAS las políticas aplicables se cumplan.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public sealed class RequirePermissionAttribute : AuthorizeAttribute
    {
        public RequirePermissionAttribute(string permission)
        {
            Permission = permission;
            Policy = AppAuthorizationPolicies.ForPermission(permission);
        }

        public string Permission { get; }
    }

    /// <summary>
    /// Crea al vuelo una política por cada permiso del catálogo, para no tener que registrar
    /// manualmente decenas de <c>AddPolicy</c> en <c>Program.cs</c> (y que se olvide una).
    /// Cualquier otro nombre de política cae en el proveedor por defecto.
    /// </summary>
    public sealed class PermissionPolicyProvider : DefaultAuthorizationPolicyProvider
    {
        public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
            : base(options)
        {
        }

        public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
        {
            var existente = await base.GetPolicyAsync(policyName);
            if (existente is not null)
            {
                return existente;
            }

            if (!policyName.StartsWith(AppAuthorizationPolicies.PermissionPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            var permiso = policyName[AppAuthorizationPolicies.PermissionPrefix.Length..];

            // Fail-closed: una clave que no está en el catálogo NO genera política, así que la
            // autorización falla en vez de dejar pasar un permiso inventado.
            if (!AppPermissions.EsValido(permiso))
            {
                return null;
            }

            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permiso))
                .Build();
        }
    }

    /// <summary>
    /// Decide si el usuario actual tiene un permiso. Orden de evaluación:
    ///
    /// <list type="number">
    ///   <item>Superadmin de plataforma → acceso total (soporte interno).</item>
    ///   <item>Rol <c>Administrador</c> → acceso total, sin conceder permisos uno por uno.</item>
    ///   <item>Rol <c>Asociado</c> → se consulta la base de datos (permisos vigentes AHORA).</item>
    ///   <item>Cualquier otro caso → denegado.</item>
    /// </list>
    ///
    /// <para>
    /// A propósito NO se leen los permisos desde claims de la cookie: si estuvieran ahí, quitarle
    /// un permiso a alguien no surtiría efecto hasta que cerrara sesión. La consulta se cachea por
    /// request, así que un request con varias verificaciones sigue costando una sola lectura.
    /// </para>
    /// </summary>
    public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
    {
        private readonly IAssociatePermissionService _permissionService;

        public PermissionAuthorizationHandler(IAssociatePermissionService permissionService)
        {
            _permissionService = permissionService;
        }

        protected override async Task HandleRequirementAsync(
            AuthorizationHandlerContext context,
            PermissionRequirement requirement)
        {
            if (context.User.Identity?.IsAuthenticated != true)
            {
                return;
            }

            if (context.User.HasClaim(CustomClaimTypes.PlatformSuperAdmin, bool.TrueString))
            {
                context.Succeed(requirement);
                return;
            }

            if (context.User.IsInRole(AppRoles.Administrador))
            {
                context.Succeed(requirement);
                return;
            }

            if (!context.User.IsInRole(AppRoles.Asociado))
            {
                return;
            }

            var permisos = await _permissionService.ObtenerDelUsuarioActualAsync();
            if (permisos.Tiene(requirement.Permission))
            {
                context.Succeed(requirement);
            }
        }
    }
}
