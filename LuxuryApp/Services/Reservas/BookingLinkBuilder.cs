using System.Globalization;
using LuxuryApp.Services.Common;
using Microsoft.AspNetCore.Http;

namespace LuxuryApp.Services.Reservas
{
    /// <summary>
    /// Construye el link público de reservas del tenant a partir del slug.
    /// Centraliza el formato de URL para reutilizarlo en la configuración, el panel y
    /// los mensajes personalizados (variable {{booking_link}}).
    /// </summary>
    public static class BookingLinkBuilder
    {
        public const string BookingLinkToken = "{{booking_link}}";
        private const string PublicBookingBasePath = "reservar";

        /// <summary>
        /// Devuelve la URL absoluta de reservas. Usa el host del request actual cuando está
        /// disponible (funciona en producción y desarrollo sin configuración extra).
        /// </summary>
        public static string? Build(HttpRequest? request, string? slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                return null;
            }

            var trimmed = slug.Trim();

            if (request is not null && request.Host.HasValue)
            {
                return $"{request.Scheme}://{request.Host.Value}/{PublicBookingBasePath}/{trimmed}";
            }

            // Fallback relativo: si no hay request (ej. background) se devuelve la ruta.
            return $"/{PublicBookingBasePath}/{trimmed}";
        }

        /// <summary>
        /// URL CANÓNICA del enlace de reservas: la base pública configurada de la aplicación
        /// (<c>PublicBaseUrl</c>) cuando existe y es válida, y si no el host del request.
        ///
        /// <para>
        /// Es la que se muestra al negocio y la que se codifica en el QR, para que lo que el
        /// cliente copia, abre y escanea sea siempre la misma dirección. El fallback al request
        /// mantiene el comportamiento en desarrollo y detrás de Nginx (UseForwardedHeaders ya
        /// entrega el esquema y host públicos); nunca se quema una URL en el código.
        /// </para>
        /// </summary>
        public static string? BuildCanonical(PublicSiteOptions? options, HttpRequest? request, string? slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                return null;
            }

            var canonical = options?.ResolveAbsoluteUrl($"/{PublicBookingBasePath}/{slug.Trim()}");

            return string.IsNullOrWhiteSpace(canonical) ? Build(request, slug) : canonical;
        }

        public static string? BuildForService(HttpRequest? request, string? slug, int serviceId)
        {
            var bookingUrl = Build(request, slug);
            if (string.IsNullOrWhiteSpace(bookingUrl) || serviceId <= 0)
            {
                return bookingUrl;
            }

            var separator = bookingUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            return $"{bookingUrl}{separator}servicioId={serviceId.ToString(CultureInfo.InvariantCulture)}";
        }

        /// <summary>
        /// Reemplaza la variable {{booking_link}} dentro de un texto por el link real del tenant.
        /// Si no hay slug, elimina la variable para no dejar el placeholder visible.
        /// </summary>
        public static string ReplaceToken(string? text, HttpRequest? request, string? slug)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            if (!text.Contains(BookingLinkToken, StringComparison.Ordinal))
            {
                return text;
            }

            var link = Build(request, slug) ?? string.Empty;
            return text.Replace(BookingLinkToken, link, StringComparison.Ordinal);
        }
    }
}
