using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Reports;
using LuxuryApp.Services.Reports;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class MonthlyReportSchedulerTests
    {
        // 1 de julio de 2026, 08:00 → reporte de junio 2026.
        private static readonly DateTime DueNow = new(2026, 7, 1, 8, 0, 0);

        [Fact]
        public async Task Process_SchedulerDisabled_DoesNothing()
        {
            var ctx = await BuildAsync(schedulerEnabled: false, isEnabled: true, withAdmin: true);

            var outcome = await ctx.Scheduler.ProcessTenantAsync(ctx.TenantId, DueNow);

            Assert.Equal(MonthlyReportScheduleOutcome.SchedulerDisabled, outcome);
            Assert.Empty(ctx.Sender.Attempts);
        }

        [Fact]
        public async Task Process_NotEnabled_DoesNothing()
        {
            var ctx = await BuildAsync(schedulerEnabled: true, isEnabled: false, withAdmin: true);

            var outcome = await ctx.Scheduler.ProcessTenantAsync(ctx.TenantId, DueNow);

            Assert.Equal(MonthlyReportScheduleOutcome.NotEnabled, outcome);
            Assert.Empty(ctx.Sender.Attempts);
        }

        [Theory]
        [InlineData(2, 8)]   // otro día
        [InlineData(1, 7)]   // hora aún no alcanzada
        public async Task Process_NotDue_DoesNothing(int day, int hour)
        {
            var ctx = await BuildAsync(schedulerEnabled: true, isEnabled: true, withAdmin: true);

            var outcome = await ctx.Scheduler.ProcessTenantAsync(ctx.TenantId, new DateTime(2026, 7, day, hour, 0, 0));

            Assert.Equal(MonthlyReportScheduleOutcome.NotDue, outcome);
            Assert.Empty(ctx.Sender.Attempts);
        }

        [Fact]
        public async Task Process_Due_SendsPreviousMonth_AndMarksPeriod()
        {
            var ctx = await BuildAsync(schedulerEnabled: true, isEnabled: true, withAdmin: true);

            var outcome = await ctx.Scheduler.ProcessTenantAsync(ctx.TenantId, DueNow);

            Assert.Equal(MonthlyReportScheduleOutcome.Sent, outcome);
            Assert.Single(ctx.Sender.Attempts);

            // El reporte automático corresponde a JUNIO 2026 (mes anterior).
            var log = Assert.Single(await ctx.Context.TenantMonthlyReportEmailLogs.AsNoTracking().ToListAsync());
            Assert.Equal(2026, log.ReportYear);
            Assert.Equal(6, log.ReportMonth);
            Assert.False(log.IsTest);
            Assert.Equal(MonthlyReportEmailStatus.Sent, log.Status);

            var settings = await ctx.Context.TenantMonthlyReportSettings.AsNoTracking().FirstAsync();
            Assert.Equal(202606, settings.LastAutomaticPeriod);
            Assert.NotNull(settings.LastAutomaticSentAt);
            Assert.Null(settings.LastAutomaticError);
        }

        [Fact]
        public async Task Process_RunTwice_DoesNotDuplicate()
        {
            var ctx = await BuildAsync(schedulerEnabled: true, isEnabled: true, withAdmin: true);

            var first = await ctx.Scheduler.ProcessTenantAsync(ctx.TenantId, DueNow);
            var second = await ctx.Scheduler.ProcessTenantAsync(ctx.TenantId, DueNow);

            Assert.Equal(MonthlyReportScheduleOutcome.Sent, first);
            Assert.Equal(MonthlyReportScheduleOutcome.AlreadyProcessed, second);
            Assert.Single(ctx.Sender.Attempts);
        }

        [Fact]
        public async Task Process_Failed_DoesNotMarkPeriod_AndRetriesSuccessfully()
        {
            var ctx = await BuildAsync(schedulerEnabled: true, isEnabled: true, withAdmin: true);
            ctx.Sender.Succeed = false;

            var failed = await ctx.Scheduler.ProcessTenantAsync(ctx.TenantId, DueNow);
            Assert.Equal(MonthlyReportScheduleOutcome.Failed, failed);

            var afterFail = await ctx.Context.TenantMonthlyReportSettings.AsNoTracking().FirstAsync();
            Assert.Null(afterFail.LastAutomaticPeriod); // no cerró el periodo → permite reintento
            Assert.NotNull(afterFail.LastAutomaticError);

            // Reintento con proveedor sano: ahora envía.
            ctx.Sender.Succeed = true;
            var retry = await ctx.Scheduler.ProcessTenantAsync(ctx.TenantId, DueNow);
            Assert.Equal(MonthlyReportScheduleOutcome.Sent, retry);

            var afterRetry = await ctx.Context.TenantMonthlyReportSettings.AsNoTracking().FirstAsync();
            Assert.Equal(202606, afterRetry.LastAutomaticPeriod);
        }

        [Fact]
        public async Task Process_NoRecipients_ReportsFailure_WithoutSending()
        {
            var ctx = await BuildAsync(schedulerEnabled: true, isEnabled: true, withAdmin: false);

            var outcome = await ctx.Scheduler.ProcessTenantAsync(ctx.TenantId, DueNow);

            Assert.Equal(MonthlyReportScheduleOutcome.Failed, outcome);
            Assert.Empty(ctx.Sender.Attempts);

            var settings = await ctx.Context.TenantMonthlyReportSettings.AsNoTracking().FirstAsync();
            Assert.Null(settings.LastAutomaticPeriod);
            Assert.NotNull(settings.LastAutomaticError);
        }

        [Fact]
        public async Task Process_NoCommercialAccess_SkipsWithoutSending()
        {
            var ctx = await BuildAsync(schedulerEnabled: true, isEnabled: true, withAdmin: true);
            ctx.Access.CanAccessApp = false;

            var outcome = await ctx.Scheduler.ProcessTenantAsync(ctx.TenantId, DueNow);

            Assert.Equal(MonthlyReportScheduleOutcome.NoAccess, outcome);
            Assert.Empty(ctx.Sender.Attempts);

            var settings = await ctx.Context.TenantMonthlyReportSettings.AsNoTracking().FirstAsync();
            Assert.Null(settings.LastAutomaticPeriod);
        }

        // ─────────────── Armado ───────────────

        private sealed class SchedulerContext
        {
            public required Guid TenantId { get; init; }
            public required ProyectoIdentity.Datos.ApplicationDbContext Context { get; init; }
            public required Microsoft.Data.Sqlite.SqliteConnection Connection { get; init; }
            public required IMonthlyReportScheduler Scheduler { get; init; }
            public required FakeMonthlyReportEmailSender Sender { get; init; }
            public required FakeCommercialAccessResolver Access { get; init; }
        }

        private static async Task<SchedulerContext> BuildAsync(bool schedulerEnabled, bool isEnabled, bool withAdmin)
        {
            var tenantId = Guid.NewGuid();
            var tenantProvider = new TestTenantProvider { TenantId = tenantId };
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);

            context.Tenants.Add(new LuxuryApp.Models.SaaS.Tenant { Id = tenantId, Nombre = "Negocio Test" });

            if (withAdmin)
            {
                var adminRole = new IdentityRole { Id = Guid.NewGuid().ToString(), Name = "Administrador", NormalizedName = "ADMINISTRADOR" };
                context.Roles.Add(adminRole);
                var admin = new AppUsuario
                {
                    Id = Guid.NewGuid().ToString(),
                    UserName = "admin@negocio.cr",
                    NormalizedUserName = "ADMIN@NEGOCIO.CR",
                    Email = "admin@negocio.cr",
                    NormalizedEmail = "ADMIN@NEGOCIO.CR",
                    EmailConfirmed = true,
                    TenantId = tenantId,
                    State = true
                };
                context.Users.Add(admin);
                context.UserRoles.Add(new IdentityUserRole<string> { UserId = admin.Id, RoleId = adminRole.Id });
            }

            context.TenantMonthlyReportSettings.Add(new TenantMonthlyReportSettings
            {
                TenantId = tenantId,
                IsEnabled = isEnabled,
                SendToAllAdmins = true,
                SendDayOfMonth = 1,
                SendHour = 8,
                CreatedAt = new DateTime(2026, 6, 1),
                UpdatedAt = new DateTime(2026, 6, 1)
            });
            await context.SaveChangesAsync();

            var sender = new FakeMonthlyReportEmailSender();
            var access = new FakeCommercialAccessResolver();
            var reportService = ControllerTestSupport.CreateMonthlyBusinessReportService(context, tenantProvider, sender);

            var options = new StaticOptionsMonitor<MonthlyReportSchedulerOptions>(
                new MonthlyReportSchedulerOptions { SchedulerEnabled = schedulerEnabled });

            var scheduler = new MonthlyReportScheduler(
                context,
                reportService,
                access,
                options,
                ControllerTestSupport.BusinessDateTimeProvider,
                NullLogger<MonthlyReportScheduler>.Instance);

            return new SchedulerContext
            {
                TenantId = tenantId,
                Context = context,
                Connection = connection,
                Scheduler = scheduler,
                Sender = sender,
                Access = access
            };
        }
    }
}
