using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Platform;
using LuxuryApp.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class PlatformAuditServiceTests
    {
        [Fact]
        public async Task TryLogAsync_ShouldPersistEntryWhenContextIsHealthy()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var logger = new CapturingLogger<PlatformAuditService>();
            var service = new PlatformAuditService(context, new HttpContextAccessor(), logger);

            await service.TryLogAsync(new PlatformAuditEntry
            {
                Action = "TestAction",
                EntityType = "TestEntity"
            });

            var persisted = await context.PlatformAuditLogs
                .IgnoreQueryFilters()
                .SingleAsync();

            Assert.Equal("TestAction", persisted.Action);
            Assert.Empty(logger.Errors);
        }

        [Fact]
        public async Task TryLogAsync_ShouldLogErrorInsteadOfThrowingWhenSaveFails()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;

            // Conexión eliminada = el guardado de la bitácora falla de verdad.
            connection.Dispose();

            var logger = new CapturingLogger<PlatformAuditService>();
            var service = new PlatformAuditService(context, new HttpContextAccessor(), logger);

            var exception = await Record.ExceptionAsync(() => service.TryLogAsync(new PlatformAuditEntry
            {
                Action = "TestAction",
                EntityType = "TestEntity"
            }));

            Assert.Null(exception);

            var error = Assert.Single(logger.Errors);
            Assert.Contains("TestAction", error.Message);
            Assert.NotNull(error.Exception);
        }

        private sealed class CapturingLogger<T> : ILogger<T>
        {
            public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

            public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Errors =>
                Entries.Where(entry => entry.Level == LogLevel.Error).ToList();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
