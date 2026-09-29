namespace LuxuryApp.Models.SaaS
{
    /// <summary>Rol funcional de una cuenta dentro del tenant, ya resuelto contra AspNetUserRoles.</summary>
    public enum TenantUserKind
    {
        /// <summary>Cuenta sin rol reconocido (ni Administrador ni Funcionario).</summary>
        Otro = 0,

        /// <summary>Administrador del tenant (dueño / acceso completo).</summary>
        Administrador = 1,

        /// <summary>Cuenta de acceso individual de un funcionario (rol Funcionario o FuncionarioId).</summary>
        Funcionario = 2
    }

    /// <summary>Motivo por el que se eligio al owner. Se muestra en plataforma para no "inventar" el dato.</summary>
    public enum TenantOwnerSource
    {
        /// <summary>El tenant no tiene ninguna cuenta utilizable.</summary>
        None = 0,

        /// <summary>Administrador que ademas tiene el rol Registrado (dueño original del registro).</summary>
        AdminRegistrado = 1,

        /// <summary>Administrador sin rol Registrado.</summary>
        Administrador = 2,

        /// <summary>No hay administradores: se uso una cuenta activa que NO es de funcionario.</summary>
        FallbackUsuarioActivo = 3,

        /// <summary>Ultimo recurso: la unica cuenta del tenant es de funcionario.</summary>
        FallbackFuncionario = 4
    }

    /// <summary>Una cuenta del tenant con su clasificacion ya resuelta.</summary>
    public sealed class TenantUserSummary
    {
        public string UserId { get; init; } = string.Empty;
        public string? Email { get; init; }
        public string? Name { get; init; }
        public bool State { get; init; }
        public bool EmailConfirmed { get; init; }
        public bool IsPlatformSuperAdmin { get; init; }
        public TenantUserKind Kind { get; init; }
        public bool IsRegistrado { get; init; }
        public bool IsAdmin => Kind == TenantUserKind.Administrador;
        public bool IsFuncionario => Kind == TenantUserKind.Funcionario;

        /// <summary>Roles crudos, para mostrarlos tal cual en la ficha.</summary>
        public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();

        public string RolesLabel => Roles.Count == 0 ? "Sin rol" : string.Join(", ", Roles);
    }

    /// <summary>
    /// Resultado determinista de "quien es el contacto principal del tenant". Reemplaza el
    /// <c>OrderBy(u =&gt; u.Email).First()</c> que hacia ganar a un funcionario sobre el
    /// administrador solo por orden alfabetico.
    /// </summary>
    public sealed class TenantOwnerResolution
    {
        public Guid TenantId { get; init; }

        /// <summary>Contacto principal. Null solo si el tenant no tiene ninguna cuenta.</summary>
        public TenantUserSummary? Owner { get; init; }

        public TenantOwnerSource Source { get; init; }

        public string? OwnerEmail => Owner?.Email;
        public string? OwnerName => Owner?.Name;

        /// <summary>Administradores distintos del owner.</summary>
        public IReadOnlyList<TenantUserSummary> AdditionalAdmins { get; init; } = Array.Empty<TenantUserSummary>();

        /// <summary>Cuentas de funcionario del tenant.</summary>
        public IReadOnlyList<TenantUserSummary> Funcionarios { get; init; } = Array.Empty<TenantUserSummary>();

        /// <summary>Cuentas que no son admin ni funcionario.</summary>
        public IReadOnlyList<TenantUserSummary> OtherUsers { get; init; } = Array.Empty<TenantUserSummary>();

        /// <summary>Todas las cuentas del tenant, ya clasificadas.</summary>
        public IReadOnlyList<TenantUserSummary> AllUsers { get; init; } = Array.Empty<TenantUserSummary>();

        /// <summary>
        /// Inconsistencias operativas detectadas (sin admin, admin inactivo, admin que tambien es
        /// funcionario, varios admins...). Se MUESTRAN, no se corrigen en silencio.
        /// </summary>
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        public bool HasWarnings => Warnings.Count > 0;

        /// <summary>True cuando el contacto que se muestra NO proviene de un administrador.</summary>
        public bool OwnerIsFallback =>
            Source is TenantOwnerSource.FallbackUsuarioActivo or TenantOwnerSource.FallbackFuncionario;

        public static TenantOwnerResolution Empty(Guid tenantId) => new()
        {
            TenantId = tenantId,
            Source = TenantOwnerSource.None,
            Warnings = new[] { "El tenant no tiene ninguna cuenta de usuario asociada." }
        };

        public static string DescribeSource(TenantOwnerSource source) => source switch
        {
            TenantOwnerSource.AdminRegistrado => "Administrador registrado",
            TenantOwnerSource.Administrador => "Administrador",
            TenantOwnerSource.FallbackUsuarioActivo => "Sin administrador: usuario activo",
            TenantOwnerSource.FallbackFuncionario => "Sin administrador: cuenta de funcionario",
            _ => "Sin usuario"
        };
    }
}
