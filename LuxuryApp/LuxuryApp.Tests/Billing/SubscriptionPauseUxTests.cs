using LuxuryApp.Models.SaaS;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.WhatsApp;
using LuxuryApp.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Billing
{
    /// <summary>
    /// Polish de ciclo de vida: pausa del proveedor visible en la UI del cliente y accesos de
    /// gestión en Platform. Cubre (1) que el summary del cliente clasifique "Pause By Commerce"
    /// como pausa, (2) que las vistas tengan los enlaces/botones/textos requeridos. No re-testea la
    /// lógica del ProviderSubscriptionManager (ya cubierta en SubscriptionLifecycleTests).
    /// </summary>
    public class SubscriptionPauseUxTests
    {
        // ── El summary del cliente clasifica la pausa del proveedor ──

        [Theory]
        [InlineData("Pause By Commerce", true)]
        [InlineData("3", true)]
        [InlineData("paused", true)]
        [InlineData("Active", false)]
        [InlineData("1", false)]
        [InlineData("Delete", false)]
        [InlineData(null, false)]
        public async Task Summary_MapsProviderStatusRaw_ToIsRenewalPaused(string? providerStatusRaw, bool expectedPaused)
        {
            using var ctx = CreateContext(out var connection);
            using var _ = connection;

            var tenantId = Guid.NewGuid();
            SeedRecurringSubscription(ctx, tenantId, providerStatusRaw);
            await ctx.SaveChangesAsync();

            var summary = await BuildAsync(ctx, tenantId);

            Assert.NotNull(summary);
            Assert.Equal(expectedPaused, summary!.IsRenewalPaused);
            Assert.Equal(providerStatusRaw, summary.ProviderStatusRaw);

            // Pausada ⇒ nunca se ofrece cancelar la renovación en línea.
            if (expectedPaused)
            {
                Assert.False(summary.CanRequestCancellation);
            }
        }

        [Fact]
        public async Task Summary_Active_AllowsCancellation_AndIsNotPaused()
        {
            using var ctx = CreateContext(out var connection);
            using var _ = connection;

            var tenantId = Guid.NewGuid();
            SeedRecurringSubscription(ctx, tenantId, providerStatusRaw: "Active");
            await ctx.SaveChangesAsync();

            var summary = await BuildAsync(ctx, tenantId);

            Assert.False(summary!.IsRenewalPaused);
            Assert.True(summary.CanRequestCancellation); // vuelve a UI normal
        }

        // ── Las vistas tienen enlaces/botones/textos requeridos ──

        [Fact]
        public void PlatformTenantsList_ShowsManageSubscriptionLink()
        {
            var view = ReadView("Views", "Platform", "Tenants.cshtml");
            Assert.Contains("PlatformProviderSubscription", view, StringComparison.Ordinal);
            Assert.Contains("Gestionar suscripción", view, StringComparison.Ordinal);
        }

        [Fact]
        public void PlatformTenantFicha_ShowsManageSubscriptionLink()
        {
            var view = ReadView("Views", "Platform", "TenantFicha.cshtml");
            Assert.Contains("PlatformProviderSubscription", view, StringComparison.Ordinal);
            Assert.Contains("Gestionar suscripción", view, StringComparison.Ordinal);
        }

        [Fact]
        public void PlatformManage_ShowsSyncProviderStatusButton()
        {
            var view = ReadView("Views", "PlatformProviderSubscription", "Manage.cshtml");
            Assert.Contains("SyncProviderStatus", view, StringComparison.Ordinal);
            Assert.Contains("Actualizar estado proveedor", view, StringComparison.Ordinal);
        }

        [Fact]
        public void PlatformManage_PausedShowsIdempotentPauseButton()
        {
            var view = ReadView("Views", "PlatformProviderSubscription", "Manage.cshtml");
            Assert.Contains("ProviderIsPaused", view, StringComparison.Ordinal);
            Assert.Contains("Ya pausada", view, StringComparison.Ordinal);
        }

        [Fact]
        public void BillingSuscripcion_ShowsPauseBannerAndPausedNextBilling()
        {
            var view = ReadView("Views", "Billing", "Suscripcion.cshtml");
            // Banner de pausa
            Assert.Contains("IsRenewalPaused", view, StringComparison.Ordinal);
            Assert.Contains("pausada por soporte", view, StringComparison.Ordinal);
            // Próximo cobro = Pausado
            Assert.Contains("Pausado", view, StringComparison.Ordinal);
            // Bloqueo de cambio de plan
            Assert.Contains("Contactá soporte para reactivarla antes de cambiar de plan", view, StringComparison.Ordinal);
            // Etiqueta clara junto al estado
            Assert.Contains("Renovación pausada", view, StringComparison.Ordinal);
        }

        // ── Helpers ──

        private static string ReadView(params string[] relativeParts)
        {
            var path = TestProjectPaths.ProjectPath(relativeParts);
            Assert.True(File.Exists(path), $"No se encontró la vista: {path}");
            return File.ReadAllText(path);
        }

        private static async Task<BillingSubscriptionSummaryViewModel?> BuildAsync(ApplicationDbContext ctx, Guid tenantId)
        {
            var cache = new MemoryCache(new MemoryCacheOptions());
            var accessCache = new TenantCommercialAccessCache(cache);
            var clock = new FixedBusinessDateTimeProvider();
            var repeatOptions = CalculatorCatalog.BuildRepeatOptions();
            var subscriptionService = new SuscripcionService(
                ctx, cache, accessCache, clock,
                Options.Create(repeatOptions), NullLogger<SuscripcionService>.Instance);

            var accessResolver = new TenantCommercialAccessResolver(ctx, cache, accessCache, subscriptionService, clock);
            var service = new SubscriptionSummaryService(ctx, subscriptionService, new StubWhatsAppSettings(), accessResolver);
            return await service.BuildAsync(tenantId);
        }

        private static void SeedRecurringSubscription(ApplicationDbContext ctx, Guid tenantId, string? providerStatusRaw)
        {
            ctx.Tenants.Add(new Tenant { Id = tenantId, Nombre = "Tenant Beta", Activo = true });
            var plan = new Plan
            {
                Id = Guid.NewGuid(),
                Codigo = "LC_M_03",
                Nombre = "LC_M_03",
                PrecioMensual = 20000m,
                BillingCycle = BillingCycle.Monthly,
                Moneda = "CRC",
                MaxFuncionarios = 3,
                Activo = true
            };
            ctx.Planes.Add(plan);
            var endUtc = DateTime.UtcNow.AddDays(20);
            ctx.Suscripciones.Add(new Suscripcion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                PlanId = plan.Id,
                CodigoPlan = "LC_M_03",
                Estado = EstadoSuscripcion.Activa,
                Proveedor = PaymentProviderType.Tilopay,
                TilopayRecurringPlanId = 7103,
                ProviderSubscriptionId = "900370",
                FechaInicio = endUtc.AddMonths(-1),
                FechaFin = endUtc,
                FechaProximoCobroUtc = endUtc,
                ProviderStatusRaw = providerStatusRaw,
                ProviderStatusLastSyncedUtc = providerStatusRaw is null ? null : DateTime.UtcNow
            });
        }

        private static ApplicationDbContext CreateContext(out Microsoft.Data.Sqlite.SqliteConnection connection)
        {
            var (ctx, conn) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            connection = conn;
            return ctx;
        }

        /// <summary>Sin add-on activo, el summary nunca llama a estos métodos: basta un stub que lanza.</summary>
        private sealed class StubWhatsAppSettings : ITenantWhatsAppSettingsService
        {
            public Task<TenantWhatsAppSettingsSnapshot> GetSettingsForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("No debería llamarse sin add-on activo.");
            public Task<TenantWhatsAppSettingsSnapshot> EnsureDefaultSettingsAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();
            public Task<bool> IsWhatsAppEnabledForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
                Task.FromResult(false);
            public Task<TenantWhatsAppSendDecision> CanSendNotificationAsync(Guid tenantId, string notificationType, long? reservedMessageLogId = null, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();
            public Task<int> GetTodayUsageAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
                Task.FromResult(0);
            public Task<bool> HasActiveWhatsAppAddonAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
                Task.FromResult(false);
            public Task UpdateSettingsAsync(Guid tenantId, TenantWhatsAppSettingsUpdateDto dto, string? updatedByUserId, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException();
        }
    }
}
