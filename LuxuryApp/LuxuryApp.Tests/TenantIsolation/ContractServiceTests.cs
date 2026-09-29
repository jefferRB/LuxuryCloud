using LuxuryApp.Models.Identity;
using LuxuryApp.Models.Legal;
using LuxuryApp.Services.Contracts;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class ContractServiceTests
    {
        [Fact]
        public async Task RegisterAcceptanceAsync_ShouldPersistLegalEvidenceWithCurrentHash()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var user = await SeedUserAsync(context, "contract-evidence@test.local");
            var service = new ContractService(context, NullLogger<ContractService>.Instance);
            var activeDocument = await service.GetActiveContractAsync();

            Assert.NotNull(activeDocument);

            var acceptance = await service.RegisterAcceptanceAsync(
                user.Id,
                activeDocument!.Id,
                ContractAcceptanceSources.Register,
                "203.0.113.42",
                new string('a', 2100));

            var persisted = await context.ContractAcceptanceRecords.SingleAsync();

            Assert.Equal(acceptance.Id, persisted.Id);
            Assert.Equal(user.Id, persisted.UserId);
            Assert.Equal(activeDocument.Id, persisted.ContractDocumentId);
            Assert.Equal(activeDocument.VersionNumber, persisted.ContractVersion);
            Assert.Equal(activeDocument.ContentHash, persisted.AcceptedContentHash);
            Assert.Equal(ContractAcceptanceSources.Register, persisted.AcceptanceSource);
            Assert.Equal("203.0.113.42", persisted.IpAddress);
            Assert.Equal(2048, persisted.UserAgent!.Length);
            Assert.NotEqual(default, persisted.AcceptedAtUtc);
        }

        [Fact]
        public async Task GetAcceptanceStatusAsync_ShouldRequireReacceptanceWhenActiveVersionChanges()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var user = await SeedUserAsync(context, "contract-reaccept@test.local");
            var service = new ContractService(context, NullLogger<ContractService>.Instance);
            var initialDocument = await service.GetActiveContractAsync();

            Assert.NotNull(initialDocument);

            await service.RegisterAcceptanceAsync(
                user.Id,
                initialDocument!.Id,
                ContractAcceptanceSources.Register,
                "203.0.113.43",
                "ContractServiceTests/reaccept");

            var statusBeforeChange = await service.GetAcceptanceStatusAsync(user.Id);
            Assert.True(statusBeforeChange.HasAcceptedCurrentVersion);

            var trackedInitialDocument = await context.ContractDocuments.SingleAsync(document => document.Id == initialDocument.Id);
            trackedInitialDocument.IsActive = false;
            trackedInitialDocument.UpdatedAtUtc = DateTime.UtcNow;

            var replacementContent = "<section><h2>Contrato actualizado</h2><p>Version nueva.</p></section>";
            var replacementDocument = new ContractDocument
            {
                Id = Guid.NewGuid(),
                Title = "Contrato de Uso del Servicio LuxuryApp",
                VersionNumber = "2.0.0",
                ContentHtml = replacementContent,
                ContentHash = ContractHashing.ComputeSha256(replacementContent),
                IsActive = true,
                EffectiveFromUtc = DateTime.UtcNow.AddDays(1),
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            context.ContractDocuments.Add(replacementDocument);
            await context.SaveChangesAsync();

            var statusAfterChange = await service.GetAcceptanceStatusAsync(user.Id);

            Assert.True(statusAfterChange.RequiresAcceptance);
            Assert.True(statusAfterChange.BlocksApplicationAccess);
            Assert.False(statusAfterChange.HasAcceptedCurrentVersion);
            Assert.Equal(replacementDocument.Id, statusAfterChange.ActiveDocument?.Id);
        }

        [Fact]
        public async Task GetAcceptanceStatusAsync_ShouldBlockAccessWhenActiveContractContentIsTampered()
        {
            var tenantProvider = new TestTenantProvider();
            var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
            using var disposableContext = context;
            using var disposableConnection = connection;

            var user = await SeedUserAsync(context, "contract-integrity@test.local");
            var service = new ContractService(context, NullLogger<ContractService>.Instance);
            var activeDocument = await service.GetActiveContractAsync();

            Assert.NotNull(activeDocument);

            await service.RegisterAcceptanceAsync(
                user.Id,
                activeDocument!.Id,
                ContractAcceptanceSources.Register,
                "203.0.113.44",
                "ContractServiceTests/integrity");

            var trackedDocument = await context.ContractDocuments.SingleAsync(document => document.Id == activeDocument.Id);
            trackedDocument.ContentHtml += "<p>Contenido alterado fuera del versionado.</p>";
            trackedDocument.UpdatedAtUtc = DateTime.UtcNow;
            await context.SaveChangesAsync();

            var status = await service.GetAcceptanceStatusAsync(user.Id);

            Assert.True(status.BlocksApplicationAccess);
            Assert.False(status.HasAcceptedCurrentVersion);
            Assert.Equal(activeDocument.Id, status.ActiveDocument?.Id);
        }

        private static async Task<AppUsuario> SeedUserAsync(ProyectoIdentity.Datos.ApplicationDbContext context, string email)
        {
            var tenant = new LuxuryApp.Models.SaaS.Tenant
            {
                Id = Guid.NewGuid(),
                Nombre = "Tenant contrato",
                Activo = true
            };

            var user = new AppUsuario
            {
                Id = Guid.NewGuid().ToString(),
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                State = true,
                TenantId = tenant.Id
            };

            context.Tenants.Add(tenant);
            context.Users.Add(user);
            await context.SaveChangesAsync();
            return user;
        }
    }
}
