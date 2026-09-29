using LuxuryApp.Controllers;
using LuxuryApp.Tests.Support;
using LuxuryApp.Models.Marketing;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.PublicSite;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class HomeControllerPublicRouteTests
    {
        [Fact]
        public void Privacy_ShouldDeclareCanonicalPrivacidadRoute()
        {
            var method = typeof(HomeController).GetMethod(nameof(HomeController.Privacy));

            var attribute = Assert.Single(method!.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false))
                as HttpGetAttribute;

            Assert.NotNull(attribute);
            Assert.Equal("/privacidad", attribute!.Template);
        }

        [Fact]
        public void Privacy_ShouldDeclareCanonicalPrivacidadHeadRoute()
        {
            var method = typeof(HomeController).GetMethod(nameof(HomeController.Privacy));

            var attribute = Assert.Single(method!.GetCustomAttributes(typeof(HttpHeadAttribute), inherit: false))
                as HttpHeadAttribute;

            Assert.NotNull(attribute);
            Assert.Equal("/privacidad", attribute!.Template);
        }

        [Fact]
        public void Privacy_ShouldAllowAnonymous()
        {
            var method = typeof(HomeController).GetMethod(nameof(HomeController.Privacy));

            var attribute = method!.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false)
                .OfType<AllowAnonymousAttribute>()
                .SingleOrDefault();

            Assert.NotNull(attribute);
        }

        [Fact]
        public void DataDeletion_ShouldDeclareCanonicalRoute()
        {
            var method = typeof(HomeController).GetMethod(nameof(HomeController.DataDeletion));

            var attribute = Assert.Single(method!.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false))
                as HttpGetAttribute;

            Assert.NotNull(attribute);
            Assert.Equal("/eliminacion-datos", attribute!.Template);
        }

        [Fact]
        public void DataDeletion_ShouldDeclareCanonicalHeadRoute()
        {
            var method = typeof(HomeController).GetMethod(nameof(HomeController.DataDeletion));

            var attribute = Assert.Single(method!.GetCustomAttributes(typeof(HttpHeadAttribute), inherit: false))
                as HttpHeadAttribute;

            Assert.NotNull(attribute);
            Assert.Equal("/eliminacion-datos", attribute!.Template);
        }

        [Fact]
        public void DataDeletion_ShouldAllowAnonymous()
        {
            var method = typeof(HomeController).GetMethod(nameof(HomeController.DataDeletion));

            var attribute = method!.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false)
                .OfType<AllowAnonymousAttribute>()
                .SingleOrDefault();

            Assert.NotNull(attribute);
        }

        [Fact]
        public void PrivacyLegacy_ShouldDeclareLegacyHomePrivacyRoute()
        {
            var method = typeof(HomeController).GetMethod(nameof(HomeController.PrivacyLegacy));

            var attribute = Assert.Single(method!.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false))
                as HttpGetAttribute;

            Assert.NotNull(attribute);
            Assert.Equal("/Home/Privacy", attribute!.Template);
        }

        [Fact]
        public void PrivacyLegacy_ShouldRedirectPermanentlyToCanonicalRoute()
        {
            var controller = new HomeController(
                NullLogger<HomeController>.Instance,
                new StubPublicSiteContentService(),
                new StubPostLoginDestinationService());

            var result = controller.PrivacyLegacy();

            var redirect = Assert.IsType<RedirectResult>(result);
            Assert.True(redirect.Permanent);
            Assert.Equal("/privacidad", redirect.Url);
        }

        private sealed class StubPublicSiteContentService : IPublicSiteContentService
        {
            public IReadOnlyCollection<MarketingMetricViewModel> GetHeroMetrics() =>
                Array.Empty<MarketingMetricViewModel>();

            public IReadOnlyCollection<MarketingModuleViewModel> GetModules() =>
                Array.Empty<MarketingModuleViewModel>();

            public Task<IReadOnlyCollection<MarketingPlanCardViewModel>> GetPlanCardsAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyCollection<MarketingPlanCardViewModel>>(
                    Array.Empty<MarketingPlanCardViewModel>());

            public Task<IReadOnlyCollection<MarketingPlanCardViewModel>> GetWhatsAppAddonCardsAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyCollection<MarketingPlanCardViewModel>>(
                    Array.Empty<MarketingPlanCardViewModel>());

            public Task<IReadOnlyCollection<MarketingPlanCardViewModel>> GetInternalPlanCardsAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyCollection<MarketingPlanCardViewModel>>(
                    Array.Empty<MarketingPlanCardViewModel>());

            public Task<Plan?> FindAvailablePlanAsync(Guid planId, CancellationToken cancellationToken = default) =>
                Task.FromResult<Plan?>(null);

            public Task<string?> GetPlanNameAsync(Guid? planId, CancellationToken cancellationToken = default) =>
                Task.FromResult<string?>(null);
        }
    }
}
