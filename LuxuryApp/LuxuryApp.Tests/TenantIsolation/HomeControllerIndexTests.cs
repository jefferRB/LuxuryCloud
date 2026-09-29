using LuxuryApp.Controllers;
using LuxuryApp.Models.Marketing;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.PublicSite;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class HomeControllerIndexTests
    {
        private const int ClientClosedRequest = 499;

        [Fact]
        public async Task Index_WhenTokenAlreadyCancelled_ReturnsClientClosedRequestWithoutLoadingPricing()
        {
            var logger = new CapturingLogger<HomeController>();
            var content = new ConfigurablePublicSiteContentService(_ => Task.FromResult(AvailablePreview()));
            var controller = CreateController(logger, content);

            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var result = await controller.Index(cts.Token);

            var statusResult = Assert.IsType<StatusCodeResult>(result);
            Assert.Equal(ClientClosedRequest, statusResult.StatusCode);
            Assert.Equal(0, content.GetPricingCallCount);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
        }

        [Fact]
        public async Task Index_WhenClientCancelsDuringPricingLoad_ReturnsClientClosedRequestAndDoesNotLogError()
        {
            var logger = new CapturingLogger<HomeController>();
            using var cts = new CancellationTokenSource();

            var content = new ConfigurablePublicSiteContentService(token =>
            {
                cts.Cancel();
                throw new OperationCanceledException(token);
            });
            var controller = CreateController(logger, content);

            var result = await controller.Index(cts.Token);

            var statusResult = Assert.IsType<StatusCodeResult>(result);
            Assert.Equal(ClientClosedRequest, statusResult.StatusCode);
            Assert.Equal(1, content.GetPricingCallCount);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug);
        }

        [Fact]
        public async Task Index_WhenPricingLoadCancelledUnrelatedToRequest_LogsWarningAndRendersUnavailable()
        {
            var logger = new CapturingLogger<HomeController>();
            var content = new ConfigurablePublicSiteContentService(
                _ => throw new TaskCanceledException("Tiempo de espera de base de datos agotado."));
            var controller = CreateController(logger, content);

            var result = await controller.Index(CancellationToken.None);

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<PublicHomeViewModel>(view.Model);
            Assert.False(model.Pricing.IsAvailable);
            Assert.NotEmpty(model.HeroMetrics);
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
        }

        [Fact]
        public async Task Index_WhenPricingLoadThrowsGeneralException_LogsWarningAndRendersUnavailable()
        {
            var logger = new CapturingLogger<HomeController>();
            var content = new ConfigurablePublicSiteContentService(
                _ => throw new InvalidOperationException("Fallo inesperado al cargar el catálogo."));
            var controller = CreateController(logger, content);

            var result = await controller.Index(CancellationToken.None);

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<PublicHomeViewModel>(view.Model);
            Assert.False(model.Pricing.IsAvailable);
            Assert.NotEmpty(model.Modules);
            Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
        }

        [Fact]
        public async Task Index_WhenPricingLoadSucceeds_RendersLandingWithAvailablePricing()
        {
            var logger = new CapturingLogger<HomeController>();
            var content = new ConfigurablePublicSiteContentService(_ => Task.FromResult(AvailablePreview()));
            var controller = CreateController(logger, content);

            var result = await controller.Index(CancellationToken.None);

            var view = Assert.IsType<ViewResult>(result);
            var model = Assert.IsType<PublicHomeViewModel>(view.Model);
            Assert.True(model.Pricing.IsAvailable);
            Assert.Equal(8000m, model.Pricing.StartingMonthlyCharge);
            Assert.NotEmpty(model.HeroMetrics);
            Assert.NotEmpty(model.Modules);
            Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
        }

        private static CommercialPricingPreview AvailablePreview() =>
            new()
            {
                IsAvailable = true,
                Currency = "CRC",
                MinWorkers = 1,
                MaxWorkers = 11,
                StartingMonthlyCharge = 8000m,
                HasMonthly = true,
                HasAnnual = false,
                Tiers = new[]
                {
                    new CommercialPricingTier
                    {
                        Workers = 1,
                        Cycle = "Monthly",
                        ChargeAmount = 8000m,
                        MonthlyEquivalentAmount = 8000m
                    }
                }
            };

        private static HomeController CreateController(
            ILogger<HomeController> logger,
            IPublicSiteContentService content)
        {
            var controller = new HomeController(logger, content, new StubPostLoginDestinationService());
            ControllerTestSupport.AttachHttpContext(controller);
            return controller;
        }

        private sealed class ConfigurablePublicSiteContentService : IPublicSiteContentService
        {
            private readonly Func<CancellationToken, Task<CommercialPricingPreview>> _pricing;

            public ConfigurablePublicSiteContentService(
                Func<CancellationToken, Task<CommercialPricingPreview>> pricing) =>
                _pricing = pricing;

            public int GetPricingCallCount { get; private set; }

            public IReadOnlyCollection<MarketingMetricViewModel> GetHeroMetrics() =>
                new[] { new MarketingMetricViewModel { Value = "1", Label = "panel" } };

            public IReadOnlyCollection<MarketingModuleViewModel> GetModules() =>
                new[] { new MarketingModuleViewModel { Id = "agenda", Title = "Agenda" } };

            public Task<CommercialPricingPreview> GetCommercialPricingPreviewAsync(
                CancellationToken cancellationToken = default)
            {
                GetPricingCallCount++;
                return _pricing(cancellationToken);
            }

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

        private sealed class CapturingLogger<T> : ILogger<T>
        {
            public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
