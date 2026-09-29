using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using LuxuryApp.Models.SaaS;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.WhatsApp
{
    public sealed class TenantWhatsAppSettings : ITenantEntity
    {
        public const int DefaultDailyMessageLimit = 30;
        public const string DefaultTimeZoneId = "America/Costa_Rica";
        public const int DefaultConfirmationHoursBefore = 24;
        public const int DefaultReminderHoursBefore = 3;
        public static readonly TimeOnly DefaultMorningStart = new(4, 0);
        public static readonly TimeOnly DefaultMorningEnd = new(12, 0);

        public Guid Id { get; set; } = Guid.NewGuid();

        [BindNever]
        public Guid TenantId { get; set; }

        public Tenant? Tenant { get; set; }

        public bool IsEnabled { get; set; }

        public bool SendConfirmationOnCreate { get; set; } = true;

        public bool SendReminderThreeHoursBefore { get; set; } = true;

        public int DailyMessageLimit { get; set; } = DefaultDailyMessageLimit;

        [MaxLength(100)]
        public string TimeZoneId { get; set; } = DefaultTimeZoneId;

        // ── Programación de confirmaciones (configurable por tenant) ──
        [MaxLength(40)]
        public string ConfirmationScheduleMode { get; set; } = WhatsAppConfirmationScheduleModes.RelativeBeforeAppointment;

        public int ConfirmationHoursBefore { get; set; } = DefaultConfirmationHoursBefore;

        public TimeOnly? ConfirmationBatchTime { get; set; }

        [MaxLength(30)]
        public string ConfirmationBatchTarget { get; set; } = WhatsAppConfirmationBatchTargets.TomorrowAllDay;

        public TimeOnly? ConfirmationMorningStart { get; set; } = DefaultMorningStart;

        public TimeOnly? ConfirmationMorningEnd { get; set; } = DefaultMorningEnd;

        public bool SendConfirmationImmediatelyIfInsideWindow { get; set; } = true;

        // ── Programación de recordatorios (configurable por tenant) ──
        [MaxLength(40)]
        public string ReminderScheduleMode { get; set; } = WhatsAppReminderScheduleModes.RelativeBeforeAppointment;

        public int ReminderHoursBefore { get; set; } = DefaultReminderHoursBefore;

        public TimeOnly? ReminderBatchTime { get; set; }

        [MaxLength(30)]
        public string ReminderBatchTarget { get; set; } = WhatsAppReminderBatchTargets.SameDayRemaining;

        public int ReminderLookAheadHours { get; set; } = DefaultReminderHoursBefore;

        public bool SendReminderImmediatelyIfInsideWindow { get; set; } = true;

        // ── Horas de silencio ──
        public bool QuietHoursEnabled { get; set; }

        public TimeOnly? QuietHoursStart { get; set; }

        public TimeOnly? QuietHoursEnd { get; set; }

        // ── Control de ejecución de lotes diarios (se usa en fase batch) ──
        public DateOnly? LastConfirmationBatchRunDateLocal { get; set; }

        public DateOnly? LastReminderBatchRunDateLocal { get; set; }

        [MaxLength(2000)]
        public string? Notes { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        [MaxLength(450)]
        public string? UpdatedByUserId { get; set; }
    }
}
