using System.Text.Json;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.Reports;
using LuxuryApp.Services.Identity;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.Platform
{
    /// <summary>
    /// Consola de Plataforma (SuperAdmin) del Resumen Ejecutivo Mensual. Es el ÚNICO lugar donde
    /// se administra esta función: los tenants no tienen vista propia. Exclusiva del super admin
    /// (policy PlatformSuperAdmin); nunca accesible para cuentas de tenant, ni por URL directa.
    /// Toda acción (guardar config, prueba, envío real) queda auditada en <c>PlatformAuditLog</c>.
    /// </summary>
    [Authorize(Policy = PlatformAuthorizationPolicies.PlatformSuperAdmin)]
    [Route("Platform")]
    public class PlatformMonthlyReportsController : Controller
    {
        private readonly IPlatformMonthlyReportService _service;
        private readonly IPlatformAuditService _auditService;
        private readonly ILogger<PlatformMonthlyReportsController> _logger;

        public PlatformMonthlyReportsController(
            IPlatformMonthlyReportService service,
            IPlatformAuditService auditService,
            ILogger<PlatformMonthlyReportsController> logger)
        {
            _service = service;
            _auditService = auditService;
            _logger = logger;
        }

        [HttpGet("MonthlyReports")]
        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            var overview = await _service.GetOverviewAsync(cancellationToken);
            return View("~/Views/Platform/MonthlyReports.cshtml", overview);
        }

        [HttpGet("MonthlyReports/{tenantId:guid}")]
        public async Task<IActionResult> Detalle(Guid tenantId, CancellationToken cancellationToken)
        {
            var detail = await _service.GetTenantDetailAsync(tenantId, take: 100, cancellationToken);
            if (detail is null)
            {
                return NotFound();
            }

            return View("~/Views/Platform/MonthlyReportsDetalle.cshtml", detail);
        }

        [HttpPost("MonthlyReports/{tenantId:guid}/SaveSettings")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveSettings(
            Guid tenantId,
            [Bind(Prefix = "Settings")] PlatformMonthlyReportSettingsForm form,
            CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                TempData["PlatformError"] = "Revisá el día (1-28) y la hora (0-23) de envío.";
                return RedirectToAction(nameof(Detalle), new { tenantId });
            }

            var result = await _service.SaveSettingsAsync(tenantId, form, cancellationToken);
            if (!result.TenantFound)
            {
                return NotFound();
            }

            await SafeAuditAsync(new PlatformAuditEntry
            {
                Action = "MonthlyReportSettingsSaved",
                EntityType = "TenantMonthlyReport",
                EntityId = tenantId.ToString(),
                TenantId = tenantId,
                AfterJson = JsonSerializer.Serialize(form),
                Reason = form.IsEnabled ? "Resumen mensual activado/actualizado." : "Resumen mensual desactivado/actualizado."
            }, cancellationToken);

            TempData["PlatformSuccess"] = "Configuración del resumen mensual guardada.";
            return RedirectToAction(nameof(Detalle), new { tenantId });
        }

        [HttpPost("MonthlyReports/{tenantId:guid}/SendTest")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendTest(
            Guid tenantId,
            int year,
            int month,
            string recipientEmail,
            CancellationToken cancellationToken)
        {
            var normalized = MonthlyBusinessReportService.TryNormalizeEmail(recipientEmail);
            if (normalized is null)
            {
                TempData["PlatformError"] = "El correo interno de prueba no es válido.";
                return RedirectToAction(nameof(Detalle), new { tenantId });
            }

            MonthlyReportSendResult result;
            try
            {
                result = await _service.SendTestAsync(tenantId, year, month, normalized, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en envío de prueba de plataforma para el tenant {TenantId}.", tenantId);
                TempData["PlatformError"] = "No fue posible enviar la prueba.";
                return RedirectToAction(nameof(Detalle), new { tenantId });
            }

            await SafeAuditAsync(new PlatformAuditEntry
            {
                Action = "MonthlyReportTestSend",
                EntityType = "TenantMonthlyReport",
                EntityId = tenantId.ToString(),
                TenantId = tenantId,
                Reason = $"Prueba {month:00}/{year} a {normalized}: {result.Outcome}."
            }, cancellationToken);

            TempData[result.Outcome == MonthlyReportSendOutcome.Sent ? "PlatformSuccess" : "PlatformError"] = result.Message;
            return RedirectToAction(nameof(Detalle), new { tenantId });
        }

        [HttpPost("MonthlyReports/{tenantId:guid}/SendReal")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendReal(
            Guid tenantId,
            int year,
            int month,
            string confirmacion,
            CancellationToken cancellationToken)
        {
            // Confirmación fuerte: el super admin debe escribir ENVIAR.
            if (!string.Equals(confirmacion?.Trim(), "ENVIAR", StringComparison.OrdinalIgnoreCase))
            {
                TempData["PlatformError"] = "Para el envío real escribí ENVIAR en la confirmación.";
                return RedirectToAction(nameof(Detalle), new { tenantId });
            }

            MonthlyReportSendResult result;
            try
            {
                result = await _service.SendRealAsync(tenantId, year, month, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en envío real de plataforma para el tenant {TenantId}.", tenantId);
                TempData["PlatformError"] = "No fue posible ejecutar el envío real.";
                return RedirectToAction(nameof(Detalle), new { tenantId });
            }

            await SafeAuditAsync(new PlatformAuditEntry
            {
                Action = "MonthlyReportRealSend",
                EntityType = "TenantMonthlyReport",
                EntityId = tenantId.ToString(),
                TenantId = tenantId,
                Reason = $"Envío real {month:00}/{year}: {result.Message}"
            }, cancellationToken);

            TempData[result.Outcome is MonthlyReportSendOutcome.Failed ? "PlatformError" : "PlatformSuccess"] = result.Message;
            return RedirectToAction(nameof(Detalle), new { tenantId });
        }

        // La auditoría no debe tumbar una acción que ya se ejecutó; el fallo queda en el log (S6).
        private Task SafeAuditAsync(PlatformAuditEntry entry, CancellationToken cancellationToken) =>
            _auditService.TryLogAsync(entry, cancellationToken);
    }
}
