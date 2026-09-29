using LuxuryApp.Models.Inversionistas;

namespace LuxuryApp.Services.Inversionistas
{
    /// <summary>
    /// Gestión de inversionistas, acuerdos de participación y política de cálculo del tenant.
    /// Todas las validaciones de negocio (100 %, solapes, cambios a mitad de periodo) viven acá.
    /// </summary>
    public interface IInvestorService
    {
        Task<InvestorsIndexViewModel> BuildIndexAsync(CancellationToken cancellationToken = default);

        Task<InvestorFormViewModel> BuildCreateFormAsync(CancellationToken cancellationToken = default);

        /// <summary>Crea el inversionista y su primer acuerdo. Devuelve el Id creado.</summary>
        Task<int> CreateAsync(
            InvestorFormViewModel form,
            string? userId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Actualiza los datos del inversionista. Si el porcentaje, la frecuencia o el tratamiento
        /// de pérdidas cambian, cierra el acuerdo vigente y crea una nueva versión desde la fecha
        /// efectiva indicada (que debe ser el inicio de un periodo).
        /// </summary>
        Task UpdateAsync(
            int investorId,
            InvestorFormViewModel form,
            string? userId,
            CancellationToken cancellationToken = default);

        Task SetActivoAsync(
            int investorId,
            bool activo,
            string? userId,
            CancellationToken cancellationToken = default);

        Task<InvestorProfitPolicy> GetPolicyAsync(CancellationToken cancellationToken = default);

        Task<InvestorPolicyViewModel> BuildPolicyFormAsync(CancellationToken cancellationToken = default);

        Task SavePolicyAsync(
            InvestorPolicyViewModel form,
            string? userId,
            CancellationToken cancellationToken = default);

        /// <summary>Acuerdo vigente del inversionista en la fecha indicada, o null.</summary>
        Task<InvestorAgreement?> GetAgreementForDateAsync(
            int investorId,
            DateOnly fecha,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Perfil de inversionista del asociado indicado, con sus acuerdos cargados. Null si el
        /// asociado todavía no participa de la ganancia.
        /// </summary>
        Task<TenantInvestor?> GetByAssociateAsync(
            int associateId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Enlaza un perfil de inversionista con su asociado. Idempotente; falla si el perfil ya
        /// pertenece a OTRO asociado (jamás se reasigna un histórico financiero en silencio).
        /// </summary>
        Task LinkToAssociateAsync(
            int investorId,
            int associateId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Asociado dueño del perfil de inversionista indicado. Null si el perfil no existe o
        /// todavía no está enlazado. Lo usan las rutas antiguas de /Inversionistas para redirigir.
        /// </summary>
        Task<int?> GetAssociateIdAsync(
            int investorId,
            CancellationToken cancellationToken = default);
    }
}
