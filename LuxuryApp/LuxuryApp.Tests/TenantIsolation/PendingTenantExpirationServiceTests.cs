using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Services.Security;
using LuxuryApp.Services.Tenant;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class PendingTenantExpirationServiceTests
    {
        [Fact]
        public async Task ExpirePendingTenantsAsync_WhenDisabled_ShouldNotChangePendingTenant()
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = await SeedTenantAsync(context, emailConfirmed: false, daysOld: 20);
            var service = CreateService(context, enabled: false);

            var result = await service.ExpirePendingTenantsAsync();

            Assert.False(result.Enabled);
            Assert.Equal(0, result.ExpiredCount);
            Assert.True((await context.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId)).Activo);
        }

        [Fact]
        public async Task ExpirePendingTenantsAsync_ShouldSoftDisableOldUnverifiedTenantWithoutPaymentOrActivity()
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            using var disposableContext = context;
            using var disposableConnection = connection;

            var tenantId = await SeedTenantAsync(context, emailConfirmed: false, daysOld: 20);
            var audit = new RecordingAuditService();
            var service = CreateService(context, enabled: true, audit);

            var result = await service.ExpirePendingTenantsAsync();

            Assert.True(result.Enabled);
            Assert.Equal(1, result.ExpiredCount);
            Assert.Contains(tenantId, result.TenantIds);

            var tenant = await context.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
            var user = await context.Users.IgnoreQueryFilters().SingleAsync(u => u.TenantId == tenantId);

            Assert.False(tenant.Activo);
            Assert.Equal(TenantCommercialAccessMode.PendingVerification, tenant.CommercialAccessMode);
            Assert.Contains("Registro pendiente expirado", tenant.CommercialNotes);
            Assert.Equal("system:pending-tenant-expiration", tenant.CommercialUpdatedByUserId);
            Assert.False(user.State);
            Assert.False(string.IsNullOrWhiteSpace(user.SecurityStamp));

            var entry = Assert.Single(audit.Entries);
            Assert.Equal(PlatformAuditActions.TenantPendingRegistrationExpired, entry.Action);
            Assert.Equal(tenantId, entry.TenantId);
        }

        [Fact]
        public async Task ExpirePendingTenantsAsync_ShouldSkipWhenEmailConfirmedOrPaymentConfirmed()
        {
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(new TestTenantProvider());
            using var disposableContext = context;
            using var disposableConnection = connection;

            var confirmedEmailTenantId = await SeedTenantAsync(context, emailConfirmed: true, daysOld: 20);
            var paidTenantId = await SeedTenantAsync(context, emailConfirmed: false, daysOld: 20, withConfirmedPayment: true);
            var service = CreateService(context, enabled: true);

            var result = await service.ExpirePendingTenantsAsync();

            Assert.Equal(0, result.ExpiredCount);
            Assert.True((await context.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == confirmedEmailTenantId)).Activo);
            Assert.True((await context.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == paidTenantId)).Activo);
        }

        private static PendingTenantExpirationService CreateService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            bool enabled,
            IPlatformAuditService? audit = null)
        {
            var options = new RegistrationSecurityOptions
            {
                ExpirePendingTenantsEnabled = enabled,
                PendingTenantExpirationDays = 7
            };

            var cache = new MemoryCache(new MemoryCacheOptions());
            return new PendingTenantExpirationService(
                context,
                new StaticOptionsMonitor<RegistrationSecurityOptions>(options),
                new TenantCommercialAccessCache(cache),
                audit ?? new RecordingAuditService(),
                NullLogger<PendingTenantExpirationService>.Instance);
        }

        private static async Task<Guid> SeedTenantAsync(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            bool emailConfirmed,
            int daysOld,
            bool withConfirmedPayment = false)
        {
            var tenantId = Guid.NewGuid();
            context.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Nombre = $"Tenant pending {tenantId:N}"[..30],
                Activo = true,
                CommercialAccessMode = TenantCommercialAccessMode.PendingVerification,
                FechaCreacion = DateTime.UtcNow.AddDays(-daysOld)
            });

            var email = $"owner-{tenantId:N}@luxurytest.example";
            context.Users.Add(new AppUsuario
            {
                Id = Guid.NewGuid().ToString("N"),
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                EmailConfirmed = emailConfirmed,
                TenantId = tenantId,
                State = true,
                SecurityStamp = Guid.NewGuid().ToString("N")
            });

            if (withConfirmedPayment)
            {
                var planId = Guid.NewGuid();
                context.Planes.Add(new Plan
                {
                    Id = planId,
                    Nombre = "Plan seguro",
                    Codigo = $"LC_TEST_{Guid.NewGuid():N}"[..24],
                    Moneda = "CRC",
                    PrecioMensual = 1000m,
                    Activo = true
                });
                context.PagosSuscripcion.Add(new PagoSuscripcion
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    PlanId = planId,
                    Estado = EstadoPagoProveedor.Confirmado,
                    ReferenciaInterna = $"LXA-{Guid.NewGuid():N}"[..20],
                    Descripcion = "Pago confirmado test",
                    Monto = 1000m,
                    Moneda = "CRC"
                });
            }

            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            return tenantId;
        }

        private sealed class RecordingAuditService : IPlatformAuditService
        {
            public List<PlatformAuditEntry> Entries { get; } = new();

            public Task LogAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default)
            {
                Entries.Add(entry);
                return Task.CompletedTask;
            }

            public Task TryLogAsync(PlatformAuditEntry entry, CancellationToken cancellationToken = default) =>
                LogAsync(entry, cancellationToken);

            public Task<IReadOnlyList<PlatformAuditLog>> GetRecentAsync(int take = 100, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());

            public Task<IReadOnlyList<PlatformAuditLog>> GetByTenantAsync(Guid tenantId, int take = 100, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());

            public Task<IReadOnlyList<PlatformAuditLog>> GetByUserAsync(string targetUserId, int take = 100, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<PlatformAuditLog>>(Array.Empty<PlatformAuditLog>());

            public Task<int> CountActorFailuresSinceAsync(string actorUserId, DateTime sinceUtc, CancellationToken cancellationToken = default) =>
                Task.FromResult(0);
        }
    }
}
