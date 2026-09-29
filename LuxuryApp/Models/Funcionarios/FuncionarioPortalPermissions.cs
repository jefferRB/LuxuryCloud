namespace LuxuryApp.Models.Funcionarios
{
    /// <summary>
    /// Permisos disponibles dentro del portal del funcionario. Strings centralizados
    /// para no repetirlos en controladores ni vistas.
    /// </summary>
    public static class FuncionarioPortalPermissions
    {
        public const string VerMiPanel = "VerMiPanel";
        public const string VerMiCalendario = "VerMiCalendario";
        public const string CrearMisCitas = "CrearMisCitas";
        public const string EditarMisCitas = "EditarMisCitas";
        public const string CancelarMisCitas = "CancelarMisCitas";
        public const string VerMisCobros = "VerMisCobros";
        public const string RegistrarMisCobros = "RegistrarMisCobros";
        public const string RegistrarMisCobrosManuales = "RegistrarMisCobrosManuales";
        public const string EnviarMisComprobantes = "EnviarMisComprobantes";
        public const string VerMisGanancias = "VerMisGanancias";
        public const string VerMisPagos = "VerMisPagos";

        /// <summary>Todos los permisos en orden de presentación.</summary>
        public static readonly IReadOnlyList<string> Todos = new[]
        {
            VerMiPanel,
            VerMiCalendario,
            CrearMisCitas,
            EditarMisCitas,
            CancelarMisCitas,
            VerMisCobros,
            RegistrarMisCobros,
            RegistrarMisCobrosManuales,
            EnviarMisComprobantes,
            VerMisGanancias,
            VerMisPagos
        };

        /// <summary>
        /// Valor por defecto de cada permiso. Por defecto el funcionario es de SOLO LECTURA:
        /// ve sus secciones pero no opera (crear citas / registrar cobros = false).
        /// Un permiso sin fila en BD usa este valor, así no hay que respaldar datos viejos.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, bool> Defaults =
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [VerMiPanel] = true,
                [VerMiCalendario] = true,
                [VerMisGanancias] = true,
                [VerMisPagos] = true,
                [VerMisCobros] = true,
                [CrearMisCitas] = false,
                [EditarMisCitas] = false,
                [CancelarMisCitas] = false,
                [RegistrarMisCobros] = false,
                [RegistrarMisCobrosManuales] = false,
                // Por defecto ENCENDIDO: quien puede registrar cobros también puede enviar
                // comprobantes, salvo que el admin lo desactive explícitamente. La capacidad
                // real se combina con RegistrarMisCobros (ver PuedeEnviarComprobantes).
                [EnviarMisComprobantes] = true
            };

        public static bool EsPermisoValido(string? permiso) =>
            !string.IsNullOrWhiteSpace(permiso) && Defaults.ContainsKey(permiso);

        public static bool DefaultDe(string permiso) =>
            Defaults.TryGetValue(permiso, out var valor) && valor;
    }

    /// <summary>Conjunto resuelto de permisos de un funcionario (defaults + overrides de BD).</summary>
    public sealed class FuncionarioPortalPermisosSet
    {
        private readonly IReadOnlyDictionary<string, bool> _valores;

        public FuncionarioPortalPermisosSet(IReadOnlyDictionary<string, bool> valores)
        {
            _valores = valores;
        }

        public bool Tiene(string permiso) =>
            _valores.TryGetValue(permiso, out var valor)
                ? valor
                : FuncionarioPortalPermissions.DefaultDe(permiso);

        public bool VerMiCalendario => Tiene(FuncionarioPortalPermissions.VerMiCalendario);
        public bool CrearMisCitas => Tiene(FuncionarioPortalPermissions.CrearMisCitas);
        public bool EditarMisCitas => Tiene(FuncionarioPortalPermissions.EditarMisCitas);
        public bool CancelarMisCitas => Tiene(FuncionarioPortalPermissions.CancelarMisCitas);
        public bool VerMisCobros => Tiene(FuncionarioPortalPermissions.VerMisCobros);
        public bool RegistrarMisCobros => Tiene(FuncionarioPortalPermissions.RegistrarMisCobros);
        public bool RegistrarMisCobrosManuales => Tiene(FuncionarioPortalPermissions.RegistrarMisCobrosManuales);

        /// <summary>
        /// Capacidad efectiva de enviar comprobantes: solo si el funcionario puede registrar
        /// cobros Y no se le desactivó el permiso de comprobantes. Así "default ligado a cobros".
        /// </summary>
        public bool PuedeEnviarComprobantes =>
            RegistrarMisCobros && Tiene(FuncionarioPortalPermissions.EnviarMisComprobantes);
        public bool VerMisGanancias => Tiene(FuncionarioPortalPermissions.VerMisGanancias);
        public bool VerMisPagos => Tiene(FuncionarioPortalPermissions.VerMisPagos);

        public static FuncionarioPortalPermisosSet Defaults() =>
            new(FuncionarioPortalPermissions.Defaults);
    }
}
