using System.Security.Claims;
using LuxuryApp.Models.Layout;

namespace LuxuryApp.Services.Layout
{
    public interface IPrivateNavigationService
    {
        Task<PrivateNavigationViewModel> BuildAsync(
            ClaimsPrincipal principal,
            CancellationToken cancellationToken = default);
    }
}
