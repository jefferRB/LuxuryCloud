using System.Net;
using System.Text.Json;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.WhatsApp;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    /// <summary>
    /// Respuesta automatica del numero central: neutral, una sola vez por mensaje entrante, y
    /// nunca disparada por los acuses de nuestros propios envios.
    /// </summary>
    public class WhatsAppInboundAutoReplyServiceTests
    {
        private const string NeutralReply =
            "Hola 👋 Soy el asistente automático de LuxuryCloud.\n\n" +
            "Este número se utiliza únicamente para notificaciones automáticas y no cuenta con atención al cliente.\n\n" +
            "Para consultas, cambios o reprogramaciones, comunícate directamente con el negocio donde realizaste tu reserva utilizando su número habitual.";

        [Fact]
        public async Task MensajeDeTexto_RespondeConElTextoNeutralExacto()
        {
            using var fixture = Fixture.Create();

            await fixture.Service.ProcessInboundMessagesAsync(InboundTextPayload("wamid.HOLA1", "50688887777", "Hola"));

            var sent = Assert.Single(fixture.MetaClient.SentTextMessages);
            Assert.Equal("+50650688887777", sent.RecipientPhone);
            Assert.Equal(NeutralReply, sent.Message);

            var log = await fixture.Context.WhatsAppInboundAutoReplies.AsNoTracking().SingleAsync();
            Assert.Equal("wamid.HOLA1", log.InboundMessageId);
            Assert.Equal(WhatsAppMessageStatuses.Sent, log.Status);
        }

        [Fact]
        public async Task RespuestaNeutral_NoMencionaNingunNegocio()
        {
            using var fixture = Fixture.Create();

            await fixture.Service.ProcessInboundMessagesAsync(InboundTextPayload("wamid.HOLA2", "50688887777", "Hola"));

            var sent = Assert.Single(fixture.MetaClient.SentTextMessages);
            Assert.DoesNotContain("@", sent.Message, StringComparison.Ordinal);
            Assert.Contains("LuxuryCloud", sent.Message, StringComparison.Ordinal);
            Assert.Contains("su número habitual", sent.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("sent")]
        [InlineData("delivered")]
        [InlineData("read")]
        [InlineData("failed")]
        public async Task AcusesDeEstado_NoDisparanRespuesta(string status)
        {
            using var fixture = Fixture.Create();

            await fixture.Service.ProcessInboundMessagesAsync(StatusPayload("wamid.OUT1", status));

            Assert.Empty(fixture.MetaClient.SentTextMessages);
            Assert.Empty(await fixture.Context.WhatsAppInboundAutoReplies.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task MismoMessageIdDosVeces_RespondeUnaSolaVez()
        {
            using var fixture = Fixture.Create();
            var payload = InboundTextPayload("wamid.REPETIDO", "50688887777", "Hola");

            await fixture.Service.ProcessInboundMessagesAsync(payload);
            await fixture.Service.ProcessInboundMessagesAsync(payload);

            Assert.Single(fixture.MetaClient.SentTextMessages);
            Assert.Single(await fixture.Context.WhatsAppInboundAutoReplies.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task SinTenantResuelto_RespondeIgualPorqueElMensajeEsNeutral()
        {
            // El webhook es anonimo: no hay tenant en el contexto. La respuesta no depende de eso.
            using var fixture = Fixture.Create(withTenant: false);

            await fixture.Service.ProcessInboundMessagesAsync(InboundTextPayload("wamid.SINTENANT", "50688887777", "Hola"));

            Assert.Single(fixture.MetaClient.SentTextMessages);
            Assert.Equal(WhatsAppMessageStatuses.Sent,
                (await fixture.Context.WhatsAppInboundAutoReplies.AsNoTracking().SingleAsync()).Status);
        }

        [Fact]
        public async Task NoConsultaNiEscribeDatosDeNingunTenant()
        {
            using var fixture = Fixture.Create();

            await fixture.Service.ProcessInboundMessagesAsync(InboundTextPayload("wamid.AISLADO", "50688887777", "Hola"));

            // No toca la bitacora por tenant ni ninguna entidad de negocio.
            Assert.Empty(await fixture.Context.WhatsAppMessageLogs.IgnoreQueryFilters().AsNoTracking().ToListAsync());
            Assert.Empty(await fixture.Context.Citas.IgnoreQueryFilters().AsNoTracking().ToListAsync());
            Assert.Empty(await fixture.Context.Clientes.IgnoreQueryFilters().AsNoTracking().ToListAsync());

            // Y la bitacora que sí escribe no guarda ninguna referencia a un tenant.
            var log = await fixture.Context.WhatsAppInboundAutoReplies.AsNoTracking().SingleAsync();
            Assert.DoesNotContain(
                "TenantId",
                log.GetType().GetProperties().Select(property => property.Name));
        }

        [Theory]
        [InlineData("system")]
        [InlineData("unsupported")]
        [InlineData("reaction")]
        public async Task EventosQueNoSonMensajeDeUnaPersona_NoRespondan(string messageType)
        {
            using var fixture = Fixture.Create();

            await fixture.Service.ProcessInboundMessagesAsync(
                InboundTextPayload("wamid.NOAPLICA", "50688887777", "x", messageType));

            Assert.Empty(fixture.MetaClient.SentTextMessages);
            Assert.Empty(await fixture.Context.WhatsAppInboundAutoReplies.AsNoTracking().ToListAsync());
        }

        [Theory]
        // Boton de plantilla: Meta lo entrega como type "button" con button.text/button.payload.
        [InlineData("button", "Confirmar", "CONFIRMAR_CITA")]
        [InlineData("button", "Cancelar", "CANCELAR_CITA")]
        public async Task BotonDePlantilla_NoRecibeAutoReply(string messageType, string text, string payload)
        {
            using var fixture = Fixture.Create();

            await fixture.Service.ProcessInboundMessagesAsync(
                TemplateButtonPayload("wamid.BOTON", "50688887777", messageType, text, payload));

            Assert.Empty(fixture.MetaClient.SentTextMessages);
            Assert.Empty(await fixture.Context.WhatsAppInboundAutoReplies.AsNoTracking().ToListAsync());
        }

        [Theory]
        // Respuesta interactiva: button_reply (botones) y list_reply (lista).
        [InlineData("button_reply", "confirmar_cita", "Confirmar")]
        [InlineData("button_reply", "cancelar_cita", "Cancelar")]
        [InlineData("list_reply", "confirmar_cita", "Confirmar")]
        [InlineData("list_reply", "cancelar_cita", "Cancelar")]
        public async Task RespuestaInteractiva_NoRecibeAutoReply(string replyKind, string id, string title)
        {
            using var fixture = Fixture.Create();

            await fixture.Service.ProcessInboundMessagesAsync(
                InteractiveReplyPayload("wamid.INTERACTIVO", "50688887777", replyKind, id, title));

            Assert.Empty(fixture.MetaClient.SentTextMessages);
            Assert.Empty(await fixture.Context.WhatsAppInboundAutoReplies.AsNoTracking().ToListAsync());
        }

        [Theory]
        [InlineData("1")]
        [InlineData("2")]
        [InlineData("Confirmar")]
        [InlineData("cancelar")]
        [InlineData("S\u00ed")]
        public async Task TextoQueEsComandoDeCita_NoRecibeAutoReply(string texto)
        {
            using var fixture = Fixture.Create();

            await fixture.Service.ProcessInboundMessagesAsync(
                InboundTextPayload("wamid.COMANDO", "50688887777", texto));

            Assert.Empty(fixture.MetaClient.SentTextMessages);
            Assert.Empty(await fixture.Context.WhatsAppInboundAutoReplies.AsNoTracking().ToListAsync());
        }

        [Fact]
        public void ElResolverReconoceLasMismasAccionesQueElFlujoDeCitas()
        {
            // Un solo vocabulario compartido: si esto cambia, cambian los dos consumidores a la vez.
            Assert.Equal(
                WhatsAppReplyAction.Confirm,
                WhatsAppAppointmentReplyResolver.Resolve(Message(text: "Confirmar")));
            Assert.Equal(
                WhatsAppReplyAction.Cancel,
                WhatsAppAppointmentReplyResolver.Resolve(Message(buttonText: "Cancelar")));
            Assert.Equal(
                WhatsAppReplyAction.Confirm,
                WhatsAppAppointmentReplyResolver.Resolve(Message(interactiveButtonId: "CONFIRMAR_CITA")));
            Assert.Equal(
                WhatsAppReplyAction.Unknown,
                WhatsAppAppointmentReplyResolver.Resolve(Message(text: "Hola")));
        }

        [Fact]
        public async Task EcoDelPropioNumero_NoSeRespondeASiMismo()
        {
            using var fixture = Fixture.Create();

            // El remitente coincide con el numero del negocio (metadata.display_phone_number).
            await fixture.Service.ProcessInboundMessagesAsync(
                InboundTextPayload("wamid.ECO", "50600000000", "Hola", businessDisplayPhoneNumber: "50600000000"));

            Assert.Empty(fixture.MetaClient.SentTextMessages);
        }

        [Fact]
        public async Task SiMetaFalla_QuedaRegistradoYNoLanza()
        {
            using var fixture = Fixture.Create();
            fixture.MetaClient.NextSendResult = MetaWhatsAppSendResult.Failed(
                "131026",
                "Message undeliverable.",
                HttpStatusCode.BadRequest);

            await fixture.Service.ProcessInboundMessagesAsync(InboundTextPayload("wamid.FALLO", "50688887777", "Hola"));

            var log = await fixture.Context.WhatsAppInboundAutoReplies.AsNoTracking().SingleAsync();
            Assert.Equal(WhatsAppMessageStatuses.Failed, log.Status);
            Assert.Equal("131026", log.ErrorCode);
        }

        private static JsonElement InboundTextPayload(
            string messageId,
            string from,
            string text,
            string messageType = "text",
            string businessDisplayPhoneNumber = "50600000000")
        {
            var json = $$"""
            {
              "object": "whatsapp_business_account",
              "entry": [{
                "id": "2000000000000002",
                "changes": [{
                  "field": "messages",
                  "value": {
                    "messaging_product": "whatsapp",
                    "metadata": {
                      "display_phone_number": "{{businessDisplayPhoneNumber}}",
                      "phone_number_id": "1000000000000001"
                    },
                    "contacts": [{ "profile": { "name": "Cliente" }, "wa_id": "{{from}}" }],
                    "messages": [{
                      "from": "{{from}}",
                      "id": "{{messageId}}",
                      "timestamp": "1780000000",
                      "type": "{{messageType}}",
                      "text": { "body": "{{text}}" }
                    }]
                  }
                }]
              }]
            }
            """;

            return JsonDocument.Parse(json).RootElement.Clone();
        }

        private static InboundWhatsAppMessage Message(
            string? text = null,
            string? buttonText = null,
            string? interactiveButtonId = null) =>
            new(
                MessageId: "wamid.TEST",
                From: "50688887777",
                WaId: "50688887777",
                ContextMessageId: null,
                Text: text,
                ButtonText: buttonText,
                ButtonPayload: null,
                InteractiveButtonId: interactiveButtonId,
                InteractiveButtonTitle: null,
                MessageType: "text",
                BusinessDisplayPhoneNumber: "50600000000");

        private static JsonElement TemplateButtonPayload(
            string messageId,
            string from,
            string messageType,
            string text,
            string payload)
        {
            var json = $$"""
            {
              "object": "whatsapp_business_account",
              "entry": [{
                "id": "2000000000000002",
                "changes": [{
                  "field": "messages",
                  "value": {
                    "messaging_product": "whatsapp",
                    "metadata": {
                      "display_phone_number": "50600000000",
                      "phone_number_id": "1000000000000001"
                    },
                    "contacts": [{ "profile": { "name": "Cliente" }, "wa_id": "{{from}}" }],
                    "messages": [{
                      "from": "{{from}}",
                      "id": "{{messageId}}",
                      "timestamp": "1780000000",
                      "type": "{{messageType}}",
                      "context": { "id": "wamid.CONFIRMACION" },
                      "button": { "text": "{{text}}", "payload": "{{payload}}" }
                    }]
                  }
                }]
              }]
            }
            """;

            return JsonDocument.Parse(json).RootElement.Clone();
        }

        private static JsonElement InteractiveReplyPayload(
            string messageId,
            string from,
            string replyKind,
            string id,
            string title)
        {
            var json = $$"""
            {
              "object": "whatsapp_business_account",
              "entry": [{
                "id": "2000000000000002",
                "changes": [{
                  "field": "messages",
                  "value": {
                    "messaging_product": "whatsapp",
                    "metadata": {
                      "display_phone_number": "50600000000",
                      "phone_number_id": "1000000000000001"
                    },
                    "contacts": [{ "profile": { "name": "Cliente" }, "wa_id": "{{from}}" }],
                    "messages": [{
                      "from": "{{from}}",
                      "id": "{{messageId}}",
                      "timestamp": "1780000000",
                      "type": "interactive",
                      "interactive": {
                        "type": "{{replyKind}}",
                        "{{replyKind}}": { "id": "{{id}}", "title": "{{title}}" }
                      }
                    }]
                  }
                }]
              }]
            }
            """;

            return JsonDocument.Parse(json).RootElement.Clone();
        }

        private static JsonElement StatusPayload(string messageId, string status)
        {
            var json = $$"""
            {
              "object": "whatsapp_business_account",
              "entry": [{
                "id": "2000000000000002",
                "changes": [{
                  "field": "messages",
                  "value": {
                    "messaging_product": "whatsapp",
                    "metadata": {
                      "display_phone_number": "50600000000",
                      "phone_number_id": "1000000000000001"
                    },
                    "statuses": [{
                      "id": "{{messageId}}",
                      "status": "{{status}}",
                      "timestamp": "1780000000",
                      "recipient_id": "50688887777"
                    }]
                  }
                }]
              }]
            }
            """;

            return JsonDocument.Parse(json).RootElement.Clone();
        }

        private sealed class Fixture : IDisposable
        {
            private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

            private Fixture(
                ProyectoIdentity.Datos.ApplicationDbContext context,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                RecordingMetaWhatsAppClient metaClient,
                WhatsAppInboundAutoReplyService service)
            {
                Context = context;
                _connection = connection;
                MetaClient = metaClient;
                Service = service;
            }

            public ProyectoIdentity.Datos.ApplicationDbContext Context { get; }
            public RecordingMetaWhatsAppClient MetaClient { get; }
            public WhatsAppInboundAutoReplyService Service { get; }

            public static Fixture Create(bool withTenant = true)
            {
                // El webhook de Meta es anonimo: por defecto se prueba SIN tenant resuelto.
                var tenantProvider = new TestTenantProvider
                {
                    TenantId = withTenant ? Guid.NewGuid() : Guid.Empty
                };

                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
                var metaClient = new RecordingMetaWhatsAppClient();
                var service = new WhatsAppInboundAutoReplyService(
                    context,
                    metaClient,
                    new StaticOptionsMonitor<MetaWhatsAppOptions>(new MetaWhatsAppOptions { Enabled = true }),
                    NullLogger<WhatsAppInboundAutoReplyService>.Instance);

                return new Fixture(context, connection, metaClient, service);
            }

            public void Dispose()
            {
                Context.Dispose();
                _connection.Dispose();
            }
        }

        private sealed record SentTextMessage(string RecipientPhone, string Message);

        private sealed class RecordingMetaWhatsAppClient : IMetaWhatsAppClient
        {
            public List<SentTextMessage> SentTextMessages { get; } = [];

            public MetaWhatsAppSendResult? NextSendResult { get; set; }

            public string? NormalizePhoneNumber(string? phoneNumber) =>
                string.IsNullOrWhiteSpace(phoneNumber)
                    ? null
                    : $"+506{new string(phoneNumber.Where(char.IsDigit).ToArray())}";

            public bool IsValidPhoneNumber(string? phoneNumber) => NormalizePhoneNumber(phoneNumber) is not null;

            public Task<MetaWhatsAppSendResult> SendConfirmationTemplateAsync(
                string recipientPhone,
                string customerName,
                string businessName,
                string appointmentDate,
                string appointmentTime,
                string professionalName,
                CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("La respuesta automatica jamas envia plantillas.");

            public Task<MetaWhatsAppSendResult> SendReminderTemplateAsync(
                string recipientPhone,
                string customerName,
                string businessName,
                string appointmentTime,
                string professionalName,
                CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("La respuesta automatica jamas envia plantillas.");

            public Task<MetaWhatsAppSendResult> SendCancellationTemplateAsync(
                string recipientPhone,
                WhatsAppCancellationTemplateParameters parameters,
                CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("La respuesta automatica jamas envia plantillas.");

            public Task<MetaWhatsAppSendResult> SendTextMessageAsync(
                string recipientPhone,
                string message,
                CancellationToken cancellationToken = default)
            {
                SentTextMessages.Add(new SentTextMessage(recipientPhone, message));

                if (NextSendResult is not null)
                {
                    var configured = NextSendResult;
                    NextSendResult = null;
                    return Task.FromResult(configured);
                }

                return Task.FromResult(MetaWhatsAppSendResult.Succeeded(
                    $"reply-{SentTextMessages.Count}",
                    HttpStatusCode.OK,
                    null));
            }

            public Task<MetaWhatsAppConfigurationDiagnosticResult> TestConfigurationAsync(
                CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();
        }
    }
}
