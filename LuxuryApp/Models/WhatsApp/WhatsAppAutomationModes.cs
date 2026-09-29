namespace LuxuryApp.Models.WhatsApp
{
    /// <summary>
    /// Modos de programación de confirmaciones por tenant.
    /// Fase 1 implementa <see cref="RelativeBeforeAppointment"/> y <see cref="Disabled"/>.
    /// Los modos batch quedan persistidos pero se activan en una fase posterior.
    /// </summary>
    public static class WhatsAppConfirmationScheduleModes
    {
        public const string RelativeBeforeAppointment = "RelativeBeforeAppointment";
        public const string DailyBatchPreviousDay = "DailyBatchPreviousDay";
        public const string DailyBatchSameDay = "DailyBatchSameDay";
        public const string Disabled = "Disabled";

        public static bool IsValid(string? value) =>
            value is RelativeBeforeAppointment or DailyBatchPreviousDay or DailyBatchSameDay or Disabled;
    }

    public static class WhatsAppReminderScheduleModes
    {
        public const string RelativeBeforeAppointment = "RelativeBeforeAppointment";
        public const string DailyBatchSameDay = "DailyBatchSameDay";
        public const string Disabled = "Disabled";

        public static bool IsValid(string? value) =>
            value is RelativeBeforeAppointment or DailyBatchSameDay or Disabled;
    }

    public static class WhatsAppConfirmationBatchTargets
    {
        public const string TomorrowAllDay = "TomorrowAllDay";
        public const string TomorrowMorning = "TomorrowMorning";
        public const string SameDayRemaining = "SameDayRemaining";
        public const string Next24Hours = "Next24Hours";

        public static bool IsValid(string? value) =>
            value is TomorrowAllDay or TomorrowMorning or SameDayRemaining or Next24Hours;
    }

    public static class WhatsAppReminderBatchTargets
    {
        public const string SameDayRemaining = "SameDayRemaining";
        public const string NextAppointmentsToday = "NextAppointmentsToday";
        public const string NextXHours = "NextXHours";

        public static bool IsValid(string? value) =>
            value is SameDayRemaining or NextAppointmentsToday or NextXHours;
    }
}
