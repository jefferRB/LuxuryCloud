namespace LuxuryApp.Models.WhatsApp
{
    /// <summary>
    /// Modelo enlazado desde la UI "Automatizaciones WhatsApp" (guardado AJAX).
    /// Fase 1: programación relativa (horas antes + envío inmediato).
    /// Fase 2: lote diario a hora fija (confirmaciones y recordatorios).
    /// </summary>
    public sealed class WhatsAppAutomationRequest
    {
        // ── Confirmaciones ──
        public bool ConfirmationsEnabled { get; set; }

        /// "relative" | "batch"
        public string ConfirmationMode { get; set; } = "relative";

        public int ConfirmationHoursBefore { get; set; } = TenantWhatsAppSettings.DefaultConfirmationHoursBefore;

        public bool SendConfirmationImmediatelyIfInsideWindow { get; set; } = true;

        public TimeOnly? ConfirmationBatchTime { get; set; }

        public string ConfirmationBatchTarget { get; set; } = WhatsAppConfirmationBatchTargets.TomorrowAllDay;

        public TimeOnly? ConfirmationMorningStart { get; set; }

        public TimeOnly? ConfirmationMorningEnd { get; set; }

        // ── Recordatorios ──
        public bool RemindersEnabled { get; set; }

        /// "relative" | "batch"
        public string ReminderMode { get; set; } = "relative";

        public int ReminderHoursBefore { get; set; } = TenantWhatsAppSettings.DefaultReminderHoursBefore;

        public bool SendReminderImmediatelyIfInsideWindow { get; set; } = true;

        public TimeOnly? ReminderBatchTime { get; set; }

        public string ReminderBatchTarget { get; set; } = WhatsAppReminderBatchTargets.SameDayRemaining;

        // ── Horas de silencio ──
        public bool QuietHoursEnabled { get; set; }

        public TimeOnly? QuietHoursStart { get; set; }

        public TimeOnly? QuietHoursEnd { get; set; }
    }
}
