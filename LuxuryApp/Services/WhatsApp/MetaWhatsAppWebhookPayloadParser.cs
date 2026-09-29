using System.Globalization;
using System.Text.Json;

namespace LuxuryApp.Services.WhatsApp
{
    /// <summary>
    /// Mensaje ENTRANTE (lo escribio una persona hacia el numero de LuxuryCloud). Meta lo entrega
    /// en <c>entry[].changes[].value.messages[]</c>. Los acuses de nuestros propios envios
    /// (sent/delivered/read/failed) viajan en <c>value.statuses[]</c> y NO producen instancias de
    /// este tipo: esa separacion es la primera barrera contra los bucles de respuesta.
    /// </summary>
    public sealed record InboundWhatsAppMessage(
        string MessageId,
        string? From,
        string? WaId,
        string? ContextMessageId,
        string? Text,
        string? ButtonText,
        string? ButtonPayload,
        string? InteractiveButtonId,
        string? InteractiveButtonTitle,
        string? MessageType,
        string? BusinessDisplayPhoneNumber);

    /// <summary>Acuse de estado de un mensaje SALIENTE nuestro.</summary>
    public sealed record MetaWhatsAppStatusUpdate(
        string MessageId,
        string Status,
        DateTime? TimestampUtc,
        string? RecipientPhone,
        string? ErrorCode,
        string? ErrorMessage);

    /// <summary>
    /// Lectura pura del payload del webhook de Meta. Sin estado, sin base de datos y sin efectos:
    /// la usan tanto el flujo de respuestas de citas como la respuesta automatica del numero
    /// central, para que exista un solo parser del contrato de Meta.
    /// </summary>
    public static class MetaWhatsAppWebhookPayloadParser
    {
        public static IReadOnlyList<InboundWhatsAppMessage> ExtractInboundMessages(JsonElement payload)
        {
            var messages = new List<InboundWhatsAppMessage>();

            foreach (var value in EnumerateWebhookValues(payload))
            {
                var contactsByWaId = ExtractContactsByWaId(value);
                var businessDisplayPhoneNumber = TryGetProperty(value, "metadata", out var metadata)
                    ? TryGetString(metadata, "display_phone_number")
                    : null;

                if (!TryGetArray(value, "messages", out var messageElements))
                {
                    continue;
                }

                foreach (var message in messageElements.EnumerateArray())
                {
                    var messageId = TryGetString(message, "id");
                    if (string.IsNullOrWhiteSpace(messageId))
                    {
                        continue;
                    }

                    var from = TryGetString(message, "from");
                    var waId = from is not null && contactsByWaId.TryGetValue(from, out var contactWaId)
                        ? contactWaId
                        : from;

                    var contextMessageId = TryGetProperty(message, "context", out var context)
                        ? TryGetString(context, "id")
                        : null;

                    var text = TryGetProperty(message, "text", out var textElement)
                        ? TryGetString(textElement, "body")
                        : null;

                    string? buttonText = null;
                    string? buttonPayload = null;
                    if (TryGetProperty(message, "button", out var buttonElement))
                    {
                        buttonText = TryGetString(buttonElement, "text");
                        buttonPayload = TryGetString(buttonElement, "payload");
                    }

                    // Meta entrega la eleccion del usuario en dos formas distintas segun el tipo de
                    // interactivo: button_reply (botones) y list_reply (lista). Se leen ambas para no
                    // perder una accion conocida por el formato del payload.
                    string? interactiveButtonId = null;
                    string? interactiveButtonTitle = null;
                    if (TryGetProperty(message, "interactive", out var interactiveElement))
                    {
                        if (TryGetProperty(interactiveElement, "button_reply", out var buttonReplyElement))
                        {
                            interactiveButtonId = TryGetString(buttonReplyElement, "id");
                            interactiveButtonTitle = TryGetString(buttonReplyElement, "title");
                        }
                        else if (TryGetProperty(interactiveElement, "list_reply", out var listReplyElement))
                        {
                            interactiveButtonId = TryGetString(listReplyElement, "id");
                            interactiveButtonTitle = TryGetString(listReplyElement, "title");
                        }
                    }

                    messages.Add(new InboundWhatsAppMessage(
                        messageId,
                        from,
                        waId,
                        contextMessageId,
                        text,
                        buttonText,
                        buttonPayload,
                        interactiveButtonId,
                        interactiveButtonTitle,
                        TryGetString(message, "type"),
                        businessDisplayPhoneNumber));
                }
            }

            return messages;
        }

        public static IReadOnlyList<MetaWhatsAppStatusUpdate> ExtractStatusUpdates(JsonElement payload)
        {
            var statuses = new List<MetaWhatsAppStatusUpdate>();

            foreach (var value in EnumerateWebhookValues(payload))
            {
                if (!TryGetArray(value, "statuses", out var statusElements))
                {
                    continue;
                }

                foreach (var status in statusElements.EnumerateArray())
                {
                    var messageId = TryGetString(status, "id");
                    if (string.IsNullOrWhiteSpace(messageId))
                    {
                        continue;
                    }

                    string? errorCode = null;
                    string? errorMessage = null;
                    if (TryGetArray(status, "errors", out var errors) && errors.GetArrayLength() > 0)
                    {
                        var firstError = errors[0];
                        errorCode = TryGetString(firstError, "code");
                        errorMessage = TryGetString(firstError, "message") ?? TryGetString(firstError, "title");
                    }

                    statuses.Add(new MetaWhatsAppStatusUpdate(
                        messageId,
                        TryGetString(status, "status") ?? string.Empty,
                        TryParseUnixTimestamp(TryGetString(status, "timestamp")),
                        TryGetString(status, "recipient_id"),
                        errorCode,
                        errorMessage));
                }
            }

            return statuses;
        }

        private static IEnumerable<JsonElement> EnumerateWebhookValues(JsonElement payload)
        {
            if (!TryGetArray(payload, "entry", out var entries))
            {
                yield break;
            }

            foreach (var entry in entries.EnumerateArray())
            {
                if (!TryGetArray(entry, "changes", out var changes))
                {
                    continue;
                }

                foreach (var change in changes.EnumerateArray())
                {
                    if (TryGetProperty(change, "value", out var value))
                    {
                        yield return value;
                    }
                }
            }
        }

        private static Dictionary<string, string> ExtractContactsByWaId(JsonElement value)
        {
            var contacts = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!TryGetArray(value, "contacts", out var contactElements))
            {
                return contacts;
            }

            foreach (var contact in contactElements.EnumerateArray())
            {
                var waId = TryGetString(contact, "wa_id");
                if (!string.IsNullOrWhiteSpace(waId))
                {
                    contacts[waId] = waId;
                }
            }

            return contacts;
        }

        private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty(propertyName, out property))
            {
                return true;
            }

            property = default;
            return false;
        }

        private static bool TryGetArray(JsonElement element, string propertyName, out JsonElement property)
        {
            if (TryGetProperty(element, propertyName, out property) &&
                property.ValueKind == JsonValueKind.Array)
            {
                return true;
            }

            property = default;
            return false;
        }

        private static string? TryGetString(JsonElement element, string propertyName)
        {
            if (!TryGetProperty(element, propertyName, out var property))
            {
                return null;
            }

            return property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.Number => property.ToString(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => null
            };
        }

        private static DateTime? TryParseUnixTimestamp(string? timestamp)
        {
            if (!long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}
