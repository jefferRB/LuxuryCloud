namespace LuxuryApp.Services.Common
{
    /// <summary>
    /// URL pública oficial de LuxuryCloud, centralizada para construir enlaces absolutos en
    /// correos y procesos de fondo (sin depender del host del request ni de HttpContext).
    /// <para>
    /// Se enlaza desde la raíz de configuración con la clave <c>PublicBaseUrl</c>
    /// (variable de entorno <c>PublicBaseUrl</c>). Nunca debe apuntar a ngrok ni localhost:
    /// <see cref="ResolveDashboardUrl"/> descarta esos hosts para no filtrar enlaces internos.
    /// </para>
    /// </summary>
    public sealed class PublicSiteOptions
    {
        /// <summary>
        /// URL base absoluta (ej. <c>https://app.luxurycloud.app</c>). Vacía => no se genera
        /// el botón "Ver dashboard completo" (fallback seguro, nunca un host de desarrollo).
        /// </summary>
        public string PublicBaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// URL absoluta del dashboard, o <c>null</c> si <see cref="PublicBaseUrl"/> falta o no
        /// es una URL pública válida. No duplica slashes.
        /// </summary>
        public string? ResolveDashboardUrl() => ResolveAbsoluteUrl("/Dashboard");

        /// <summary>Construye una URL absoluta a partir de la base pública y una ruta relativa.</summary>
        public string? ResolveAbsoluteUrl(string relativePath)
        {
            if (!IsPublicBaseValid(PublicBaseUrl))
            {
                return null;
            }

            var baseUrl = PublicBaseUrl.Trim().TrimEnd('/');
            var path = string.IsNullOrEmpty(relativePath)
                ? string.Empty
                : "/" + relativePath.TrimStart('/');

            return baseUrl + path;
        }

        /// <summary>
        /// true solo si es una URL http/https absoluta que NO es localhost ni ngrok. Se usa para
        /// impedir que un valor de desarrollo termine en un correo de producción.
        /// </summary>
        public static bool IsPublicBaseValid(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            {
                return false;
            }

            var host = uri.Host.ToLowerInvariant();

            if (host is "localhost" or "127.0.0.1" or "::1" || host.EndsWith(".local", StringComparison.Ordinal))
            {
                return false;
            }

            // Rechaza cualquier túnel de desarrollo (ngrok, loca.lt, trycloudflare, etc.).
            if (host.Contains("ngrok", StringComparison.Ordinal) ||
                host.Contains("loca.lt", StringComparison.Ordinal) ||
                host.Contains("trycloudflare", StringComparison.Ordinal) ||
                host.Contains("devtunnels", StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }
    }
}
