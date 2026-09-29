using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.PublicSite;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.WhatsApp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Controllers.WhatsApp
{
    /// <summary>
    /// Modulo privado de WhatsApp (/WhatsApp). Recibe toda la configuracion operativa que antes
    /// vivia embebida en la vista publica de Billing/Planes: consumo/saldo, paquete activo y
    /// automatizaciones (confirmaciones, recordatorios, horas de silencio).
    /// La compra/cambio de paquetes sigue ocurriendo en Suscripcion (Billing).
    /// </summary>
    [Authorize(Roles = "Administrador")]
    [Route("WhatsApp")]
    public class WhatsAppController : Controller
    {
        private readonly ILogger<WhatsAppController> _logger;
        private readonly ApplicationDbContext _context;
        private readonly UserManager<AppUsuario> _userManager;
        private readonly ISubscriptionSummaryService _subscriptionSummaryService;
        private readonly IPublicSiteContentService _publicSiteContentService;
        private readonly ITenantCommercialAccessResolver _commercialAccessResolver;
        private readonly SuscripcionService _suscripcionService;
        private readonly ITenantWhatsAppSettingsService _tenantWhatsAppSettingsService;

        public WhatsAppController(
            ILogger<WhatsAppController> logger,
            ApplicationDbContext context,
            UserManager<AppUsuario> userManager,
            ISubscriptionSummaryService subscriptionSummaryService,
            IPublicSiteContentService publicSiteContentService,
            ITenantCommercialAccessResolver commercialAccessResolver,
            SuscripcionService suscripcionService,
            ITenantWhatsAppSettingsService tenantWhatsAppSettingsService)
        {
            _logger = logger;
            _context = context;
            _userManager = userManager;
            _subscriptionSummaryService = subscriptionSummaryService;
            _publicSiteContentService = publicSiteContentService;
            _commercialAccessResolver = commercialAccessResolver;
            _suscripcionService = suscripcionService;
            _tenantWhatsAppSettingsService = tenantWhatsAppSettingsService;
        }

        [HttpGet("")]
        [HttpGet("Index")]
        public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null || user.TenantId == Guid.Empty)
            {
                // Sin tenant no hay nada operativo; lo enviamos a la suscripcion privada.
                return RedirectToAction("Suscripcion", "Billing");
            }

            var summary = await _subscriptionSummaryService.BuildAsync(user.TenantId, cancellationToken);
            var whatsAppAddonCards = await _publicSiteContentService.GetWhatsAppAddonCardsAsync(cancellationToken);

            var access = await _commercialAccessResolver.ResolveAsync(user.TenantId, user, cancellationToken);

            return View(new WhatsAppSettingsPageViewModel
            {
                Summary = summary,
                WhatsAppAddonCards = whatsAppAddonCards,
                HasBaseAccess = access.CanAccessApp
            });
        }

        /// <summary>
        /// Guardado AJAX de la programacion de automatizaciones WhatsApp (sin recargar la pantalla).
        /// Logica movida verbatim desde BillingController.UpdateWhatsAppAutomation; no cambia el
        /// comportamiento de envio ni las reglas de programacion.
        /// </summary>
        [HttpPost("UpdateAutomation")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateAutomation(
            [FromForm] Models.WhatsApp.WhatsAppAutomationRequest request,
            CancellationToken cancellationToken = default)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null)
            {
                return Json(new { success = false, message = "Sesión expirada. Vuelve a iniciar sesión." });
            }

            if (user.TenantId == Guid.Empty)
            {
                return Json(new { success = false, message = "El usuario autenticado no tiene un negocio asociado." });
            }

            request ??= new Models.WhatsApp.WhatsAppAutomationRequest();

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
                return Json(new
                {
                    success = false,
                    message = "Activa un paquete de WhatsApp antes de configurar las automatizaciones."
                });
            }

            var current = await _tenantWhatsAppSettingsService.GetSettingsForTenantAsync(user.TenantId, cancellationToken);

            var confirmationBatch = string.Equals(request.ConfirmationMode, "batch", StringComparison.OrdinalIgnoreCase);
            var reminderBatch = string.Equals(request.ReminderMode, "batch", StringComparison.OrdinalIgnoreCase);

            // Partimos de la configuración actual para no perder campos que esta UI no edita
            // (zona horaria, notas, horas de silencio).
            var dto = new Models.WhatsApp.TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = request.ConfirmationsEnabled || request.RemindersEnabled,
                SendConfirmationOnCreate = request.ConfirmationsEnabled,
                SendReminderThreeHoursBefore = request.RemindersEnabled,
                DailyMessageLimit = _suscripcionService.ResolveWhatsAppDailyMessageLimit(addon, current.DailyMessageLimit),
                TimeZoneId = current.TimeZoneId,
                Notes = current.Notes,

                ConfirmationScheduleMode = confirmationBatch
                    ? Models.WhatsApp.WhatsAppConfirmationScheduleModes.DailyBatchPreviousDay
                    : Models.WhatsApp.WhatsAppConfirmationScheduleModes.RelativeBeforeAppointment,
                ConfirmationHoursBefore = request.ConfirmationHoursBefore,
                ConfirmationBatchTime = confirmationBatch ? request.ConfirmationBatchTime : current.ConfirmationBatchTime,
                ConfirmationBatchTarget = Models.WhatsApp.WhatsAppConfirmationBatchTargets.IsValid(request.ConfirmationBatchTarget)
                    ? request.ConfirmationBatchTarget
                    : current.ConfirmationBatchTarget,
                ConfirmationMorningStart = request.ConfirmationMorningStart ?? current.ConfirmationMorningStart,
                ConfirmationMorningEnd = request.ConfirmationMorningEnd ?? current.ConfirmationMorningEnd,
                SendConfirmationImmediatelyIfInsideWindow = request.SendConfirmationImmediatelyIfInsideWindow,

                ReminderScheduleMode = reminderBatch
                    ? Models.WhatsApp.WhatsAppReminderScheduleModes.DailyBatchSameDay
                    : Models.WhatsApp.WhatsAppReminderScheduleModes.RelativeBeforeAppointment,
                ReminderHoursBefore = request.ReminderHoursBefore,
                ReminderBatchTime = reminderBatch ? request.ReminderBatchTime : current.ReminderBatchTime,
                ReminderBatchTarget = Models.WhatsApp.WhatsAppReminderBatchTargets.IsValid(request.ReminderBatchTarget)
                    ? request.ReminderBatchTarget
                    : current.ReminderBatchTarget,
                ReminderLookAheadHours = request.ReminderHoursBefore,
                SendReminderImmediatelyIfInsideWindow = request.SendReminderImmediatelyIfInsideWindow,

                QuietHoursEnabled = request.QuietHoursEnabled,
                QuietHoursStart = request.QuietHoursEnabled ? request.QuietHoursStart : current.QuietHoursStart,
                QuietHoursEnd = request.QuietHoursEnabled ? request.QuietHoursEnd : current.QuietHoursEnd
            };

            try
            {
                await _tenantWhatsAppSettingsService.UpdateSettingsAsync(user.TenantId, dto, user.Id, cancellationToken);
            }
            catch (ArgumentException ex)
            {
                return Json(new { success = false, message = ex.Message });
            }

            string? warning = null;
            if (request.ConfirmationsEnabled && request.RemindersEnabled &&
                !confirmationBatch && !reminderBatch &&
                request.ConfirmationHoursBefore == request.ReminderHoursBefore)
            {
                warning = "La confirmación y el recordatorio quedaron con la misma anticipación; podrían enviarse muy cerca. Revisa la configuración.";
            }

            return Json(new
            {
                success = true,
                message = "Automatizaciones de WhatsApp actualizadas correctamente.",
                warning
            });
        }
    }
}
