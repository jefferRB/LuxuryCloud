using System.Diagnostics;
using LuxuryApp.Models;
using LuxuryApp.Models.Marketing;
using LuxuryApp.Services.PublicSite;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly IPublicSiteContentService _publicSiteContentService;
        private readonly LuxuryApp.Services.Asociados.IPostLoginDestinationService _postLoginDestinationService;

        public HomeController(
            ILogger<HomeController> logger,
            IPublicSiteContentService publicSiteContentService,
            LuxuryApp.Services.Asociados.IPostLoginDestinationService postLoginDestinationService)
        {
            _logger = logger;
            _publicSiteContentService = publicSiteContentService;
            _postLoginDestinationService = postLoginDestinationService;
        }

        // Convención de facto (nginx): "el cliente cerró la conexión antes de responder".
        // Coincide con ClientDisconnectMiddleware para dar una respuesta silenciosa.
        private const int ClientClosedRequest = 499;

        [AllowAnonymous]
        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                // Funcionario: su área es el portal limitado, no el dashboard del negocio.
                if (User.IsInRole(LuxuryApp.Services.Identity.AppRoles.Funcionario) &&
                    !User.IsInRole(LuxuryApp.Services.Identity.AppRoles.Administrador))
                {
                    return Redirect("/MiPortal");
                }

                // Un asociado puede no tener el dashboard: se le manda al primer módulo que sí
                // puede abrir, en vez de rebotarlo contra un Access Denied.
                var destino = await _postLoginDestinationService.ResolveAsync(User, cancellationToken);
                return LocalRedirect(destino);
            }

            // El visitante ya abandonó (navegación rápida, cierre o refresco): no hacemos
            // trabajo extra ni construimos la vista. No es un error de la aplicación.
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Landing pública abortada por el cliente antes de cargar los planes.");
                return new StatusCodeResult(ClientClosedRequest);
            }

            var pricing = await LoadPricingPreviewResilientlyAsync(cancellationToken);
            if (pricing is null)
            {
                // El cliente canceló mientras se cargaba el catálogo. Respuesta 499 silenciosa.
                return new StatusCodeResult(ClientClosedRequest);
            }

            var model = new PublicHomeViewModel
            {
                HeroMetrics = _publicSiteContentService.GetHeroMetrics(),
                Modules = _publicSiteContentService.GetModules(),
                Pricing = pricing
            };

            return View(model);
        }

        /// <summary>
        /// Carga el catálogo comercial tolerando fallos: la landing debe renderizarse siempre.
        /// Devuelve <c>null</c> únicamente cuando el propio cliente canceló el request; ante
        /// cualquier otro fallo devuelve un preview "no disponible" (fallback seguro, sin precios
        /// inventados).
        /// </summary>
        private async Task<CommercialPricingPreview?> LoadPricingPreviewResilientlyAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                return await _publicSiteContentService.GetCommercialPricingPreviewAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Cancelación del cliente (navegador). No es un error: como máximo Debug, sin 500.
                _logger.LogDebug("Carga del catálogo comercial de la landing cancelada por el cliente.");
                return null;
            }
            catch (OperationCanceledException ex)
            {
                // Cancelación/timeout NO originado por el request (p. ej. límite de base de datos).
                _logger.LogWarning(
                    ex,
                    "Carga del catálogo comercial cancelada por timeout u origen ajeno al request. " +
                    "Se renderiza la landing con precios no disponibles.");
                return CommercialPricingPreview.Unavailable();
            }
            catch (Exception ex)
            {
                // Cualquier otro fallo no debe tumbar la landing.
                _logger.LogWarning(
                    ex,
                    "No fue posible cargar el catálogo comercial. " +
                    "Se renderiza la landing con precios no disponibles.");
                return CommercialPricingPreview.Unavailable();
            }
        }

        [AllowAnonymous]
        [HttpGet("/privacidad")]
        [HttpHead("/privacidad")]
        public IActionResult Privacy()
        {
            return View();
        }

        [AllowAnonymous]
        [HttpGet("/eliminacion-datos")]
        [HttpHead("/eliminacion-datos")]
        public IActionResult DataDeletion()
        {
            return View();
        }

        [AllowAnonymous]
        [HttpGet("/Home/Privacy")]
        public IActionResult PrivacyLegacy()
        {
            return RedirectPermanent("/privacidad");
        }

        /// <summary>
        /// Aterrizaje neutro para una cuenta autenticada a la que todavía no le concedieron
        /// ningún módulo (típicamente un asociado recién creado). Existe para que su primer
        /// segundo dentro del producto sea una explicación y no un "Acceso denegado".
        /// </summary>
        [HttpGet]
        public IActionResult SinPermisos()
        {
            return View();
        }

        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
