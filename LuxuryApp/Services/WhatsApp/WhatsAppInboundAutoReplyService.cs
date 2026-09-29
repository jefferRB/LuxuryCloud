using System.Text.Json;
using LuxuryApp.Models.WhatsApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.WhatsApp
{
    /// <inheritdoc cref="IWhatsAppInboundAutoReplyService"/>
    public sealed class WhatsAppInboundAutoReplyService : IWhatsAppInboundAutoReplyService
    {
        /// <summary>
        /// Texto exacto de la respuesta. NO es una plantilla de Meta: como la conversacion la
        /// inicio la persona, cae dentro de la ventana de servicio de 24 h y se envia como texto
        /// libre. Es neutral a proposito: no nombra negocios, telefonos ni reservas.
        /// </summary>
        public const string NeutralReplyText =
            "Hola 👋 Soy el asistente automático de LuxuryCloud.\n\n" +
            "Este número se utiliza únicamente para notificaciones automáticas y no cuenta con atención al cliente.\n\n" +
            "Para consultas, cambios o reprogramaciones, comunícate directamente con el negocio donde realizaste tu reserva utilizando su número habitual.";

        /// <summary>
        /// Tipos que NO son un mensaje escrito por la persona: <c>system</c> son avisos de la
        /// plataforma (cambio de numero), <c>unsupported</c> es contenido que Meta no pudo entregar
        /// y <c>reaction</c> es un emoji sobre un mensaje nuestro, no una consulta.
        /// </summary>
        private static readonly HashSet<string> NonRepliableMessageTypes =
            new(StringComparer.OrdinalIgnoreCase) { "system", "unsupported", "reaction" };

        private readonly ApplicationDbContext _context;
        private readonly IMetaWhatsAppClient _metaClient;
        private readonly IOptionsMonitor<MetaWhatsAppOptions> _options;
        private readonly ILogger<WhatsAppInboundAutoReplyService> _logger;

        public WhatsAppInboundAutoReplyService(
            ApplicationDbContext context,
            IMetaWhatsAppClient metaClient,
            IOptionsMonitor<MetaWhatsAppOptions> options,
            ILogger<WhatsAppInboundAutoReplyService> logger)
        {
            _context = context;
            _metaClient = metaClient;
            _options = options;
            _logger = logger;
        }

        public async Task ProcessInboundMessagesAsync(
            JsonElement payload,
            CancellationToken cancellationToken = default)
        {
            // Solo se leen mensajes de value.messages[]. Los acuses de nuestros propios envios
            // (sent/delivered/read/failed) viven en value.statuses[] y este parser ni los mira:
            // ahi se corta el bucle "respondemos -> Meta nos avisa -> volveriamos a responder".
            var inboundMessages = MetaWhatsAppWebhookPayloadParser.ExtractInboundMessages(payload);
            if (inboundMessages.Count == 0)
            {
                _logger.LogDebug("Webhook WhatsApp sin mensajes entrantes: no corresponde respuesta automatica.");
                return;
            }

            foreach (var inboundMessage in inboundMessages)
            {
                try
                {
                    await ProcessSingleAsync(inboundMessage, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Error procesando la respuesta automatica de un mensaje entrante. InboundMessageId {InboundMessageId}.",
                        inboundMessage.MessageId);
                }
            }
        }

        private async Task ProcessSingleAsync(
            InboundWhatsAppMessage inboundMessage,
            CancellationToken cancellationToken)
        {
            if (!ShouldReply(inboundMessage, out var skipReason))
            {
                _logger.LogInformation(
                    "Respuesta automatica omitida: el evento no es un mensaje de una persona. InboundMessageId {InboundMessageId}. Motivo {SkipReason}.",
                    inboundMessage.MessageId,
                    skipReason);
                return;
            }

            var senderPhone = _metaClient.NormalizePhoneNumber(inboundMessage.From);
            if (senderPhone is null)
            {
                _logger.LogInformation(
                    "Respuesta automatica omitida: remitente sin telefono valido. InboundMessageId {InboundMessageId}.",
                    inboundMessage.MessageId);
                return;
            }

            _logger.LogInformation(
                "Mensaje entrante recibido en el numero central. InboundMessageId {InboundMessageId}. MessageType {MessageType}.",
                inboundMessage.MessageId,
                inboundMessage.MessageType);

            // Reserva ANTES de responder: si Meta reenvia el webhook, el indice unico sobre
            // InboundMessageId rechaza la segunda fila y nadie recibe dos respuestas.
            var claim = new WhatsAppInboundAutoReply
            {
                InboundMessageId = inboundMessage.MessageId,
                SenderPhoneE164 = senderPhone,
                MessageType = Truncate(inboundMessage.MessageType, 40),
                Status = WhatsAppMessageStatuses.Pending,
                ReceivedAtUtc = DateTime.UtcNow
            };

            _context.WhatsAppInboundAutoReplies.Add(claim);

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                _context.Entry(claim).State = EntityState.Detached;
                _logger.LogInformation(
                    "Respuesta automatica duplicada ignorada: el mensaje entrante ya fue atendido. InboundMessageId {InboundMessageId}.",
                    inboundMessage.MessageId);
                return;
            }

            if (!_options.CurrentValue.Enabled)
            {
                claim.Status = WhatsAppMessageStatuses.Ignored;
                claim.ErrorCode = WhatsAppErrorCodes.ConfigurationDisabled;
                claim.ErrorMessage = "Meta WhatsApp esta deshabilitado globalmente.";
                claim.ProcessedAtUtc = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Respuesta automatica omitida: integracion Meta deshabilitada. InboundMessageId {InboundMessageId}.",
                    inboundMessage.MessageId);
                return;
            }

            var sendResult = await _metaClient.SendTextMessageAsync(senderPhone, NeutralReplyText, cancellationToken);

            if (sendResult.Success && !string.IsNullOrWhiteSpace(sendResult.MetaMessageId))
            {
                claim.Status = WhatsAppMessageStatuses.Sent;
                claim.ReplyMetaMessageId = Truncate(sendResult.MetaMessageId, 128);
                claim.ProcessedAtUtc = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Respuesta automatica enviada. InboundMessageId {InboundMessageId}. ReplyMetaMessageId {ReplyMetaMessageId}.",
                    inboundMessage.MessageId,
                    sendResult.MetaMessageId);
                return;
            }

            claim.Status = WhatsAppMessageStatuses.Failed;
            claim.ErrorCode = Truncate(sendResult.ErrorCode, 80);
            claim.ErrorMessage = Truncate(sendResult.ErrorMessage, 1000);
            claim.ProcessedAtUtc = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "Fallo la respuesta automatica al mensaje entrante. InboundMessageId {InboundMessageId}. ErrorCode {ErrorCode}.",
                inboundMessage.MessageId,
                sendResult.ErrorCode);
        }

        /// <summary>
        /// Decide si el evento representa un mensaje real de una persona. No interpreta intencion:
        /// cualquier mensaje valido recibe la misma respuesta.
        /// </summary>
        private static bool ShouldReply(InboundWhatsAppMessage inboundMessage, out string skipReason)
        {
            if (string.IsNullOrWhiteSpace(inboundMessage.MessageId))
            {
                skipReason = "SinMessageId";
                return false;
            }

            if (string.IsNullOrWhiteSpace(inboundMessage.From))
            {
                skipReason = "SinRemitente";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(inboundMessage.MessageType) &&
                NonRepliableMessageTypes.Contains(inboundMessage.MessageType!))
            {
                skipReason = $"TipoNoRespondible:{inboundMessage.MessageType}";
                return false;
            }

            // El auto-reply neutral es el FALLBACK de los mensajes libres. Confirmar/Cancelar (boton
            // de plantilla, respuesta interactiva o los textos 1/2) ya los procesa el flujo de citas:
            // contestarles "este numero no tiene atencion al cliente" seria incorrecto. El
            // vocabulario se consulta al mismo resolver que usa ese flujo, nunca duplicado aca.
            if (WhatsAppAppointmentReplyResolver.IsKnownAppointmentCommand(inboundMessage))
            {
                skipReason = $"AccionDeCita:{WhatsAppAppointmentReplyResolver.Resolve(inboundMessage)}";
                return false;
            }

            // Red de seguridad ante un eventual eco de nuestros propios envios: si el remitente es
            // el propio numero del negocio, no nos respondemos a nosotros mismos.
            if (!string.IsNullOrWhiteSpace(inboundMessage.BusinessDisplayPhoneNumber) &&
                SameDigits(inboundMessage.From, inboundMessage.BusinessDisplayPhoneNumber))
            {
                skipReason = "EcoDelPropioNumero";
                return false;
            }

            skipReason = string.Empty;
            return true;
        }

        private static bool SameDigits(string? left, string? right)
        {
            static string OnlyDigits(string? value) =>
                value is null ? string.Empty : new string(value.Where(char.IsDigit).ToArray());

            var leftDigits = OnlyDigits(left);
            return leftDigits.Length > 0 && string.Equals(leftDigits, OnlyDigits(right), StringComparison.Ordinal);
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }
    }
}
