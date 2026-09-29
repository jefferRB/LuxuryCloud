using Microsoft.AspNetCore.Http;

namespace LuxuryApp.Services.PublicPages
{
    public interface ITenantPublicPageRedirectService
    {
        Task<PublicPageRedirectTarget?> ResolveReserveAsync(
            string slug,
            HttpRequest? request,
            CancellationToken cancellationToken = default);

        Task<PublicPageRedirectTarget?> ResolveServiceReserveAsync(
            string slug,
            int servicioId,
            HttpRequest? request,
            CancellationToken cancellationToken = default);

        Task<PublicPageRedirectTarget?> ResolveWhatsAppAsync(
            string slug,
            CancellationToken cancellationToken = default);

        Task<PublicPageRedirectTarget?> ResolveMapsAsync(
            string slug,
            CancellationToken cancellationToken = default);

        Task<PublicPageRedirectTarget?> ResolveWazeAsync(
            string slug,
            CancellationToken cancellationToken = default);
    }

    public sealed record PublicPageRedirectTarget(string Url);
}
