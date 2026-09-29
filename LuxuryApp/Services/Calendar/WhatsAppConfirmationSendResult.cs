using LuxuryApp.Services.WhatsApp;

namespace LuxuryApp.Services.Calendar
{
    /// <summary>
    /// Resultado del intento de enviar la confirmación de WhatsApp de forma inmediata
    /// (por ejemplo, al aprobar una reserva online). Refleja honestamente lo que ocurrió
    /// para que la UI pueda informar al negocio sin mentir sobre el estado del envío.
    /// </summary>
    public enum WhatsAppConfirmationOutcome
    {
        /// <summary>Se envió la confirmación en este intento.</summary>
        Sent,

        /// <summary>Ya existía una confirmación enviada; no se reenvió (idempotente).</summary>
        AlreadySent,

        /// <summary>Quedó encolada y se enviará en el próximo ciclo (p. ej. horas de silencio).</summary>
        Pending,

        /// <summary>No se envió por configuración/consentimiento/teléfono/límite/plan.</summary>
        Skipped,

        /// <summary>Meta rechazó el mensaje.</summary>
        Failed
    }

    /// <param name="Reason">
    /// Motivo semantico. La UI decide con esto, no releyendo codigos de error: sin complemento
    /// contratado (<see cref="WhatsAppNotificationReason.AddonInactive"/>) no se muestra nada.
    /// </param>
    public sealed record WhatsAppConfirmationSendResult(
        WhatsAppConfirmationOutcome Outcome,
        string Message,
        string? ErrorCode = null,
        WhatsAppNotificationReason Reason = WhatsAppNotificationReason.Other)
    {
        /// <summary>La confirmación quedó efectivamente enviada (ahora o previamente).</summary>
        public bool WasSent => Outcome is WhatsAppConfirmationOutcome.Sent or WhatsAppConfirmationOutcome.AlreadySent;
    }
}
