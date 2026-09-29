using LuxuryApp.Controllers.Platform;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.Reports;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.Reports;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.TenantIsolation
{
    /// <summary>
    /// Fase 2.1: el resumen mensual se administra SOLO desde Plataforma. Verifica que la vista
    /// del tenant fue removida y que el guardado cross-tenant desde Plataforma es seguro/aislado.
    /// </summary>
    public class PlatformMonthlyReportAdminTests
    {
        [Fact]
        public void TenantResumenMensualController_NoLongerExists()
        {
            var webAssembly = typeof(PlatformMonthlyReportsController).Assembly;

            var offending = webAssembly.GetTypes()
                .Where(t => t.Name == "ResumenMensualController")
                .ToArray();

            Assert.Empty(offending);
        }

        [Fact]
        public void PrivateNavigation_DoesNotExposeResumenMensualToTenants()
        {
            var repoRoot = TestProjectPaths.RepositoryRoot;
            var navFile = Path.Combine(repoRoot, "Services", "Layout", "PrivateNavigationService.cs");

            Assert.True(File.Exists(navFile), $"No se encontró {navFile}");

            var content = File.ReadAllText(navFile);

            // El menú del tenant no debe navegar al controlador ResumenMensual.
            Assert.DoesNotContain("Controller = \"ResumenMensual\"", content, StringComparison.Ordinal);
        }

        [Fact]
        public async Task SaveSettings_PersistsForTenant_WithoutTouchingOtherTenant()
        {
            await using var harness = await AdminHarness.CreateAsync();

            var tenantA = harness.TenantA;
            var tenantB = harness.TenantB;

            // B arranca con una configuración activada por defecto (día 1).
            await harness.SeedSettingsAsync(tenantB, isEnabled: true, day: 1, hour: 8);

            var service = harness.CreateService();

            var result = await service.SaveSettingsAsync(tenantA, new PlatformMonthlyReportSettingsForm
            {
                IsEnabled = true,
                SendToAllAdmins = true,
                RequireConfirmedEmail = true,
                IncludeManualRecipients = true,
                AdditionalRecipients = "Extra@Negocio.CR, extra@negocio.cr, no-sirve",
                IncludeFinancialData = true,
                IncludeOperationalData = false,
                IncludeMonthOverMonth = true,
                IncludeRecommendations = false,
                SendDayOfMonth = 5,
                SendHour = 9
            });

            Assert.True(result.TenantFound);
            Assert.True(result.Saved);

            var savedA = await harness.GetSettingsAsync(tenantA);
            Assert.NotNull(savedA);
            Assert.True(savedA!.IsEnabled);
            Assert.Equal(5, savedA.SendDayOfMonth);
            Assert.Equal(9, savedA.SendHour);
            Assert.False(savedA.IncludeOperationalData);
            Assert.True(savedA.RequireConfirmedEmail);
            Assert.True(savedA.SendToOwnerEmail); // sincronizado con SendToAllAdmins
            Assert.Equal("extra@negocio.cr", savedA.AdditionalRecipients); // normalizado + dedup, inválido descartado

            // Tenant B intacto.
            var savedB = await harness.GetSettingsAsync(tenantB);
            Assert.NotNull(savedB);
            Assert.Equal(1, savedB!.SendDayOfMonth);
            Assert.Equal(8, savedB.SendHour);
        }

        [Fact]
        public async Task SaveSettings_UnknownTenant_ReturnsNotFound()
        {
            await using var harness = await AdminHarness.CreateAsync();
            var service = harness.CreateService();

            var result = await service.SaveSettingsAsync(Guid.NewGuid(), new PlatformMonthlyReportSettingsForm());

            Assert.False(result.TenantFound);
            Assert.False(result.Saved);
        }

        [Fact]
        public async Task SaveSettings_WorksWhileGlobalSchedulerDisabled()
        {
            await using var harness = await AdminHarness.CreateAsync(schedulerEnabled: false);
            var service = harness.CreateService();

            var result = await service.SaveSettingsAsync(harness.TenantA, new PlatformMonthlyReportSettingsForm
            {
                IsEnabled = true,
                SendDayOfMonth = 3,
                SendHour = 7
            });

            Assert.True(result.Saved);
            var saved = await harness.GetSettingsAsync(harness.TenantA);
            Assert.True(saved!.IsEnabled); // se guarda aunque el scheduler global esté apagado
        }

        // ─────────────── Harness DI mínimo compartiendo la conexión SQLite ───────────────

        private sealed class AdminHarness : IAsyncDisposable
        {
            private readonly SqliteConnection _connection;
            private readonly ServiceProvider _provider;
            private readonly List<IServiceScope> _scopes = new();

            public Guid TenantA { get; } = Guid.NewGuid();
            public Guid TenantB { get; } = Guid.NewGuid();

            private readonly bool _schedulerEnabled;

            private AdminHarness(SqliteConnection connection, ServiceProvider provider, bool schedulerEnabled)
            {
                _connection = connection;
                _provider = provider;
                _schedulerEnabled = schedulerEnabled;
            }

            public static async Task<AdminHarness> CreateAsync(bool schedulerEnabled = true)
            {
                var connection = new SqliteConnection("DataSource=:memory:");
                connection.Open();

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddHttpContextAccessor();
                services.AddSingleton<ITenantExecutionContextAccessor, TenantExecutionContextAccessor>();
                services.AddScoped<ITenantProvider, TenantProvider>();
                services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(connection));

                var provider = services.BuildServiceProvider();

                var harness = new AdminHarness(connection, provider, schedulerEnabled);

                using (var scope = provider.CreateScope())
                {
                    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await context.Database.EnsureCreatedAsync();

                    // Tenants no son ITenantEntity: se pueden sembrar sin scope de tenant.
                    context.Tenants.Add(new LuxuryApp.Models.SaaS.Tenant { Id = harness.TenantA, Nombre = "Salón Alfa" });
                    context.Tenants.Add(new LuxuryApp.Models.SaaS.Tenant { Id = harness.TenantB, Nombre = "Salón Beta" });
                    await context.SaveChangesAsync();
                }

                return harness;
            }

            public IPlatformMonthlyReportService CreateService()
            {
                // Contexto "externo" para lecturas (sin scope de tenant → filtros permiten Guid.Empty).
                var scope = _provider.CreateScope();
                _scopes.Add(scope);
                var outerContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var tenantExecution = new TenantExecutionService(
                    _provider.GetRequiredService<IServiceScopeFactory>(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<TenantExecutionService>.Instance);

                return new PlatformMonthlyReportService(
                    outerContext,
                    new MonthlyReportRecipientResolver(outerContext),
                    tenantExecution,
                    ControllerTestSupport.BusinessDateTimeProvider,
                    new StaticOptionsMonitor<MonthlyReportSchedulerOptions>(
                        new MonthlyReportSchedulerOptions { SchedulerEnabled = _schedulerEnabled }));
            }

            public async Task SeedSettingsAsync(Guid tenantId, bool isEnabled, int day, int hour)
            {
                using var scope = _provider.CreateScope();
                var accessor = scope.ServiceProvider.GetRequiredService<ITenantExecutionContextAccessor>();
                using var _ = accessor.BeginScope(tenantId);

                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                context.TenantMonthlyReportSettings.Add(new TenantMonthlyReportSettings
                {
                    TenantId = tenantId,
                    IsEnabled = isEnabled,
                    SendDayOfMonth = day,
                    SendHour = hour,
                    CreatedAt = new DateTime(2026, 6, 1),
                    UpdatedAt = new DateTime(2026, 6, 1)
                });
                await context.SaveChangesAsync();
            }

            public async Task<TenantMonthlyReportSettings?> GetSettingsAsync(Guid tenantId)
            {
                using var scope = _provider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                return await context.TenantMonthlyReportSettings
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.TenantId == tenantId);
            }

            public async ValueTask DisposeAsync()
            {
                foreach (var scope in _scopes)
                {
                    scope.Dispose();
                }

                await _provider.DisposeAsync();
                await _connection.DisposeAsync();
            }
        }
    }
}
