using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.WhatsApp;
using LuxuryApp.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class TenantWhatsAppSettingsServiceTests
    {
        [Fact]
        public async Task GetSettingsForTenantAsync_WhenSettingsDoNotExist_ShouldAssumeDisabledWithoutPersisting()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            var settings = await service.GetSettingsForTenantAsync(tenantId);

            Assert.False(settings.Exists);
            Assert.False(settings.IsEnabled);
            Assert.Equal(TenantWhatsAppSettings.DefaultDailyMessageLimit, settings.DailyMessageLimit);
            Assert.Empty(context.TenantWhatsAppSettings);
        }

        [Fact]
        public async Task UpdateSettingsAsync_ShouldPersistConfigurableSchedulingFields()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);
            await SeedTenantAsync(context, tenantId);

            await service.UpdateSettingsAsync(tenantId, new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 30,
                ConfirmationHoursBefore = 12,
                SendConfirmationImmediatelyIfInsideWindow = false,
                ReminderHoursBefore = 6,
                SendReminderImmediatelyIfInsideWindow = false
            }, "platform-user");

            var settings = await service.GetSettingsForTenantAsync(tenantId);

            Assert.True(settings.Exists);
            Assert.Equal(12, settings.ConfirmationHoursBefore);
            Assert.False(settings.SendConfirmationImmediatelyIfInsideWindow);
            Assert.Equal(6, settings.ReminderHoursBefore);
            Assert.False(settings.SendReminderImmediatelyIfInsideWindow);
            Assert.Equal(WhatsAppConfirmationScheduleModes.RelativeBeforeAppointment, settings.ConfirmationScheduleMode);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(200)]
        public async Task UpdateSettingsAsync_WithInvalidConfirmationHours_ShouldThrow(int invalidHours)
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateSettingsAsync(
                tenantId,
                new TenantWhatsAppSettingsUpdateDto
                {
                    IsEnabled = true,
                    SendConfirmationOnCreate = true,
                    DailyMessageLimit = 30,
                    ConfirmationHoursBefore = invalidHours
                },
                "platform-user"));
        }

        [Theory]
        [InlineData(PlanCodes.WhatsApp400, "WhatsApp 400", 6000, 400, 15)]
        [InlineData(PlanCodes.WhatsApp800, "WhatsApp 800", 12000, 800, 30)]
        [InlineData(PlanCodes.WhatsApp1200, "WhatsApp 1200", 18000, 1200, 45)]
        public async Task GetSettingsForTenantAsync_WithActiveAddonAndNoStoredSettings_ShouldExposeEnabledDefaults(
            string addonCode,
            string addonName,
            decimal monthlyPrice,
            int monthlyLimit,
            int dailyLimit)
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId, addonCode, addonName, monthlyPrice, monthlyLimit);

            var settings = await service.GetSettingsForTenantAsync(tenantId);

            Assert.False(settings.Exists);
            Assert.True(settings.IsEnabled);
            Assert.True(settings.SendConfirmationOnCreate);
            Assert.True(settings.SendReminderThreeHoursBefore);
            Assert.Equal(dailyLimit, settings.DailyMessageLimit);
        }

        [Fact]
        public async Task CanSendNotificationAsync_WhenDailyLimitWasReached_ShouldDenyWithStableCode()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId);
            await service.UpdateSettingsAsync(
                tenantId,
                new TenantWhatsAppSettingsUpdateDto
                {
                    IsEnabled = true,
                    DailyMessageLimit = 1
                },
                "platform-user");

            context.WhatsAppMessageLogs.Add(CreateLog(
                tenantId,
                WhatsAppMessageDirections.Outbound,
                WhatsAppNotificationTypes.Confirmation,
                WhatsAppMessageStatuses.Sent));
            await context.SaveChangesAsync();

            var decision = await service.CanSendNotificationAsync(tenantId, WhatsAppNotificationTypes.Reminder3Hours);

            Assert.False(decision.CanSend);
            Assert.Equal(WhatsAppErrorCodes.DailyLimitExceeded, decision.ErrorCode);
            Assert.Equal(1, decision.TodayUsage);
            Assert.Equal(1, decision.DailyMessageLimit);
        }

        [Theory]
        [InlineData(PlanCodes.WhatsApp400, "WhatsApp 400", 6000, 400)]
        [InlineData(PlanCodes.WhatsApp800, "WhatsApp 800", 12000, 800)]
        [InlineData(PlanCodes.WhatsApp1200, "WhatsApp 1200", 18000, 1200)]
        public async Task CanSendNotificationAsync_WithActiveAddon_ShouldExposeConfiguredMonthlyLimit(
            string addonCode,
            string addonName,
            decimal monthlyPrice,
            int monthlyLimit)
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId, addonCode, addonName, monthlyPrice, monthlyLimit);
            // Opción A: para enviar hace falta configuración persistida y habilitada.
            await service.UpdateSettingsAsync(
                tenantId,
                new TenantWhatsAppSettingsUpdateDto { IsEnabled = true, DailyMessageLimit = 30 },
                "platform-user");

            var decision = await service.CanSendNotificationAsync(tenantId, WhatsAppNotificationTypes.Confirmation);

            Assert.True(decision.CanSend);
            Assert.Equal(0, decision.MonthlyUsage);
            // La cuota mensual se resuelve desde el add-on activo (fuente comercial), no desde settings.
            Assert.Equal(monthlyLimit, decision.MonthlyMessageLimit);
        }

        [Fact]
        public async Task CanSendNotificationAsync_WithActiveAddonButNoStoredSettings_ShouldDenyNotConfigured()
        {
            // Opción A: comprar el paquete crea el add-on comercial pero NO habilita envíos. Sin una
            // configuración persistida (TenantWhatsAppSettings) el envío se bloquea con NotConfigured.
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId, PlanCodes.WhatsApp400, "WhatsApp 400", 6000m, 400);

            var decision = await service.CanSendNotificationAsync(tenantId, WhatsAppNotificationTypes.Confirmation);

            Assert.False(decision.CanSend);
            Assert.Equal(WhatsAppErrorCodes.NotConfigured, decision.ErrorCode);
            // El add-on activo debe seguir exponiendo la cuota mensual comercial en la decisión.
            Assert.Equal(400, decision.MonthlyMessageLimit);
            // No hay fila de settings persistida.
            Assert.Empty(context.TenantWhatsAppSettings);
        }

        [Fact]
        public async Task CanSendNotificationAsync_WithoutActiveAddon_ShouldDenyWithNoActiveWhatsAppAddon()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId);
            await service.UpdateSettingsAsync(
                tenantId,
                new TenantWhatsAppSettingsUpdateDto
                {
                    IsEnabled = true,
                    DailyMessageLimit = 10
                },
                "platform-user");

            var decision = await service.CanSendNotificationAsync(tenantId, WhatsAppNotificationTypes.Confirmation);

            Assert.False(decision.CanSend);
            Assert.Equal(WhatsAppErrorCodes.NoActiveWhatsAppAddon, decision.ErrorCode);
        }

        [Fact]
        public async Task CanSendNotificationAsync_WithoutActiveBaseSubscription_ShouldDenyWithNoActiveBaseSubscription()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId);

            var decision = await service.CanSendNotificationAsync(tenantId, WhatsAppNotificationTypes.Confirmation);

            Assert.False(decision.CanSend);
            Assert.Equal(WhatsAppErrorCodes.NoActiveBaseSubscription, decision.ErrorCode);
        }

        [Fact]
        public async Task CanSendNotificationAsync_WhenMonthlyLimitWasReached_ShouldDenyWithMonthlyLimitExceeded()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId, PlanCodes.WhatsApp400, "WhatsApp 400", 6000m, 1);
            // Opción A: configuración persistida y habilitada; el bloqueo debe ser por saldo mensual.
            await service.UpdateSettingsAsync(
                tenantId,
                new TenantWhatsAppSettingsUpdateDto { IsEnabled = true, DailyMessageLimit = 30 },
                "platform-user");

            context.WhatsAppMessageLogs.Add(CreateLog(
                tenantId,
                WhatsAppMessageDirections.Outbound,
                WhatsAppNotificationTypes.Confirmation,
                WhatsAppMessageStatuses.Sent));
            await context.SaveChangesAsync();

            var decision = await service.CanSendNotificationAsync(tenantId, WhatsAppNotificationTypes.Reminder3Hours);

            Assert.False(decision.CanSend);
            Assert.Equal(WhatsAppErrorCodes.MonthlyLimitExceeded, decision.ErrorCode);
            Assert.Equal(1, decision.MonthlyUsage);
            Assert.Equal(1, decision.MonthlyMessageLimit);
        }

        [Fact]
        public async Task GetTodayUsageAsync_ShouldCountOnlySuccessfulOutboundNotifications()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);

            context.WhatsAppMessageLogs.AddRange(
                CreateLog(tenantId, WhatsAppMessageDirections.Outbound, WhatsAppNotificationTypes.Confirmation, WhatsAppMessageStatuses.Pending),
                CreateLog(tenantId, WhatsAppMessageDirections.Outbound, WhatsAppNotificationTypes.Reminder3Hours, WhatsAppMessageStatuses.Processing),
                CreateLog(tenantId, WhatsAppMessageDirections.Outbound, WhatsAppNotificationTypes.Confirmation, WhatsAppMessageStatuses.Sent),
                CreateLog(tenantId, WhatsAppMessageDirections.Inbound, WhatsAppNotificationTypes.Reply, WhatsAppMessageStatuses.Received),
                CreateLog(tenantId, WhatsAppMessageDirections.Status, WhatsAppNotificationTypes.Status, WhatsAppMessageStatuses.Delivered),
                CreateLog(tenantId, WhatsAppMessageDirections.Outbound, WhatsAppNotificationTypes.Confirmation, WhatsAppMessageStatuses.Delivered),
                CreateLog(tenantId, WhatsAppMessageDirections.Outbound, WhatsAppNotificationTypes.Confirmation, WhatsAppMessageStatuses.Read),
                CreateLog(tenantId, WhatsAppMessageDirections.Outbound, WhatsAppNotificationTypes.Confirmation, WhatsAppMessageStatuses.SkippedTenantDisabled));
            await context.SaveChangesAsync();

            var usage = await service.GetTodayUsageAsync(tenantId);

            Assert.Equal(3, usage);
        }

        [Fact]
        public async Task Service_ShouldRejectCrossTenantSettingsAccess()
        {
            var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.GetSettingsForTenantAsync(Guid.NewGuid()));

            Assert.Contains("contexto de su tenant", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task UpdateSettingsAsync_WhenBothEnabled_ShouldPersistBothActive()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId);

            await service.UpdateSettingsAsync(tenantId, new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 30
            }, "user1");

            var result = await service.GetSettingsForTenantAsync(tenantId);

            Assert.True(result.SendConfirmationOnCreate);
            Assert.True(result.SendReminderThreeHoursBefore);
            Assert.True(result.IsEnabled);
        }

        [Fact]
        public async Task UpdateSettingsAsync_WhenOnlyConfirmationsEnabled_ShouldPersistOnlyConfirmations()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId);

            await service.UpdateSettingsAsync(tenantId, new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = false,
                DailyMessageLimit = 30
            }, "user1");

            var result = await service.GetSettingsForTenantAsync(tenantId);

            Assert.True(result.SendConfirmationOnCreate);
            Assert.False(result.SendReminderThreeHoursBefore);
            Assert.True(result.IsEnabled);
        }

        [Fact]
        public async Task UpdateSettingsAsync_WhenOnlyRemindersEnabled_ShouldPersistOnlyReminders()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId);

            await service.UpdateSettingsAsync(tenantId, new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = false,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 30
            }, "user1");

            var result = await service.GetSettingsForTenantAsync(tenantId);

            Assert.False(result.SendConfirmationOnCreate);
            Assert.True(result.SendReminderThreeHoursBefore);
            Assert.True(result.IsEnabled);
        }

        [Fact]
        public async Task UpdateSettingsAsync_WhenBothDisabled_ShouldPersistBothDisabled()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId);

            await service.UpdateSettingsAsync(tenantId, new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = false,
                SendConfirmationOnCreate = false,
                SendReminderThreeHoursBefore = false,
                DailyMessageLimit = 30
            }, "user1");

            var result = await service.GetSettingsForTenantAsync(tenantId);

            Assert.False(result.SendConfirmationOnCreate);
            Assert.False(result.SendReminderThreeHoursBefore);
            Assert.False(result.IsEnabled);
        }

        [Fact]
        public async Task UpdateSettingsAsync_ShouldPersistAndReadbackMatchSavedValues()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId);

            await service.UpdateSettingsAsync(tenantId, new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = false,
                DailyMessageLimit = 25
            }, "user1");

            await service.UpdateSettingsAsync(tenantId, new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = false,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = 25
            }, "user1");

            var result = await service.GetSettingsForTenantAsync(tenantId);

            Assert.False(result.SendConfirmationOnCreate);
            Assert.True(result.SendReminderThreeHoursBefore);
        }

        [Fact]
        public async Task UpdateSettingsAsync_ShouldPreserveDailyMessageLimit()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId, PlanCodes.WhatsApp800, "WhatsApp 800", 12000m, 800);

            var expectedDailyLimit = 30;
            await service.UpdateSettingsAsync(tenantId, new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = true,
                DailyMessageLimit = expectedDailyLimit
            }, "user1");

            await service.UpdateSettingsAsync(tenantId, new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = false,
                SendReminderThreeHoursBefore = false,
                DailyMessageLimit = expectedDailyLimit
            }, "user1");

            var result = await service.GetSettingsForTenantAsync(tenantId);

            Assert.Equal(expectedDailyLimit, result.DailyMessageLimit);
        }

        [Fact]
        public async Task UpdateSettingsAsync_ShouldNotModifyAddonMonthlyMessageLimit()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId, PlanCodes.WhatsApp800, "WhatsApp 800", 12000m, 800);

            var addonBefore = context.TenantSubscriptionAddons.First(a => a.TenantId == tenantId);
            var monthlyLimitBefore = addonBefore.MonthlyMessageLimit;

            await service.UpdateSettingsAsync(tenantId, new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = false,
                SendReminderThreeHoursBefore = false,
                DailyMessageLimit = 30
            }, "user1");

            var addonAfter = context.TenantSubscriptionAddons.First(a => a.TenantId == tenantId);
            Assert.Equal(monthlyLimitBefore, addonAfter.MonthlyMessageLimit);
        }

        [Fact]
        public async Task UpdateSettingsAsync_ShouldNotDeactivateActiveAddon()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId);

            await service.UpdateSettingsAsync(tenantId, new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = false,
                SendConfirmationOnCreate = false,
                SendReminderThreeHoursBefore = false,
                DailyMessageLimit = 15
            }, "user1");

            var addonAfter = context.TenantSubscriptionAddons.First(a => a.TenantId == tenantId);
            Assert.Equal(EstadoSuscripcion.Activa, addonAfter.Estado);
        }

        [Fact]
        public async Task UpdateSettingsAsync_ShouldNotModifyBaseSubscription()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveBaseSubscriptionAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId);

            var subscriptionBefore = context.Suscripciones.First(s => s.TenantId == tenantId);
            var estadoBefore = subscriptionBefore.Estado;
            var fechaFinBefore = subscriptionBefore.FechaFin;

            await service.UpdateSettingsAsync(tenantId, new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = false,
                SendConfirmationOnCreate = false,
                SendReminderThreeHoursBefore = false,
                DailyMessageLimit = 15
            }, "user1");

            var subscriptionAfter = context.Suscripciones.First(s => s.TenantId == tenantId);
            Assert.Equal(estadoBefore, subscriptionAfter.Estado);
            Assert.Equal(fechaFinBefore, subscriptionAfter.FechaFin);
        }

        [Fact]
        public async Task GetSettingsForTenantAsync_ShouldExposeIdenticalValuesAsUpdateSettingsAsync_SimulatingMiSuscripcionAndPlatformSync()
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedTenantAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId);

            await service.UpdateSettingsAsync(tenantId, new TenantWhatsAppSettingsUpdateDto
            {
                IsEnabled = true,
                SendConfirmationOnCreate = true,
                SendReminderThreeHoursBefore = false,
                DailyMessageLimit = 20
            }, "platform-user");

            // Mi Suscripción y Platform llaman a GetSettingsForTenantAsync — ambas deben leer los mismos valores.
            var snapshotA = await service.GetSettingsForTenantAsync(tenantId);
            var snapshotB = await service.GetSettingsForTenantAsync(tenantId);

            Assert.Equal(snapshotA.SendConfirmationOnCreate, snapshotB.SendConfirmationOnCreate);
            Assert.Equal(snapshotA.SendReminderThreeHoursBefore, snapshotB.SendReminderThreeHoursBefore);
            Assert.Equal(snapshotA.IsEnabled, snapshotB.IsEnabled);
            Assert.Equal(snapshotA.DailyMessageLimit, snapshotB.DailyMessageLimit);
            Assert.True(snapshotA.SendConfirmationOnCreate);
            Assert.False(snapshotA.SendReminderThreeHoursBefore);
        }

        private static TenantWhatsAppSettingsService CreateService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            TestTenantProvider tenantProvider)
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            var accessCache = new TenantCommercialAccessCache(cache);
            var businessDateTimeProvider = new FixedBusinessDateTimeProvider();
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

            return new TenantWhatsAppSettingsService(
                context,
                tenantProvider,
                new StaticOptionsMonitor<MetaWhatsAppOptions>(new MetaWhatsAppOptions { Enabled = true }),
                subscriptionService,
                businessDateTimeProvider,
                commercialAccessResolver,
                NullLogger<TenantWhatsAppSettingsService>.Instance);
        }

        private static WhatsAppMessageLog CreateLog(
            Guid tenantId,
            string direction,
            string notificationType,
            string status)
        {
            var createdAtUtc = GetFixedNowUtc();
            var log = new WhatsAppMessageLog
            {
                TenantId = tenantId,
                Direction = direction,
                NotificationType = notificationType,
                Status = status,
                CreatedAtUtc = createdAtUtc
            };

            switch (status)
            {
                case WhatsAppMessageStatuses.Sent:
                    log.SentAtUtc = createdAtUtc;
                    break;
                case WhatsAppMessageStatuses.Delivered:
                    log.DeliveredAtUtc = createdAtUtc;
                    break;
                case WhatsAppMessageStatuses.Read:
                    log.ReadAtUtc = createdAtUtc;
                    break;
            }

            return log;
        }

        private static async Task SeedTenantAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId)
        {
            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant WhatsApp"
            });
            await context.SaveChangesAsync();
        }

        private static async Task SeedActiveBaseSubscriptionAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId)
        {
            var planId = Guid.NewGuid();
            var nowUtc = GetFixedNowUtc();
            context.Planes.Add(new Plan
            {
                Id = planId,
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
                PlanId = planId,
                CodigoPlan = PlanCodes.Basic,
                Estado = EstadoSuscripcion.Activa,
                Proveedor = PaymentProviderType.Tilopay,
                FechaInicio = nowUtc.AddDays(-3),
                FechaFin = nowUtc.AddDays(27),
                FechaProximoCobroUtc = nowUtc.AddDays(27),
                FechaUltimaActualizacionUtc = nowUtc
            });

            await context.SaveChangesAsync();
        }

        private static async Task SeedActiveAddonAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId,
            string addonCode = PlanCodes.WhatsApp400,
            string addonName = "WhatsApp 400",
            decimal monthlyPrice = 6000m,
            int monthlyLimit = 400)
        {
            var nowUtc = GetFixedNowUtc();
            var planId = Guid.NewGuid();
            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = addonCode,
                Nombre = addonName,
                Moneda = "CRC",
                PrecioMensual = monthlyPrice,
                LimiteMensajesMensual = monthlyLimit,
                Activo = true
            });

            context.TenantSubscriptionAddons.Add(new TenantSubscriptionAddon
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                AddonCode = addonCode,
                Estado = EstadoSuscripcion.Activa,
                MonthlyMessageLimit = monthlyLimit,
                FechaInicio = nowUtc.AddDays(-1),
                FechaFin = nowUtc.AddDays(29),
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            });

            await context.SaveChangesAsync();
        }

        // ── Nuevos tests: exención comercial y WhatsApp ─────────────────────────────

        [Fact]
        public async Task CanSendNotificationAsync_ExemptTenantWithActiveForcedPlan_ShouldAllowWithoutPaidSubscription()
        {
            // Regresión del bug: tenant exento con plan forzado Business debe poder enviar
            // WhatsApp aunque no tenga suscripción pagada activa.
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedExemptTenantWithForcedPlanAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId);
            // Opción A: el tenant exento ya configuró WhatsApp (settings persistidos y habilitados).
            await service.UpdateSettingsAsync(
                tenantId,
                new TenantWhatsAppSettingsUpdateDto { IsEnabled = true, DailyMessageLimit = 30 },
                "platform-user");

            var decision = await service.CanSendNotificationAsync(tenantId, WhatsAppNotificationTypes.Confirmation);

            Assert.True(decision.CanSend);
        }

        [Fact]
        public async Task CanSendNotificationAsync_ExemptTenantWithoutForcedPlan_ShouldDenyWithNoActiveBaseSubscription()
        {
            // Tenant marcado como exento pero sin plan forzado asignado: debe seguir bloqueado.
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedExemptTenantWithoutForcedPlanAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId);

            var decision = await service.CanSendNotificationAsync(tenantId, WhatsAppNotificationTypes.Confirmation);

            Assert.False(decision.CanSend);
            Assert.Equal(WhatsAppErrorCodes.NoActiveBaseSubscription, decision.ErrorCode);
        }

        [Fact]
        public async Task CanSendNotificationAsync_ExemptTenantWithInactiveForcedPlan_ShouldDenyWithNoActiveBaseSubscription()
        {
            // Tenant exento cuyo plan forzado fue desactivado: debe quedar bloqueado.
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedExemptTenantWithInactiveForcedPlanAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId);

            var decision = await service.CanSendNotificationAsync(tenantId, WhatsAppNotificationTypes.Confirmation);

            Assert.False(decision.CanSend);
            Assert.Equal(WhatsAppErrorCodes.NoActiveBaseSubscription, decision.ErrorCode);
        }

        [Fact]
        public async Task CanSendNotificationAsync_InactiveTenant_ShouldDenyWithNoActiveBaseSubscription()
        {
            // Tenant suspendido/inactivo: bloqueado aunque tenga add-on y saldo.
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedInactiveTenantAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId);

            var decision = await service.CanSendNotificationAsync(tenantId, WhatsAppNotificationTypes.Confirmation);

            Assert.False(decision.CanSend);
            Assert.Equal(WhatsAppErrorCodes.NoActiveBaseSubscription, decision.ErrorCode);
        }

        [Fact]
        public async Task CanSendNotificationAsync_ExemptTenantWithExhaustedMonthlyBalance_ShouldDenyWithMonthlyLimitExceeded()
        {
            // Tenant exento con plan forzado activo pero saldo mensual agotado:
            // el bloqueo debe ser por saldo, NO por base subscription.
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedExemptTenantWithForcedPlanAsync(context, tenantId);
            await SeedActiveAddonAsync(context, tenantId, PlanCodes.WhatsApp400, "WhatsApp 400", 6000m, monthlyLimit: 1);
            // Opción A: settings persistidos y habilitados; el bloqueo debe ser por saldo, no por config.
            await service.UpdateSettingsAsync(
                tenantId,
                new TenantWhatsAppSettingsUpdateDto { IsEnabled = true, DailyMessageLimit = 30 },
                "platform-user");

            context.WhatsAppMessageLogs.Add(CreateLog(
                tenantId,
                WhatsAppMessageDirections.Outbound,
                WhatsAppNotificationTypes.Confirmation,
                WhatsAppMessageStatuses.Sent));
            await context.SaveChangesAsync();

            var decision = await service.CanSendNotificationAsync(tenantId, WhatsAppNotificationTypes.Reminder3Hours);

            Assert.False(decision.CanSend);
            Assert.Equal(WhatsAppErrorCodes.MonthlyLimitExceeded, decision.ErrorCode);
        }

        [Fact]
        public async Task CanSendNotificationAsync_ExemptTenantWithExpiredAddon_ShouldDenyWithNoActiveWhatsAppAddon()
        {
            // Tenant exento con plan forzado activo pero add-on WhatsApp vencido:
            // el bloqueo debe ser por add-on, NO por base subscription.
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;
            var service = CreateService(context, tenantProvider);

            await SeedExemptTenantWithForcedPlanAsync(context, tenantId);
            await SeedExpiredAddonAsync(context, tenantId);

            var decision = await service.CanSendNotificationAsync(tenantId, WhatsAppNotificationTypes.Confirmation);

            Assert.False(decision.CanSend);
            Assert.Equal(WhatsAppErrorCodes.NoActiveWhatsAppAddon, decision.ErrorCode);
        }

        // ── Helpers de seeding para exención comercial ───────────────────────────

        private static async Task SeedExemptTenantWithForcedPlanAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId)
        {
            var planId = Guid.NewGuid();
            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.Business,
                Nombre = "Business",
                Moneda = "CRC",
                PrecioMensual = 25000m,
                MaxFuncionarios = 10,
                Activo = true
            });
            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Exento Patrocinado",
                Activo = true,
                CommercialAccessMode = TenantCommercialAccessMode.Exempt,
                ForcedPlanId = planId
            });
            await context.SaveChangesAsync();
        }

        private static async Task SeedExemptTenantWithoutForcedPlanAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId)
        {
            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Exento Sin Plan",
                Activo = true,
                CommercialAccessMode = TenantCommercialAccessMode.Exempt,
                ForcedPlanId = null
            });
            await context.SaveChangesAsync();
        }

        private static async Task SeedExemptTenantWithInactiveForcedPlanAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId)
        {
            var planId = Guid.NewGuid();
            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.Business,
                Nombre = "Business Desactivado",
                Moneda = "CRC",
                PrecioMensual = 25000m,
                MaxFuncionarios = 10,
                Activo = false   // plan desactivado — exención no es válida
            });
            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Exento Plan Inactivo",
                Activo = true,
                CommercialAccessMode = TenantCommercialAccessMode.Exempt,
                ForcedPlanId = planId
            });
            await context.SaveChangesAsync();
        }

        private static async Task SeedInactiveTenantAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId)
        {
            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = "Tenant Inactivo",
                Activo = false
            });
            await context.SaveChangesAsync();
        }

        private static async Task SeedExpiredAddonAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            Guid tenantId)
        {
            var nowUtc = GetFixedNowUtc();
            var planId = Guid.NewGuid();
            context.Planes.Add(new Plan
            {
                Id = planId,
                Codigo = PlanCodes.WhatsApp400,
                Nombre = "WhatsApp 400",
                Moneda = "CRC",
                PrecioMensual = 6000m,
                LimiteMensajesMensual = 400,
                Activo = true
            });
            context.TenantSubscriptionAddons.Add(new TenantSubscriptionAddon
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = planId,
                AddonCode = PlanCodes.WhatsApp400,
                Estado = EstadoSuscripcion.Activa,
                MonthlyMessageLimit = 400,
                FechaInicio = nowUtc.AddDays(-31),
                FechaFin = nowUtc.AddDays(-1),   // venció ayer
                CreatedAtUtc = nowUtc.AddDays(-31),
                UpdatedAtUtc = nowUtc.AddDays(-1)
            });
            await context.SaveChangesAsync();
        }

        private static DateTime GetFixedNowUtc() =>
            new DateTimeOffset(new DateTime(2026, 5, 26, 10, 30, 0), TimeSpan.FromHours(-6)).UtcDateTime;
    }
}
