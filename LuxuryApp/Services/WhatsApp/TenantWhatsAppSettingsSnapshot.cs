using LuxuryApp.Models.WhatsApp;

namespace LuxuryApp.Services.WhatsApp
{
    /// <summary>
    /// Vista de solo lectura de la configuración WhatsApp de un tenant.
    /// Incluye los toggles maestros (compatibilidad) y la programación configurable
    /// de confirmaciones/recordatorios (horas relativas, lotes diarios, horas de silencio).
    /// </summary>
    public sealed record TenantWhatsAppSettingsSnapshot
    {
        public Guid TenantId { get; init; }
        public bool Exists { get; init; }
        public bool IsEnabled { get; init; }
        public bool SendConfirmationOnCreate { get; init; }
        public bool SendReminderThreeHoursBefore { get; init; }
        public int DailyMessageLimit { get; init; }
        public string TimeZoneId { get; init; } = TenantWhatsAppSettings.DefaultTimeZoneId;
        public string? Notes { get; init; }

        // Confirmaciones.
        public string ConfirmationScheduleMode { get; init; } = WhatsAppConfirmationScheduleModes.RelativeBeforeAppointment;
        public int ConfirmationHoursBefore { get; init; } = TenantWhatsAppSettings.DefaultConfirmationHoursBefore;
        public TimeOnly? ConfirmationBatchTime { get; init; }
        public string ConfirmationBatchTarget { get; init; } = WhatsAppConfirmationBatchTargets.TomorrowAllDay;
        public TimeOnly? ConfirmationMorningStart { get; init; } = TenantWhatsAppSettings.DefaultMorningStart;
        public TimeOnly? ConfirmationMorningEnd { get; init; } = TenantWhatsAppSettings.DefaultMorningEnd;
        public bool SendConfirmationImmediatelyIfInsideWindow { get; init; } = true;

        // Recordatorios.
        public string ReminderScheduleMode { get; init; } = WhatsAppReminderScheduleModes.RelativeBeforeAppointment;
        public int ReminderHoursBefore { get; init; } = TenantWhatsAppSettings.DefaultReminderHoursBefore;
        public TimeOnly? ReminderBatchTime { get; init; }
        public string ReminderBatchTarget { get; init; } = WhatsAppReminderBatchTargets.SameDayRemaining;
        public int ReminderLookAheadHours { get; init; } = TenantWhatsAppSettings.DefaultReminderHoursBefore;
        public bool SendReminderImmediatelyIfInsideWindow { get; init; } = true;

        // Horas de silencio.
        public bool QuietHoursEnabled { get; init; }
        public TimeOnly? QuietHoursStart { get; init; }
        public TimeOnly? QuietHoursEnd { get; init; }

        // Control de ejecución de lotes diarios (una corrida por día local).
        public DateOnly? LastConfirmationBatchRunDateLocal { get; init; }
        public DateOnly? LastReminderBatchRunDateLocal { get; init; }

        public static TenantWhatsAppSettingsSnapshot CreateDefault(Guid tenantId) =>
            new()
            {
                TenantId = tenantId,
                Exists = false,
                IsEnabled = false,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = TenantWhatsAppSettings.DefaultDailyMessageLimit,
                TimeZoneId = TenantWhatsAppSettings.DefaultTimeZoneId,
                Notes = null
            };

        public static TenantWhatsAppSettingsSnapshot CreateEnabledDefaultsForAddon(
            Guid tenantId,
            int dailyMessageLimit) =>
            new()
            {
                TenantId = tenantId,
                Exists = false,
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = dailyMessageLimit > 0 ? dailyMessageLimit : TenantWhatsAppSettings.DefaultDailyMessageLimit,
                TimeZoneId = TenantWhatsAppSettings.DefaultTimeZoneId,
                Notes = null
            };

        public static TenantWhatsAppSettingsSnapshot FromEntity(
            TenantWhatsAppSettings settings,
            int effectiveDailyLimit) =>
            new()
            {
                TenantId = settings.TenantId,
                Exists = true,
                IsEnabled = settings.IsEnabled,
                SendConfirmationOnCreate = settings.SendConfirmationOnCreate,
                SendReminderThreeHoursBefore = settings.SendReminderThreeHoursBefore,
                DailyMessageLimit = effectiveDailyLimit,
                TimeZoneId = string.IsNullOrWhiteSpace(settings.TimeZoneId)
                    ? TenantWhatsAppSettings.DefaultTimeZoneId
                    : settings.TimeZoneId,
                Notes = settings.Notes,
                ConfirmationScheduleMode = string.IsNullOrWhiteSpace(settings.ConfirmationScheduleMode)
                    ? WhatsAppConfirmationScheduleModes.RelativeBeforeAppointment
                    : settings.ConfirmationScheduleMode,
                ConfirmationHoursBefore = settings.ConfirmationHoursBefore > 0
                    ? settings.ConfirmationHoursBefore
                    : TenantWhatsAppSettings.DefaultConfirmationHoursBefore,
                ConfirmationBatchTime = settings.ConfirmationBatchTime,
                ConfirmationBatchTarget = string.IsNullOrWhiteSpace(settings.ConfirmationBatchTarget)
                    ? WhatsAppConfirmationBatchTargets.TomorrowAllDay
                    : settings.ConfirmationBatchTarget,
                ConfirmationMorningStart = settings.ConfirmationMorningStart ?? TenantWhatsAppSettings.DefaultMorningStart,
                ConfirmationMorningEnd = settings.ConfirmationMorningEnd ?? TenantWhatsAppSettings.DefaultMorningEnd,
                SendConfirmationImmediatelyIfInsideWindow = settings.SendConfirmationImmediatelyIfInsideWindow,
                ReminderScheduleMode = string.IsNullOrWhiteSpace(settings.ReminderScheduleMode)
                    ? WhatsAppReminderScheduleModes.RelativeBeforeAppointment
                    : settings.ReminderScheduleMode,
                ReminderHoursBefore = settings.ReminderHoursBefore > 0
                    ? settings.ReminderHoursBefore
                    : TenantWhatsAppSettings.DefaultReminderHoursBefore,
                ReminderBatchTime = settings.ReminderBatchTime,
                ReminderBatchTarget = string.IsNullOrWhiteSpace(settings.ReminderBatchTarget)
                    ? WhatsAppReminderBatchTargets.SameDayRemaining
                    : settings.ReminderBatchTarget,
                ReminderLookAheadHours = settings.ReminderLookAheadHours > 0
                    ? settings.ReminderLookAheadHours
                    : TenantWhatsAppSettings.DefaultReminderHoursBefore,
                SendReminderImmediatelyIfInsideWindow = settings.SendReminderImmediatelyIfInsideWindow,
                QuietHoursEnabled = settings.QuietHoursEnabled,
                QuietHoursStart = settings.QuietHoursStart,
                QuietHoursEnd = settings.QuietHoursEnd,
                LastConfirmationBatchRunDateLocal = settings.LastConfirmationBatchRunDateLocal,
                LastReminderBatchRunDateLocal = settings.LastReminderBatchRunDateLocal
            };
    }
}
