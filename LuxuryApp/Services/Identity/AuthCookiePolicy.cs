using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace LuxuryApp.Services.Identity
{
    /// <summary>
    /// Política centralizada de la cookie de autenticación de LuxuryCloud y de la
    /// resolución de la ruta de llaves de Data Protection. Se extrae de Program.cs para
    /// poder probar los valores sin levantar todo el host.
    /// </summary>
    public static class AuthCookiePolicy
    {
        /// <summary>
        /// Duración de la cookie persistente y ventana de expiración deslizante. Un
        /// dispositivo de confianza permanece autenticado hasta 30 días de inactividad,
        /// acotado por el tope absoluto de
        /// <see cref="AbsoluteSessionLifetimeEnforcer.AbsoluteLifetime"/> (90 días).
        /// </summary>
        public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

        public const string LoginPath = "/Accounts/Acceso";
        public const string AccessDeniedPath = "/Accounts/Bloqueado";

        // Ruta estable de producción, fuera del directorio de publicación para que un nuevo
        // despliegue (que reemplaza el contenido publicado) no borre las llaves.
        public const string ProductionDataProtectionKeysPath = "/var/lib/luxury/dataprotection-keys";

        // Nombre estable de aplicación de Data Protection. NO cambiar: alterarlo
        // invalidaría todas las cookies existentes y expulsaría a los usuarios activos.
        public const string DataProtectionApplicationName = "Luxury";

        /// <summary>
        /// Aplica los valores estáticos de la cookie de aplicación (nombre de cookie por
        /// defecto conservado a propósito, banderas de seguridad, duración y sliding).
        /// Los <c>Events</c> se configuran aparte en Program.cs porque dependen de DI.
        /// </summary>
        public static void ConfigureApplicationCookie(CookieAuthenticationOptions options, bool isDevelopment)
        {
            options.LoginPath = new PathString(LoginPath);
            options.AccessDeniedPath = new PathString(AccessDeniedPath);

            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
            // En desarrollo (HTTP local) SameAsRequest evita perder la cookie; en
            // producción siempre exige HTTPS.
            options.Cookie.SecurePolicy = isDevelopment
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;

            options.ExpireTimeSpan = SessionLifetime;
            options.SlidingExpiration = true;
        }

        /// <summary>
        /// Resuelve la ruta de llaves de Data Protection sin tocar el sistema de archivos.
        /// <list type="bullet">
        ///   <item>Si <paramref name="configuredPath"/> viene definido, se usa tal cual.</item>
        ///   <item>En producción sin configuración, se usa la ruta estable recomendada.</item>
        ///   <item>En desarrollo sin configuración, devuelve <c>null</c>: se conserva el
        ///   almacén por defecto de la plataforma (nunca una ruta Linux absoluta en Windows).</item>
        /// </list>
        /// </summary>
        public static string? ResolveDataProtectionKeysPath(string? configuredPath, bool isDevelopment)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return configuredPath.Trim();
            }

            return isDevelopment ? null : ProductionDataProtectionKeysPath;
        }
    }
}
