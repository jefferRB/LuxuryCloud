using LuxuryApp.Models.Fiscal;
using LuxuryApp.Models.Identity;
using LuxuryApp.Services.Account;
using LuxuryApp.Services.Tenant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Controllers.Configuracion
{
    /// <summary>
    /// Configuración fiscal del negocio (IVA incluido en precio + tarifa por defecto).
    ///
    /// <para>
    /// La pantalla dejó de ser una página suelta: ahora es la sección "Configuración fiscal" de
    /// Mi cuenta. El <b>cálculo y el guardado no cambiaron</b>; lo único que se movió es dónde se
    /// dibuja el formulario. <c>Index</c> (GET) sobrevive como redirección para no romper enlaces
    /// ni favoritos viejos.
    /// </para>
    /// </summary>
    [Authorize(Roles = "Administrador")]
    public class ConfiguracionFiscalController : Controller
    {
        /// <summary>Vista de Mi cuenta, que es donde se repinta el formulario si hay errores.</summary>
        private const string CuentaView = "~/Views/Accounts/Cuenta.cshtml";

        private readonly ApplicationDbContext _context;
        private readonly ITenantProvider _tenantProvider;
        private readonly IAccountSettingsPageBuilder _accountSettingsPageBuilder;
        private readonly LuxuryApp.Services.Finanzas.ILegacyFinancialImpactService _impactoHistorico;
        private readonly ILogger<ConfiguracionFiscalController> _logger;

        public ConfiguracionFiscalController(
            ApplicationDbContext context,
            ITenantProvider tenantProvider,
            IAccountSettingsPageBuilder accountSettingsPageBuilder,
            ILogger<ConfiguracionFiscalController> logger,
            LuxuryApp.Services.Finanzas.ILegacyFinancialImpactService impactoHistorico)
        {
            _context = context;
            _tenantProvider = tenantProvider;
            _accountSettingsPageBuilder = accountSettingsPageBuilder;
            _logger = logger;
            _impactoHistorico = impactoHistorico;
        }

        /// <summary>
        /// Ruta legacy (/ConfiguracionFiscal). Ya no tiene pantalla propia: manda a la sección
        /// equivalente de Mi cuenta. No genera 404 ni cicla, porque el destino es otro controlador.
        /// </summary>
        [HttpGet]
        public IActionResult Index() => RedirectToAction(
            "Cuenta",
            "Accounts",
            routeValues: null,
            fragment: AccountSettingsSections.Fiscal);

        /// <summary>
        /// Guarda SOLO la configuración fiscal. Lógica idéntica a la que tenía la pantalla
        /// independiente: misma validación, mismo aviso de impacto histórico con confirmación
        /// explícita, mismo redondeo y mismo alcance (aplica hacia adelante; no reescribe cobros
        /// ni comprobantes ya emitidos).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Index(
            ConfiguracionFiscalViewModel model,
            CancellationToken cancellationToken,
            bool confirmarImpactoHistorico = false)
        {
            if (!ModelState.IsValid)
            {
                return await VolverAlFormularioAsync(model, cancellationToken);
            }

            var tenantId = _tenantProvider.GetTenantId();
            var tenant = await _context.Tenants
                .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

            if (tenant is null)
            {
                return NotFound();
            }

            // Esta pantalla es la que HEREDAN todos los servicios y productos sin override propio,
            // así que su alcance sobre los cobros legacy es el más amplio del sistema.
            var cambioFiscal =
                tenant.PreciosIncluyenIva != model.PreciosIncluyenIva ||
                tenant.TarifaIvaPorDefecto != Math.Round(model.TarifaIvaPorDefecto, 2, MidpointRounding.AwayFromZero);

            if (cambioFiscal && !confirmarImpactoHistorico)
            {
                var legacy = await _impactoHistorico.ContarCobrosLegacyDelNegocioAsync(cancellationToken);
                if (legacy > 0)
                {
                    ViewData[LuxuryApp.Models.Fiscal.AvisoImpactoHistorico.CampoConfirmacion] =
                        LuxuryApp.Models.Fiscal.AvisoImpactoHistorico.Negocio(legacy);

                    ModelState.AddModelError(
                        string.Empty,
                        LuxuryApp.Models.Fiscal.AvisoImpactoHistorico.Confirmacion);

                    return await VolverAlFormularioAsync(model, cancellationToken);
                }
            }

            tenant.PreciosIncluyenIva = model.PreciosIncluyenIva;
            tenant.TarifaIvaPorDefecto = Math.Round(model.TarifaIvaPorDefecto, 2, MidpointRounding.AwayFromZero);

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                TempData["Mensaje"] = "Configuración fiscal actualizada correctamente.";

                // Post-Redirect-Get: refrescar no reenvía el formulario.
                return RedirectToAction(
                    "Cuenta",
                    "Accounts",
                    routeValues: null,
                    fragment: AccountSettingsSections.Fiscal);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al guardar la configuración fiscal del tenant {TenantId}.", tenantId);
                ModelState.AddModelError(string.Empty, "No fue posible guardar la configuración fiscal.");
                return await VolverAlFormularioAsync(model, cancellationToken);
            }
        }

        /// <summary>
        /// Repinta Mi cuenta conservando el ModelState y el ViewData de ESTE request (es lo que
        /// hace visible el aviso de impacto histórico y su casilla de confirmación), con los
        /// valores que el usuario acaba de escribir.
        /// </summary>
        private async Task<IActionResult> VolverAlFormularioAsync(
            ConfiguracionFiscalViewModel model,
            CancellationToken cancellationToken)
        {
            var page = await _accountSettingsPageBuilder.BuildAsync(User, cancellationToken);
            if (page is null)
            {
                return RedirectToAction("Acceso", "Accounts");
            }

            page.Fiscal = model;
            page.FocusSection = AccountSettingsSections.Fiscal;

            return View(CuentaView, page);
        }
    }
}
