using System.Globalization;
using System.Text;

namespace LuxuryApp.Services.WhatsApp
{
    public enum WhatsAppReplyAction
    {
        Unknown,
        Confirm,
        Cancel
    }

    /// <summary>
    /// Reconoce si un mensaje entrante es una ACCION conocida sobre una cita (confirmar/cancelar).
    ///
    /// <para>
    /// Es el unico lugar donde vive ese vocabulario. Lo consultan los dos consumidores del webhook:
    /// el flujo de respuestas de citas, para aplicar la accion, y la respuesta automatica neutral,
    /// para NO contestarle a alguien que solo pulso un boton del template. Si estuviera duplicado,
    /// los dos podrian discrepar y el cliente recibiria "este numero no tiene atencion al cliente"
    /// justo despues de confirmar su cita.
    /// </para>
    ///
    /// <para>
    /// No mira solo el texto visible: Meta entrega los botones de plantilla como
    /// <c>button.text</c>/<c>button.payload</c> y las respuestas interactivas como
    /// <c>interactive.button_reply</c> o <c>interactive.list_reply</c> (id y titulo). Se evalua
    /// cualquiera de esos campos.
    /// </para>
    /// </summary>
    public static class WhatsAppAppointmentReplyResolver
    {
        private static readonly HashSet<string> ConfirmTokens = new(StringComparer.Ordinal)
        {
            "1", "confirmar", "confirmo", "confirmado", "si", "confirmar_cita",
            "confirm", "confirm_appointment", "confirmar_reserva"
        };

        private static readonly HashSet<string> CancelTokens = new(StringComparer.Ordinal)
        {
            "2", "cancelar", "cancelo", "cancelado", "cancelar_cita",
            "cancel", "cancel_appointment", "cancelar_reserva"
        };

        public static WhatsAppReplyAction Resolve(InboundWhatsAppMessage inboundMessage)
        {
            ArgumentNullException.ThrowIfNull(inboundMessage);

            var values = new[]
            {
                inboundMessage.Text,
                inboundMessage.ButtonText,
                inboundMessage.ButtonPayload,
                inboundMessage.InteractiveButtonId,
                inboundMessage.InteractiveButtonTitle
            };

            foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var token = NormalizeToken(value!);

                if (ConfirmTokens.Contains(token))
                {
                    return WhatsAppReplyAction.Confirm;
                }

                if (CancelTokens.Contains(token))
                {
                    return WhatsAppReplyAction.Cancel;
                }
            }

            return WhatsAppReplyAction.Unknown;
        }

        /// <summary>
        /// Un mensaje que el sistema ya entiende como accion sobre una cita. La respuesta automatica
        /// neutral es un fallback para mensajes libres: sobre estos NO debe ejecutarse.
        /// </summary>
        public static bool IsKnownAppointmentCommand(InboundWhatsAppMessage inboundMessage) =>
            Resolve(inboundMessage) != WhatsAppReplyAction.Unknown;

        internal static string NormalizeToken(string value)
        {
            var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);

            foreach (var character in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(character);
                }
            }

            return builder
                .ToString()
                .Normalize(NormalizationForm.FormC)
                .Replace(" ", "_", StringComparison.Ordinal)
                .Replace("-", "_", StringComparison.Ordinal);
        }
    }
}
