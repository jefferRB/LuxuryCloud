using LuxuryApp.Models.Legal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Contracts
{
    public sealed class ContractService : IContractService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<ContractService> _logger;
        private readonly IHostEnvironment? _environment;

        public ContractService(
            ApplicationDbContext context,
            ILogger<ContractService> logger,
            IHostEnvironment? environment = null)
        {
            _context = context;
            _logger = logger;
            _environment = environment;
        }

        // Single source of truth for the current enforceable contract version.
        public Task<ContractDocument?> GetActiveContractAsync(CancellationToken cancellationToken = default) =>
            _context.ContractDocuments
                .AsNoTracking()
                .Where(document => document.IsActive)
                .OrderByDescending(document => document.EffectiveFromUtc)
                .ThenByDescending(document => document.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

        // Detects whether a user must re-accept the current contract version.
        public async Task<ContractAcceptanceStatus> GetAcceptanceStatusAsync(
            string userId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentException("El userId es obligatorio.", nameof(userId));
            }

            var activeDocument = await GetActiveContractAsync(cancellationToken);
            if (activeDocument is null)
            {
                _logger.LogError("No existe una version vigente del contrato configurada.");
                return new ContractAcceptanceStatus();
            }

            var currentHash = ContractHashing.ComputeSha256(activeDocument.ContentHtml);
            if (!string.Equals(activeDocument.ContentHash, currentHash, StringComparison.Ordinal))
            {
                _logger.LogCritical(
                    "El hash almacenado del contrato vigente no coincide con su contenido. ContractDocumentId {ContractDocumentId}.",
                    activeDocument.Id);

                return new ContractAcceptanceStatus
                {
                    ActiveDocument = activeDocument,
                    HasAcceptedCurrentVersion = false
                };
            }

            var acceptance = await _context.ContractAcceptanceRecords
                .AsNoTracking()
                .Where(record =>
                    record.UserId == userId &&
                    record.ContractDocumentId == activeDocument.Id &&
                    record.AcceptedContentHash == currentHash)
                .OrderByDescending(record => record.AcceptedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            return new ContractAcceptanceStatus
            {
                ActiveDocument = activeDocument,
                HasAcceptedCurrentVersion = acceptance is not null,
                AcceptedAtUtc = acceptance?.AcceptedAtUtc
            };
        }

        // Persists the legal evidence associated with one concrete acceptance event.
        public async Task<ContractAcceptanceRecord> RegisterAcceptanceAsync(
            string userId,
            Guid submittedContractDocumentId,
            string acceptanceSource,
            string? ipAddress,
            string? userAgent,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentException("El userId es obligatorio.", nameof(userId));
            }

            var activeDocument = await _context.ContractDocuments
                .FirstOrDefaultAsync(document => document.IsActive, cancellationToken);

            LogDevelopmentAcceptanceTrace(
                "Received",
                userId,
                submittedContractDocumentId,
                activeDocument?.Id);

            if (submittedContractDocumentId == Guid.Empty)
            {
                LogDevelopmentAcceptanceTrace(
                    "Failure",
                    userId,
                    submittedContractDocumentId,
                    activeDocument?.Id,
                    "SubmittedContractDocumentIdMissing");

                throw new InvalidOperationException("No se recibio una version valida del contrato para registrar la aceptacion.");
            }

            if (activeDocument is null)
            {
                LogDevelopmentAcceptanceTrace(
                    "Failure",
                    userId,
                    submittedContractDocumentId,
                    null,
                    "ActiveContractMissing");

                throw new InvalidOperationException("No existe un contrato vigente configurado para aceptar.");
            }

            if (activeDocument.Id != submittedContractDocumentId)
            {
                LogDevelopmentAcceptanceTrace(
                    "Failure",
                    userId,
                    submittedContractDocumentId,
                    activeDocument.Id,
                    "SubmittedContractDocumentIdMismatch");

                throw new InvalidOperationException("La version vigente del contrato cambio. Vuelve a revisar el documento y acepta la version actual.");
            }

            var currentHash = ContractHashing.ComputeSha256(activeDocument.ContentHtml);
            if (!string.Equals(activeDocument.ContentHash, currentHash, StringComparison.Ordinal))
            {
                _logger.LogCritical(
                    "El hash almacenado del contrato vigente no coincide con su contenido. ContractDocumentId {ContractDocumentId}.",
                    activeDocument.Id);

                LogDevelopmentAcceptanceTrace(
                    "Failure",
                    userId,
                    submittedContractDocumentId,
                    activeDocument.Id,
                    "ActiveContractHashMismatch");

                throw new InvalidOperationException("No fue posible validar la integridad del contrato vigente.");
            }

            var acceptance = new ContractAcceptanceRecord
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ContractDocumentId = activeDocument.Id,
                ContractVersion = activeDocument.VersionNumber,
                AcceptedContentHash = currentHash,
                AcceptanceSource = NormalizeSource(acceptanceSource),
                AcceptedAtUtc = DateTime.UtcNow,
                IpAddress = NormalizeIpAddress(ipAddress),
                UserAgent = NormalizeUserAgent(userAgent)
            };

            _context.ContractAcceptanceRecords.Add(acceptance);
            await _context.SaveChangesAsync(cancellationToken);

            LogDevelopmentAcceptanceTrace(
                "Success",
                userId,
                submittedContractDocumentId,
                activeDocument.Id,
                result: "AcceptanceRegistered");

            return acceptance;
        }

        private void LogDevelopmentAcceptanceTrace(
            string stage,
            string userId,
            Guid submittedContractDocumentId,
            Guid? activeContractDocumentId,
            string? result = null)
        {
            if (_environment?.IsDevelopment() != true)
            {
                return;
            }

            _logger.LogInformation(
                "Contract acceptance registration trace. Stage {Stage}. UserId {UserId}. SubmittedContractDocumentId {SubmittedContractDocumentId}. ActiveContractDocumentId {ActiveContractDocumentId}. Result {Result}.",
                stage,
                userId,
                submittedContractDocumentId,
                activeContractDocumentId,
                result);
        }

        private static string NormalizeSource(string? acceptanceSource)
        {
            if (string.Equals(acceptanceSource, ContractAcceptanceSources.Register, StringComparison.OrdinalIgnoreCase))
            {
                return ContractAcceptanceSources.Register;
            }

            return ContractAcceptanceSources.Reaccept;
        }

        private static string? NormalizeIpAddress(string? ipAddress)
        {
            if (string.IsNullOrWhiteSpace(ipAddress))
            {
                return null;
            }

            var normalized = ipAddress.Trim();
            return normalized.Length <= 64
                ? normalized
                : normalized[..64];
        }

        private static string? NormalizeUserAgent(string? userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent))
            {
                return null;
            }

            var normalized = userAgent.Trim();
            return normalized.Length <= 2048
                ? normalized
                : normalized[..2048];
        }
    }
}
