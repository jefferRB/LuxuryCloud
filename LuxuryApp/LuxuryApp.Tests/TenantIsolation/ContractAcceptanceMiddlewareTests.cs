using System.Security.Claims;
using LuxuryApp.Middleware;
using LuxuryApp.Models.Legal;
using LuxuryApp.Services.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class ContractAcceptanceMiddlewareTests
    {
        [Fact]
        public async Task Invoke_ShouldRedirectAuthenticatedUserWithoutCurrentAcceptance()
        {
            var nextCalled = false;
            var status = BuildStatus(hasAcceptedCurrentVersion: false);
            var middleware = new ContractAcceptanceMiddleware(
                _ =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                },
                NullLogger<ContractAcceptanceMiddleware>.Instance);

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/Dashboard";
            httpContext.Request.QueryString = new QueryString("?tab=finance");
            httpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "user-1") },
                    "TestAuth"));

            await middleware.Invoke(httpContext, new StubContractService(status));

            Assert.False(nextCalled);
            Assert.Equal(StatusCodes.Status302Found, httpContext.Response.StatusCode);
            Assert.Equal("/Contract/Reaccept?returnurl=%2FDashboard%3Ftab%3Dfinance", httpContext.Response.Headers.Location.ToString());
            Assert.Same(status, httpContext.Items["ContractAcceptanceStatus"]);
        }

        [Fact]
        public async Task Invoke_ShouldContinueWhenCurrentAcceptanceExists()
        {
            var nextCalled = false;
            var status = BuildStatus(hasAcceptedCurrentVersion: true);
            var middleware = new ContractAcceptanceMiddleware(
                _ =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                },
                NullLogger<ContractAcceptanceMiddleware>.Instance);

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = "/Dashboard";
            httpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "user-2") },
                    "TestAuth"));

            await middleware.Invoke(httpContext, new StubContractService(status));

            Assert.True(nextCalled);
            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
            Assert.Equal(string.Empty, httpContext.Response.Headers.Location.ToString());
            Assert.Same(status, httpContext.Items["ContractAcceptanceStatus"]);
        }

        [Theory]
        [InlineData("/privacidad")]
        [InlineData("/Contract")]
        [InlineData("/eliminacion-datos")]
        public async Task Invoke_ShouldAllowPublicLegalRoutesWithoutContractGate(string path)
        {
            var nextCalled = false;
            var status = BuildStatus(hasAcceptedCurrentVersion: false);
            var middleware = new ContractAcceptanceMiddleware(
                _ =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                },
                NullLogger<ContractAcceptanceMiddleware>.Instance);

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Path = path;
            httpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "user-privacy") },
                    "TestAuth"));

            await middleware.Invoke(httpContext, new StubContractService(status));

            Assert.True(nextCalled);
            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
            Assert.Equal(string.Empty, httpContext.Response.Headers.Location.ToString());
            Assert.False(httpContext.Items.ContainsKey("ContractAcceptanceStatus"));
        }

        private static ContractAcceptanceStatus BuildStatus(bool hasAcceptedCurrentVersion)
        {
            var contentHtml = "<section><h2>Contrato</h2><p>Contenido vigente.</p></section>";

            return new ContractAcceptanceStatus
            {
                ActiveDocument = new ContractDocument
                {
                    Id = Guid.NewGuid(),
                    Title = "Contrato de Uso del Servicio LuxuryApp",
                    VersionNumber = "1.0.0",
                    ContentHtml = contentHtml,
                    ContentHash = ContractHashing.ComputeSha256(contentHtml),
                    IsActive = true,
                    EffectiveFromUtc = DateTime.UtcNow,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                },
                HasAcceptedCurrentVersion = hasAcceptedCurrentVersion,
                AcceptedAtUtc = hasAcceptedCurrentVersion ? DateTime.UtcNow : null
            };
        }

        private sealed class StubContractService : IContractService
        {
            private readonly ContractAcceptanceStatus _status;

            public StubContractService(ContractAcceptanceStatus status)
            {
                _status = status;
            }

            public Task<ContractDocument?> GetActiveContractAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(_status.ActiveDocument);

            public Task<ContractAcceptanceStatus> GetAcceptanceStatusAsync(
                string userId,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(_status);

            public Task<ContractAcceptanceRecord> RegisterAcceptanceAsync(
                string userId,
                Guid submittedContractDocumentId,
                string acceptanceSource,
                string? ipAddress,
                string? userAgent,
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }
    }
}
