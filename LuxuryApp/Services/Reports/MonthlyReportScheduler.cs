using LuxuryApp.Models.Reports;
using LuxuryApp.Services.SaaS;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Reports
{
    /// <summary>
    /// Constante de auditoría: identifica los envíos disparados por el scheduler en los logs.
    /// </summary>
    public static class MonthlyReportTriggers
    {
        public const string Scheduler = "system:scheduler";
        public const string Platform = "platform:superadmin";
    }

    public sealed class MonthlyReportScheduler : IMonthlyReportScheduler
    {
        private readonly ApplicationDbContext _context;
        private readonly IMonthlyBusinessReportService _reportService;
        private readonly ITenantCommercialAccessResolver _commercialAccessResolver;
        private readonly IOptionsMonitor<MonthlyReportSchedulerOptions> _options;
        private readonly LuxuryApp.Services.BusinessTime.IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ILogger<MonthlyReportScheduler> _logger;

        public MonthlyReportScheduler(
            ApplicationDbContext context,
            IMonthlyBusinessReportService reportService,
            ITenantCommercialAccessResolver commercialAccessResolver,
            IOptionsMonitor<MonthlyReportSchedulerOptions> options,
            LuxuryApp.Services.BusinessTime.IBusinessDateTimeProvider businessDateTimeProvider,
            ILogger<MonthlyReportScheduler> logger)
        {
            _context = context;
            _reportService = reportService;
            _commercialAccessResolver = commercialAccessResolver;
            _options = options;
            _businessDateTimeProvider = businessDateTimeProvider;
            _logger = logger;
        }

        public async Task<MonthlyReportScheduleOutcome> ProcessTenantAsync(
            Guid tenantId,
            DateTime nowLocal,
            CancellationToken cancellationToken = default)
        {
            // Interruptor maestro: sin él, el scheduler NUNCA envía (anti envío masivo accidental).
            if (!_options.CurrentValue.SchedulerEnabled)
            {
                return MonthlyReportScheduleOutcome.SchedulerDisabled;
            }

            var settings = await _context.TenantMonthlyReportSettings
                .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

            if (settings is null || !settings.IsEnabled)
            {
                return MonthlyReportScheduleOutcome.NotEnabled;
            }

            // ¿Toca hoy? Día exacto configurado y hora ya alcanzada.
            if (nowLocal.Day != settings.SendDayOfMonth || nowLocal.Hour < settings.SendHour)
            {
                return MonthlyReportScheduleOutcome.NotDue;
            }

            // El reporte automático resume el mes calendario ANTERIOR.
            var previous = new DateTime(nowLocal.Year, nowLocal.Month, 1).AddMonths(-1);
            var periodKey = (previous.Year * 100) + previous.Month;

            if (settings.LastAutomaticPeriod == periodKey)
            {
                return MonthlyReportScheduleOutcome.AlreadyProcessed;
            }

            // No enviar a tenants sin acceso comercial vigente. No marca el periodo: reintenta luego.
            var access = await _commercialAccessResolver.ResolveAsync(tenantId, cancellationToken: cancellationToken);
            if (!access.CanAccessApp)
            {
                settings.LastAutomaticRunAt = _businessDateTimeProvider.Now();
                settings.LastAutomaticError = "El negocio no tiene acceso comercial vigente.";
                await _context.SaveChangesAsync(cancellationToken);
                return MonthlyReportScheduleOutcome.NoAccess;
            }

            MonthlyReportSendResult result;
            try
            {
                result = await _reportService.SendMonthlyReportAsync(
                    tenantId,
                    previous.Year,
                    previous.Month,
                    MonthlyReportTriggers.Scheduler,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al enviar el resumen mensual automático del tenant {TenantId}.", tenantId);
                settings.LastAutomaticRunAt = _businessDateTimeProvider.Now();
                settings.LastAutomaticError = Truncate(ex.Message, 500);
                await _context.SaveChangesAsync(cancellationToken);
                return MonthlyReportScheduleOutcome.Failed;
            }

            return await ApplyResultAsync(settings, periodKey, result, cancellationToken);
        }

        private async Task<MonthlyReportScheduleOutcome> ApplyResultAsync(
            TenantMonthlyReportSettings settings,
            int periodKey,
            MonthlyReportSendResult result,
            CancellationToken cancellationToken)
        {
            var now = _businessDateTimeProvider.Now();
            settings.LastAutomaticRunAt = now;

            var outcome = result.Outcome switch
            {
                MonthlyReportSendOutcome.Sent => MonthlyReportScheduleOutcome.Sent,
                MonthlyReportSendOutcome.PartiallySent => MonthlyReportScheduleOutcome.PartiallySent,
                MonthlyReportSendOutcome.Skipped => MonthlyReportScheduleOutcome.Skipped,
                _ => MonthlyReportScheduleOutcome.Failed
            };

            switch (result.Outcome)
            {
                case MonthlyReportSendOutcome.Sent:
                    settings.LastAutomaticSentAt = now;
                    settings.LastAutomaticPeriod = periodKey;
                    settings.LastAutomaticError = null;
                    break;

                case MonthlyReportSendOutcome.Skipped:
                    // Ya estaba enviado (p. ej. envío manual real previo): el periodo queda cerrado.
                    settings.LastAutomaticPeriod = periodKey;
                    settings.LastAutomaticError = null;
                    break;

                case MonthlyReportSendOutcome.PartiallySent:
                    // Algunos fallaron: NO cerramos el periodo para permitir reintento controlado.
                    settings.LastAutomaticSentAt = now;
                    settings.LastAutomaticError = Truncate(result.Message, 500);
                    break;

                default:
                    // Failed / sin destinatarios: se reintenta mientras siga siendo el día configurado.
                    settings.LastAutomaticError = Truncate(result.Message, 500);
                    break;
            }

            await _context.SaveChangesAsync(cancellationToken);
            return outcome;
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }
    }
}
