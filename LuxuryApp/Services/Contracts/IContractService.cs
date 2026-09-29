using LuxuryApp.Models.Legal;

namespace LuxuryApp.Services.Contracts
{
    public interface IContractService
    {
        Task<ContractDocument?> GetActiveContractAsync(CancellationToken cancellationToken = default);
        Task<ContractAcceptanceStatus> GetAcceptanceStatusAsync(string userId, CancellationToken cancellationToken = default);
        Task<ContractAcceptanceRecord> RegisterAcceptanceAsync(
            string userId,
            Guid submittedContractDocumentId,
            string acceptanceSource,
            string? ipAddress,
            string? userAgent,
            CancellationToken cancellationToken = default);
    }
}
