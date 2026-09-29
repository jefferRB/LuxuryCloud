using LuxuryApp.Controllers;
using LuxuryApp.Controllers.Configuracion;
using LuxuryApp.Services.Identity;
using LuxuryApp.Models.Asociados;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class TenantPublicPageControllerTests
    {
        [Fact]
        public void PublicSiteController_IsAnonymousAndUsesSitioRoute()
        {
            Assert.NotEmpty(typeof(PublicSiteController)
                .GetCustomAttributes(typeof(AllowAnonymousAttribute), inherit: true));

            var route = typeof(PublicSiteController)
                .GetCustomAttributes(typeof(RouteAttribute), inherit: true)
                .OfType<RouteAttribute>()
                .SingleOrDefault();

            Assert.NotNull(route);
            Assert.Equal("sitio", route!.Template);
        }

        [Theory]
        [InlineData(nameof(PublicSiteController.GoReserve), "{slug}/go/reservar")]
        [InlineData(nameof(PublicSiteController.GoServiceReserve), "{slug}/go/servicio/{servicioId:int}/reservar")]
        [InlineData(nameof(PublicSiteController.GoWhatsApp), "{slug}/go/whatsapp")]
        [InlineData(nameof(PublicSiteController.GoMaps), "{slug}/go/maps")]
        [InlineData(nameof(PublicSiteController.GoWaze), "{slug}/go/waze")]
        public void PublicSiteController_GoEndpointsUseInternalRedirectRoutes(
            string actionName,
            string expectedTemplate)
        {
            var action = typeof(PublicSiteController).GetMethod(actionName);

            Assert.NotNull(action);
            var route = action!
                .GetCustomAttributes(typeof(HttpGetAttribute), inherit: true)
                .OfType<HttpGetAttribute>()
                .SingleOrDefault();

            Assert.NotNull(route);
            Assert.Equal(expectedTemplate, route!.Template);
        }

        [Fact]
        public void PaginaPublicaController_RequiresPublicWebsitePermission()
        {
            // Marketing administra la página pública sin ser administrador del negocio: la
            // autorización pasó de rol a permiso. Ver PublicWebsite.View abre la pantalla y
            // PublicWebsite.Manage protege cada POST (uploads incluidos).
            var permisos = typeof(PaginaPublicaController)
                .GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true)
                .OfType<RequirePermissionAttribute>()
                .Select(atributo => atributo.Permission)
                .ToArray();

            Assert.Contains(AppPermissions.PublicWebsiteView, permisos);

            var postsSinManage = typeof(PaginaPublicaController)
                .GetMethods()
                .Where(method => method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true).Any())
                .Where(method => !method
                    .GetCustomAttributes(typeof(RequirePermissionAttribute), inherit: true)
                    .OfType<RequirePermissionAttribute>()
                    .Any(atributo => atributo.Permission == AppPermissions.PublicWebsiteManage))
                .Select(method => method.Name)
                .ToArray();

            Assert.Empty(postsSinManage);
        }

        [Fact]
        public void PaginaPublicaPost_RequiresAntiForgeryToken()
        {
            var post = typeof(PaginaPublicaController)
                .GetMethods()
                .Single(method =>
                    method.Name == nameof(PaginaPublicaController.Index) &&
                    method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true).Any());

            Assert.True(post
                .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true)
                .Any());
        }

        [Fact]
        public void PaginaPublicaImagePosts_RequireAntiForgeryToken()
        {
            var actionNames = new[]
            {
                nameof(PaginaPublicaController.UploadLogo),
                nameof(PaginaPublicaController.RemoveLogo),
                nameof(PaginaPublicaController.UploadCover),
                nameof(PaginaPublicaController.RemoveCover),
                nameof(PaginaPublicaController.UploadBusinessGalleryImage),
                nameof(PaginaPublicaController.RemoveBusinessGalleryImage),
                nameof(PaginaPublicaController.UploadServiceMainImage),
                nameof(PaginaPublicaController.RemoveServiceMainImage),
                nameof(PaginaPublicaController.UploadLocationImage),
                nameof(PaginaPublicaController.RemoveLocationImage)
            };

            foreach (var actionName in actionNames)
            {
                var post = typeof(PaginaPublicaController)
                    .GetMethods()
                    .Single(method =>
                        method.Name == actionName &&
                        method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true).Any());

                Assert.True(post
                    .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), inherit: true)
                    .Any());
            }
        }
    }
}
