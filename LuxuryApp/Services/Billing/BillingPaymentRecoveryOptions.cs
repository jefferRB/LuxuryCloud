namespace LuxuryApp.Services.Billing
{
    /// <summary>
    /// Configuración de la recuperación de pago recurrente (pago fallido → gracia → notificación →
    /// suspensión). Los defaults son SEGUROS para producción inicial: se abren incidentes y se
    /// notifica, pero NO se suspende automáticamente (<see cref="AutoSuspendAfterGrace"/> = false)
    /// para poder observar el comportamiento sin cortar acceso por accidente.
    /// </summary>
    public sealed class BillingPaymentRecoveryOptions
    {
        public const string SectionName = "BillingPaymentRecovery";

        /// <summary>
        /// Kill-switch maestro. En false, un pago fallido conserva SOLO el comportamiento actual
        /// (Morosa + gracia) sin crear incidentes ni notificar, y el worker queda inerte.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Días de gracia tras un pago fallido antes de considerar el incidente vencido.</summary>
        public int GraceDays { get; set; } = 5;

        /// <summary>
        /// Si false (default en producción inicial), al vencer la gracia NO se suspende el acceso:
        /// solo se marca GraceExpired y se alerta (dry-run). En true, se suspende por impago.
        /// </summary>
        public bool AutoSuspendAfterGrace { get; set; } = false;

        /// <summary>Habilita el envío de notificaciones por email (inicio de gracia + recordatorio).</summary>
        public bool SendEmailNotifications { get; set; } = true;

        /// <summary>Horas antes de vencer la gracia para enviar el recordatorio (máx 1 por incidente).</summary>
        public int ReminderBeforeGraceEndsHours { get; set; } = 24;

        /// <summary>Intervalo (minutos) del worker de recuperación. Clamp [5, 120].</summary>
        public int WorkerIntervalMinutes { get; set; } = 30;

        /// <summary>Espera inicial (minutos) del worker antes del primer pase. Clamp [0, 10].</summary>
        public int WorkerInitialDelayMinutes { get; set; } = 1;
    }
}
