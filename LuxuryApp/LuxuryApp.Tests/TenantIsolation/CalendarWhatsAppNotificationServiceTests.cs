using System.Net;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Identity;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.Calendar;
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
    public class CalendarWhatsAppNotificationServiceTests
    {
        [Fact]
        public async Task QueueConfirmation_WhenAddonActiveButNoStoredSettings_ShouldNotQueue_OptionA()
        {
            // Opción A: comprar el paquete crea el add-on comercial pero NO habilita envíos. Sin una
            // configuración persistida (TenantWhatsAppSettings) no se debe encolar/enviar nada; el mensaje
            // queda en un estado de "saltado por configuración", no Pending.
            using var fixture = await Fixture.CreateAsync(seedEnabledSettings: false);
            var cita = await fixture.SeedCitaAsync();

            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);

            // El snapshot sintético sigue exponiendo defaults "habilitados" para prellenar la pantalla
            // de configuración, pero eso NO autoriza el envío: no hay fila persistida.
            var settings = await fixture.Settings.GetSettingsForTenantAsync(fixture.TenantId);
            Assert.False(settings.Exists);
            Assert.Empty(fixture.Context.TenantWhatsAppSettings);

            var message = await fixture.Context.WhatsAppMessageLogs.SingleOrDefaultAsync();
            if (message is not null)
            {
                Assert.NotEqual(WhatsAppMessageStatuses.Pending, message.Status);
                Assert.Equal(WhatsAppMessageStatuses.SkippedConfiguration, message.Status);
            }
        }

        [Fact]
        public async Task QueueConfirmation_WithCustomHoursBefore_ShouldScheduleAtConfiguredOffset()
        {
            using var fixture = await Fixture.CreateAsync();
            // Confirmación 2 horas antes (en vez de 24).
            await fixture.UpdateAutomationAsync(new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 30,
                ConfirmationHoursBefore = 2,
                ReminderHoursBefore = 3
            });

            // Cita 10 horas en el futuro respecto al "now" fijo (2026-05-26 10:30 -06:00).
            var citaFechaHora = new DateTime(2026, 5, 26, 20, 30, 0);
            var cita = await fixture.SeedCitaAsync(fechaHora: citaFechaHora);

            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);

            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            var expectedSendAtUtc = new DateTimeOffset(citaFechaHora, TimeSpan.FromHours(-6)).UtcDateTime.AddHours(-2);
            Assert.Equal(WhatsAppMessageStatuses.Pending, message.Status);
            Assert.Equal(expectedSendAtUtc, message.NextAttemptAtUtc);
        }

        [Fact]
        public async Task QueueConfirmation_WhenInsideWindowAndImmediateDisabled_ShouldNotQueue()
        {
            using var fixture = await Fixture.CreateAsync();
            // 24h antes pero sin envío inmediato si ya está dentro del rango.
            await fixture.UpdateAutomationAsync(new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 30,
                ConfirmationHoursBefore = 24,
                SendConfirmationImmediatelyIfInsideWindow = false
            });

            // Cita a 2h del "now": ya dentro de la ventana de 24h.
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 26, 12, 30, 0));

            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);

            Assert.False(await fixture.Context.WhatsAppMessageLogs.AnyAsync(),
                "No debe encolarse confirmación cuando la cita ya entró a la ventana y el envío inmediato está desactivado.");
        }

        [Fact]
        public async Task QueueReminder_WithExtendedLookAhead_ShouldQueueAppointmentOutsideDefaultWindow()
        {
            using var fixture = await Fixture.CreateAsync();
            // Recordatorio 6 horas antes (en vez de 3).
            await fixture.UpdateAutomationAsync(new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 30,
                ReminderHoursBefore = 6
            });

            // Cita a 5h del "now": fuera de la ventana default de 3h, dentro de la de 6h.
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 26, 15, 30, 0));

            await fixture.Notifications.QueueAppointmentReminderAsync(cita.Id);

            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.Equal(WhatsAppNotificationTypes.Reminder3Hours, message.NotificationType);
            Assert.Equal(WhatsAppMessageStatuses.Pending, message.Status);
        }

        [Fact]
        public async Task ProcessPending_WhenConfirmationScheduledInFuture_ShouldNotSendBeforeDue()
        {
            using var fixture = await Fixture.CreateAsync();
            await fixture.UpdateAutomationAsync(new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 30,
                ConfirmationHoursBefore = 24
            });

            // Cita 48h en el futuro → la confirmación se programa para dentro de 24h (aún no vence).
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 28, 10, 30, 0));
            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);

            var queued = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.Equal(WhatsAppMessageStatuses.Pending, queued.Status);
            Assert.NotNull(queued.NextAttemptAtUtc);
            Assert.True(queued.NextAttemptAtUtc > Fixture.FixedNowUtc);

            // El worker procesa: NO debe enviar porque todavía no llega la hora programada.
            await fixture.Notifications.ProcessPendingNotificationsAsync();

            Assert.Equal(0, fixture.MetaClient.SendCount);
            var afterProcess = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.Equal(WhatsAppMessageStatuses.Pending, afterProcess.Status);
        }

        [Fact]
        public async Task ProcessPending_WhenConfirmationInsideWindow_ShouldSendImmediately()
        {
            using var fixture = await Fixture.CreateAsync();
            await fixture.UpdateAutomationAsync(new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 30,
                ConfirmationHoursBefore = 24,
                SendConfirmationImmediatelyIfInsideWindow = true,
                ReminderHoursBefore = 3
            });

            // Cita a 10h del "now": dentro de la ventana de confirmación (24h) pero fuera de la de
            // recordatorio (3h) → la confirmación se envía de inmediato.
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 26, 20, 30, 0));
            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);

            var queued = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.Null(queued.NextAttemptAtUtc);

            await fixture.Notifications.ProcessPendingNotificationsAsync();

            Assert.Equal(1, fixture.MetaClient.SendCount);
            var sent = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.Equal(WhatsAppMessageStatuses.Sent, sent.Status);
            var updatedCita = await fixture.Context.Citas.SingleAsync(c => c.Id == cita.Id);
            Assert.NotNull(updatedCita.ConfirmacionWhatsAppEnviadaUtc);
        }

        [Fact]
        public async Task ProcessPending_ShouldUseUpdatedAccountDisplayNameAsBusinessName()
        {
            using var fixture = await Fixture.CreateAsync();
            await fixture.SeedAccountDisplayNameAsync("Barberia jor");
            await fixture.UpdateAutomationAsync(new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 30,
                ConfirmationHoursBefore = 24,
                SendConfirmationImmediatelyIfInsideWindow = true,
                ReminderHoursBefore = 3
            });

            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 26, 20, 30, 0));
            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);

            await fixture.Notifications.ProcessPendingNotificationsAsync();

            Assert.Equal("Barberia jor", fixture.MetaClient.LastBusinessName);
        }

        [Fact]
        public async Task DailyBatch_TomorrowAllDay_AfterBatchTime_ShouldQueueAndSuppressCreateTimeConfirmation()
        {
            using var fixture = await Fixture.CreateAsync();
            await fixture.UpdateAutomationAsync(new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 30,
                ConfirmationScheduleMode = WhatsAppConfirmationScheduleModes.DailyBatchPreviousDay,
                ConfirmationBatchTime = new TimeOnly(9, 0),
                ConfirmationBatchTarget = WhatsAppConfirmationBatchTargets.TomorrowAllDay
            });

            // Cita de mañana (now fijo: 2026-05-26 10:30 → mañana = 2026-05-27).
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 27, 14, 0, 0));

            // En modo lote, crear la cita NO debe encolar confirmación.
            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);
            Assert.False(await fixture.Context.WhatsAppMessageLogs.AnyAsync());

            // El lote corre (now 10:30 >= 09:00) y encola la confirmación de la cita de mañana.
            await fixture.Notifications.GenerateDailyBatchAsync();

            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.Equal(WhatsAppNotificationTypes.Confirmation, message.NotificationType);
            Assert.Equal(WhatsAppMessageStatuses.Pending, message.Status);
            Assert.Null(message.NextAttemptAtUtc);
        }

        [Fact]
        public async Task DailyBatch_BeforeBatchTime_ShouldNotQueue()
        {
            using var fixture = await Fixture.CreateAsync();
            await fixture.UpdateAutomationAsync(new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 30,
                ConfirmationScheduleMode = WhatsAppConfirmationScheduleModes.DailyBatchPreviousDay,
                ConfirmationBatchTime = new TimeOnly(20, 0), // 8 pm, posterior al now fijo (10:30)
                ConfirmationBatchTarget = WhatsAppConfirmationBatchTargets.TomorrowAllDay
            });

            await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 27, 14, 0, 0));

            await fixture.Notifications.GenerateDailyBatchAsync();

            Assert.False(await fixture.Context.WhatsAppMessageLogs.AnyAsync());
        }

        [Fact]
        public async Task DailyBatch_RunTwiceSameDay_ShouldNotDuplicate()
        {
            using var fixture = await Fixture.CreateAsync();
            await fixture.UpdateAutomationAsync(new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 30,
                ConfirmationScheduleMode = WhatsAppConfirmationScheduleModes.DailyBatchPreviousDay,
                ConfirmationBatchTime = new TimeOnly(9, 0),
                ConfirmationBatchTarget = WhatsAppConfirmationBatchTargets.TomorrowAllDay
            });

            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 27, 14, 0, 0));

            await fixture.Notifications.GenerateDailyBatchAsync();
            await fixture.Notifications.GenerateDailyBatchAsync();

            var count = await fixture.Context.WhatsAppMessageLogs.CountAsync(m => m.CitaId == cita.Id);
            Assert.Equal(1, count);
        }

        [Fact]
        public async Task QueueConfirmation_WhenInsideReminderWindow_ShouldSkipSilentlyAndAllowOnlyReminder()
        {
            using var fixture = await Fixture.CreateAsync();
            await fixture.UpdateAutomationAsync(new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 30,
                ConfirmationHoursBefore = 24,
                ReminderHoursBefore = 3
            });

            // Cita a 2h del "now": ya dentro de la ventana del recordatorio (3h).
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 26, 12, 30, 0));

            // La confirmación se omite EN SILENCIO (sin fila de log de error/omitida).
            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);
            Assert.False(await fixture.Context.WhatsAppMessageLogs.AnyAsync());

            // Solo se encola/envía el recordatorio.
            await fixture.Notifications.QueueAppointmentReminderAsync(cita.Id);
            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.Equal(WhatsAppNotificationTypes.Reminder3Hours, message.NotificationType);
            Assert.Equal(WhatsAppMessageStatuses.Pending, message.Status);
        }

        [Fact]
        public async Task ProcessPending_DuringQuietHours_ShouldDeferInsteadOfSending()
        {
            using var fixture = await Fixture.CreateAsync();
            // now fijo = 2026-05-26 10:30; silencio 09:00–12:00 cubre el "now".
            await fixture.UpdateAutomationAsync(new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 30,
                ConfirmationHoursBefore = 24,
                ReminderHoursBefore = 3,
                QuietHoursEnabled = true,
                QuietHoursStart = new TimeOnly(9, 0),
                QuietHoursEnd = new TimeOnly(12, 0)
            });

            // Cita a 10h: confirmación inmediata (dentro de 24h, fuera de 3h) → queda pendiente y vencida.
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 26, 20, 30, 0));
            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);

            var queued = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.Equal(WhatsAppMessageStatuses.Pending, queued.Status);
            Assert.Null(queued.NextAttemptAtUtc);

            // Procesar durante el silencio: no envía y reprograma al fin del silencio (12:00 local).
            await fixture.Notifications.ProcessPendingNotificationsAsync();

            Assert.Equal(0, fixture.MetaClient.SendCount);
            // ExecuteUpdate es masivo (no refresca entidades trackeadas): leer sin tracking.
            var deferred = await fixture.Context.WhatsAppMessageLogs.AsNoTracking().SingleAsync();
            Assert.Equal(WhatsAppMessageStatuses.Pending, deferred.Status);
            var expectedResumeUtc = new DateTimeOffset(new DateTime(2026, 5, 26, 12, 0, 0), TimeSpan.FromHours(-6)).UtcDateTime;
            Assert.Equal(expectedResumeUtc, deferred.NextAttemptAtUtc);
        }

        [Fact]
        public async Task QueueConfirmation_WhenTenantIsDisabled_ShouldSkipAsTenantDisabled()
        {
            using var fixture = await Fixture.CreateAsync();
            await fixture.UpdateSettingsAsync(isEnabled: false);
            var cita = await fixture.SeedCitaAsync();

            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);

            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.Equal(WhatsAppMessageStatuses.SkippedTenantDisabled, message.Status);
            Assert.Equal(WhatsAppErrorCodes.TenantDisabled, message.ErrorCode);
            Assert.Equal(0, fixture.MetaClient.SendCount);
            Assert.Equal(0, await fixture.Settings.GetTodayUsageAsync(fixture.TenantId));
        }

        [Fact]
        public async Task QueueConfirmation_WhenConfirmationsWereDisabledByTenant_ShouldSkipAsUserDisabled()
        {
            using var fixture = await Fixture.CreateAsync();
            await fixture.UpdateSettingsAsync(
                isEnabled: true,
                sendConfirmation: false,
                sendReminder: true);
            var cita = await fixture.SeedCitaAsync();

            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);

            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.Equal(WhatsAppMessageStatuses.SkippedUserDisabled, message.Status);
            Assert.Equal(WhatsAppErrorCodes.UserDisabled, message.ErrorCode);
        }

        [Fact]
        public async Task QueueReminder_WhenRemindersWereDisabledByTenant_ShouldSkipAsUserDisabled()
        {
            using var fixture = await Fixture.CreateAsync();
            await fixture.UpdateSettingsAsync(
                isEnabled: true,
                sendConfirmation: true,
                sendReminder: false);
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 26, 13, 0, 0));

            await fixture.Notifications.QueueAppointmentReminderAsync(cita.Id);

            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.Equal(WhatsAppMessageStatuses.SkippedUserDisabled, message.Status);
            Assert.Equal(WhatsAppErrorCodes.UserDisabled, message.ErrorCode);
        }

        [Fact]
        public async Task QueueReminder_WithoutActiveAddon_ShouldSkipAsNoActiveWhatsAppAddon()
        {
            using var fixture = await Fixture.CreateAsync(seedActiveAddon: false);
            await fixture.UpdateSettingsAsync(isEnabled: true);
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 26, 13, 0, 0));

            await fixture.Notifications.QueueAppointmentReminderAsync(cita.Id);

            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync(current => current.CitaId == cita.Id);
            Assert.Equal(WhatsAppMessageStatuses.SkippedSubscriptionRequired, message.Status);
            Assert.Equal(WhatsAppErrorCodes.NoActiveWhatsAppAddon, message.ErrorCode);
            Assert.Equal(0, fixture.MetaClient.SendCount);
        }

        [Fact]
        public async Task QueueReminder_WithoutActiveBaseSubscription_ShouldSkipAsNoActiveBaseSubscription()
        {
            using var fixture = await Fixture.CreateAsync(seedActiveBaseSubscription: false);
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 26, 13, 0, 0));

            await fixture.Notifications.QueueAppointmentReminderAsync(cita.Id);

            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync(current => current.CitaId == cita.Id);
            Assert.Equal(WhatsAppMessageStatuses.SkippedSubscriptionRequired, message.Status);
            Assert.Equal(WhatsAppErrorCodes.NoActiveBaseSubscription, message.ErrorCode);
        }

        [Fact]
        public async Task QueueReminder_WhenAddonExpired_ShouldSkipAsNoActiveWhatsAppAddon()
        {
            using var fixture = await Fixture.CreateAsync(addonEndsUtc: Fixture.FixedNowUtc.AddMinutes(-1));
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 26, 13, 0, 0));

            await fixture.Notifications.QueueAppointmentReminderAsync(cita.Id);

            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync(current => current.CitaId == cita.Id);
            Assert.Equal(WhatsAppMessageStatuses.SkippedSubscriptionRequired, message.Status);
            Assert.Equal(WhatsAppErrorCodes.NoActiveWhatsAppAddon, message.ErrorCode);
        }

        [Fact]
        public async Task QueueReminder_WhenMonthlyBalanceWasExhausted_ShouldSkipAsMonthlyLimitExceeded()
        {
            using var fixture = await Fixture.CreateAsync(addonMonthlyLimit: 1);
            fixture.Context.WhatsAppMessageLogs.Add(new WhatsAppMessageLog
            {
                TenantId = fixture.TenantId,
                Direction = WhatsAppMessageDirections.Outbound,
                NotificationType = WhatsAppNotificationTypes.Confirmation,
                Status = WhatsAppMessageStatuses.Sent,
                CreatedAtUtc = Fixture.FixedNowUtc,
                SentAtUtc = Fixture.FixedNowUtc
            });
            await fixture.Context.SaveChangesAsync();

            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 26, 13, 0, 0));

            await fixture.Notifications.QueueAppointmentReminderAsync(cita.Id);

            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync(current => current.CitaId == cita.Id);
            Assert.Equal(WhatsAppMessageStatuses.SkippedMonthlyLimitExceeded, message.Status);
            Assert.Equal(WhatsAppErrorCodes.MonthlyLimitExceeded, message.ErrorCode);
            Assert.Equal(0, fixture.MetaClient.SendCount);
        }

        [Fact]
        public async Task QueueReminder_WhenActualDailyUsageReached_ShouldSkipAsDailyLimitExceeded()
        {
            using var fixture = await Fixture.CreateAsync();
            await fixture.UpdateSettingsAsync(isEnabled: true, dailyLimit: 1);
            var firstCita = await fixture.SeedCitaAsync();
            await fixture.Notifications.QueueAppointmentConfirmationAsync(firstCita.Id);
            await fixture.Notifications.ProcessPendingNotificationsAsync();

            var reminder = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 26, 13, 0, 0));

            await fixture.Notifications.QueueAppointmentReminderAsync(reminder.Id);

            var reminderMessage = await fixture.Context.WhatsAppMessageLogs
                .SingleAsync(message => message.CitaId == reminder.Id);
            Assert.Equal(WhatsAppMessageStatuses.SkippedDailyLimitExceeded, reminderMessage.Status);
            Assert.Equal(WhatsAppErrorCodes.DailyLimitExceeded, reminderMessage.ErrorCode);
        }

        [Fact]
        public async Task ProcessPendingNotifications_WhenDailyLimitAllowsOnlyOne_ShouldSendOldestAndSkipTheRest()
        {
            using var fixture = await Fixture.CreateAsync();
            await fixture.UpdateSettingsAsync(isEnabled: true, dailyLimit: 1);
            var firstCita = await fixture.SeedCitaAsync();
            var secondCita = await fixture.SeedCitaAsync(phone: "89990000");

            await fixture.Notifications.QueueAppointmentConfirmationAsync(firstCita.Id);
            await fixture.Notifications.QueueAppointmentConfirmationAsync(secondCita.Id);
            await fixture.Notifications.ProcessPendingNotificationsAsync();

            var messages = await fixture.Context.WhatsAppMessageLogs
                .OrderBy(message => message.Id)
                .ToListAsync();

            Assert.Equal(2, messages.Count);
            Assert.Equal(WhatsAppMessageStatuses.Sent, messages[0].Status);
            Assert.Equal(WhatsAppMessageStatuses.SkippedDailyLimitExceeded, messages[1].Status);
            Assert.Equal(1, fixture.MetaClient.SendCount);
            Assert.Equal(1, await fixture.Settings.GetTodayUsageAsync(fixture.TenantId));
        }

        [Fact]
        public async Task ProcessPendingNotifications_WhenMetaAcceptsMessage_ShouldConsumeDailyAndMonthlyUsage()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync();

            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);
            await fixture.Notifications.ProcessPendingNotificationsAsync();

            var addon = await fixture.Context.TenantSubscriptionAddons.SingleAsync();
            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            var monthlyUsage = await fixture.SubscriptionService.GetWhatsAppUsageInCurrentPeriodAsync(
                fixture.TenantId,
                addon.FechaInicio,
                addon.FechaFin);

            Assert.Equal(WhatsAppMessageStatuses.Sent, message.Status);
            Assert.Equal(1, await fixture.Settings.GetTodayUsageAsync(fixture.TenantId));
            Assert.Equal(1, monthlyUsage);
        }

        [Fact]
        public async Task ProcessPendingNotifications_WhenMetaRejectsMessage_ShouldNotConsumeBalance()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync();

            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);
            fixture.MetaClient.NextSendResult = MetaWhatsAppSendResult.Failed(
                "190",
                "Meta API error HTTP 401, type=OAuthException, code=190, subcode=463, message=Error validating access token, fbtrace_id=test-trace",
                HttpStatusCode.Unauthorized,
                "{\"error\":{\"message\":\"Error validating access token\",\"type\":\"OAuthException\",\"code\":190,\"error_subcode\":463,\"fbtrace_id\":\"test-trace\"}}",
                errorType: "OAuthException",
                errorSubcode: 463,
                fbTraceId: "test-trace",
                shouldRetry: false);

            await fixture.Notifications.ProcessPendingNotificationsAsync();

            var addon = await fixture.Context.TenantSubscriptionAddons.SingleAsync();
            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            var monthlyUsage = await fixture.SubscriptionService.GetWhatsAppUsageInCurrentPeriodAsync(
                fixture.TenantId,
                addon.FechaInicio,
                addon.FechaFin);

            Assert.Equal(WhatsAppMessageStatuses.Failed, message.Status);
            Assert.Equal(0, await fixture.Settings.GetTodayUsageAsync(fixture.TenantId));
            Assert.Equal(0, monthlyUsage);
        }

        [Fact]
        public async Task QueueConfirmation_DoubleSubmitForSameAppointment_ShouldNotDuplicateConsumption()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync();

            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);
            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);
            await fixture.Notifications.ProcessPendingNotificationsAsync();

            var messages = await fixture.Context.WhatsAppMessageLogs.ToListAsync();
            var addon = await fixture.Context.TenantSubscriptionAddons.SingleAsync();
            var monthlyUsage = await fixture.SubscriptionService.GetWhatsAppUsageInCurrentPeriodAsync(
                fixture.TenantId,
                addon.FechaInicio,
                addon.FechaFin);

            Assert.Single(messages);
            Assert.Equal(1, await fixture.Settings.GetTodayUsageAsync(fixture.TenantId));
            Assert.Equal(1, monthlyUsage);
        }

        [Fact]
        public async Task QueueReminder_DoubleExecutionForSameAppointment_ShouldNotDuplicateConsumption()
        {
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 26, 13, 0, 0));

            await fixture.Notifications.QueueAppointmentReminderAsync(cita.Id);
            await fixture.Notifications.QueueAppointmentReminderAsync(cita.Id);
            await fixture.Notifications.ProcessPendingNotificationsAsync();

            var messages = await fixture.Context.WhatsAppMessageLogs.ToListAsync();
            var addon = await fixture.Context.TenantSubscriptionAddons.SingleAsync();
            var monthlyUsage = await fixture.SubscriptionService.GetWhatsAppUsageInCurrentPeriodAsync(
                fixture.TenantId,
                addon.FechaInicio,
                addon.FechaFin);

            Assert.Single(messages);
            Assert.Equal(1, await fixture.Settings.GetTodayUsageAsync(fixture.TenantId));
            Assert.Equal(1, monthlyUsage);
        }

        [Fact]
        public async Task QueueConfirmation_WhenCitaIsFarInFuture_ShouldScheduleFor24HoursBeforeCita()
        {
            // Cita 3 días en el futuro → NextAttemptAtUtc debe ser ~24h antes de la cita.
            using var fixture = await Fixture.CreateAsync();
            var citaFechaHora = new DateTime(2026, 5, 29, 10, 0, 0); // 3 días después de FixedNowLocal
            var cita = await fixture.SeedCitaAsync(fechaHora: citaFechaHora);

            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);

            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.Equal(WhatsAppMessageStatuses.Pending, message.Status);
            Assert.NotNull(message.NextAttemptAtUtc);

            // 24h antes: 2026-05-28 10:00 CR = 2026-05-28 16:00 UTC
            var expectedScheduleUtc = new DateTimeOffset(new DateTime(2026, 5, 28, 10, 0, 0), TimeSpan.FromHours(-6)).UtcDateTime;
            Assert.Equal(expectedScheduleUtc, message.NextAttemptAtUtc!.Value, TimeSpan.FromSeconds(1));
            Assert.Equal(0, fixture.MetaClient.SendCount);
        }

        [Fact]
        public async Task QueueConfirmation_WhenCitaIsWithin24Hours_ShouldQueueForImmediateSending()
        {
            // Cita mañana a las 09:00 (menos de 24h) → NextAttemptAtUtc debe ser null (enviar de inmediato).
            using var fixture = await Fixture.CreateAsync();
            var citaFechaHora = new DateTime(2026, 5, 27, 9, 0, 0); // ~22.5h desde FixedNowLocal
            var cita = await fixture.SeedCitaAsync(fechaHora: citaFechaHora);

            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);

            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.Equal(WhatsAppMessageStatuses.Pending, message.Status);
            Assert.Null(message.NextAttemptAtUtc);
        }

        [Fact]
        public async Task QueueConfirmation_WhenCitaIsInPast_ShouldNotCreateMessage()
        {
            // Cita ayer → no debe crearse ningún mensaje.
            using var fixture = await Fixture.CreateAsync();
            var citaFechaHora = new DateTime(2026, 5, 25, 10, 0, 0); // ayer
            var cita = await fixture.SeedCitaAsync(fechaHora: citaFechaHora);

            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);

            var messageCount = await fixture.Context.WhatsAppMessageLogs.CountAsync();
            Assert.Equal(0, messageCount);
            Assert.Equal(0, fixture.MetaClient.SendCount);
        }

        [Fact]
        public async Task QueueConfirmation_MultipleWeeklyAppointments_ShouldNotSendAnyImmediately()
        {
            // Simula citas semanales por 4 semanas: ninguna debe enviarse de inmediato.
            using var fixture = await Fixture.CreateAsync();

            var fechas = new[]
            {
                new DateTime(2026, 6, 2, 10, 0, 0),   // 7 días desde FixedNowLocal
                new DateTime(2026, 6, 9, 10, 0, 0),   // 14 días
                new DateTime(2026, 6, 16, 10, 0, 0),  // 21 días
                new DateTime(2026, 6, 23, 10, 0, 0)   // 28 días
            };

            foreach (var fecha in fechas)
            {
                var cita = await fixture.SeedCitaAsync(phone: $"8888{fecha.Day:D4}", fechaHora: fecha);
                await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);
            }

            // Ninguna debe estar lista para envío inmediato.
            await fixture.Notifications.ProcessPendingNotificationsAsync();

            Assert.Equal(0, fixture.MetaClient.SendCount);

            var messages = await fixture.Context.WhatsAppMessageLogs.ToListAsync();
            Assert.Equal(4, messages.Count);
            Assert.All(messages, m =>
            {
                Assert.Equal(WhatsAppMessageStatuses.Pending, m.Status);
                Assert.NotNull(m.NextAttemptAtUtc);
            });
        }

        [Fact]
        public async Task ProcessPendingNotifications_WhenScheduleTimeNotReached_ShouldNotSend()
        {
            // Cita en 7 días: el worker no debe enviarla todavía.
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 6, 2, 10, 0, 0));

            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);
            await fixture.Notifications.ProcessPendingNotificationsAsync();

            Assert.Equal(0, fixture.MetaClient.SendCount);
            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.Equal(WhatsAppMessageStatuses.Pending, message.Status);
        }

        [Fact]
        public async Task RescheduleConfirmationIfPending_WhenCitaMovedFarther_ShouldUpdateScheduledTime()
        {
            // Cita dentro de 3 días, luego se mueve a 10 días: NextAttemptAtUtc debe actualizarse.
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 29, 10, 0, 0));
            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);

            var originalMessage = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.NotNull(originalMessage.NextAttemptAtUtc);

            var newFechaHora = new DateTime(2026, 6, 5, 10, 0, 0); // 10 días desde FixedNowLocal
            await fixture.Notifications.RescheduleConfirmationIfPendingAsync(cita.Id, newFechaHora);

            fixture.Context.ChangeTracker.Clear();
            var updatedMessage = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            var expectedNewSchedule = new DateTimeOffset(new DateTime(2026, 6, 4, 10, 0, 0), TimeSpan.FromHours(-6)).UtcDateTime;
            Assert.Equal(WhatsAppMessageStatuses.Pending, updatedMessage.Status);
            Assert.Equal(expectedNewSchedule, updatedMessage.NextAttemptAtUtc!.Value, TimeSpan.FromSeconds(1));
        }

        [Fact]
        public async Task RescheduleConfirmationIfPending_WhenCitaMovedToWithin24Hours_ShouldClearSchedule()
        {
            // Cita en 3 días, luego se mueve a dentro de 20h: NextAttemptAtUtc debe ser null.
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 29, 10, 0, 0));
            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);

            var newFechaHora = new DateTime(2026, 5, 27, 6, 0, 0); // 19.5h desde FixedNowLocal
            await fixture.Notifications.RescheduleConfirmationIfPendingAsync(cita.Id, newFechaHora);

            fixture.Context.ChangeTracker.Clear();
            var updatedMessage = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.Equal(WhatsAppMessageStatuses.Pending, updatedMessage.Status);
            Assert.Null(updatedMessage.NextAttemptAtUtc);
        }

        [Fact]
        public async Task CancelPendingNotifications_ShouldMarkMessageAsCancelled()
        {
            // Cita en 3 días, luego se cancela: mensaje debe quedar como Cancelled.
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 29, 10, 0, 0));
            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);

            await fixture.Notifications.CancelPendingNotificationsAsync(cita.Id);

            fixture.Context.ChangeTracker.Clear();
            var message = await fixture.Context.WhatsAppMessageLogs.SingleAsync();
            Assert.Equal(WhatsAppMessageStatuses.Cancelled, message.Status);
            Assert.Equal(WhatsAppErrorCodes.CitaCancellada, message.ErrorCode);
            Assert.Null(message.NextAttemptAtUtc);
            Assert.Equal(0, fixture.MetaClient.SendCount);
        }

        [Fact]
        public async Task QueueConfirmation_AfterCancelledMessage_ShouldAllowNewMessage()
        {
            // Un mensaje cancelado no debe bloquear la creación de uno nuevo (idempotencia correcta).
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 29, 10, 0, 0));
            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);
            await fixture.Notifications.CancelPendingNotificationsAsync(cita.Id);

            // Re-encolar (simula cita restaurada o nuevo intento)
            await fixture.Notifications.QueueAppointmentConfirmationAsync(cita.Id);

            fixture.Context.ChangeTracker.Clear();
            var messages = await fixture.Context.WhatsAppMessageLogs.ToListAsync();
            Assert.Equal(2, messages.Count);
            Assert.Single(messages, m => m.Status == WhatsAppMessageStatuses.Cancelled);
            Assert.Single(messages, m => m.Status == WhatsAppMessageStatuses.Pending);
        }

        // --- SendConfirmationNowAsync: "reserva online aprobada = cita confirmada" ---

        [Fact]
        public async Task SendConfirmationNow_WhenValid_ShouldSendImmediatelyAndMarkCita()
        {
            // Escenario 1: al aprobar la reserva se envía la confirmación y se registra.
            using var fixture = await Fixture.CreateAsync();
            // Cita a 2 días: en modo relativo se programaría 24h antes; SendConfirmationNow la fuerza ya.
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 28, 10, 0, 0));

            var result = await fixture.Notifications.SendConfirmationNowAsync(cita.Id, "ReservaOnlineAprobada");

            Assert.Equal(WhatsAppConfirmationOutcome.Sent, result.Outcome);
            Assert.Equal(1, fixture.MetaClient.SendCount);

            var message = await fixture.Context.WhatsAppMessageLogs
                .SingleAsync(m => m.NotificationType == WhatsAppNotificationTypes.Confirmation);
            Assert.Equal(WhatsAppMessageStatuses.Sent, message.Status);

            var updatedCita = await fixture.Context.Citas.AsNoTracking().SingleAsync(c => c.Id == cita.Id);
            Assert.NotNull(updatedCita.ConfirmacionWhatsAppEnviadaUtc);
            Assert.True(updatedCita.ConfirmacionEnviada);
        }

        [Fact]
        public async Task SendConfirmationNow_InBatchMode_ShouldSendNowAndBatchShouldNotResend()
        {
            // Escenario 3: la confirmación inmediata al aprobar impide que el lote de las 7am reenvíe.
            using var fixture = await Fixture.CreateAsync();
            await fixture.UpdateAutomationAsync(new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 30,
                ConfirmationScheduleMode = WhatsAppConfirmationScheduleModes.DailyBatchPreviousDay,
                ConfirmationBatchTime = new TimeOnly(9, 0),
                ConfirmationBatchTarget = WhatsAppConfirmationBatchTargets.TomorrowAllDay
            });

            // Cita de mañana: en modo lote NO se encolaría al crear; SendConfirmationNow la fuerza.
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 27, 14, 0, 0));

            var result = await fixture.Notifications.SendConfirmationNowAsync(cita.Id, "ReservaOnlineAprobada");
            Assert.Equal(WhatsAppConfirmationOutcome.Sent, result.Outcome);
            Assert.Equal(1, fixture.MetaClient.SendCount);

            // El lote diario NO debe reenviar: ya existe una confirmación enviada.
            await fixture.Notifications.GenerateDailyBatchAsync();
            await fixture.Notifications.ProcessPendingNotificationsAsync();

            Assert.Equal(1, fixture.MetaClient.SendCount);
            var confirmations = await fixture.Context.WhatsAppMessageLogs
                .CountAsync(m => m.CitaId == cita.Id && m.NotificationType == WhatsAppNotificationTypes.Confirmation);
            Assert.Equal(1, confirmations);
        }

        [Fact]
        public async Task SendConfirmationNow_CalledTwice_ShouldNotDuplicate()
        {
            // Escenario 2: doble click / reintento no debe enviar ni registrar dos veces.
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 28, 10, 0, 0));

            var first = await fixture.Notifications.SendConfirmationNowAsync(cita.Id, "ReservaOnlineAprobada");
            var second = await fixture.Notifications.SendConfirmationNowAsync(cita.Id, "ReservaOnlineAprobada");

            Assert.Equal(WhatsAppConfirmationOutcome.Sent, first.Outcome);
            Assert.Equal(WhatsAppConfirmationOutcome.AlreadySent, second.Outcome);
            Assert.Equal(1, fixture.MetaClient.SendCount);
            var confirmations = await fixture.Context.WhatsAppMessageLogs
                .CountAsync(m => m.CitaId == cita.Id && m.NotificationType == WhatsAppNotificationTypes.Confirmation);
            Assert.Equal(1, confirmations);
        }

        [Fact]
        public async Task SendConfirmationNow_WhenMetaFails_ShouldReturnFailedAndNotMarkSent()
        {
            // Escenario 5: si Meta rechaza, la cita sigue creada pero no se marca como enviada.
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 28, 10, 0, 0));
            fixture.MetaClient.NextSendResult = MetaWhatsAppSendResult.Failed(
                "131026",
                "Message undeliverable",
                HttpStatusCode.BadRequest,
                "{\"error\":{\"message\":\"Message undeliverable\",\"code\":131026}}",
                shouldRetry: false);

            var result = await fixture.Notifications.SendConfirmationNowAsync(cita.Id, "ReservaOnlineAprobada");

            Assert.Equal(WhatsAppConfirmationOutcome.Failed, result.Outcome);
            var message = await fixture.Context.WhatsAppMessageLogs
                .SingleAsync(m => m.NotificationType == WhatsAppNotificationTypes.Confirmation);
            Assert.Equal(WhatsAppMessageStatuses.Failed, message.Status);

            var updatedCita = await fixture.Context.Citas.AsNoTracking().SingleAsync(c => c.Id == cita.Id);
            Assert.Null(updatedCita.ConfirmacionWhatsAppEnviadaUtc);
            Assert.Equal(WhatsAppConfirmationStates.ErrorEnvio, updatedCita.EstadoConfirmacionWhatsApp);
        }

        [Fact]
        public async Task SendConfirmationNow_WhenTenantDisabled_ShouldSkipWithoutSending()
        {
            // Escenario 6: WhatsApp desactivado → no intenta enviar, devuelve Skipped.
            using var fixture = await Fixture.CreateAsync();
            await fixture.UpdateSettingsAsync(isEnabled: false);
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 28, 10, 0, 0));

            var result = await fixture.Notifications.SendConfirmationNowAsync(cita.Id, "ReservaOnlineAprobada");

            Assert.Equal(WhatsAppConfirmationOutcome.Skipped, result.Outcome);
            Assert.Equal(0, fixture.MetaClient.SendCount);
            var message = await fixture.Context.WhatsAppMessageLogs
                .SingleAsync(m => m.NotificationType == WhatsAppNotificationTypes.Confirmation);
            Assert.Equal(WhatsAppMessageStatuses.SkippedTenantDisabled, message.Status);
        }

        [Fact]
        public async Task SendConfirmationNow_WhenPhoneInvalid_ShouldSkipWithoutSending()
        {
            // Escenario 7: teléfono inválido → no revienta, se registra el motivo, devuelve Skipped.
            using var fixture = await Fixture.CreateAsync();
            var cita = await fixture.SeedCitaAsync(phone: "invalid", fechaHora: new DateTime(2026, 5, 28, 10, 0, 0));

            var result = await fixture.Notifications.SendConfirmationNowAsync(cita.Id, "ReservaOnlineAprobada");

            Assert.Equal(WhatsAppConfirmationOutcome.Skipped, result.Outcome);
            Assert.Equal(0, fixture.MetaClient.SendCount);
            var message = await fixture.Context.WhatsAppMessageLogs
                .SingleAsync(m => m.NotificationType == WhatsAppNotificationTypes.Confirmation);
            Assert.Equal(WhatsAppMessageStatuses.SkippedInvalidPhone, message.Status);
        }

        [Fact]
        public async Task SendConfirmationNow_ThenReminderStillWorks()
        {
            // Escenario 4: tras la confirmación inmediata, el recordatorio sigue enviándose normal.
            using var fixture = await Fixture.CreateAsync();
            // Cita a 2h del now: dentro de la ventana de recordatorio (3h) y de confirmación (24h).
            var cita = await fixture.SeedCitaAsync(fechaHora: new DateTime(2026, 5, 26, 12, 30, 0));

            var result = await fixture.Notifications.SendConfirmationNowAsync(cita.Id, "ReservaOnlineAprobada");
            Assert.Equal(WhatsAppConfirmationOutcome.Sent, result.Outcome);

            // El recordatorio se encola y envía por separado (no lo bloquea la confirmación).
            await fixture.Notifications.QueueAppointmentReminderAsync(cita.Id);
            await fixture.Notifications.ProcessPendingNotificationsAsync();

            Assert.Equal(2, fixture.MetaClient.SendCount);
            var reminder = await fixture.Context.WhatsAppMessageLogs
                .SingleAsync(m => m.NotificationType == WhatsAppNotificationTypes.Reminder3Hours);
            Assert.Equal(WhatsAppMessageStatuses.Sent, reminder.Status);
        }

        private sealed class Fixture : IDisposable
        {
            private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
            private readonly ServiceProvider _serviceProvider;

            private Fixture(
                Guid tenantId,
                TestTenantProvider tenantProvider,
                ProyectoIdentity.Datos.ApplicationDbContext context,
                Microsoft.Data.Sqlite.SqliteConnection connection,
                SuscripcionService subscriptionService,
                TenantWhatsAppSettingsService settings,
                FakeMetaWhatsAppClient metaClient,
                CalendarWhatsAppNotificationService notifications,
                ServiceProvider serviceProvider)
            {
                TenantId = tenantId;
                TenantProvider = tenantProvider;
                Context = context;
                _connection = connection;
                SubscriptionService = subscriptionService;
                Settings = settings;
                MetaClient = metaClient;
                Notifications = notifications;
                _serviceProvider = serviceProvider;
            }

            public static DateTime FixedNowLocal => new(2026, 5, 26, 10, 30, 0);

            public static DateTime FixedNowUtc =>
                new DateTimeOffset(FixedNowLocal, TimeSpan.FromHours(-6)).UtcDateTime;

            public Guid TenantId { get; }
            public TestTenantProvider TenantProvider { get; }
            public ProyectoIdentity.Datos.ApplicationDbContext Context { get; }
            public SuscripcionService SubscriptionService { get; }
            public TenantWhatsAppSettingsService Settings { get; }
            public FakeMetaWhatsAppClient MetaClient { get; }
            public CalendarWhatsAppNotificationService Notifications { get; }

            public static async Task<Fixture> CreateAsync(
                bool seedActiveAddon = true,
                bool seedActiveBaseSubscription = true,
                int addonMonthlyLimit = 400,
                DateTime? addonEndsUtc = null,
                // Opción A: comprar el add-on ya no habilita envíos por sí solo. Estos tests validan la
                // programación/envío/consumo de un tenant YA CONFIGURADO, así que por defecto persisten
                // una configuración habilitada con los MISMOS valores efectivos que antes daba el snapshot
                // sintético del add-on (WA400 → diario 15). Los tests de "sin configurar" pasan false.
                bool seedEnabledSettings = true)
            {
                var tenantId = Guid.NewGuid();
                var tenantProvider = new TestTenantProvider { TenantId = tenantId };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
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
                var serviceProvider = new ServiceCollection().BuildServiceProvider();
                var tenantExecution = new TenantExecutionService(
                    serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<TenantExecutionService>.Instance);
                var tenantDisplayNameService = new TenantDisplayNameService(
                    context,
                    tenantProvider,
                    new HttpContextAccessor());
                var metaClient = new FakeMetaWhatsAppClient();
                var notifications = new CalendarWhatsAppNotificationService(
                    context,
                    metaClient,
                    options,
                    businessDateTimeProvider,
                    settings,
                    tenantProvider,
                    tenantDisplayNameService,
                    tenantExecution,
                    NullLogger<CalendarWhatsAppNotificationService>.Instance);

                context.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Tenant WhatsApp" });

                if (seedActiveBaseSubscription)
                {
                    var basePlanId = Guid.NewGuid();
                    context.Planes.Add(new Plan
                    {
                        Id = basePlanId,
                        Codigo = PlanCodes.Basic,
                        Nombre = "Basico",
                        Moneda = "CRC",
                        PrecioMensual = 8000m,
                        MaxFuncionarios = 1,
                        Activo = true
                    });

                    context.Suscripciones.Add(new Suscripcion
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        PlanId = basePlanId,
                        CodigoPlan = PlanCodes.Basic,
                        Estado = EstadoSuscripcion.Activa,
                        Proveedor = PaymentProviderType.Tilopay,
                        FechaInicio = FixedNowUtc.AddDays(-3),
                        FechaFin = FixedNowUtc.AddDays(27),
                        FechaProximoCobroUtc = FixedNowUtc.AddDays(27),
                        FechaUltimaActualizacionUtc = FixedNowUtc
                    });
                }

                if (seedActiveAddon)
                {
                    var addOnPlanId = Guid.NewGuid();
                    context.Planes.Add(new Plan
                    {
                        Id = addOnPlanId,
                        Codigo = PlanCodes.WhatsApp400,
                        Nombre = "WhatsApp 400",
                        Moneda = "CRC",
                        PrecioMensual = 6000m,
                        LimiteMensajesMensual = addonMonthlyLimit,
                        Activo = true
                    });
                    context.TenantSubscriptionAddons.Add(new TenantSubscriptionAddon
                    {
                        Id = Guid.NewGuid(),
                        TenantId = tenantId,
                        PlanId = addOnPlanId,
                        AddonCode = PlanCodes.WhatsApp400,
                        Estado = EstadoSuscripcion.Activa,
                        MonthlyMessageLimit = addonMonthlyLimit,
                        FechaInicio = FixedNowUtc.AddDays(-1),
                        FechaFin = addonEndsUtc ?? FixedNowUtc.AddDays(29),
                        CreatedAtUtc = FixedNowUtc,
                        UpdatedAtUtc = FixedNowUtc
                    });

                    if (seedEnabledSettings)
                    {
                        // Configuración técnica persistida (tenant ya configuró WhatsApp). El límite diario
                        // replica el default del add-on WA400 (15) para no alterar los asserts existentes.
                        context.TenantWhatsAppSettings.Add(new TenantWhatsAppSettings
                        {
                            TenantId = tenantId,
                            IsEnabled = true,
                            SendConfirmationOnCreate = true,
                            SendReminderThreeHoursBefore = true,
                            DailyMessageLimit = 15,
                            TimeZoneId = TenantWhatsAppSettings.DefaultTimeZoneId,
                            CreatedAtUtc = FixedNowUtc,
                            UpdatedAtUtc = FixedNowUtc
                        });
                    }
                }

                await context.SaveChangesAsync();

                return new Fixture(
                    tenantId,
                    tenantProvider,
                    context,
                    connection,
                    subscriptionService,
                    settings,
                    metaClient,
                    notifications,
                    serviceProvider);
            }

            public Task UpdateSettingsAsync(
                bool isEnabled,
                int dailyLimit = TenantWhatsAppSettings.DefaultDailyMessageLimit,
                bool sendConfirmation = true,
                bool sendReminder = true) =>
                Settings.UpdateSettingsAsync(
                    TenantId,
                    new TenantWhatsAppSettingsUpdateDto
                    {
                        IsEnabled = isEnabled,
                        SendConfirmationOnCreate = sendConfirmation,
                        SendReminderThreeHoursBefore = sendReminder,
                        DailyMessageLimit = dailyLimit
                    },
                    "platform-user");

            public Task UpdateAutomationAsync(TenantWhatsAppSettingsUpdateDto dto) =>
                Settings.UpdateSettingsAsync(TenantId, dto, "platform-user");

            public async Task SeedAccountDisplayNameAsync(string displayName)
            {
                Context.Users.Add(new AppUsuario
                {
                    Id = $"owner-{TenantId:N}",
                    TenantId = TenantId,
                    UserName = $"owner-{TenantId:N}@test.local",
                    Email = $"owner-{TenantId:N}@test.local",
                    Name = displayName,
                    State = true
                });

                await Context.SaveChangesAsync();
                Context.ChangeTracker.Clear();
            }

            public async Task<LuxuryApp.Models.DataBase.ClientesModel> SeedClienteAsync(
                string nombre,
                string telefono,
                bool aceptaMensajesWhatsApp)
            {
                var cliente = new LuxuryApp.Models.DataBase.ClientesModel
                {
                    Nombre = nombre,
                    NumeroTelefono = telefono,
                    AceptaMensajesWhatsApp = aceptaMensajesWhatsApp,
                    WhatsAppConsentUpdatedAtUtc = new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc),
                    WhatsAppConsentSource = "ClienteForm",
                    WhatsAppConsentTextVersion = "wa_optin_v1",
                    FrecuenciaVisita = 30,
                    FechaUltimaVisita = new DateTime(2026, 5, 1)
                };

                Context.Clientes.Add(cliente);
                await Context.SaveChangesAsync();
                return cliente;
            }

            public async Task<Cita> SeedCitaAsync(
                string tipo = "CITA",
                string? phone = "88889999",
                DateTime? fechaHora = null,
                int? clienteId = null,
                bool whatsAppConsentAtCreation = true)
            {
                var puesto = new Puesto
                {
                    NombrePuesto = $"Puesto {Guid.NewGuid():N}",
                    Detalle = "WhatsApp",
                    Activo = true
                };
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

                LuxuryApp.Models.DataBase.ClientesModel? cliente = null;
                if (clienteId.HasValue)
                {
                    cliente = await Context.Clientes.FirstAsync(current => current.Id == clienteId.Value);
                }

                var cita = new Cita
                {
                    NombreCliente = tipo == "DESCANSO" ? "DESCANSO" : cliente?.Nombre ?? "Cliente WhatsApp",
                    TelefonoCliente = tipo == "DESCANSO" ? null : cliente?.NumeroTelefono ?? phone,
                    ClienteId = cliente?.Id,
                    FechaHoraCita = fechaHora ?? new DateTime(2026, 5, 27, 10, 0, 0),
                    FuncionarioId = funcionario.IdFuncionario,
                    Tipo = tipo,
                    WhatsAppConsentAtCreation = tipo == "DESCANSO"
                        ? false
                        : cliente?.AceptaMensajesWhatsApp ?? whatsAppConsentAtCreation,
                    WhatsAppConsentSource = tipo == "DESCANSO"
                        ? null
                        : cliente is not null
                            ? "ClienteRegistrado"
                            : (whatsAppConsentAtCreation ? "CitaManual" : "SinConsentimiento"),
                    WhatsAppConsentCapturedAtUtc = tipo == "DESCANSO"
                        ? null
                        : (cliente is not null || whatsAppConsentAtCreation
                            ? new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc)
                            : null)
                };
                Context.Citas.Add(cita);
                await Context.SaveChangesAsync();
                return cita;
            }

            public void Dispose()
            {
                Context.Dispose();
                _connection.Dispose();
                _serviceProvider.Dispose();
            }
        }

        private sealed class FakeMetaWhatsAppClient : IMetaWhatsAppClient
        {
            public int SendCount { get; private set; }
            public string? LastBusinessName { get; private set; }

            public MetaWhatsAppSendResult? NextSendResult { get; set; }

            public string? NormalizePhoneNumber(string? phoneNumber) =>
                string.IsNullOrWhiteSpace(phoneNumber) ||
                string.Equals(phoneNumber, "invalid", StringComparison.OrdinalIgnoreCase)
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
                CancellationToken cancellationToken = default)
            {
                SendCount++;
                LastBusinessName = businessName;
                return Task.FromResult(ConsumeResult($"confirmation-{SendCount}"));
            }

            public Task<MetaWhatsAppSendResult> SendReminderTemplateAsync(
                string recipientPhone,
                string customerName,
                string businessName,
                string appointmentTime,
                string professionalName,
                CancellationToken cancellationToken = default)
            {
                SendCount++;
                LastBusinessName = businessName;
                return Task.FromResult(ConsumeResult($"reminder-{SendCount}"));
            }

            public Task<MetaWhatsAppSendResult> SendCancellationTemplateAsync(
                string recipientPhone,
                WhatsAppCancellationTemplateParameters parameters,
                CancellationToken cancellationToken = default)
            {
                SendCount++;
                LastBusinessName = parameters.BusinessName;
                return Task.FromResult(ConsumeResult($"cancellation-{SendCount}"));
            }

            public Task<MetaWhatsAppSendResult> SendTextMessageAsync(
                string recipientPhone,
                string message,
                CancellationToken cancellationToken = default)
            {
                SendCount++;
                return Task.FromResult(ConsumeResult($"text-{SendCount}"));
            }

            public Task<MetaWhatsAppConfigurationDiagnosticResult> TestConfigurationAsync(
                CancellationToken cancellationToken = default) =>
                Task.FromResult(new MetaWhatsAppConfigurationDiagnosticResult(
                    Success: true,
                    Configuration: MetaWhatsAppConfigurationSnapshot.Create(new MetaWhatsAppOptions
                    {
                        Enabled = true,
                        GraphApiVersion = "v25.0",
                        BaseUrl = "https://graph.facebook.com",
                        PhoneNumberId = "1000000000000001",
                        WhatsAppBusinessAccountId = "2000000000000002",
                        AccessToken = "EAATEST000000000000000000000",
                        AppSecret = "00000000000000000000000000000000"
                    }),
                    PhoneNumberProbe: new MetaWhatsAppEndpointProbeResult(
                        Success: true,
                        Endpoint: "https://graph.facebook.com/v25.0/1000000000000001?fields=id,display_phone_number,verified_name",
                        HttpStatus: 200,
                        DisplayPhoneNumber: "+50688889999",
                        VerifiedName: "LuxuryCloud",
                        ErrorType: null,
                        ErrorCode: null,
                        ErrorSubcode: null,
                        ErrorMessage: null,
                        FbTraceId: null,
                        ResponsePreview: null),
                    WabaPhoneNumbersProbe: null,
                    PhoneNumberBelongsToConfiguredWaba: null));

            private MetaWhatsAppSendResult ConsumeResult(string successId)
            {
                if (NextSendResult is null)
                {
                    return MetaWhatsAppSendResult.Succeeded(successId, HttpStatusCode.OK, responseBody: null);
                }

                var result = NextSendResult;
                NextSendResult = null;
                return result;
            }
        }
    }
}
