using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.WhatsApp;
using LuxuryApp.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class WhatsAppInboxServiceTests
    {
        private static readonly DateTime AppointmentDate = new(2026, 4, 24, 9, 0, 0);

        [Fact]
        public async Task GetInboxAsync_ShouldReturnOnlyCurrentTenantAppointments()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionarioA = await SeedFuncionarioAsync(context, "Ana");
            var servicioA = await SeedServicioAsync(context, "Corte", 45);
            await SeedCitaAsync(context, funcionarioA.IdFuncionario, AppointmentDate, servicioA.Id, consent: true);

            tenantProvider.TenantId = tenantB;
            context.ChangeTracker.Clear();

            var funcionarioB = await SeedFuncionarioAsync(context, "Externa");
            var servicioB = await SeedServicioAsync(context, "Color", 60);
            await SeedCitaAsync(context, funcionarioB.IdFuncionario, AppointmentDate.AddHours(1), servicioB.Id, consent: true);

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            var service = CreateService(context, tenantProvider);

            var inbox = await service.GetInboxAsync(AppointmentDate, funcionarioId: null, whatsAppEnabled: true);

            Assert.Single(inbox.Items);
            Assert.Equal("Ana", inbox.Items[0].FuncionarioNombre);
        }

        [Fact]
        public async Task GetInboxAsync_ShouldMarkConsentedAppointmentAsSendable()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Sofia");
            var servicio = await SeedServicioAsync(context, "Corte", 30);
            await SeedCitaAsync(context, funcionario.IdFuncionario, AppointmentDate, servicio.Id, consent: true);

            var service = CreateService(context, tenantProvider);

            var inbox = await service.GetInboxAsync(AppointmentDate, funcionarioId: null, whatsAppEnabled: true);

            var item = Assert.Single(inbox.Items);
            Assert.Equal("not_sent", item.WaStatusKey);
            Assert.True(item.PuedeEnviar);
            Assert.False(item.PuedeReenviar);
        }

        [Fact]
        public async Task GetInboxAsync_ShouldNotAllowSendingWithoutConsent()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Sofia");
            var servicio = await SeedServicioAsync(context, "Corte", 30);
            await SeedCitaAsync(context, funcionario.IdFuncionario, AppointmentDate, servicio.Id, consent: false);

            var service = CreateService(context, tenantProvider);

            var inbox = await service.GetInboxAsync(AppointmentDate, funcionarioId: null, whatsAppEnabled: true);

            var item = Assert.Single(inbox.Items);
            Assert.Equal("no_consent", item.WaStatusKey);
            Assert.False(item.PuedeEnviar);
        }

        [Fact]
        public async Task GetInboxAsync_ShouldDisableActionsWhenWhatsAppDisabledForTenant()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Sofia");
            var servicio = await SeedServicioAsync(context, "Corte", 30);
            await SeedCitaAsync(context, funcionario.IdFuncionario, AppointmentDate, servicio.Id, consent: true);

            var service = CreateService(context, tenantProvider);

            var inbox = await service.GetInboxAsync(AppointmentDate, funcionarioId: null, whatsAppEnabled: false);

            var item = Assert.Single(inbox.Items);
            Assert.False(inbox.WhatsAppEnabled);
            Assert.False(item.PuedeEnviar);
            Assert.False(item.PuedeReenviar);
        }

        [Fact]
        public async Task GetCitaChatAsync_ShouldReturnNullForAnotherTenantCita()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantB };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var foreignFuncionario = await SeedFuncionarioAsync(context, "Privado");
            var foreignServicio = await SeedServicioAsync(context, "Servicio Privado", 30);
            var foreignCita = await SeedCitaAsync(context, foreignFuncionario.IdFuncionario, AppointmentDate, foreignServicio.Id, consent: true);

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            var service = CreateService(context, tenantProvider);

            var chat = await service.GetCitaChatAsync(foreignCita.Id);

            Assert.Null(chat);
        }

        [Fact]
        public async Task FuncionarioExistsForCurrentTenantAsync_ShouldRespectTenantIsolation()
        {
            var tenantA = Guid.NewGuid();
            var tenantB = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantA };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var ownFuncionario = await SeedFuncionarioAsync(context, "Propio");

            tenantProvider.TenantId = tenantB;
            context.ChangeTracker.Clear();
            var foreignFuncionario = await SeedFuncionarioAsync(context, "Ajeno");

            tenantProvider.TenantId = tenantA;
            context.ChangeTracker.Clear();

            var service = CreateService(context, tenantProvider);

            Assert.True(await service.FuncionarioExistsForCurrentTenantAsync(ownFuncionario.IdFuncionario));
            Assert.False(await service.FuncionarioExistsForCurrentTenantAsync(foreignFuncionario.IdFuncionario));
            Assert.False(await service.FuncionarioExistsForCurrentTenantAsync(0));
        }

        [Fact]
        public async Task GetInboxAsync_ShouldExcludePastAppointmentsOnlyWhenSelectedDateIsToday()
        {
            var businessNow = new DateTime(2026, 5, 26, 15, 0, 0);
            var businessDateTimeProvider = new FixedBusinessDateTimeProvider(businessNow);
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Sofia");
            var servicio = await SeedServicioAsync(context, "Corte", 30);

            await SeedCitaAsync(context, funcionario.IdFuncionario, businessNow.Date.AddHours(8), servicio.Id, consent: true);
            var futureToday = await SeedCitaAsync(context, funcionario.IdFuncionario, businessNow.Date.AddHours(16), servicio.Id, consent: true);
            var futureDateMorning = await SeedCitaAsync(context, funcionario.IdFuncionario, businessNow.Date.AddDays(1).AddHours(8), servicio.Id, consent: true);
            var pastDateMorning = await SeedCitaAsync(context, funcionario.IdFuncionario, businessNow.Date.AddDays(-1).AddHours(8), servicio.Id, consent: true);

            var service = CreateService(context, tenantProvider, businessDateTimeProvider);

            var todayInbox = await service.GetInboxAsync(businessNow.Date, funcionarioId: null, whatsAppEnabled: true);
            var tomorrowInbox = await service.GetInboxAsync(businessNow.Date.AddDays(1), funcionarioId: null, whatsAppEnabled: true);
            var yesterdayInbox = await service.GetInboxAsync(businessNow.Date.AddDays(-1), funcionarioId: null, whatsAppEnabled: true);

            var todayItem = Assert.Single(todayInbox.Items);
            Assert.Equal(futureToday.Id, todayItem.CitaId);
            Assert.True(todayItem.EsFutura);

            Assert.Equal(futureDateMorning.Id, Assert.Single(tomorrowInbox.Items).CitaId);
            Assert.Equal(pastDateMorning.Id, Assert.Single(yesterdayInbox.Items).CitaId);
        }

        [Fact]
        public async Task GetFollowUpAsync_ShouldStartAtBusinessNowAndCalculateStatsAfterStatusFilter()
        {
            var businessNow = new DateTime(2026, 5, 26, 15, 0, 0);
            var businessDateTimeProvider = new FixedBusinessDateTimeProvider(businessNow);
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var funcionario = await SeedFuncionarioAsync(context, "Andrea");
            var servicio = await SeedServicioAsync(context, "Peinado", 45);
            var authorizedClient = await SeedClienteAsync(context, "Cliente Autorizado", "70000001", aceptaWhatsApp: true);
            var unauthorizedClient = await SeedClienteAsync(context, "Cliente Sin Autorizacion", "70000002", aceptaWhatsApp: false);
            var nowUtc = businessDateTimeProvider.NowOffset().UtcDateTime;

            var pastToday = await SeedCitaAsync(
                context,
                funcionario.IdFuncionario,
                businessNow.Date.AddHours(9),
                servicio.Id,
                consent: true,
                cliente: authorizedClient);
            var pendingFuture = await SeedCitaAsync(
                context,
                funcionario.IdFuncionario,
                businessNow.Date.AddHours(16),
                servicio.Id,
                consent: true,
                cliente: authorizedClient);
            var confirmed = await SeedCitaAsync(
                context,
                funcionario.IdFuncionario,
                businessNow.Date.AddDays(1).AddHours(10),
                servicio.Id,
                consent: true,
                cliente: authorizedClient,
                estadoConfirmacionWhatsApp: WhatsAppConfirmationStates.Confirmada,
                confirmadaPorWhatsAppUtc: nowUtc.AddMinutes(-30));
            var failed = await SeedCitaAsync(
                context,
                funcionario.IdFuncionario,
                businessNow.Date.AddDays(2).AddHours(11),
                servicio.Id,
                consent: true,
                cliente: authorizedClient);
            var noConsent = await SeedCitaAsync(
                context,
                funcionario.IdFuncionario,
                businessNow.Date.AddDays(3).AddHours(12),
                servicio.Id,
                consent: false,
                cliente: unauthorizedClient);
            var outOfRange = await SeedCitaAsync(
                context,
                funcionario.IdFuncionario,
                businessNow.Date.AddDays(6).AddHours(9),
                servicio.Id,
                consent: true,
                cliente: authorizedClient);

            await SeedWhatsAppLogAsync(
                context,
                confirmed.Id,
                WhatsAppMessageStatuses.Sent,
                WhatsAppNotificationTypes.Confirmation,
                nowUtc.AddMinutes(-45));
            await SeedWhatsAppLogAsync(
                context,
                failed.Id,
                WhatsAppMessageStatuses.Failed,
                WhatsAppNotificationTypes.Confirmation,
                nowUtc.AddMinutes(-15));

            context.ChangeTracker.Clear();

            var service = CreateService(context, tenantProvider, businessDateTimeProvider);
            var toExclusive = businessNow.Date.AddDays(5);

            var followUp = await service.GetFollowUpAsync(
                businessNow,
                toExclusive,
                funcionarioId: null,
                statusKey: null,
                rangeKey: "5d",
                whatsAppEnabled: true);

            Assert.DoesNotContain(followUp.Items, item => item.CitaId == pastToday.Id);
            Assert.DoesNotContain(followUp.Items, item => item.CitaId == outOfRange.Id);
            Assert.Equal(new[] { pendingFuture.Id, confirmed.Id, failed.Id, noConsent.Id }, followUp.Items.Select(i => i.CitaId).ToArray());
            Assert.Equal(4, followUp.Stats.TotalTracking);
            Assert.Equal(1, followUp.Stats.Confirmed);
            Assert.Equal(1, followUp.Stats.Pending);
            Assert.Equal(1, followUp.Stats.Sent);
            Assert.Equal(1, followUp.Stats.Failed);
            Assert.Equal(25m, followUp.Stats.ConfirmationRate);

            var sentOnly = await service.GetFollowUpAsync(
                businessNow,
                toExclusive,
                funcionarioId: null,
                statusKey: "enviados",
                rangeKey: "5d",
                whatsAppEnabled: true);

            var sentItem = Assert.Single(sentOnly.Items);
            Assert.Equal(confirmed.Id, sentItem.CitaId);
            Assert.Equal(1, sentOnly.Stats.TotalTracking);
            Assert.Equal(1, sentOnly.Stats.Sent);
            Assert.Equal(100m, sentOnly.Stats.ConfirmationRate);
        }

        private static WhatsAppInboxService CreateService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            TestTenantProvider tenantProvider,
            IBusinessDateTimeProvider? businessDateTimeProvider = null) =>
            new(
                context,
                businessDateTimeProvider ?? ControllerTestSupport.BusinessDateTimeProvider,
                tenantProvider,
                NullLogger<WhatsAppInboxService>.Instance);

        private static async Task<Funcionario> SeedFuncionarioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre)
        {
            var puesto = new Puesto
            {
                NombrePuesto = $"Puesto {Guid.NewGuid():N}",
                Detalle = "Calendario",
                Activo = true
            };

            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            var funcionario = new Funcionario
            {
                Nombre = nombre,
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#654321",
                PorcentajeGanancia = 35m,
                PorcentajeProducto = 8m,
                FechaIngreso = new DateTime(2026, 4, 1),
                Activo = true
            };

            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();
            return funcionario;
        }

        private static async Task<Servicio> SeedServicioAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            int duracionMinutos)
        {
            var servicio = new Servicio
            {
                Nombre = nombre,
                Precio = 30m,
                DuracionMinutos = duracionMinutos,
                Activo = true
            };

            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();
            return servicio;
        }

        private static async Task<Cita> SeedCitaAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            int funcionarioId,
            DateTime fechaHora,
            int servicioId,
            bool consent,
            ClientesModel? cliente = null,
            string estadoConfirmacionWhatsApp = WhatsAppConfirmationStates.Pendiente,
            DateTime? confirmadaPorWhatsAppUtc = null)
        {
            var nombreCliente = cliente?.Nombre ?? $"Cliente {Guid.NewGuid():N}";
            var telefonoCliente = cliente?.NumeroTelefono ?? "70000000";

            var cita = new Cita
            {
                NombreCliente = nombreCliente,
                TelefonoCliente = telefonoCliente,
                ClienteId = cliente?.Id,
                ServicioId = servicioId,
                FechaHoraCita = fechaHora,
                FuncionarioId = funcionarioId,
                Tipo = "CITA",
                EstadoConfirmacionWhatsApp = estadoConfirmacionWhatsApp,
                ConfirmadaPorWhatsAppUtc = confirmadaPorWhatsAppUtc,
                WhatsAppConsentAtCreation = consent,
                WhatsAppConsentSource = cliente is not null
                    ? WhatsAppConsentSources.ClienteRegistrado
                    : consent
                        ? WhatsAppConsentSources.CitaManual
                        : WhatsAppConsentSources.SinConsentimiento
            };

            context.Citas.Add(cita);
            await context.SaveChangesAsync();
            return cita;
        }

        private static async Task<ClientesModel> SeedClienteAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            string nombre,
            string telefono,
            bool aceptaWhatsApp)
        {
            var cliente = new ClientesModel
            {
                Nombre = nombre,
                NumeroTelefono = telefono,
                AceptaMensajesWhatsApp = aceptaWhatsApp,
                WhatsAppConsentSource = aceptaWhatsApp
                    ? WhatsAppConsentSources.ClienteForm
                    : WhatsAppConsentSources.SinConsentimiento,
                WhatsAppConsentTextVersion = aceptaWhatsApp
                    ? WhatsAppConsentTextVersions.WaOptInV1
                    : null,
                WhatsAppConsentUpdatedAtUtc = aceptaWhatsApp ? DateTime.UtcNow : null,
                FrecuenciaVisita = 30,
                FechaUltimaVisita = new DateTime(2026, 5, 1)
            };

            context.Clientes.Add(cliente);
            await context.SaveChangesAsync();
            return cliente;
        }

        private static async Task<WhatsAppMessageLog> SeedWhatsAppLogAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            int citaId,
            string status,
            string notificationType,
            DateTime createdAtUtc)
        {
            var log = new WhatsAppMessageLog
            {
                CitaId = citaId,
                Direction = WhatsAppMessageDirections.Outbound,
                NotificationType = notificationType,
                Provider = WhatsAppProviders.Meta,
                Status = status,
                CreatedAtUtc = createdAtUtc,
                SentAtUtc = status == WhatsAppMessageStatuses.Sent ? createdAtUtc : null,
                FailedAtUtc = status == WhatsAppMessageStatuses.Failed ? createdAtUtc : null,
                RecipientPhoneE164 = "50670000000"
            };

            context.WhatsAppMessageLogs.Add(log);
            await context.SaveChangesAsync();
            return log;
        }
    }
}
