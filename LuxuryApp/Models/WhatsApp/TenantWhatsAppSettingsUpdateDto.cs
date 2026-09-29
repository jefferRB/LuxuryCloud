using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.WhatsApp
{
    public sealed class TenantWhatsAppSettingsUpdateDto
    {
        public bool IsEnabled { get; set; }

        public bool SendConfirmationOnCreate { get; set; } = true;

        public bool SendReminderThreeHoursBefore { get; set; } = true;

        [Range(0, int.MaxValue, ErrorMessage = "El limite diario no puede ser negativo.")]
        public int DailyMessageLimit { get; set; } = TenantWhatsAppSettings.DefaultDailyMessageLimit;

        [Required]
        [MaxLength(100)]
        public string TimeZoneId { get; set; } = TenantWhatsAppSettings.DefaultTimeZoneId;

        [MaxLength(2000)]
        public string? Notes { get; set; }

        /// Código de paquete a asignar manual. null/"" = sin cambio; "NONE" = revocar; "WA400"/"WA800"/"WA1200" = asignar.
        [MaxLength(10)]
        public string? AddonCode { get; set; }

        /// <summary>Motivo/nota del acceso manual (obligatorio al otorgar/revocar). Reutilizado como razón.</summary>
        [MaxLength(2000)]
        public string? ManualAssignmentObservation { get; set; }

        // ── Acceso manual/cortesía/canje (solo aplica cuando AddonCode = WA400/WA800/WA1200) ──

        /// <summary>Tipo de acceso manual (cortesía/canje/interno/prueba/otro).</summary>
        public LuxuryApp.Models.SaaS.ManualWhatsAppGrantType ManualGrantType { get; set; } =
            LuxuryApp.Models.SaaS.ManualWhatsAppGrantType.Courtesy;

        /// <summary>Acceso manual permanente (sin vencimiento). Si false, usar ManualGrantExpiresOn.</summary>
        public bool ManualGrantIndefinite { get; set; }

        /// <summary>Fecha de vencimiento del acceso manual temporal (fin del día Costa Rica).</summary>
        [DataType(DataType.Date)]
        public DateTime? ManualGrantExpiresOn { get; set; }

        /// <summary>Confirmación explícita para hacer override sobre un add-on TiloPay activo (no cancela TiloPay).</summary>
        public bool ManualGrantAllowOverride { get; set; }

        // ── Programación de confirmaciones ──
        [MaxLength(40)]
        public string ConfirmationScheduleMode { get; set; } = WhatsAppConfirmationScheduleModes.RelativeBeforeAppointment;

        [Range(1, 168, ErrorMessage = "Las horas de anticipación deben estar entre 1 y 168.")]
        public int ConfirmationHoursBefore { get; set; } = TenantWhatsAppSettings.DefaultConfirmationHoursBefore;

        public TimeOnly? ConfirmationBatchTime { get; set; }

        [MaxLength(30)]
        public string ConfirmationBatchTarget { get; set; } = WhatsAppConfirmationBatchTargets.TomorrowAllDay;

        public TimeOnly? ConfirmationMorningStart { get; set; }

        public TimeOnly? ConfirmationMorningEnd { get; set; }

        public bool SendConfirmationImmediatelyIfInsideWindow { get; set; } = true;

        // ── Programación de recordatorios ──
        [MaxLength(40)]
        public string ReminderScheduleMode { get; set; } = WhatsAppReminderScheduleModes.RelativeBeforeAppointment;

        [Range(1, 168, ErrorMessage = "Las horas de anticipación deben estar entre 1 y 168.")]
        public int ReminderHoursBefore { get; set; } = TenantWhatsAppSettings.DefaultReminderHoursBefore;

        public TimeOnly? ReminderBatchTime { get; set; }

        [MaxLength(30)]
        public string ReminderBatchTarget { get; set; } = WhatsAppReminderBatchTargets.SameDayRemaining;

        [Range(1, 168, ErrorMessage = "Las horas de anticipación deben estar entre 1 y 168.")]
        public int ReminderLookAheadHours { get; set; } = TenantWhatsAppSettings.DefaultReminderHoursBefore;

        public bool SendReminderImmediatelyIfInsideWindow { get; set; } = true;

        // ── Horas de silencio ──
        public bool QuietHoursEnabled { get; set; }

        public TimeOnly? QuietHoursStart { get; set; }

        public TimeOnly? QuietHoursEnd { get; set; }
    }
}
