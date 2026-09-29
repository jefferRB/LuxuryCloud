using System.Net;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Marketing;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Billing;
using LuxuryApp.Services.Contracts;
using LuxuryApp.Services.Payments;
using LuxuryApp.Services.PublicSite;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Security;
using LuxuryApp.Services.WhatsApp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Controllers
{
    [Authorize(Roles = "Administrador")]
    public class BillingController : Controller
    {
        private const int CheckoutAutoRefreshSeconds = 4;
        private const int CheckoutAutoRefreshMaxAttempts = 5;

        /// <summary>Ventana para ligar un retorno sin referencia a un intento reciente del tenant.</summary>
        private const int CheckoutReturnCorrelationWindowHours = 3;

        private readonly ILogger<BillingController> _logger;
        private readonly ApplicationDbContext _context;
        private readonly SaaSPaymentService _paymentService;
        private readonly SuscripcionService _suscripcionService;
        private readonly IPromotionalCodeService _promotionalCodeService;
        private readonly ITenantCommercialAccessResolver _commercialAccessResolver;
        private readonly IPublicSiteContentService _publicSiteContentService;
        private readonly PublicCallbackHealthService _publicCallbackHealthService;
        private readonly UserManager<AppUsuario> _userManager;
        private readonly ITenantWhatsAppSettingsService _tenantWhatsAppSettingsService;
        private readonly ISubscriptionSummaryService _subscriptionSummaryService;
        private readonly ISubscriptionPricingCatalog _pricingCatalog;
        private readonly IPlanChangeService _planChangeService;
        private readonly IPlanChangeDecisionService _planChangeDecisionService;
        private readonly IWebHostEnvironment _environment;
        private readonly OpcionesTilopay _tilopayOptions;
        private readonly OpcionesPago _paymentOptions;
        private readonly TilopayRepeatOptions _tilopayRepeatOptions;
        private readonly LuxuryApp.Services.Tilopay.ITilopayRepeatAdminService _tilopayRepeatAdminService;
        private readonly LuxuryApp.Services.Billing.IProviderSubscriptionManager _providerSubscriptionManager;
        private readonly LuxuryApp.Services.Billing.IAddonSubscriptionManager _addonSubscriptionManager;
        private readonly LuxuryApp.Services.Billing.IPaymentMethodUpdateService _paymentMethodUpdateService;

        public BillingController(
            ILogger<BillingController> logger,
            ApplicationDbContext context,
            SaaSPaymentService paymentService,
            SuscripcionService suscripcionService,
            IPromotionalCodeService promotionalCodeService,
            ITenantCommercialAccessResolver commercialAccessResolver,
            IPublicSiteContentService publicSiteContentService,
            PublicCallbackHealthService publicCallbackHealthService,
            UserManager<AppUsuario> userManager,
            ITenantWhatsAppSettingsService tenantWhatsAppSettingsService,
            ISubscriptionSummaryService subscriptionSummaryService,
            ISubscriptionPricingCatalog pricingCatalog,
            IPlanChangeService planChangeService,
            IPlanChangeDecisionService planChangeDecisionService,
            IWebHostEnvironment environment,
            IOptions<OpcionesTilopay> tilopayOptions,
            IOptions<OpcionesPago> paymentOptions,
            IOptions<TilopayRepeatOptions> tilopayRepeatOptions,
            LuxuryApp.Services.Tilopay.ITilopayRepeatAdminService tilopayRepeatAdminService,
            LuxuryApp.Services.Billing.IProviderSubscriptionManager providerSubscriptionManager,
            LuxuryApp.Services.Billing.IAddonSubscriptionManager addonSubscriptionManager,
            LuxuryApp.Services.Billing.IPaymentMethodUpdateService paymentMethodUpdateService)
        {
            _logger = logger;
            _context = context;
            _paymentService = paymentService;
            _suscripcionService = suscripcionService;
            _promotionalCodeService = promotionalCodeService;
            _commercialAccessResolver = commercialAccessResolver;
            _publicSiteContentService = publicSiteContentService;
            _publicCallbackHealthService = publicCallbackHealthService;
            _userManager = userManager;
            _tenantWhatsAppSettingsService = tenantWhatsAppSettingsService;
            _subscriptionSummaryService = subscriptionSummaryService;
            _pricingCatalog = pricingCatalog;
            _planChangeService = planChangeService;
            _planChangeDecisionService = planChangeDecisionService;
            _environment = environment;
            _tilopayOptions = tilopayOptions.Value;
            _paymentOptions = paymentOptions.Value;
            _tilopayRepeatOptions = tilopayRepeatOptions.Value;
            _tilopayRepeatAdminService = tilopayRepeatAdminService;
            _providerSubscriptionManager = providerSubscriptionManager;
            _addonSubscriptionManager = addonSubscriptionManager;
            _paymentMethodUpdateService = paymentMethodUpdateService;
        }

        /// <summary>
        /// Genera y redirige a la recurrentUrl de TiloPay para actualizar tarjeta / reintentar el
        /// cobro SIN crear un suscriptor nuevo. Se usa cuando la suscripción está morosa/fallida.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActualizarTarjeta(CancellationToken cancellationToken)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return Challenge();
            }

            if (user.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(user.Email))
            {
                TempData["BillingError"] = "Tu cuenta no tiene los datos necesarios para actualizar el pago.";
                return RedirectToAction(nameof(Suscripcion));
            }

            // El servicio valida ownership/estado, genera la recurrentUrl on-demand y valida que sea
            // HTTPS del dominio de TiloPay (anti open-redirect). El controller NO llama a TiloPay.
            var result = await _paymentMethodUpdateService.GenerateUpdateUrlAsync(
                user.TenantId, user.Email, user.Id, user.Email, cancellationToken);

            if (result.Succeeded && !string.IsNullOrWhiteSpace(result.Url))
            {
                return Redirect(result.Url);
            }

            TempData["BillingError"] = result.Message ?? "No pudimos generar el enlace de actualización. Contactá soporte.";
            return RedirectToAction(nameof(Suscripcion));
        }

        /// <summary>
        /// Cancelación de la RENOVACIÓN por el cliente (admin del tenant): no corta el acceso ya
        /// pagado. Delega TODA la lógica y la verificación contra TiloPay en
        /// <see cref="LuxuryApp.Services.Billing.IProviderSubscriptionManager"/>; el controller nunca
        /// llama al proveedor directamente. Idempotente ante doble click (lo garantiza el servicio).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelarRenovacion(CancellationToken cancellationToken)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return Challenge();
            }

            if (user.TenantId == Guid.Empty)
            {
                TempData["BillingError"] = "Tu cuenta no tiene un tenant asociado.";
                return RedirectToAction(nameof(Suscripcion));
            }

            if (!_tilopayRepeatAdminService.IsEnabled)
            {
                TempData["BillingError"] = "La cancelación en línea no está disponible en este momento. Contactá soporte.";
                return RedirectToAction(nameof(Suscripcion));
            }

            var result = await _providerSubscriptionManager.RequestCancellationAtPeriodEndAsync(
                user.TenantId,
                user.Id,
                user.Email ?? user.Id,
                "Cancelación de renovación solicitada por el cliente desde /Billing/Suscripcion.",
                cancellationToken);

            if (result.Succeeded)
            {
                // Mensaje con la fecha efectiva Tica reutilizando el mismo summary de la vista.
                var summary = await _subscriptionSummaryService.BuildAsync(user.TenantId, cancellationToken);
                var fecha = summary?.CurrentPeriodEndDisplay;
                TempData["BillingSuccess"] = string.IsNullOrEmpty(fecha)
                    ? "Listo. No se harán nuevos cobros. Tu acceso seguirá activo hasta el fin del período."
                    : $"Listo. No se harán nuevos cobros. Tu acceso seguirá activo hasta {fecha}.";
            }
            else
            {
                _logger.LogWarning(
                    "CancelarRenovacion no se pudo completar automáticamente. TenantId {TenantId}.",
                    user.TenantId);
                TempData["BillingError"] = "No pudimos completar la solicitud automáticamente. Soporte fue notificado.";
            }

            return RedirectToAction(nameof(Suscripcion));
        }

        /// <summary>
        /// Cancela la RENOVACIÓN del add-on de WhatsApp (independiente del plan base). Da de baja el
        /// suscriptor del add-on en TiloPay con verificación (el servicio nunca deja doble cobro
        /// silencioso: si no verifica, queda en revisión). El uso del paquete sigue hasta el fin del
        /// período ya pagado. NO toca el plan base.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelarWhatsAppAddon(CancellationToken cancellationToken)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return Challenge();
            }

            if (user.TenantId == Guid.Empty)
            {
                TempData["BillingError"] = "Tu cuenta no tiene un tenant asociado.";
                return RedirectToAction(nameof(Suscripcion));
            }

            if (!_addonSubscriptionManager.IsEnabled)
            {
                TempData["BillingError"] = "La cancelación en línea del paquete no está disponible en este momento. Contactá soporte.";
                return RedirectToAction(nameof(Suscripcion));
            }

            var result = await _addonSubscriptionManager.RequestAddonCancellationAtPeriodEndAsync(
                user.TenantId,
                user.Id,
                user.Email ?? user.Id,
                "Cancelación del paquete de WhatsApp solicitada por el cliente desde /Billing/Suscripcion.",
                cancellationToken);

            if (result.Succeeded)
            {
                TempData["BillingSuccess"] = result.Message
                    ?? "Listo. Tu paquete de WhatsApp no se renovará.";
            }
            else
            {
                _logger.LogWarning(
                    "CancelarWhatsAppAddon no se pudo completar automáticamente. TenantId {TenantId}.",
                    user.TenantId);
                TempData["BillingError"] = result.Message
                    ?? "No pudimos completar la solicitud automáticamente. Soporte fue notificado.";
            }

            return RedirectToAction(nameof(Suscripcion));
        }

        /// <summary>
        /// Reactivación de una RENOVACIÓN cancelada AÚN vigente (Caso B) por el cliente. Distinta de
        /// la "reactivación de suscripción suspendida" de plataforma. NO crea checkout: reactiva el
        /// mismo suscriptor vía el servicio (el controller nunca llama a TiloPay). El servicio valida
        /// que sea CancelAtPeriodEnd y que el período no haya vencido.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReactivarRenovacion(CancellationToken cancellationToken)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return Challenge();
            }

            if (user.TenantId == Guid.Empty)
            {
                TempData["BillingError"] = "Tu cuenta no tiene un tenant asociado.";
                return RedirectToAction(nameof(Suscripcion));
            }

            if (!_tilopayRepeatAdminService.IsEnabled)
            {
                TempData["BillingError"] = "La reactivación en línea no está disponible en este momento. Contactá soporte.";
                return RedirectToAction(nameof(Suscripcion));
            }

            var result = await _providerSubscriptionManager.ReactivateRenewalAsync(
                user.TenantId,
                user.Id,
                user.Email ?? user.Id,
                cancellationToken);

            if (result.Succeeded)
            {
                TempData["BillingSuccess"] = result.Message ?? "Tu renovación fue reactivada. Tu suscripción continuará normalmente.";
            }
            else
            {
                _logger.LogWarning(
                    "ReactivarRenovacion no se pudo completar automáticamente. TenantId {TenantId}.",
                    user.TenantId);
                TempData["BillingError"] = result.Message ?? "No pudimos completar la reactivación automáticamente. Soporte fue notificado.";
            }

            return RedirectToAction(nameof(Suscripcion));
        }

        [AllowAnonymous]
        public async Task<IActionResult> Planes(Guid? selectedPlanId = null, CancellationToken cancellationToken = default)
        {
            var basePlanCards = await _publicSiteContentService.GetPlanCardsAsync(cancellationToken);
            var whatsAppAddonCards = await _publicSiteContentService.GetWhatsAppAddonCardsAsync(cancellationToken);

            var user = await _userManager.GetUserAsync(User);
            TenantCommercialAccessResult? currentAccess = null;
            BillingSubscriptionSummaryViewModel? currentSubscription = null;
            IReadOnlyCollection<MarketingPlanCardViewModel> internalPlanCards;
            if (_environment.IsDevelopment() && _paymentOptions.EnableValidationPlans)
            {
                internalPlanCards = await _publicSiteContentService.GetInternalPlanCardsAsync(cancellationToken);
            }
            else
            {
                internalPlanCards = Array.Empty<MarketingPlanCardViewModel>();
            }

            if (user is not null && user.TenantId != Guid.Empty)
            {
                currentAccess = await _commercialAccessResolver.ResolveAsync(
                    user.TenantId,
                    user,
                    cancellationToken);

                currentSubscription = await _subscriptionSummaryService.BuildAsync(
                    user.TenantId,
                    cancellationToken);
            }

            return View(new BillingPlanesViewModel
            {
                BasePlanCards = basePlanCards,
                WhatsAppAddonCards = whatsAppAddonCards,
                InternalPlanCards = internalPlanCards,
                CurrentAccess = currentAccess,
                CurrentSubscription = currentSubscription,
                IsAuthenticated = user is not null,
                SelectedPlanId = selectedPlanId,
                Calculator = BuildCalculator(currentSubscription)
            });
        }

        /// <summary>
        /// Vista PRIVADA de suscripcion (layout privado del panel). Enfocada en lo comercial:
        /// plan, estado, renovacion, funcionarios y paquetes WhatsApp como add-on.
        /// La configuracion operativa de WhatsApp vive en el modulo /WhatsApp.
        /// </summary>
        public async Task<IActionResult> Suscripcion(Guid? selectedPlanId = null, CancellationToken cancellationToken = default)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null || user.TenantId == Guid.Empty)
            {
                // Sin tenant no hay nada comercial que mostrar; volvemos al pricing publico.
                return RedirectToAction(nameof(Planes));
            }

            var basePlanCards = await _publicSiteContentService.GetPlanCardsAsync(cancellationToken);
            var whatsAppAddonCards = await _publicSiteContentService.GetWhatsAppAddonCardsAsync(cancellationToken);

            IReadOnlyCollection<MarketingPlanCardViewModel> internalPlanCards =
                _environment.IsDevelopment() && _paymentOptions.EnableValidationPlans
                    ? await _publicSiteContentService.GetInternalPlanCardsAsync(cancellationToken)
                    : Array.Empty<MarketingPlanCardViewModel>();

            var currentAccess = await _commercialAccessResolver.ResolveAsync(
                user.TenantId,
                user,
                cancellationToken);

            var currentSubscription = await _subscriptionSummaryService.BuildAsync(
                user.TenantId,
                cancellationToken);

            return View(new BillingPlanesViewModel
            {
                BasePlanCards = basePlanCards,
                WhatsAppAddonCards = whatsAppAddonCards,
                InternalPlanCards = internalPlanCards,
                CurrentAccess = currentAccess,
                CurrentSubscription = currentSubscription,
                IsAuthenticated = true,
                SelectedPlanId = selectedPlanId,
                Calculator = BuildCalculator(currentSubscription),
                RecurrentUpdateAvailable = _tilopayRepeatAdminService.IsEnabled
            });
        }

        /// <summary>
        /// Inicia el checkout recurrente para una combinacion (funcionarios, ciclo) de la calculadora.
        /// El backend resuelve la opcion server-side; NUNCA confia en monto/codigo enviados por el cliente.
        /// Reutiliza el pending abierto (anti doble-click) via CreateRecurringCheckoutAsync.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CheckoutCalculadora(int workers, string? cycle, CancellationToken cancellationToken)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return Challenge();
            }

            if (user.TenantId == Guid.Empty)
            {
                return BadRequest("El usuario autenticado no tiene tenant asociado.");
            }

            if (string.IsNullOrWhiteSpace(user.Email))
            {
                return BadRequest("El usuario autenticado no tiene correo asociado.");
            }

            // Renovación cancelada pero AÚN vigente (Caso B): NO se abre checkout de cambio de plan;
            // primero hay que reactivar la renovación. Si ya venció (Caso C), sí se permite
            // suscribirse de nuevo. Defensa server-side; la UI también ajusta la calculadora.
            var summaryForGuard = await _subscriptionSummaryService.BuildAsync(user.TenantId, cancellationToken);
            if (summaryForGuard?.IsRenewalPaused == true)
            {
                // Pausada por soporte/plataforma: no se abre checkout de cambio (mezcla estados).
                // Solo lectura del estado; el corte/reactivación de la pausa es acción Platform.
                TempData["BillingError"] = "Tu suscripción está pausada. Contactá soporte para reactivarla antes de cambiar de plan.";
                return RedirectToAction(nameof(Suscripcion));
            }

            if (summaryForGuard?.PaymentSuspended == true)
            {
                // Suspendida por impago: el camino es actualizar el método de pago, no cambiar de plan.
                TempData["BillingError"] = "Tu cuenta está suspendida por pago pendiente. Actualizá tu método de pago para reactivarla antes de cambiar de plan.";
                return RedirectToAction(nameof(Suscripcion));
            }

            if (summaryForGuard?.CanReactivateRenewal == true)
            {
                TempData["BillingError"] = "Para cambiar de plan, primero reactivá tu renovación.";
                return RedirectToAction(nameof(Suscripcion));
            }

            var billingCycle = ParseBillingCycle(cycle);

            // Resolucion autoritativa server-side. Cualquier monto/codigo del cliente se ignora.
            var resolution = _pricingCatalog.Resolve(workers, billingCycle);
            if (!resolution.IsAvailable || resolution.Option is not { } option || !option.IsPublic)
            {
                _logger.LogWarning(
                    "Checkout calculadora rechazado. TenantId {TenantId}. Workers {Workers}. Cycle {Cycle}. Reason {Reason}.",
                    user.TenantId,
                    workers,
                    billingCycle,
                    resolution.Error ?? "opcion no publica");
                TempData["BillingError"] = "La combinacion seleccionada no esta disponible. Revisa la cantidad de funcionarios y el ciclo, o contacta soporte.";
                return RedirectToAction(nameof(Suscripcion));
            }

            // Funcionarios es tenant-scoped por el query filter del contexto del request autenticado.
            var activeFuncionarios = await _context.Funcionarios.CountAsync(f => f.Activo, cancellationToken);

            // Resolver la fila Plan por Codigo (server-side). El monto a cobrar lo define el hosted link.
            var plan = await _context.Planes
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Codigo == option.Code && p.Activo, cancellationToken);

            if (plan is null)
            {
                _logger.LogError(
                    "Plan {Code} no existe o no esta activo en BD para checkout calculadora. TenantId {TenantId}.",
                    option.Code,
                    user.TenantId);
                TempData["BillingError"] = "El plan seleccionado no esta disponible en este momento. Contacta soporte.";
                return RedirectToAction(nameof(Suscripcion));
            }

            // Decisión determinística y testeable: compra normal / mismo plan / cambio / bloqueo.
            // Centraliza las guardas de dinero recurrente (downgrade, ProviderSubscriptionId ausente,
            // cancelación vieja pendiente). Fail-closed para cambios de plan.
            var evaluation = await _planChangeDecisionService.EvaluateAsync(
                user.TenantId,
                option.TilopayRecurringPlanId,
                option.WorkerCount,
                activeFuncionarios,
                cancellationToken);

            switch (evaluation.Decision)
            {
                case PlanChangeDecision.SamePlan:
                    TempData["BillingError"] = evaluation.Message ?? "Ese ya es tu plan actual.";
                    return RedirectToAction(nameof(Suscripcion));

                case PlanChangeDecision.BlockedFuncionarioLimit:
                    await AuditPlanChangeBlockAsync(
                        user,
                        Models.Platform.PlatformAuditActions.PlanChangeBlockedDowngradeFuncionarioLimit,
                        $"Cambio a {option.Code} bloqueado por límite de funcionarios ({activeFuncionarios} activos > {option.WorkerCount}).",
                        cancellationToken);
                    TempData["BillingError"] = evaluation.Message;
                    return RedirectToAction(nameof(Suscripcion));

                case PlanChangeDecision.BlockedMissingProviderSubscription:
                    await AuditPlanChangeBlockAsync(
                        user,
                        Models.Platform.PlatformAuditActions.PlanChangeBlockedMissingCurrentProviderSubscription,
                        $"Cambio a {option.Code} bloqueado: la suscripción activa no tiene id_suscriptor del proveedor; no se puede cancelar el plan anterior con seguridad.",
                        cancellationToken);
                    TempData["BillingError"] = evaluation.Message;
                    return RedirectToAction(nameof(Suscripcion));

                case PlanChangeDecision.BlockedPendingOldCancellation:
                    await AuditPlanChangeBlockAsync(
                        user,
                        Models.Platform.PlatformAuditActions.PlanChangeBlockedPendingOldCancellation,
                        $"Cambio a {option.Code} bloqueado: existe un cambio previo aplicado cuyo suscriptor viejo aún no se canceló (riesgo de múltiples rebajos).",
                        cancellationToken);
                    TempData["BillingError"] = evaluation.Message;
                    return RedirectToAction(nameof(Suscripcion));

                case PlanChangeDecision.BlockedAutoCancellationDisabled:
                    await AuditPlanChangeBlockAsync(
                        user,
                        Models.Platform.PlatformAuditActions.PlanChangeBlockedAutoCancellationDisabled,
                        $"Cambio a {option.Code} bloqueado: la cancelación automática del suscriptor viejo está deshabilitada; permitir el pago dejaría dos suscriptores cobrando.",
                        cancellationToken);
                    TempData["BillingError"] = evaluation.Message;
                    return RedirectToAction(nameof(Suscripcion));
            }

            PlanChangeIntent? planChangeIntent = null;
            if (evaluation.Decision == PlanChangeDecision.ProceedPlanChange)
            {
                var currentSubscription = evaluation.CurrentSubscription!;
                var change = await _planChangeService.CreateOrReuseAsync(new PlanChangeRequest
                {
                    TenantId = user.TenantId,
                    FromPlanId = currentSubscription.PlanId,
                    FromPlanCode = currentSubscription.CodigoPlan,
                    FromWorkerCount = currentSubscription.MaxFuncionarios,
                    FromTilopayRecurringPlanId = currentSubscription.TilopayRecurringPlanId,
                    FromProviderSubscriptionId = currentSubscription.ProviderSubscriptionId,
                    ToPlanId = plan.Id,
                    ToPlanCode = option.Code,
                    ToWorkerCount = option.WorkerCount,
                    ToBillingCycle = billingCycle,
                    ToTilopayRecurringPlanId = option.TilopayRecurringPlanId
                }, cancellationToken);

                if (!change.Succeeded)
                {
                    TempData["BillingError"] = change.Error ?? "No fue posible iniciar el cambio de plan.";
                    return RedirectToAction(nameof(Suscripcion));
                }

                planChangeIntent = change.Intent;
            }

            try
            {
                EnsurePublicCallbackBaseUrl();

                if (_paymentOptions.ValidatePublicCallbackReachability)
                {
                    await _publicCallbackHealthService.EnsureReachableAsync(
                        BuildPublicCallbackHealthUrl(),
                        cancellationToken);
                }

                var checkout = await _paymentService.CreateRecurringCheckoutAsync(
                    user.TenantId,
                    plan.Id,
                    string.IsNullOrWhiteSpace(user.Name) ? user.Email : user.Name,
                    user.Email,
                    cancellationToken);

                // Correlación explícita intent ↔ pago: el checkout emite un lc_ref (CorrelationId)
                // que queda en el PagoSuscripcion; enlazamos ese pago al PlanChangeIntent para que
                // el webhook y la reconciliación tengan una cadena inequívoca sin depender de heurística.
                if (planChangeIntent is not null)
                {
                    await LinkPlanChangeIntentToPaymentAsync(
                        planChangeIntent.Id,
                        user.TenantId,
                        checkout.CorrelationId ?? checkout.ProviderReference,
                        option.TilopayRecurringPlanId,
                        cancellationToken);
                }

                _logger.LogInformation(
                    "Checkout calculadora iniciado. TenantId {TenantId}. Code {Code}. Workers {Workers}. Cycle {Cycle}. PlanChange {IsPlanChange}.",
                    user.TenantId,
                    option.Code,
                    option.WorkerCount,
                    billingCycle,
                    planChangeIntent is not null);

                return Redirect(checkout.RedirectUrl);
            }
            catch (RecurringCheckoutBlockedException ex)
            {
                // Bloqueo de negocio (pago en revision manual): el mensaje es seguro para el usuario.
                // El intent se creó antes del pre-check del proveedor y quedó sin checkout: cerrarlo
                // para que no trabe el próximo intento (el índice único deja un solo Pending/tenant).
                await ExpirePlanChangeIntentAfterBlockedCheckoutAsync(planChangeIntent, user.TenantId, ex.Message, cancellationToken);

                TempData["BillingError"] = ex.Message;
                return RedirectToAction(nameof(Suscripcion));
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error iniciando checkout calculadora. TenantId {TenantId}. Code {Code}.",
                    user.TenantId,
                    option.Code);

                await ExpirePlanChangeIntentAfterBlockedCheckoutAsync(planChangeIntent, user.TenantId, "error al iniciar el checkout", cancellationToken);

                TempData["BillingError"] = "No fue posible iniciar el checkout. Revisa la configuracion y vuelve a intentarlo.";
                return RedirectToAction(nameof(Suscripcion));
            }
        }

        /// <summary>
        /// Cierra el intent que este request acaba de crear cuando el checkout no llegó a abrirse.
        /// Best-effort: si falla, no puede convertir un bloqueo (que ya tiene su mensaje) en un 500.
        /// El servicio solo cierra Pending SIN pago, así que nunca toca dinero real.
        /// </summary>
        private async Task ExpirePlanChangeIntentAfterBlockedCheckoutAsync(
            PlanChangeIntent? planChangeIntent,
            Guid tenantId,
            string reason,
            CancellationToken cancellationToken)
        {
            if (planChangeIntent is null)
            {
                return;
            }

            try
            {
                await _planChangeService.ExpirePendingAfterBlockedCheckoutAsync(
                    tenantId,
                    planChangeIntent.Id,
                    reason,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "No fue posible cerrar el PlanChangeIntent tras un checkout bloqueado. TenantId {TenantId}. IntentId {IntentId}.",
                    tenantId,
                    planChangeIntent.Id);
            }
        }

        /// <summary>
        /// Enlaza el PlanChangeIntent con el PagoSuscripcion recién creado por el checkout, usando
        /// el lc_ref/correlation token como puente. Idempotente: solo enlaza si aún no lo estaba.
        /// </summary>
        private async Task LinkPlanChangeIntentToPaymentAsync(
            Guid intentId,
            Guid tenantId,
            string? correlationToken,
            int targetRecurringPlanId,
            CancellationToken cancellationToken)
        {
            try
            {
                var payment = await _context.PagosSuscripcion
                    .IgnoreQueryFilters()
                    .Where(p =>
                        p.TenantId == tenantId &&
                        p.Proveedor == PaymentProviderType.Tilopay &&
                        p.TilopayRecurringPlanId == targetRecurringPlanId &&
                        (p.Estado == EstadoPagoProveedor.Pendiente || p.Estado == EstadoPagoProveedor.ManualReview) &&
                        (correlationToken == null || p.CorrelationToken == correlationToken))
                    .OrderByDescending(p => p.FechaCreacionUtc)
                    .Select(p => new { p.Id })
                    .FirstOrDefaultAsync(cancellationToken);

                if (payment is null)
                {
                    return;
                }

                var intent = await _context.PlanChangeIntents
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(i => i.Id == intentId, cancellationToken);

                if (intent is not null && intent.PagoSuscripcionId != payment.Id)
                {
                    intent.PagoSuscripcionId = payment.Id;
                    intent.UpdatedAtUtc = DateTime.UtcNow;
                    await _context.SaveChangesAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                // El enlace es defensa en profundidad; si falla, la correlación por tenant+plan+Pending
                // (índice único de intents) sigue siendo determinística. No romper el checkout.
                _logger.LogWarning(ex, "No fue posible enlazar PlanChangeIntent {IntentId} con su pago.", intentId);
            }
        }

        /// <summary>Audita (append-only) un bloqueo de cambio de plan con el contexto del actor.</summary>
        private async Task AuditPlanChangeBlockAsync(
            AppUsuario user,
            string action,
            string reason,
            CancellationToken cancellationToken)
        {
            _context.PlatformAuditLogs.Add(new Models.Platform.PlatformAuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = user.Id,
                ActorEmail = user.Email,
                Action = action,
                EntityType = Models.Platform.PlatformAuditEntityTypes.Subscription,
                TenantId = user.TenantId,
                Reason = reason.Length <= 500 ? reason : reason[..500],
                CreatedAtUtc = DateTime.UtcNow
            });
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Cambio de plan bloqueado. Action {Action}. TenantId {TenantId}.",
                action,
                user.TenantId);
        }

        private static BillingCycle ParseBillingCycle(string? cycle)
        {
            var normalized = cycle?.Trim();
            return !string.IsNullOrWhiteSpace(normalized) &&
                   (normalized.Equals("Annual", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("Anual", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("1", StringComparison.Ordinal))
                ? BillingCycle.Annual
                : BillingCycle.Monthly;
        }

        private SubscriptionCalculatorViewModel BuildCalculator(BillingSubscriptionSummaryViewModel? summary)
        {
            var resolutions = _pricingCatalog.EnumerateAll();
            var availableByCode = resolutions
                .Where(resolution => resolution.Option is not null)
                .Select(resolution => resolution.Option!)
                .ToDictionary(option => option.Code, option => option, StringComparer.OrdinalIgnoreCase);

            var options = new List<SubscriptionCalculatorOption>();
            foreach (var resolution in resolutions)
            {
                if (resolution.Option is { } option)
                {
                    decimal annualSavings = 0m;
                    var savingsPercent = 0;

                    if (option.BillingCycle == BillingCycle.Annual)
                    {
                        var monthlyCode = PlanCodes.BuildCalculatorCode(option.WorkerCount, BillingCycle.Monthly);
                        if (monthlyCode is not null && availableByCode.TryGetValue(monthlyCode, out var monthlyOption))
                        {
                            var yearAtMonthly = monthlyOption.ChargeAmount * 12m;
                            annualSavings = yearAtMonthly - option.ChargeAmount;
                            savingsPercent = yearAtMonthly > 0
                                ? (int)Math.Round(annualSavings / yearAtMonthly * 100m, MidpointRounding.AwayFromZero)
                                : 0;
                        }
                    }

                    options.Add(new SubscriptionCalculatorOption
                    {
                        Code = option.Code,
                        Workers = option.WorkerCount,
                        Cycle = option.BillingCycle == BillingCycle.Annual ? "Annual" : "Monthly",
                        ChargeAmount = option.ChargeAmount,
                        MonthlyEquivalentAmount = option.MonthlyEquivalentAmount,
                        AnnualSavings = annualSavings,
                        SavingsPercent = savingsPercent,
                        IsAvailable = true
                    });
                }
                else
                {
                    var code = PlanCodes.BuildCalculatorCode(resolution.WorkerCount, resolution.BillingCycle);
                    options.Add(new SubscriptionCalculatorOption
                    {
                        Code = code ?? string.Empty,
                        Workers = resolution.WorkerCount,
                        Cycle = resolution.BillingCycle == BillingCycle.Annual ? "Annual" : "Monthly",
                        IsAvailable = false,
                        UnavailableReason = resolution.Error
                    });
                }
            }

            int? currentWorkers = null;
            BillingCycle? currentCycle = null;
            var currentCode = summary?.PlanCode;
            if (PlanCodes.IsCalculatorPlanCode(currentCode))
            {
                var current = _pricingCatalog.ResolveByCode(currentCode);
                if (current.Option is { } currentOption)
                {
                    currentWorkers = currentOption.WorkerCount;
                    currentCycle = currentOption.BillingCycle;
                }
            }

            var activeFuncionarios = summary?.ActiveFuncionarios ?? 0;
            var minWorkers = Math.Clamp(
                Math.Max(PlanCodes.CalculatorMinWorkers, activeFuncionarios),
                PlanCodes.CalculatorMinWorkers,
                PlanCodes.CalculatorMaxWorkers);
            var defaultWorkers = currentWorkers.HasValue
                ? Math.Clamp(currentWorkers.Value, minWorkers, PlanCodes.CalculatorMaxWorkers)
                : minWorkers;

            return new SubscriptionCalculatorViewModel
            {
                Options = options,
                Currency = "CRC",
                MinWorkers = minWorkers,
                MaxWorkers = PlanCodes.CalculatorMaxWorkers,
                DefaultWorkers = defaultWorkers,
                DefaultCycle = currentCycle ?? BillingCycle.Monthly,
                ActiveFuncionarios = activeFuncionarios,
                HasActiveSubscription = summary?.CanAccessApp == true && currentWorkers.HasValue,
                CurrentWorkers = currentWorkers,
                CurrentCycle = currentCycle,
                CurrentPlanCode = currentCode,
                // Fecha efectiva ya resuelta por el summary (incluye el expire del proveedor).
                CurrentPlanRenewalDisplay = summary?.NextBillingDateDisplay
            };
        }

        public IActionResult SinSuscripcion()
        {
            return View();
        }

        public async Task<IActionResult> PlanVencido(CancellationToken cancellationToken = default)
        {
            // Mensaje humano según el origen del vencimiento (prueba vs suscripción pagada),
            // sin exponer términos técnicos al cliente.
            var user = await _userManager.GetUserAsync(User);
            var isTrial = false;

            if (user is not null && user.TenantId != Guid.Empty)
            {
                var hadPromotionalGrant = await _context.TenantCommercialAccessGrants
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .AnyAsync(grant => grant.TenantId == user.TenantId, cancellationToken);

                var hadSubscription = await _context.Suscripciones
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .AnyAsync(subscription => subscription.TenantId == user.TenantId, cancellationToken);

                // Si solo tuvo acceso por código promocional (prueba) y nunca una suscripción, es trial.
                isTrial = hadPromotionalGrant && !hadSubscription;

                if (hadSubscription)
                {
                    // Recuperación de pago: el acceso NO "finalizó", está en gracia o suspendido por un
                    // cobro rechazado. El resumen es la única fuente del estado de recuperación.
                    var summary = await _subscriptionSummaryService.BuildAsync(user.TenantId, cancellationToken);
                    if (summary is { PaymentInGrace: true, CanAccessApp: true })
                    {
                        // Gracia vigente: la cuenta sigue activa, no corresponde "finalizó".
                        return RedirectToAction(nameof(Suscripcion));
                    }

                    if (summary is { PaymentSuspended: true } or { PaymentGraceExpired: true })
                    {
                        ViewData["IsTrialExpired"] = false;
                        ViewData["IsPaymentRecoverySuspended"] = true;
                        ViewData["ExpiredHeading"] = "Tu acceso está suspendido por un pago pendiente";
                        ViewData["ExpiredMessage"] =
                            "No pudimos cobrar la renovación de tu suscripción y el período de gracia terminó. " +
                            "Regularizá el pago para recuperar el acceso de inmediato. Tus datos siguen guardados.";
                        return View();
                    }
                }
            }

            ViewData["IsTrialExpired"] = isTrial;
            ViewData["ExpiredHeading"] = isTrial
                ? "Tu prueba finalizó"
                : "Tu suscripción finalizó";
            ViewData["ExpiredMessage"] = isTrial
                ? "Tu prueba de LuxuryCloud finalizó. Renueva tu suscripción para continuar usando la plataforma."
                : "Tu suscripción finalizó. Renueva tu plan para continuar usando LuxuryCloud.";

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateWhatsAppPreferences(CancellationToken cancellationToken = default)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return Challenge();
            }

            if (user.TenantId == Guid.Empty)
            {
                return BadRequest("El usuario autenticado no tiene tenant asociado.");
            }

            // Leer con ContractAcceptanceBindingHelper para manejar correctamente el patrón
            // hidden(false) + checkbox(true) independientemente del orden en que el navegador los envíe.
            var sendAppointmentConfirmations = ContractAcceptanceBindingHelper.IsAccepted(
                Request.Form, "sendAppointmentConfirmations");
            var sendAppointmentReminders = ContractAcceptanceBindingHelper.IsAccepted(
                Request.Form, "sendAppointmentReminders");

            if (_environment.IsDevelopment())
            {
                _logger.LogDebug(
                    "UpdateWhatsAppPreferences form binding. FormKeys: [{FormKeys}]. RawConfirmations: [{RawConfirmations}]. RawReminders: [{RawReminders}]. ResolvedConfirmations: {ResolvedConfirmations}. ResolvedReminders: {ResolvedReminders}.",
                    string.Join(", ", Request.Form.Keys),
                    string.Join("|", ContractAcceptanceBindingHelper.GetSubmittedValues(Request, "sendAppointmentConfirmations")),
                    string.Join("|", ContractAcceptanceBindingHelper.GetSubmittedValues(Request, "sendAppointmentReminders")),
                    sendAppointmentConfirmations,
                    sendAppointmentReminders);
            }

            var addon = await _context.TenantSubscriptionAddons
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(current => current.Plan)
                .Where(current => current.TenantId == user.TenantId)
                .OrderByDescending(current => current.UpdatedAtUtc)
                .ThenByDescending(current => current.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            if (addon is null || !_suscripcionService.IsWhatsAppAddonActive(addon))
            {
                TempData["BillingError"] = "Activa un paquete de WhatsApp antes de cambiar estas preferencias.";
                return RedirectToAction(nameof(Suscripcion));
            }

            var currentSettings = await _tenantWhatsAppSettingsService.GetSettingsForTenantAsync(
                user.TenantId,
                cancellationToken);
            var automationEnabled = sendAppointmentConfirmations || sendAppointmentReminders;

            var dto = new Models.WhatsApp.TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = automationEnabled,
                SendConfirmationOnCreate = sendAppointmentConfirmations,
                SendReminderThreeHoursBefore = sendAppointmentReminders,
                DailyMessageLimit = _suscripcionService.ResolveWhatsAppDailyMessageLimit(
                    addon,
                    currentSettings.DailyMessageLimit),
                TimeZoneId = currentSettings.TimeZoneId,
                Notes = currentSettings.Notes
            };

            if (_environment.IsDevelopment())
            {
                _logger.LogDebug(
                    "UpdateWhatsAppPreferences DTO a guardar. IsEnabled: {IsEnabled}. SendConfirmationOnCreate: {SendConfirmationOnCreate}. SendReminderThreeHoursBefore: {SendReminderThreeHoursBefore}. DailyMessageLimit: {DailyMessageLimit}. TimeZoneId: {TimeZoneId}.",
                    dto.IsEnabled,
                    dto.SendConfirmationOnCreate,
                    dto.SendReminderThreeHoursBefore,
                    dto.DailyMessageLimit,
                    dto.TimeZoneId);
            }

            await _tenantWhatsAppSettingsService.UpdateSettingsAsync(
                user.TenantId,
                dto,
                user.Id,
                cancellationToken);

            TempData["BillingSuccess"] = "Preferencias de automatizacion WhatsApp actualizadas correctamente.";
            return RedirectToAction(nameof(Suscripcion));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AplicarCodigoPromocional(string accessCode, CancellationToken cancellationToken)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return Challenge();
            }

            if (user.TenantId == Guid.Empty)
            {
                TempData["BillingError"] = "Tu cuenta no tiene un tenant asociado para aplicar el código.";
                return RedirectToAction(nameof(Suscripcion));
            }

            var redemption = await _promotionalCodeService.RedeemAsync(
                accessCode,
                user.TenantId,
                user,
                cancellationToken);

            if (!redemption.Succeeded)
            {
                TempData["BillingError"] = redemption.Error ?? "No fue posible aplicar el código promocional.";
                return RedirectToAction(nameof(Suscripcion));
            }

            TempData["BillingSuccess"] = $"Código aplicado correctamente. Tu acceso queda habilitado hasta {redemption.AccessGrant!.FechaFinUtc:yyyy-MM-dd HH:mm} UTC.";
            return RedirectToAction("Index", "Home");
        }

        [HttpGet]
        public async Task<IActionResult> Exito(
            string? orderNumber = null,
            string? reference = null,
            string? code = null,
            string? description = null,
            int pollAttempt = 0)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null || user.TenantId == Guid.Empty)
            {
                return Challenge();
            }

            var requestedReference = !string.IsNullOrWhiteSpace(reference)
                ? reference.Trim()
                : orderNumber?.Trim();

            var checkoutId = TryExtractTilopayCheckoutId(orderNumber);
            var matchingPayments = await FindMatchingPaymentsAsync(requestedReference, checkoutId);

            if (matchingPayments.Count > 1)
            {
                _logger.LogError(
                    "Billing/Exito detecto una correlacion ambigua. UserId {UserId}. TenantId {TenantId}. RequestedReferenceSuffix {RequestedReferenceSuffix}. CheckoutIdSuffix {CheckoutIdSuffix}. Matches {Matches}.",
                    user.Id,
                    user.TenantId,
                    SensitiveDataMasker.MaskReference(requestedReference),
                    SensitiveDataMasker.MaskReference(checkoutId),
                    matchingPayments.Count);

                return BuildRestrictedCheckoutResult(
                    requestedReference,
                    code,
                    description,
                    "No fue posible validar de forma segura el retorno del pago. Inicia sesion nuevamente y vuelve a intentarlo.");
            }

            var pago = matchingPayments.SingleOrDefault();
            if (pago is not null && pago.TenantId != user.TenantId)
            {
                _logger.LogWarning(
                    "Billing/Exito rechazo un retorno cross-tenant. UserId {UserId}. CurrentTenantId {CurrentTenantId}. PaymentTenantId {PaymentTenantId}. RequestedReferenceSuffix {RequestedReferenceSuffix}. CheckoutIdSuffix {CheckoutIdSuffix}. PaymentId {PaymentId}.",
                    user.Id,
                    user.TenantId,
                    pago.TenantId,
                    SensitiveDataMasker.MaskReference(requestedReference),
                    SensitiveDataMasker.MaskReference(checkoutId),
                    pago.Id);

                return BuildRestrictedCheckoutResult(
                    requestedReference,
                    code,
                    description,
                    "Este retorno de pago no pertenece a la cuenta autenticada. Ingresa con la cuenta que inicio el checkout.");
            }

            // Sin match por referencia, el retorno NO puede caer al plan base: el cliente pudo haber
            // comprado un add-on y mostrarle "suscripción activa" del plan base es un éxito falso
            // Se intenta recuperar el intento reciente del propio tenant; si hay uno
            // solo, ese es el scope real. Si hay varios o ninguno, se marca correlación fallida.
            var correlacionFallida = false;
            if (pago is null)
            {
                var recientes = await FindRecentTenantCheckoutAttemptsAsync(user.TenantId);
                if (recientes.Count == 1)
                {
                    pago = recientes[0];

                    _logger.LogInformation(
                        "Billing/Exito recupero el scope por intento reciente del tenant. TenantId {TenantId}. PaymentId {PaymentId}. RequestedReferenceSuffix {RequestedReferenceSuffix}.",
                        user.TenantId,
                        pago.Id,
                        SensitiveDataMasker.MaskReference(requestedReference));
                }
                else
                {
                    // "Correlación fallida" solo si el cliente REALMENTE viene de un checkout: o el
                    // proveedor mandó señales de retorno, o hay varios intentos recientes y no se
                    // puede adivinar cuál. Entrar a esta pantalla sin nada pendiente (p. ej. desde
                    // un favorito con la suscripción ya activa) no es un error y no debe alarmar.
                    var vieneDeCheckout = !string.IsNullOrWhiteSpace(requestedReference) ||
                                          !string.IsNullOrWhiteSpace(orderNumber) ||
                                          !string.IsNullOrWhiteSpace(code) ||
                                          recientes.Count > 1;

                    if (vieneDeCheckout)
                    {
                        correlacionFallida = true;

                        _logger.LogWarning(
                            "Billing/Exito no pudo correlacionar el retorno con ningun pago local. TenantId {TenantId}. RequestedReferenceSuffix {RequestedReferenceSuffix}. CandidatosRecientes {Candidatos}.",
                            user.TenantId,
                            SensitiveDataMasker.MaskReference(requestedReference),
                            recientes.Count);
                    }
                }
            }

            var suscripcion = await FindCurrentSubscriptionAsync(user.TenantId);
            var pollAttemptNormalized = Math.Max(0, pollAttempt);
            var enRevisionManual = pago?.Estado == EstadoPagoProveedor.ManualReview;

            // Scope-aware: si el pago corresponde a un ADD-ON de WhatsApp, el retorno muestra los datos
            // del ADD-ON (paquete, mensajes mensuales, su vigencia/estado), NUNCA los del plan base
            // (funcionarios/estado base). El estado base en recovery se muestra en una sección aparte.
            var esAddon = pago is not null &&
                (_tilopayRepeatOptions.FindByRecurringPlanId(pago.TilopayRecurringPlanId)?.IsAddon == true ||
                 (pago.Plan?.Codigo is { } addonCode &&
                  PlanCodes.WhatsAppAddons.Contains(addonCode, StringComparer.OrdinalIgnoreCase)));

            if (esAddon)
            {
                var addon = await _context.TenantSubscriptionAddons
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Include(a => a.Plan)
                    .Where(a => a.TenantId == user.TenantId)
                    .OrderByDescending(a => a.UpdatedAtUtc)
                    .FirstOrDefaultAsync();

                // "Activo" en el retorno = LOCAL SANO: el add-on quedó con estado efectivo Activa (no
                // Morosa/gracia). No basta "puede enviar" (Morosa puede) — el retorno no debe mentir.
                // En REVISIÓN MANUAL nunca hay éxito: el add-on puede seguir Activa con el paquete
                // ANTERIOR (p. ej. sigue en WA800 tras un intento fallido de bajar a WA400), así
                // que mostrar "quedó activo" haría creer que el cambio se aplicó.
                var addonLocalSano = !enRevisionManual &&
                    addon is not null &&
                    _suscripcionService.GetEffectiveStatus(addon) == EstadoSuscripcion.Activa;
                var addonAprobadoProveedor = !enRevisionManual &&
                    (addonLocalSano || IsProviderApproved(code, description));
                var addonRequiereConsulta = pago is not null && addonAprobadoProveedor && !addonLocalSano;
                var addonDebeAuto = addonRequiereConsulta && pollAttemptNormalized < CheckoutAutoRefreshMaxAttempts;

                var baseEnRecovery = suscripcion is not null &&
                    (suscripcion.Estado == EstadoSuscripcion.Morosa ||
                     suscripcion.PaymentRecoveryStatus is "GraceActive" or "GraceExpired" or "Suspended");

                var addonModel = new ResultadoCheckoutViewModel
                {
                    Referencia = requestedReference,
                    CodigoProveedor = code,
                    DescripcionProveedor = description,
                    EsAddon = true,
                    EnRevisionManual = enRevisionManual,
                    // El paquete que el cliente intentó comprar (el del PAGO), no el que tiene hoy:
                    // en revisión manual el add-on local sigue siendo el anterior.
                    NombrePlan = enRevisionManual
                        ? (pago?.Plan?.Nombre ?? addon?.Plan?.Nombre)
                        : (addon?.Plan?.Nombre ?? pago?.Plan?.Nombre),
                    EstadoPago = pago?.Estado,
                    EstadoSuscripcion = addon is null ? null : _suscripcionService.GetEffectiveStatus(addon),
                    VigenciaHastaUtc = addon?.FechaFin,
                    ProximoCobroUtc = addon?.FechaProximoCobroUtc,
                    MaxFuncionarios = null, // el add-on NO tiene límite de funcionarios
                    MensajesMensuales = addon is null
                        ? null
                        : (addon.MonthlyMessageLimit > 0 ? addon.MonthlyMessageLimit : addon.Plan?.LimiteMensajesMensual),
                    PagoAprobadoPorProveedor = addonAprobadoProveedor,
                    ConfirmadoPorWebhook = addonLocalSano,
                    SuscripcionActiva = addonLocalSano,
                    DebeAutoActualizar = addonDebeAuto,
                    SegundosAutoActualizacion = addonDebeAuto ? CheckoutAutoRefreshSeconds : 0,
                    UrlActualizacion = addonRequiereConsulta
                        ? BuildCheckoutRefreshUrl(orderNumber, reference, code, description, pollAttemptNormalized + 1)
                        : null,
                    BaseRequiereAtencion = baseEnRecovery,
                    BaseAtencionMensaje = baseEnRecovery
                        ? "Tu plan base tiene un pago pendiente. Regularizalo desde tu suscripción para no perder acceso al SaaS."
                        : null
                };

                ApplyAddonCheckoutMessaging(addonModel);
                ApplyCheckoutActionLinks(addonModel);
                return View("Exito", addonModel);
            }

            // ── Plan base ──
            var effectiveStatus = suscripcion is null
                ? (EstadoSuscripcion?)null
                : _suscripcionService.GetEffectiveStatus(suscripcion);
            // LOCAL SANO = estado EFECTIVO Activa y SIN estado de recuperación. No basta "puede acceder"
            // (Morosa/gracia puede acceder pero NO está sano): así el retorno no muestra "confirmado y
            // activa" cuando en realidad está Morosa / en gracia / SinRelacion.
            // Con el pago en REVISIÓN MANUAL o sin correlación, el estado del plan base NO es la
            // confirmación de lo que el cliente acaba de pagar: puede estar Activa por el ciclo
            // anterior. Anunciarlo como éxito sería un falso verde.
            var localSano = !enRevisionManual &&
                            !correlacionFallida &&
                            suscripcion is not null &&
                            effectiveStatus == EstadoSuscripcion.Activa &&
                            string.IsNullOrEmpty(suscripcion.PaymentRecoveryStatus);
            var suscripcionActiva = localSano;
            var confirmadoPorWebhook = localSano;
            var pagoAprobadoPorProveedor = !enRevisionManual &&
                                           (confirmadoPorWebhook || IsProviderApproved(code, description));
            var requiereConsulta = pago is not null && pagoAprobadoPorProveedor && !suscripcionActiva;
            var requiereRevisionRecurrente = pago is not null &&
                                             pago.TilopayRecurringPlanId.HasValue &&
                                             !suscripcionActiva &&
                                             pago.Estado is EstadoPagoProveedor.Pendiente or EstadoPagoProveedor.ManualReview;
            var debeAutoActualizar = requiereConsulta && pollAttemptNormalized < CheckoutAutoRefreshMaxAttempts;

            var model = new ResultadoCheckoutViewModel
            {
                Referencia = requestedReference,
                CodigoProveedor = code,
                DescripcionProveedor = description,
                NombrePlan = pago?.Plan?.Nombre ?? suscripcion?.Plan?.Nombre,
                EnRevisionManual = enRevisionManual,
                CorrelacionFallida = correlacionFallida,
                EstadoPago = pago?.Estado,
                EstadoSuscripcion = effectiveStatus,
                VigenciaHastaUtc = suscripcion?.FechaFin,
                ProximoCobroUtc = suscripcion?.FechaProximoCobroUtc,
                MaxFuncionarios = suscripcion?.MaxFuncionarios ?? suscripcion?.Plan?.MaxFuncionarios,
                PagoAprobadoPorProveedor = pagoAprobadoPorProveedor,
                ConfirmadoPorWebhook = confirmadoPorWebhook,
                SuscripcionActiva = suscripcionActiva,
                DebeAutoActualizar = debeAutoActualizar,
                SegundosAutoActualizacion = debeAutoActualizar ? CheckoutAutoRefreshSeconds : 0,
                UrlActualizacion = requiereConsulta || requiereRevisionRecurrente
                    ? BuildCheckoutRefreshUrl(orderNumber, reference, code, description, pollAttemptNormalized + 1)
                    : null
            };

            ApplyCheckoutMessaging(model, pago, requestedReference);
            ApplyRecurringHostedLinkFallbackMessaging(model, pago, user);
            ApplyCheckoutActionLinks(model);

            return View("Exito", model);
        }

        /// <summary>Mensajería del retorno de un ADD-ON de WhatsApp (paquete), separada de la del plan base.</summary>
        private static void ApplyAddonCheckoutMessaging(ResultadoCheckoutViewModel model) =>
            CheckoutReturnMessaging.ApplyAddon(model);

        [HttpGet]
        public async Task<IActionResult> CheckoutReturn(
            string? orderNumber = null,
            string? reference = null,
            string? lc_ref = null,
            string? correlationToken = null,
            string? code = null,
            string? description = null,
            int pollAttempt = 0,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var resolvedReference = FirstNonEmpty(reference, lc_ref, correlationToken, orderNumber);

                if (string.IsNullOrWhiteSpace(resolvedReference))
                {
                    var user = await _userManager.GetUserAsync(User);
                    if (user is null || user.TenantId == Guid.Empty)
                    {
                        return Challenge();
                    }

                    var latestPayment = await _context.PagosSuscripcion
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .Where(payment =>
                            payment.TenantId == user.TenantId &&
                            payment.Proveedor == PaymentProviderType.Tilopay &&
                            (payment.Estado == EstadoPagoProveedor.Pendiente ||
                             payment.Estado == EstadoPagoProveedor.ManualReview ||
                             payment.Estado == EstadoPagoProveedor.Confirmado))
                        .OrderByDescending(payment => payment.FechaCreacionUtc)
                        .FirstOrDefaultAsync(cancellationToken);

                    resolvedReference = FirstNonEmpty(
                        latestPayment?.CorrelationToken,
                        latestPayment?.ProviderReference,
                        latestPayment?.ReferenciaInterna);

                    _logger.LogInformation(
                        "Billing/CheckoutReturn sin referencia explicita. TenantId {TenantId}. FallbackPaymentId {PaymentId}. ResolvedReferenceSuffix {ResolvedReferenceSuffix}.",
                        user.TenantId,
                        latestPayment?.Id,
                        SensitiveDataMasker.MaskReference(resolvedReference));
                }

                return await Exito(
                    orderNumber,
                    resolvedReference,
                    code,
                    description,
                    pollAttempt);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error preparando Billing/CheckoutReturn. TraceIdentifier {TraceIdentifier}. OrderNumberSuffix {OrderNumberSuffix}. ReferenceSuffix {ReferenceSuffix}. LcRefSuffix {LcRefSuffix}. CorrelationTokenSuffix {CorrelationTokenSuffix}.",
                    HttpContext.TraceIdentifier,
                    SensitiveDataMasker.MaskReference(orderNumber),
                    SensitiveDataMasker.MaskReference(reference),
                    SensitiveDataMasker.MaskReference(lc_ref),
                    SensitiveDataMasker.MaskReference(correlationToken));

                Response.StatusCode = StatusCodes.Status200OK;

                var model = new ResultadoCheckoutViewModel
                {
                    Referencia = FirstNonEmpty(reference, lc_ref, correlationToken, orderNumber),
                    CodigoProveedor = code,
                    DescripcionProveedor = description,
                    MensajePrincipal = "No pudimos confirmar automaticamente este pago.",
                    MensajeSecundario = $"Revisa el estado de tu suscripcion o intenta de nuevo en unos segundos. Si el problema continua, comparte el identificador {HttpContext.TraceIdentifier} con soporte.",
                    PrimaryActionLabel = "Ir a mi suscripcion",
                    PrimaryActionUrl = Url?.Action(nameof(Suscripcion), "Billing") ?? "/Billing/Suscripcion",
                    SecondaryActionLabel = "Ir al panel",
                    SecondaryActionUrl = Url?.Action("Index", "Dashboard") ?? "/Dashboard"
                };

                return View("Exito", model);
            }
        }

        private IActionResult BuildRestrictedCheckoutResult(
            string? requestedReference,
            string? code,
            string? description,
            string message)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;

            return View(
                "Exito",
                new ResultadoCheckoutViewModel
                {
                    Referencia = requestedReference,
                    CodigoProveedor = code,
                    DescripcionProveedor = description,
                    AccesoRestringido = true,
                    MensajePrincipal = message
                });
        }

        private Task<List<PagoSuscripcion>> FindMatchingPaymentsAsync(
            string? requestedReference,
            string? checkoutId)
        {
            if (string.IsNullOrWhiteSpace(requestedReference) && string.IsNullOrWhiteSpace(checkoutId))
            {
                return Task.FromResult(new List<PagoSuscripcion>());
            }

            // CorrelationToken y ProviderTransactionId también correlacionan: en el checkout
            // recurrente el token va en CorrelationToken (y en ProviderReference), y el retorno del
            // hosted link puede volver solo con el orderNumber/transaction del proveedor. Sin
            // CorrelationToken, Billing/CheckoutReturn resolvía la referencia por ese campo y luego
            // aquí no encontraba nada: el retorno caía al plan base y mostraba un éxito falso.
            return _context.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(p => p.Plan)
                .Where(
                    p => (!string.IsNullOrWhiteSpace(requestedReference) &&
                          (p.ReferenciaInterna == requestedReference ||
                           p.ProviderReference == requestedReference ||
                           p.ProviderCheckoutId == requestedReference ||
                           p.CorrelationToken == requestedReference ||
                           p.ProviderTransactionId == requestedReference)) ||
                         (!string.IsNullOrWhiteSpace(checkoutId) && p.ProviderCheckoutId == checkoutId))
                .OrderByDescending(p => p.FechaCreacionUtc)
                .Take(2)
                .ToListAsync();
        }

        /// <summary>
        /// Último recurso de correlación del retorno: intentos de checkout RECIENTES del propio
        /// tenant. Sirve para resolver el SCOPE (add-on vs plan base) cuando el proveedor vuelve
        /// solo con su orderNumber. Se piden 2 a propósito: con más de un candidato no se adivina.
        /// La ventana es corta para no ligar el retorno a una compra vieja.
        /// </summary>
        private Task<List<PagoSuscripcion>> FindRecentTenantCheckoutAttemptsAsync(Guid tenantId)
        {
            var cutoffUtc = DateTime.UtcNow.AddHours(-CheckoutReturnCorrelationWindowHours);

            return _context.PagosSuscripcion
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(p => p.Plan)
                .Where(p =>
                    p.TenantId == tenantId &&
                    p.FechaCreacionUtc >= cutoffUtc &&
                    (p.Estado == EstadoPagoProveedor.Pendiente ||
                     p.Estado == EstadoPagoProveedor.ManualReview ||
                     p.Estado == EstadoPagoProveedor.Confirmado))
                .OrderByDescending(p => p.FechaCreacionUtc)
                .Take(2)
                .ToListAsync();
        }

        private Task<Suscripcion?> FindCurrentSubscriptionAsync(Guid tenantId) =>
            _context.Suscripciones
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(s => s.Plan)
                .Where(s => s.TenantId == tenantId)
                .OrderByDescending(s => s.FechaUltimaActualizacionUtc ?? s.FechaInicio)
                .ThenByDescending(s => s.FechaInicio)
                .FirstOrDefaultAsync();

        private static bool IsProviderApproved(string? code, string? description)
        {
            if (string.Equals(code?.Trim(), "1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                return false;
            }

            return description.Contains("approved", StringComparison.OrdinalIgnoreCase) ||
                   description.Contains("aprob", StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyCheckoutMessaging(
            ResultadoCheckoutViewModel model,
            PagoSuscripcion? pago,
            string? requestedReference) =>
            CheckoutReturnMessaging.ApplyBasePlan(model, hasLocalPayment: pago is not null, requestedReference);

        private void ApplyRecurringHostedLinkFallbackMessaging(
            ResultadoCheckoutViewModel model,
            PagoSuscripcion? pago,
            AppUsuario user)
        {
            if (pago is null ||
                pago.TilopayRecurringPlanId is null ||
                model.AccesoRestringido ||
                model.SuscripcionActiva)
            {
                return;
            }

            var environment = HttpContext?.RequestServices?.GetService<IWebHostEnvironment>();
            var canUseInternalReconciliation = user.IsPlatformSuperAdmin ||
                                               (environment?.IsDevelopment() == true && User.IsInRole("Administrador"));

            model.MensajePrincipal = "Tu pago puede estar aprobado en Tilopay, pero aun no recibimos confirmacion automatica.";
            model.MensajeSecundario = canUseInternalReconciliation
                ? "Si ya pagaste, usa el boton para revisar tu suscripcion o abre la conciliacion interna con auditoria."
                : "Si ya pagaste y el estado no cambia, contacta soporte para validar la transaccion aprobada en Tilopay.";
            model.PrimaryActionLabel = "Ya pague, revisar mi suscripcion";

            if (canUseInternalReconciliation)
            {
                model.SecondaryActionLabel = "Abrir conciliacion interna";
                model.SecondaryActionUrl = Url?.Action(
                    "Index",
                    "RecurringReconciliation",
                    new { paymentId = pago.Id });
            }
        }

        private void ApplyCheckoutActionLinks(ResultadoCheckoutViewModel model)
        {
            if (model.AccesoRestringido)
            {
                model.PrimaryActionLabel ??= "Ingresar con la cuenta correcta";
                model.PrimaryActionUrl ??= Url?.Action("Acceso", "Accounts") ?? "/Accounts/Acceso";
                return;
            }

            if (model.SuscripcionActiva)
            {
                model.PrimaryActionLabel ??= "Ir al panel";
                model.PrimaryActionUrl ??= Url?.Action("Index", "Dashboard") ?? "/Dashboard";
                model.SecondaryActionLabel ??= "Ir a mi suscripcion";
                model.SecondaryActionUrl ??= Url?.Action(nameof(Suscripcion), "Billing") ?? "/Billing/Suscripcion";
                return;
            }

            if (!string.IsNullOrWhiteSpace(model.UrlActualizacion))
            {
                model.PrimaryActionLabel ??= "Actualizar estado";
                model.PrimaryActionUrl ??= model.UrlActualizacion;
                model.SecondaryActionLabel ??= "Ir a mi suscripcion";
                model.SecondaryActionUrl ??= Url?.Action(nameof(Suscripcion), "Billing") ?? "/Billing/Suscripcion";
                return;
            }

            model.PrimaryActionLabel ??= "Ir a mi suscripcion";
            model.PrimaryActionUrl ??= Url?.Action(nameof(Suscripcion), "Billing") ?? "/Billing/Suscripcion";
        }

        private string BuildCheckoutRefreshUrl(
            string? orderNumber,
            string? reference,
            string? code,
            string? description,
            int pollAttempt)
        {
            var basePath = Url?.Action(nameof(Exito), "Billing") ?? "/Billing/Exito";
            var queryValues = new List<KeyValuePair<string, string?>>();

            if (!string.IsNullOrWhiteSpace(orderNumber))
            {
                queryValues.Add(new KeyValuePair<string, string?>("orderNumber", orderNumber));
            }

            if (!string.IsNullOrWhiteSpace(reference))
            {
                queryValues.Add(new KeyValuePair<string, string?>("reference", reference));
            }

            if (!string.IsNullOrWhiteSpace(code))
            {
                queryValues.Add(new KeyValuePair<string, string?>("code", code));
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                queryValues.Add(new KeyValuePair<string, string?>("description", description));
            }

            queryValues.Add(new KeyValuePair<string, string?>("pollAttempt", pollAttempt.ToString()));

            return $"{basePath}{QueryString.Create(queryValues)}";
        }

        private static string? FirstNonEmpty(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

        private static string? TryExtractTilopayCheckoutId(string? orderNumber)
        {
            if (string.IsNullOrWhiteSpace(orderNumber))
            {
                return null;
            }

            var trimmed = orderNumber.Trim();
            if (trimmed.All(char.IsDigit))
            {
                return trimmed;
            }

            var separatorIndex = trimmed.LastIndexOf('_');
            if (separatorIndex < 0 || separatorIndex == trimmed.Length - 1)
            {
                return null;
            }

            var suffix = trimmed[(separatorIndex + 1)..];
            return suffix.All(char.IsDigit) ? suffix : null;
        }

        [HttpGet]
        public IActionResult Cancelado(string? description = null)
        {
            ViewBag.CancelMessage = string.IsNullOrWhiteSpace(description)
                ? "No completaste el proceso de pago."
                : description;

            return View();
        }

        [HttpGet]
        public IActionResult CheckoutCancel(string? description = null) =>
            Cancelado(description);

        [HttpGet]
        public async Task<IActionResult> ContinuarCheckout(Guid planId, CancellationToken cancellationToken)
        {
            var selectedPlan = await _publicSiteContentService.FindAvailablePlanAsync(planId, cancellationToken);
            if (selectedPlan is null)
            {
                TempData["BillingError"] = "El plan seleccionado ya no esta disponible en este entorno.";
                return RedirectToAction(nameof(Suscripcion));
            }

            if (selectedPlan.EsPlanValidacion && !_environment.IsDevelopment())
            {
                _logger.LogWarning(
                    "Intento de acceder a ContinuarCheckout con plan de validacion fuera de Development. TenantId {TenantId}. PlanId {PlanId}.",
                    (await _userManager.GetUserAsync(User))?.TenantId,
                    planId);
                TempData["BillingError"] = "El plan seleccionado no está disponible en este entorno.";
                return RedirectToAction(nameof(Suscripcion));
            }

            return View(new BillingCheckoutContinuationViewModel
            {
                PlanId = selectedPlan.Id,
                PlanName = selectedPlan.Nombre,
                PlanCode = selectedPlan.Codigo,
                AccountEmail = (await _userManager.GetUserAsync(User))?.Email,
                IsAddon = selectedPlan.LimiteMensajesMensual.HasValue
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Checkout(Guid planId, CancellationToken cancellationToken)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return Challenge();
            }

            if (user.TenantId == Guid.Empty)
            {
                return BadRequest("El usuario autenticado no tiene tenant asociado.");
            }

            if (string.IsNullOrWhiteSpace(user.Email))
            {
                return BadRequest("El usuario autenticado no tiene correo asociado.");
            }
            var selectedPlan = await _publicSiteContentService.FindAvailablePlanAsync(planId, cancellationToken);
            var repeatPlanRegistration = selectedPlan is null
                ? null
                : _tilopayRepeatOptions.FindRegistrationByCode(selectedPlan.Codigo);
            var repeatPlan = repeatPlanRegistration?.Plan;
            var isManagedRecurringPlan = selectedPlan is not null && (
                repeatPlanRegistration is not null ||
                TilopayRepeatOptions.IsManagedPlanCode(selectedPlan.Codigo) ||
                selectedPlan.LimiteMensajesMensual.HasValue ||
                selectedPlan.EsPlanValidacion);
            var isWhatsAppAddon = repeatPlan?.IsAddon == true || selectedPlan?.LimiteMensajesMensual.HasValue == true;

            if (selectedPlan is null)
            {
                TempData["BillingError"] = "El plan seleccionado no está disponible en este entorno.";
                return RedirectToAction(nameof(Suscripcion));
            }

            if (selectedPlan.EsPlanValidacion && !_environment.IsDevelopment())
            {
                _logger.LogWarning(
                    "Intento de checkout con plan de validacion fuera de Development. TenantId {TenantId}. PlanId {PlanId}.",
                    user.TenantId,
                    planId);
                TempData["BillingError"] = "El plan seleccionado no está disponible en este entorno.";
                return RedirectToAction(nameof(Suscripcion));
            }

            var recurringValidationError = ValidateRecurringCheckoutConfiguration(
                selectedPlan,
                repeatPlanRegistration,
                isManagedRecurringPlan);
            var shouldUseRecurringCheckout = isManagedRecurringPlan && recurringValidationError is null;

            _logger.LogInformation(
                "Plan seleccionado para checkout. TenantId {TenantId}. PlanId {PlanId}. PlanCode {PlanCode}. IsWhatsAppAddon {IsWhatsAppAddon}. UsesRecurringCheckout {UsesRecurringCheckout}.",
                user.TenantId,
                selectedPlan.Id,
                selectedPlan.Codigo ?? selectedPlan.Nombre,
                isWhatsAppAddon,
                shouldUseRecurringCheckout);

            if (isWhatsAppAddon)
            {
                var currentAccess = await _commercialAccessResolver.ResolveAsync(
                    user.TenantId,
                    user,
                    cancellationToken);

                if (!currentAccess.CanAccessApp)
                {
                    TempData["BillingError"] = "Primero activa un plan base de LuxuryCloud antes de contratar un paquete de WhatsApp.";
                    return RedirectToAction(nameof(Suscripcion), new { selectedPlanId = planId });
                }
            }

            if (recurringValidationError is not null)
            {
                _logger.LogError(
                    "Checkout recurrente bloqueado por configuracion. TenantId {TenantId}. PlanId {PlanId}. Reason {Reason}",
                    user.TenantId,
                    selectedPlan.Id,
                    recurringValidationError);
                TempData["BillingError"] = "El plan seleccionado no esta disponible para checkout en este momento. Contacta soporte para revisar la configuracion de pagos.";
                return RedirectToAction(nameof(Suscripcion), new { selectedPlanId = planId });
            }

            try
            {
                if (shouldUseRecurringCheckout)
                {
                    EnsurePublicCallbackBaseUrl();

                    if (_paymentOptions.ValidatePublicCallbackReachability)
                    {
                        await _publicCallbackHealthService.EnsureReachableAsync(
                            BuildPublicCallbackHealthUrl(),
                            cancellationToken);
                    }

                    var recurringCheckout = await _paymentService.CreateRecurringCheckoutAsync(
                        user.TenantId,
                        selectedPlan.Id,
                        string.IsNullOrWhiteSpace(user.Name) ? user.Email : user.Name,
                        user.Email,
                        cancellationToken);

                    return Redirect(recurringCheckout.RedirectUrl);
                }

                if (isWhatsAppAddon)
                {
                    TempData["BillingError"] = $"El plan {selectedPlan.Codigo ?? selectedPlan.Nombre} no tiene mapping recurrente configurado.";
                    return RedirectToAction(nameof(Suscripcion), new { selectedPlanId = planId });
                }

                if (string.IsNullOrWhiteSpace(_tilopayOptions.WebhookAccessToken))
                {
                    throw new InvalidOperationException("Falta configuracion obligatoria: Tilopay:WebhookAccessToken.");
                }

                EnsurePublicCallbackBaseUrl();
                var successUrl = BuildSuccessUrl();
                var cancelUrl = BuildCancelUrl();
                var webhookUrl = BuildWebhookUrl();

                _logger.LogInformation(
                    "URLs de callback construidas para checkout. TenantId {TenantId}. SuccessUrl {SuccessUrl}. CancelUrl {CancelUrl}. WebhookUrl {WebhookUrl}. PublicBaseUrl {PublicBaseUrl}",
                    user.TenantId,
                    SensitiveDataMasker.RedactUrl(successUrl),
                    SensitiveDataMasker.RedactUrl(cancelUrl),
                    SanitizeWebhookUrl(webhookUrl),
                    string.IsNullOrWhiteSpace(_paymentOptions.PublicBaseUrl) ? "<request-host>" : _paymentOptions.PublicBaseUrl);

                if (_paymentOptions.ValidatePublicCallbackReachability)
                {
                    await _publicCallbackHealthService.EnsureReachableAsync(
                        BuildPublicCallbackHealthUrl(),
                        cancellationToken);
                }

                var checkout = await _paymentService.CreateCheckoutAsync(
                    user.TenantId,
                    selectedPlan.Id,
                    string.IsNullOrWhiteSpace(user.Name) ? user.Email : user.Name,
                    user.Email,
                    successUrl,
                    cancelUrl,
                    webhookUrl,
                    cancellationToken);

                return Redirect(checkout.RedirectUrl);
            }
            catch (RecurringCheckoutBlockedException ex)
            {
                // Bloqueo de negocio (pago en revision manual): el mensaje es seguro para el usuario.
                TempData["BillingError"] = ex.Message;
                return RedirectToAction(nameof(Suscripcion), new { selectedPlanId = planId });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(
                    ex,
                    "Checkout Tilopay bloqueado por validacion/configuracion. TenantId {TenantId}. PlanId {PlanId}.",
                    user.TenantId,
                    selectedPlan.Id);
                TempData["BillingError"] = "No fue posible iniciar el checkout con Tilopay. Contacta soporte para revisar la configuracion de pagos.";
                return RedirectToAction(nameof(Suscripcion), new { selectedPlanId = planId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error iniciando checkout Tilopay para el tenant {TenantId}.", user.TenantId);
                TempData["BillingError"] = "No fue posible iniciar el checkout con Tilopay. Revisa la configuracion y vuelve a intentarlo.";
                return RedirectToAction(nameof(Suscripcion), new { selectedPlanId = planId });
            }
        }

        private string? ValidateRecurringCheckoutConfiguration(
            Plan selectedPlan,
            TilopayRepeatPlanRegistration? repeatPlanRegistration,
            bool requiresRecurringCheckout)
        {
            if (!requiresRecurringCheckout)
            {
                return null;
            }

            if (repeatPlanRegistration is null)
            {
                return $"El plan {selectedPlan.Codigo ?? selectedPlan.Nombre} no tiene mapping recurrente configurado.";
            }

            if (!_tilopayRepeatOptions.Enabled)
            {
                return "Tilopay Repeat esta deshabilitado: TilopayRepeat:Enabled=false.";
            }

            if (!_tilopayRepeatOptions.UseHostedLinks)
            {
                return "Tilopay Repeat requiere hosted links: TilopayRepeat:UseHostedLinks=false.";
            }

            if (repeatPlanRegistration.Plan.IsValidation && !_tilopayRepeatOptions.EnableTestRecurringPlan)
            {
                return "El plan TEST recurrente esta deshabilitado: TilopayRepeat:EnableTestRecurringPlan=false.";
            }

            if (!repeatPlanRegistration.Plan.IsValidation &&
                !repeatPlanRegistration.Plan.IsAddon &&
                !_tilopayRepeatOptions.UseRecurringCheckoutForPublicPlans &&
                !repeatPlanRegistration.Plan.UsesRecurringCheckout)
            {
                return "Tilopay Repeat esta deshabilitado para planes publicos: TilopayRepeat:UseRecurringCheckoutForPublicPlans=false.";
            }

            if (string.IsNullOrWhiteSpace(_tilopayOptions.WebhookAccessToken))
            {
                return "Falta WebhookAccessToken: Tilopay:WebhookAccessToken.";
            }

            if (string.IsNullOrWhiteSpace(repeatPlanRegistration.Plan.CheckoutUrl))
            {
                return $"Falta CheckoutUrl para {repeatPlanRegistration.Plan.Code}: TilopayRepeat:{repeatPlanRegistration.SectionKey}:CheckoutUrl.";
            }

            return null;
        }

        private static string? ResolveRepeatSectionKey(string? planCode) =>
            TilopayRepeatOptions.ResolveSectionKey(planCode);

        private string BuildSuccessUrl() => BuildAbsoluteUrl(nameof(CheckoutReturn));

        private string BuildCancelUrl() => BuildAbsoluteUrl(nameof(Cancelado));

        private string BuildPublicCallbackHealthUrl() => BuildAbsolutePath("api/health/public-callback");

        private string BuildWebhookUrl()
        {
            var queryName = Uri.EscapeDataString(_tilopayOptions.WebhookAccessTokenQueryParameter);
            var queryValue = Uri.EscapeDataString(_tilopayOptions.WebhookAccessToken);
            var webhookUrl = BuildAbsolutePath("api/webhooks/tilopay");
            var separator = webhookUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            return $"{webhookUrl}{separator}{queryName}={queryValue}";
        }

        private string BuildAbsoluteUrl(string actionName)
        {
            if (!string.IsNullOrWhiteSpace(_paymentOptions.PublicBaseUrl))
            {
                var relativePath = Url.Action(actionName, "Billing", values: null, protocol: null)
                    ?? throw new InvalidOperationException($"No fue posible construir la ruta relativa para {actionName}.");

                return BuildAbsolutePath(relativePath);
            }

            return Url.Action(actionName, "Billing", values: null, protocol: Request.Scheme)
                ?? throw new InvalidOperationException($"No fue posible construir la URL absoluta para {actionName}.");
        }

        private string BuildAbsolutePath(string relativeOrAbsolutePath)
        {
            if (!string.IsNullOrWhiteSpace(_paymentOptions.PublicBaseUrl))
            {
                var baseUri = ParsePublicBaseUri();
                return new Uri(baseUri, relativeOrAbsolutePath.TrimStart('/')).ToString();
            }

            if (Uri.TryCreate(relativeOrAbsolutePath, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri.ToString();
            }

            var builder = new UriBuilder(Request.Scheme, Request.Host.Host, Request.Host.Port ?? -1)
            {
                Path = relativeOrAbsolutePath.TrimStart('/')
            };

            return builder.Uri.ToString();
        }

        private void EnsurePublicCallbackBaseUrl()
        {
            if (!string.IsNullOrWhiteSpace(_paymentOptions.PublicBaseUrl))
            {
                _ = ParsePublicBaseUri();
                return;
            }

            var host = Request.Host.Host;
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new InvalidOperationException(
                    "No fue posible determinar el host público para construir las URLs de retorno y webhook.");
            }

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase) ||
                IPAddress.TryParse(host, out var parsedIp) && IPAddress.IsLoopback(parsedIp))
            {
                throw new InvalidOperationException(
                    "Tilopay requiere una URL pública para callback y webhook. Define Payments:PublicBaseUrl antes de iniciar un checkout desde un entorno local.");
            }
        }
        private Uri ParsePublicBaseUri()
        {
            if (!Uri.TryCreate(_paymentOptions.PublicBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) ||
                (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
            {
                throw new InvalidOperationException(
                    "Payments:PublicBaseUrl debe ser una URL absoluta valida con esquema http o https.");
            }

            return baseUri;
        }

        private string SanitizeWebhookUrl(string webhookUrl)
        {
            return SensitiveDataMasker.RedactUrl(webhookUrl);
        }
    }
}
