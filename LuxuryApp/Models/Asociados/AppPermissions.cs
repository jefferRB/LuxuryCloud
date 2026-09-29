namespace LuxuryApp.Models.Asociados
{
    /// <summary>
    /// Catálogo ÚNICO de permisos de la aplicación. Un permiso es la unidad real de autorización:
    /// las políticas de ASP.NET Core y los atributos <c>[RequirePermission]</c> se construyen a
    /// partir de estas claves. Nunca se autoriza por <see cref="AssociateType"/>.
    ///
    /// <para>
    /// Convención de la clave: <c>Modulo.Accion</c>. <c>View</c> abre la pantalla de lectura;
    /// <c>Manage</c> habilita crear/editar/eliminar. <c>Manage</c> IMPLICA <c>View</c>
    /// (ver <see cref="AssociatePermissionSet"/>), así que marcar solo "Administrar" nunca deja
    /// a alguien con una pantalla inaccesible.
    /// </para>
    ///
    /// <para>
    /// El rol Administrador y el superadmin de plataforma tienen acceso total sin necesidad de
    /// conceder permisos uno por uno: el handler corta antes de llegar a la base de datos.
    /// </para>
    /// </summary>
    public static class AppPermissions
    {
        // Dashboard financiero (ingresos, egresos, ganancia, participación de asociados).
        public const string DashboardView = "Dashboard.View";

        // Información / analítica del negocio.
        public const string InformationView = "Information.View";

        public const string ClientsView = "Clients.View";
        public const string ClientsManage = "Clients.Manage";

        public const string EmployeesView = "Employees.View";
        public const string EmployeesManage = "Employees.Manage";

        public const string ProductsView = "Products.View";
        public const string ProductsManage = "Products.Manage";

        public const string ServicesView = "Services.View";
        public const string ServicesManage = "Services.Manage";

        public const string CalendarView = "Calendar.View";
        public const string CalendarManage = "Calendar.Manage";

        public const string ReservationsView = "Reservations.View";
        public const string ReservationsManage = "Reservations.Manage";

        public const string IncomeView = "Income.View";
        public const string IncomeManage = "Income.Manage";

        public const string ExpensesView = "Expenses.View";
        public const string ExpensesManage = "Expenses.Manage";

        // Página pública del negocio: contenido, imágenes, galería y datos publicados.
        public const string PublicWebsiteView = "PublicWebsite.View";
        public const string PublicWebsiteManage = "PublicWebsite.Manage";

        // Asociados: quién participa del negocio, sus accesos y su participación financiera.
        public const string AssociatesView = "Associates.View";
        public const string AssociatesManage = "Associates.Manage";

        /// <summary>Módulos del catálogo, en orden de presentación en la UI de permisos.</summary>
        public static readonly IReadOnlyList<PermissionModule> Modulos = new[]
        {
            new PermissionModule("Dashboard financiero", "bi-grid-1x2", new[]
            {
                new PermissionDefinition(DashboardView, "Ver dashboard financiero", true)
            }),
            new PermissionModule("Información del negocio", "bi-bar-chart-line", new[]
            {
                new PermissionDefinition(InformationView, "Ver información y analítica", true)
            }),
            new PermissionModule("Página web", "bi-window", new[]
            {
                new PermissionDefinition(PublicWebsiteView, "Ver", false),
                new PermissionDefinition(PublicWebsiteManage, "Administrar", false)
            }),
            new PermissionModule("Calendario", "bi-calendar3", new[]
            {
                new PermissionDefinition(CalendarView, "Ver", false),
                new PermissionDefinition(CalendarManage, "Administrar", false)
            }),
            new PermissionModule("Reservas", "bi-calendar-check", new[]
            {
                new PermissionDefinition(ReservationsView, "Ver", false),
                new PermissionDefinition(ReservationsManage, "Administrar", false)
            }),
            new PermissionModule("Clientes", "bi-people", new[]
            {
                new PermissionDefinition(ClientsView, "Ver", false),
                new PermissionDefinition(ClientsManage, "Administrar", false)
            }),
            new PermissionModule("Funcionarios", "bi-person-badge", new[]
            {
                new PermissionDefinition(EmployeesView, "Ver", false),
                new PermissionDefinition(EmployeesManage, "Administrar", false)
            }),
            new PermissionModule("Productos", "bi-box-seam", new[]
            {
                new PermissionDefinition(ProductsView, "Ver", false),
                new PermissionDefinition(ProductsManage, "Administrar", false)
            }),
            new PermissionModule("Servicios", "bi-scissors", new[]
            {
                new PermissionDefinition(ServicesView, "Ver", false),
                new PermissionDefinition(ServicesManage, "Administrar", false)
            }),
            new PermissionModule("Ingresos", "bi-cash-coin", new[]
            {
                new PermissionDefinition(IncomeView, "Ver", false),
                new PermissionDefinition(IncomeManage, "Administrar", false)
            }),
            new PermissionModule("Egresos", "bi-receipt", new[]
            {
                new PermissionDefinition(ExpensesView, "Ver", false),
                new PermissionDefinition(ExpensesManage, "Administrar", false)
            }),
            new PermissionModule("Asociados", "bi-people-fill", new[]
            {
                new PermissionDefinition(AssociatesView, "Ver", false),
                new PermissionDefinition(AssociatesManage, "Administrar", false)
            })
        };

        /// <summary>Todas las claves válidas, en orden de catálogo.</summary>
        public static readonly IReadOnlyList<string> Todos =
            Modulos.SelectMany(modulo => modulo.Permisos.Select(permiso => permiso.Key)).ToArray();

        private static readonly HashSet<string> Validos = new(Todos, StringComparer.Ordinal);

        /// <summary>
        /// Permiso de lectura implicado por cada permiso de administración. Marcar solo
        /// "Administrar" concede automáticamente "Ver" del mismo módulo.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> ManageImplicaView =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ClientsManage] = ClientsView,
                [EmployeesManage] = EmployeesView,
                [ProductsManage] = ProductsView,
                [ServicesManage] = ServicesView,
                [CalendarManage] = CalendarView,
                [ReservationsManage] = ReservationsView,
                [IncomeManage] = IncomeView,
                [ExpensesManage] = ExpensesView,
                [PublicWebsiteManage] = PublicWebsiteView,
                [AssociatesManage] = AssociatesView
            };

        public static bool EsValido(string? permiso) =>
            !string.IsNullOrWhiteSpace(permiso) && Validos.Contains(permiso);

        /// <summary>Devuelve el permiso de lectura implicado, o null si no aplica.</summary>
        public static string? ViewImplicadoPor(string permiso) =>
            ManageImplicaView.TryGetValue(permiso, out var view) ? view : null;

        public static string Etiqueta(string permiso)
        {
            foreach (var modulo in Modulos)
            {
                foreach (var definicion in modulo.Permisos)
                {
                    if (string.Equals(definicion.Key, permiso, StringComparison.Ordinal))
                    {
                        return definicion.Standalone
                            ? definicion.Etiqueta
                            : $"{modulo.Nombre} · {definicion.Etiqueta}";
                    }
                }
            }

            return permiso;
        }
    }

    /// <summary>Agrupación visual de permisos por módulo del producto.</summary>
    public sealed record PermissionModule(
        string Nombre,
        string Icono,
        IReadOnlyList<PermissionDefinition> Permisos);

    /// <summary>
    /// Un permiso concreto. <paramref name="Standalone"/> indica que el módulo tiene un único
    /// permiso y la etiqueta ya se explica sola (no se antepone el nombre del módulo).
    /// </summary>
    public sealed record PermissionDefinition(
        string Key,
        string Etiqueta,
        bool Standalone);
}
