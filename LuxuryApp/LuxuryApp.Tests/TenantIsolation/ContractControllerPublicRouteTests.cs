using LuxuryApp.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class ContractControllerPublicRouteTests
    {
        [Fact]
        public void Index_ShouldAllowAnonymous()
        {
            var method = typeof(ContractController).GetMethod(nameof(ContractController.Index));

            var attribute = method!.GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: false)
                .OfType<AllowAnonymousAttribute>()
                .SingleOrDefault();

            Assert.NotNull(attribute);
        }

        [Fact]
        public void Index_ShouldDeclareGetSupport()
        {
            var method = typeof(ContractController).GetMethod(nameof(ContractController.Index));

            var attribute = Assert.Single(method!.GetCustomAttributes(typeof(HttpGetAttribute), inherit: false))
                as HttpGetAttribute;

            Assert.NotNull(attribute);
        }

        [Fact]
        public void Index_ShouldDeclareHeadSupport()
        {
            var method = typeof(ContractController).GetMethod(nameof(ContractController.Index));

            var attribute = Assert.Single(method!.GetCustomAttributes(typeof(HttpHeadAttribute), inherit: false))
                as HttpHeadAttribute;

            Assert.NotNull(attribute);
        }
    }
}
