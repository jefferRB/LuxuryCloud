namespace LuxuryApp.Services.Identity
{
    /// <summary>Configuración de seguridad de plataforma (sección "Security" de appsettings).</summary>
    public sealed class PlatformSecurityOptions
    {
        public const string SectionName = "Security";

        public MfaOptions Mfa { get; set; } = new();

        public sealed class MfaOptions
        {
            /// <summary>
            /// Kill-switch del enrolamiento obligatorio de TOTP para superadmins.
            /// En false (default) el superadmin puede usar la app sin MFA mientras se enrola
            /// voluntariamente (Capa 0 del plan de despliegue). Apagarlo NO desactiva la
            /// verificación TOTP de quien ya tiene TwoFactorEnabled: eso sería un downgrade
            /// de seguridad silencioso.
            /// </summary>
            public bool SuperAdminEnforcement { get; set; }
        }
    }
}
