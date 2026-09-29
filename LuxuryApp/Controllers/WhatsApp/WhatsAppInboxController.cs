using System.Globalization;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.WhatsApp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LuxuryApp.Controllers.WhatsApp
{
    [Authorize(Roles = "Administrador")]
    [Route("WhatsAppInbox")]
    public class WhatsAppInboxController : Controller
    {
        private readonly IWhatsAppInboxService _inboxService;
        private readonly ITenantWhatsAppFeatureService _tenantWhatsAppFeatureService;
        private readonly ICalendarWhatsAppNotificationService _notificationService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ILogger<WhatsAppInboxController> _logger;

        public WhatsAppInboxController(
            IWhatsAppInboxService inboxService,
            ITenantWhatsAppFeatureService tenantWhatsAppFeatureService,
            ICalendarWhatsAppNotificationService notificationService,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ILogger<WhatsAppInboxController> logger)
        {
            _inboxService = inboxService;
            _tenantWhatsAppFeatureService = tenantWhatsAppFeatureService;
            _notificationService = notificationService;
            _businessDateTimeProvider = businessDateTimeProvider;
            _logger = logger;
        }

        [HttpGet("Inbox")]
        public async Task<IActionResult> Inbox(string date, int? funcionarioId, CancellationToken cancellationToken)
        {
            if (!TryParseLocalDate(date, out var parsedDate))
            {
                return BadRequest("La fecha solicitada no es valida.");
            }

            var hasAddon = await _tenantWhatsAppFeatureService.HasWhatsAppAddonAsync(cancellationToken);
            if (!hasAddon)
            {
                return Forbid();
            }

            if (!await ValidateFuncionarioFilterAsync(funcionarioId, cancellationToken))
            {
                return BadRequest("El funcionario solicitado no es valido.");
            }

            var whatsAppEnabled = await _tenantWhatsAppFeatureService
                .IsWhatsAppEnabledForCurrentTenantAsync(cancellationToken);

            var inbox = await _inboxService.GetInboxAsync(parsedDate, funcionarioId, whatsAppEnabled, cancellationToken);
            return Ok(inbox);
        }

        [HttpGet("FollowUp")]
        public async Task<IActionResult> FollowUp(
            string? range,
            string? status,
            int? funcionarioId,
            string? from,
            string? to,
            CancellationToken cancellationToken)
        {
            var hasAddon = await _tenantWhatsAppFeatureService.HasWhatsAppAddonAsync(cancellationToken);
            if (!hasAddon)
            {
                return Forbid();
            }

            if (!await ValidateFuncionarioFilterAsync(funcionarioId, cancellationToken))
            {
                return BadRequest("El funcionario solicitado no es valido.");
            }

            var rangeKey = string.IsNullOrWhiteSpace(range) ? "5d" : range.Trim().ToLowerInvariant();
            var nowLocal = _businessDateTimeProvider.Now();
            var today = nowLocal.Date;

            DateTime fromInclusive;
            DateTime toExclusive;

            switch (rangeKey)
            {
                case "hoy":
                    fromInclusive = nowLocal;
                    toExclusive = today.AddDays(1);
                    break;
                case "24h":
                    fromInclusive = nowLocal;
                    toExclusive = nowLocal.AddHours(24);
                    break;
                case "3d":
                    fromInclusive = nowLocal;
                    toExclusive = today.AddDays(3);
                    break;
                case "7d":
                    fromInclusive = nowLocal;
                    toExclusive = today.AddDays(7);
                    break;
                case "custom":
                    if (!TryParseLocalDate(from, out var requestedFrom) || !TryParseLocalDate(to, out var toInclusive))
                    {
                        return BadRequest("El rango personalizado solicitado no es valido.");
                    }

                    fromInclusive = requestedFrom.Date < nowLocal ? nowLocal : requestedFrom.Date;
                    toExclusive = toInclusive.AddDays(1);
                    if (toExclusive <= requestedFrom.Date)
                    {
                        return BadRequest("El rango personalizado solicitado no es valido.");
                    }

                    if (toExclusive < fromInclusive)
                    {
                        toExclusive = fromInclusive;
                    }

                    // Limita rangos personalizados excesivos para proteger la consulta.
                    if ((toExclusive - fromInclusive).TotalDays > 92)
                    {
                        toExclusive = fromInclusive.AddDays(92);
                    }
                    rangeKey = "custom";
                    break;
                case "5d":
                default:
                    rangeKey = "5d";
                    fromInclusive = nowLocal;
                    toExclusive = today.AddDays(5);
                    break;
            }

            var whatsAppEnabled = await _tenantWhatsAppFeatureService
                .IsWhatsAppEnabledForCurrentTenantAsync(cancellationToken);

            var followUp = await _inboxService.GetFollowUpAsync(
                fromInclusive,
                toExclusive,
                funcionarioId,
                status,
                rangeKey,
                whatsAppEnabled,
                cancellationToken);

            _logger.LogDebug(
                "Endpoint WhatsApp FollowUp resuelto. RangeKey {RangeKey}. Status {Status}. FuncionarioId {FuncionarioId}. BusinessNow {BusinessNow:yyyy-MM-dd HH:mm}. From {From:yyyy-MM-dd HH:mm}. ToExclusive {ToExclusive:yyyy-MM-dd HH:mm}. Items {ItemsCount}.",
                rangeKey,
                status,
                funcionarioId,
                nowLocal,
                fromInclusive,
                toExclusive,
                followUp.Items.Count);

            return Ok(followUp);
        }

        [HttpGet("Chat/{citaId:int}")]
        public async Task<IActionResult> Chat(int citaId, CancellationToken cancellationToken)
        {
            if (citaId <= 0)
            {
                return NotFound();
            }

            var hasAddon = await _tenantWhatsAppFeatureService.HasWhatsAppAddonAsync(cancellationToken);
            if (!hasAddon)
            {
                return Forbid();
            }

            var logs = await _inboxService.GetCitaChatAsync(citaId, cancellationToken);
            return logs is null ? NotFound() : Ok(logs);
        }

        [HttpPost("Send/{citaId:int}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Send(int citaId, CancellationToken cancellationToken)
        {
            if (citaId <= 0)
            {
                return NotFound();
            }

            var whatsAppEnabled = await _tenantWhatsAppFeatureService
                .IsWhatsAppEnabledForCurrentTenantAsync(cancellationToken);

            if (!whatsAppEnabled)
            {
                return BadRequest("WhatsApp no está configurado para este negocio.");
            }

            try
            {
                // El servicio de notificación aplica idempotencia, consentimiento,
                // límites del tenant y filtrado por tenant (la cita debe pertenecer al tenant actual).
                await _notificationService.SendAppointmentConfirmationAsync(citaId, cancellationToken);
                return Ok(new { success = true });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enviando confirmacion WhatsApp manual. CitaId {CitaId}.", citaId);
                return BadRequest("No fue posible enviar el mensaje en este momento. Intente nuevamente.");
            }
        }

        private static bool TryParseLocalDate(string? value, out DateTime parsedDate) =>
            DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out parsedDate);

        private async Task<bool> ValidateFuncionarioFilterAsync(int? funcionarioId, CancellationToken cancellationToken)
        {
            if (!funcionarioId.HasValue)
            {
                return true;
            }

            return await _inboxService.FuncionarioExistsForCurrentTenantAsync(
                funcionarioId.Value,
                cancellationToken);
        }
    }
}
