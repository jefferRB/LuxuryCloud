using System.Net;
using System.Text.Json;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Notifications;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.Calendar;
using LuxuryApp.Services.Notifications;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Services.WhatsApp;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Tests.TenantIsolation
{
    /// <summary>
    /// Pulsar Confirmar/Cancelar en la plantilla SÍ mueve la cita (el flujo de citas lo procesa) y
    /// por eso mismo NO debe recibir la respuesta automática neutral. Las dos mitades se verifican
    /// juntas: el bug era que la acción se procesaba y el fallback contestaba igual.
    /// </summary>
    public class WhatsAppInboundAppointmentActionTests
    {
        [Fact]
        public async Task BotonConfirmar_ConfirmaLaCitaYNoDisparaAutoReply()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaConConfirmacionEnviadaAsync();

            var payload = TemplateButtonPayload("wamid.INBOUND1", "88889999", "Confirmar", "CONFIRMAR_CITA");

            await fixture.Notifications.ProcessInboundReplyAsync(payload);
            await fixture.AutoReply.ProcessInboundMessagesAsync(payload);

            var actualizada = await fixture.Context.Citas.AsNoTracking().SingleAsync(c => c.Id == cita.Id);
            Assert.Equal(WhatsAppConfirmationStates.Confirmada, actualizada.EstadoConfirmacionWhatsApp);

            Assert.Empty(fixture.MetaClient.SentTextMessages);
            Assert.Empty(await fixture.Context.WhatsAppInboundAutoReplies.AsNoTracking().ToListAsync());
        }

        [Fact]
        public async Task BotonCancelar_CancelaLaCitaYNoDisparaAutoReply()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaConConfirmacionEnviadaAsync();

            var payload = TemplateButtonPayload("wamid.INBOUND2", "88889999", "Cancelar", "CANCELAR_CITA");

            await fixture.Notifications.ProcessInboundReplyAsync(payload);
            await fixture.AutoReply.ProcessInboundMessagesAsync(payload);

            var actualizada = await fixture.Context.Citas.AsNoTracking().SingleAsync(c => c.Id == cita.Id);
            Assert.Equal(WhatsAppConfirmationStates.Cancelada, actualizada.EstadoConfirmacionWhatsApp);

            Assert.Empty(fixture.MetaClient.SentTextMessages);
        }

        [Fact]
        public async Task Texto1_ConfirmaLaCitaYNoDisparaAutoReply()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaConConfirmacionEnviadaAsync();

            var payload = TextPayload("wamid.INBOUND3", "88889999", "1");

            await fixture.Notifications.ProcessInboundReplyAsync(payload);
            await fixture.AutoReply.ProcessInboundMessagesAsync(payload);

            var actualizada = await fixture.Context.Citas.AsNoTracking().SingleAsync(c => c.Id == cita.Id);
            Assert.Equal(WhatsAppConfirmationStates.Confirmada, actualizada.EstadoConfirmacionWhatsApp);
            Assert.Empty(fixture.MetaClient.SentTextMessages);
        }

        [Fact]
        public async Task MensajeLibre_NoMueveLaCitaYSiRecibeAutoReply()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaConConfirmacionEnviadaAsync();

            var payload = TextPayload("wamid.INBOUND4", "88889999", "Hola, una consulta");

            await fixture.Notifications.ProcessInboundReplyAsync(payload);
            await fixture.AutoReply.ProcessInboundMessagesAsync(payload);

            var actualizada = await fixture.Context.Citas.AsNoTracking().SingleAsync(c => c.Id == cita.Id);
            Assert.Equal(WhatsAppConfirmationStates.Pendiente, actualizada.EstadoConfirmacionWhatsApp);

            Assert.Single(fixture.MetaClient.SentTextMessages);
        }

        [Fact]
        public async Task WebhookRepetido_NoConfirmaDosVecesNiResponde()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaConConfirmacionEnviadaAsync();

            var payload = TemplateButtonPayload("wamid.INBOUND5", "88889999", "Confirmar", "CONFIRMAR_CITA");

            await fixture.Notifications.ProcessInboundReplyAsync(payload);
            await fixture.AutoReply.ProcessInboundMessagesAsync(payload);
            await fixture.Notifications.ProcessInboundReplyAsync(payload);
            await fixture.AutoReply.ProcessInboundMessagesAsync(payload);

            // Una sola fila entrante registrada y ninguna respuesta automática.
            Assert.Equal(
                1,
                await fixture.Context.WhatsAppMessageLogs
                    .AsNoTracking()
                    .CountAsync(message => message.Direction == WhatsAppMessageDirections.Inbound));
            Assert.Empty(fixture.MetaClient.SentTextMessages);

            var actualizada = await fixture.Context.Citas.AsNoTracking().SingleAsync(c => c.Id == cita.Id);
            Assert.Equal(WhatsAppConfirmationStates.Confirmada, actualizada.EstadoConfirmacionWhatsApp);
        }

        private static JsonElement TemplateButtonPayload(
            string messageId,
            string from,
            string text,
            string payload)
        {
            var json = $$"""
            {
              "entry": [{
                "changes": [{
                  "field": "messages",
                  "value": {
                    "metadata": { "display_phone_number": "50600000000", "phone_number_id": "1" },
                    "contacts": [{ "wa_id": "{{from}}" }],
                    "messages": [{
                      "from": "{{from}}",
                      "id": "{{messageId}}",
                      "timestamp": "1780000000",
                      "type": "button",
                      "context": { "id": "meta-confirmacion-1" },
                      "button": { "text": "{{text}}", "payload": "{{payload}}" }
                    }]
                  }
                }]
              }]
            }
            """;

            return JsonDocument.Parse(json).RootElement.Clone();
        }

        private static JsonElement TextPayload(string messageId, string from, string text)
        {
            var json = $$"""
            {
              "entry": [{
                "changes": [{
                  "field": "messages",
                  "value": {
                    "metadata": { "display_phone_number": "50600000000", "phone_number_id": "1" },
                    "contacts": [{ "wa_id": "{{from}}" }],
                    "messages": [{
                      "from": "{{from}}",
                      "id": "{{messageId}}",
                      "timestamp": "1780000000",
                      "type": "text",
                      "context": { "id": "meta-confirmacion-1" },
                      "text": { "body": "{{text}}" }
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
            private readonly ServiceProvider _serviceProvider;

            private Fixture(
                Guid tenantId,
                ProyectoIdentity.Datos.ApplicationDbContext context,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                ServiceProvider serviceProvider,
                RecordingMetaWhatsAppClient metaClient,
                CalendarWhatsAppNotificationService notifications,
                WhatsAppInboundAutoReplyService autoReply)
            {
                TenantId = tenantId;
                Context = context;
                _connection = connection;
                _serviceProvider = serviceProvider;
                MetaClient = metaClient;
                Notifications = notifications;
                AutoReply = autoReply;
            }

            public static DateTime FixedNowLocal => new(2026, 5, 26, 10, 30, 0);

            public static DateTime FixedNowUtc =>
                new DateTimeOffset(FixedNowLocal, TimeSpan.FromHours(-6)).UtcDateTime;

            public Guid TenantId { get; }
            public ProyectoIdentity.Datos.ApplicationDbContext Context { get; }
            public RecordingMetaWhatsAppClient MetaClient { get; }
            public CalendarWhatsAppNotificationService Notifications { get; }
            public WhatsAppInboundAutoReplyService AutoReply { get; }

            public static async Task<Fixture> CreateAsync()
            {
                var tenantId = Guid.NewGuid();
                var tenantProvider = new TestTenantProvider { TenantId = tenantId };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

                // ProcessInboundReplyAsync trabaja por scopes de tenant, así que necesita un
                // contenedor real. Todos los scopes comparten la MISMA conexión SQLite.
                var services = new ServiceCollection();
                services.AddSingleton<ITenantExecutionContextAccessor, TenantExecutionContextAccessor>();
                services.AddScoped<ITenantProvider>(sp => new ExecutionScopedTenantProvider(
                    sp.GetRequiredService<ITenantExecutionContextAccessor>()));
                services.AddScoped(sp => new ProyectoIdentity.Datos.ApplicationDbContext(
                    new DbContextOptionsBuilder<ProyectoIdentity.Datos.ApplicationDbContext>()
                        .UseSqlite(connection)
                        .Options,
                    sp.GetRequiredService<ITenantProvider>(),
                    NullLogger<ProyectoIdentity.Datos.ApplicationDbContext>.Instance));
                services.AddScoped<INotificationService, NoOpNotificationService>();
                var serviceProvider = services.BuildServiceProvider();

                var options = new StaticOptionsMonitor<MetaWhatsAppOptions>(new MetaWhatsAppOptions { Enabled = true });
                var cache = new MemoryCache(new MemoryCacheOptions());
                var accessCache = new TenantCommercialAccessCache(cache);
                var businessDateTimeProvider = new FixedBusinessDateTimeProvider(FixedNowLocal);
                var subscriptionService = new SuscripcionService(
                    context,
                    cache,
                    accessCache,
                    businessDateTimeProvider,
                    Options.Create(new TilopayRepeatOptions()),
                    NullLogger<SuscripcionService>.Instance);
                var commercialAccessResolver = new TenantCommercialAccessResolver(
                    context,
                    cache,
                    accessCache,
                    subscriptionService,
                    businessDateTimeProvider);
                var settings = new TenantWhatsAppSettingsService(
                    context,
                    tenantProvider,
                    options,
                    subscriptionService,
                    businessDateTimeProvider,
                    commercialAccessResolver,
                    NullLogger<TenantWhatsAppSettingsService>.Instance);
                var metaClient = new RecordingMetaWhatsAppClient();

                var notifications = new CalendarWhatsAppNotificationService(
                    context,
                    metaClient,
                    options,
                    businessDateTimeProvider,
                    settings,
                    tenantProvider,
                    new TenantDisplayNameService(context, tenantProvider, new HttpContextAccessor()),
                    new TenantExecutionService(
                        serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                        NullLogger<TenantExecutionService>.Instance),
                    NullLogger<CalendarWhatsAppNotificationService>.Instance);

                var autoReply = new WhatsAppInboundAutoReplyService(
                    context,
                    metaClient,
                    options,
                    NullLogger<WhatsAppInboundAutoReplyService>.Instance);

                context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Negocio Inbound", Activo = true });
                await context.SaveChangesAsync();

                return new Fixture(
                    tenantId,
                    context,
                    connection,
                    serviceProvider,
                    metaClient,
                    notifications,
                    autoReply);
            }

            /// <summary>Cita con su confirmación ya enviada: es el mensaje al que el cliente responde.</summary>
            public async Task<Cita> SeedCitaConConfirmacionEnviadaAsync()
            {
                var puesto = new Puesto { NombrePuesto = "Barbería", Detalle = "Inbound", Activo = true };
                Context.Puestos.Add(puesto);
                await Context.SaveChangesAsync();

                var funcionario = new Funcionario
                {
                    Nombre = "Andrea",
                    IdPuesto = puesto.IdPuesto,
                    ColorCalendario = "#123456",
                    PorcentajeGanancia = 40m,
                    PorcentajeProducto = 10m,
                    FechaIngreso = new DateTime(2026, 5, 1),
                    Activo = true
                };
                Context.Funcionarios.Add(funcionario);
                await Context.SaveChangesAsync();

                var cita = new Cita
                {
                    NombreCliente = "Cliente Inbound",
                    TelefonoCliente = "88889999",
                    FechaHoraCita = new DateTime(2026, 5, 27, 10, 0, 0),
                    Tipo = "CITA",
                    DuracionMinutos = 30,
                    FuncionarioId = funcionario.IdFuncionario,
                    WhatsAppConsentAtCreation = true
                };
                Context.Citas.Add(cita);
                await Context.SaveChangesAsync();

                Context.WhatsAppMessageLogs.Add(new WhatsAppMessageLog
                {
                    CitaId = cita.Id,
                    Direction = WhatsAppMessageDirections.Outbound,
                    NotificationType = WhatsAppNotificationTypes.Confirmation,
                    Provider = WhatsAppProviders.Meta,
                    MetaMessageId = "meta-confirmacion-1",
                    RecipientPhoneE164 = "+50688889999",
                    Status = WhatsAppMessageStatuses.Sent,
                    CreatedAtUtc = FixedNowUtc,
                    SentAtUtc = FixedNowUtc
                });
                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();

                return cita;
            }

            public void Dispose()
            {
                _serviceProvider.Dispose();
                Context.Dispose();
                _connection.Dispose();
            }
        }

        /// <summary>Resuelve el tenant del scope que abre <see cref="TenantExecutionService"/>.</summary>
        private sealed class ExecutionScopedTenantProvider : ITenantProvider
        {
            private readonly ITenantExecutionContextAccessor _accessor;

            public ExecutionScopedTenantProvider(ITenantExecutionContextAccessor accessor) => _accessor = accessor;

            public Guid GetTenantId() =>
                _accessor.CurrentTenantId
                ?? throw new InvalidOperationException("Sin tenant en el scope de ejecución.");

            public bool HasTenant() => _accessor.CurrentTenantId.HasValue;
        }

        private sealed class NoOpNotificationService : INotificationService
        {
            public Task<NotificationSummary> GetSummaryAsync(int limit = 15, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<int> MarkAllAsReadAsync(CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task<bool> MarkAsReadAsync(int id, CancellationToken cancellationToken = default) =>
                throw new NotSupportedException();

            public Task CreateBookingRequestReceivedAsync(BookingRequest request, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task CreateAppointmentCancelledViaWhatsAppAsync(Cita cita, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }

        private sealed record SentTextMessage(string RecipientPhone, string Message);

        private sealed class RecordingMetaWhatsAppClient : IMetaWhatsAppClient
        {
            public List<SentTextMessage> SentTextMessages { get; } = [];

            public string? NormalizePhoneNumber(string? phoneNumber) =>
                string.IsNullOrWhiteSpace(phoneNumber)
                    ? null
                    : "+506" + new string(phoneNumber.Where(char.IsDigit).ToArray()).TrimStart('5', '0', '6');

            public bool IsValidPhoneNumber(string? phoneNumber) => NormalizePhoneNumber(phoneNumber) is not null;

            public Task<MetaWhatsAppSendResult> SendConfirmationTemplateAsync(
                string recipientPhone,
                string customerName,
                string businessName,
                string appointmentDate,
                string appointmentTime,
                string professionalName,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(MetaWhatsAppSendResult.Succeeded("confirmation", HttpStatusCode.OK, null));

            public Task<MetaWhatsAppSendResult> SendReminderTemplateAsync(
                string recipientPhone,
                string customerName,
                string businessName,
                string appointmentTime,
                string professionalName,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(MetaWhatsAppSendResult.Succeeded("reminder", HttpStatusCode.OK, null));

            public Task<MetaWhatsAppSendResult> SendCancellationTemplateAsync(
                string recipientPhone,
                WhatsAppCancellationTemplateParameters parameters,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(MetaWhatsAppSendResult.Succeeded("cancellation", HttpStatusCode.OK, null));

            public Task<MetaWhatsAppSendResult> SendTextMessageAsync(
                string recipientPhone,
                string message,
                CancellationToken cancellationToken = default)
            {
                SentTextMessages.Add(new SentTextMessage(recipientPhone, message));
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
