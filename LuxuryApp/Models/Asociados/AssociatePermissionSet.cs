namespace LuxuryApp.Models.Asociados
{
    /// <summary>
    /// Conjunto resuelto de permisos de un asociado.
    ///
    /// <para>
    /// Reglas de resolución:
    /// </para>
    /// <list type="number">
    ///   <item>Deny by default: lo que no está concedido, no se puede.</item>
    ///   <item><c>Manage</c> implica el <c>View</c> del mismo módulo.</item>
    ///   <item>Si el asociado está inactivo o su acceso bloqueado, el conjunto queda VACÍO
    ///   (<see cref="Ninguno"/>): no hay que recordar apagar permisos uno por uno.</item>
    /// </list>
    /// </summary>
    public sealed class AssociatePermissionSet
    {
        private readonly HashSet<string> _concedidos;

        private AssociatePermissionSet(HashSet<string> concedidos)
        {
            _concedidos = concedidos;
        }

        /// <summary>Nadie: ni un solo permiso. Es el fallback seguro ante cualquier duda.</summary>
        public static AssociatePermissionSet Ninguno { get; } =
            new(new HashSet<string>(StringComparer.Ordinal));

        /// <summary>Construye el conjunto a partir de las claves concedidas, aplicando Manage ⇒ View.</summary>
        public static AssociatePermissionSet Desde(IEnumerable<string> concedidos)
        {
            var resueltos = new HashSet<string>(StringComparer.Ordinal);

            foreach (var permiso in concedidos)
            {
                if (!AppPermissions.EsValido(permiso))
                {
                    // Clave desconocida (catálogo viejo en base de datos): se ignora en vez de
                    // conceder algo que ya no existe.
                    continue;
                }

                resueltos.Add(permiso);

                var view = AppPermissions.ViewImplicadoPor(permiso);
                if (view is not null)
                {
                    resueltos.Add(view);
                }
            }

            return new AssociatePermissionSet(resueltos);
        }

        public bool Tiene(string permiso) =>
            !string.IsNullOrWhiteSpace(permiso) && _concedidos.Contains(permiso);

        public bool TieneAlguno => _concedidos.Count > 0;

        /// <summary>Claves concedidas tal como quedaron resueltas (incluye los View implicados).</summary>
        public IReadOnlyCollection<string> Concedidos => _concedidos;
    }

    /// <summary>
    /// Presets de permisos: SOLO una ayuda de UX para no marcar 20 casillas a mano. Después de
    /// aplicarlos el administrador puede marcar y desmarcar lo que quiera; el preset no queda
    /// guardado ni vuelve a aplicarse. La autorización jamás mira el preset ni el tipo.
    /// </summary>
    public static class AssociatePermissionPresets
    {
        public static IReadOnlyList<string> Resolve(AssociatePermissionPreset preset) => preset switch
        {
            // Marketing administra la página pública del negocio y nada financiero.
            AssociatePermissionPreset.Marketing => new[]
            {
                AppPermissions.PublicWebsiteView,
                AppPermissions.PublicWebsiteManage
            },

            // Inversionista con acceso: solo mira la foto financiera del negocio.
            AssociatePermissionPreset.Inversionista => new[]
            {
                AppPermissions.DashboardView,
                AppPermissions.InformationView
            },

            // Contabilidad: ingresos, egresos e indicadores; no toca clientes ni la web.
            AssociatePermissionPreset.Contabilidad => new[]
            {
                AppPermissions.DashboardView,
                AppPermissions.InformationView,
                AppPermissions.IncomeView,
                AppPermissions.ExpensesView,
                AppPermissions.ExpensesManage
            },

            // Solo lectura: todos los permisos que no modifican nada.
            AssociatePermissionPreset.SoloLectura =>
                AppPermissions.Todos.Where(permiso => permiso.EndsWith(".View", StringComparison.Ordinal)).ToArray(),

            _ => Array.Empty<string>()
        };

        public static string Describe(AssociatePermissionPreset preset) => preset switch
        {
            AssociatePermissionPreset.Marketing => "Marketing",
            AssociatePermissionPreset.Inversionista => "Inversionista",
            AssociatePermissionPreset.Contabilidad => "Contabilidad",
            AssociatePermissionPreset.SoloLectura => "Solo lectura",
            _ => "Personalizado"
        };

        public static readonly IReadOnlyList<AssociatePermissionPreset> Todos = new[]
        {
            AssociatePermissionPreset.Personalizado,
            AssociatePermissionPreset.Marketing,
            AssociatePermissionPreset.Inversionista,
            AssociatePermissionPreset.Contabilidad,
            AssociatePermissionPreset.SoloLectura
        };
    }
}
