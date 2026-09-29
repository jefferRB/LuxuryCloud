namespace LuxuryApp.Services.Identity
{
    /// <summary>
    /// Nombres de roles de la aplicación. Centralizados para evitar strings mágicos
    /// repartidos por controladores, seeder y servicios.
    /// </summary>
    public static class AppRoles
    {
        /// <summary>Dueño/administrador del tenant. Acceso completo a los módulos del negocio.</summary>
        public const string Administrador = "Administrador";

        /// <summary>Rol base que recibe el dueño al registrarse.</summary>
        public const string Registrado = "Registrado";

        /// <summary>
        /// Funcionario del negocio con acceso individual y limitado a su propio portal.
        /// NO debe combinarse con Administrador.
        /// </summary>
        public const string Funcionario = "Funcionario";

        /// <summary>
        /// Asociado del negocio (inversionista, socio, marketing, contabilidad…) con acceso a los
        /// módulos que el administrador le conceda EXPLÍCITAMENTE.
        ///
        /// <para>
        /// El rol por sí solo no abre nada: es apenas el marcador de "esta cuenta se autoriza por
        /// permisos". Lo que puede hacer lo deciden las filas de <c>AssociatePermissions</c>.
        /// NO debe combinarse con Administrador ni con Funcionario.
        /// </para>
        /// </summary>
        public const string Asociado = "Asociado";
    }

    /// <summary>
    /// Políticas de autorización de tenant. Las usamos para separar claramente
    /// el acceso administrativo del acceso de funcionario.
    /// </summary>
    public static class AppAuthorizationPolicies
    {
        /// <summary>Solo administradores del tenant.</summary>
        public const string RequireTenantAdmin = "RequireTenantAdmin";

        /// <summary>Solo funcionarios con portal habilitado.</summary>
        public const string RequireFuncionario = "RequireFuncionario";

        /// <summary>Solo asociados con acceso al sistema.</summary>
        public const string RequireAsociado = "RequireAsociado";

        /// <summary>
        /// Prefijo de las políticas generadas al vuelo para cada permiso del catálogo
        /// (<c>perm:Modulo.Accion</c>). Las resuelve <c>PermissionPolicyProvider</c>.
        /// </summary>
        public const string PermissionPrefix = "perm:";

        public static string ForPermission(string permission) => PermissionPrefix + permission;
    }
}
