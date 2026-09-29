using Microsoft.AspNetCore.Http;
using System.Globalization;

namespace LuxuryApp.Services.PublicPages
{
    public static class TenantPublicPageLinkBuilder
    {
        private const string PublicSiteBasePath = "sitio";

        public static string? Build(HttpRequest? request, string? slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                return null;
            }

            var trimmed = slug.Trim();

            if (request is not null && request.Host.HasValue)
            {
                return $"{request.Scheme}://{request.Host.Value}/{PublicSiteBasePath}/{trimmed}";
            }

            return $"/{PublicSiteBasePath}/{trimmed}";
        }

        public static string? BuildReserveAction(HttpRequest? request, string? slug) =>
            BuildAction(request, slug, "go/reservar");

        public static string? BuildServiceReserveAction(HttpRequest? request, string? slug, int serviceId)
        {
            if (serviceId <= 0)
            {
                return BuildReserveAction(request, slug);
            }

            return BuildAction(
                request,
                slug,
                $"go/servicio/{serviceId.ToString(CultureInfo.InvariantCulture)}/reservar");
        }

        public static string? BuildWhatsAppAction(HttpRequest? request, string? slug) =>
            BuildAction(request, slug, "go/whatsapp");

        public static string? BuildMapsAction(HttpRequest? request, string? slug) =>
            BuildAction(request, slug, "go/maps");

        public static string? BuildWazeAction(HttpRequest? request, string? slug) =>
            BuildAction(request, slug, "go/waze");

        private static string? BuildAction(HttpRequest? request, string? slug, string actionPath)
        {
            var siteUrl = Build(request, slug);
            if (string.IsNullOrWhiteSpace(siteUrl))
            {
                return null;
            }

            return $"{siteUrl}/{actionPath}";
        }
    }
}
