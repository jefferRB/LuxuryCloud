using LuxuryApp.Models.Asociados;

namespace LuxuryApp.Services.Asociados
{
    /// <summary>
    /// Error de negocio del módulo de asociados con mensaje apto para mostrar al usuario.
    /// Mismo patrón que <c>InvestorValidationException</c>: el controlador lo traduce a
    /// ModelState o TempData, nunca a un 500.
    /// </summary>
    public sealed class AssociateValidationException : Exception
    {
        public AssociateValidationException(string message, string? modelStateKey = null)
            : base(message)
        {
            ModelStateKey = modelStateKey;
        }

        public string? ModelStateKey { get; }
    }

    /// <summary>
    /// Gestión de asociados: identidad, relación con el negocio y orquestación de su acceso y su
    /// participación financiera.
    ///
    /// <para>
    /// Este servicio NO calcula dinero ni valida porcentajes: eso vive en
    /// <c>IInvestorService</c> (100 %, solapes, cambios a mitad de periodo, versionado) y en
    /// <c>IPeriodProfitCalculationService</c> (fórmula de la ganancia). Acá solo se coordina.
    /// </para>
    /// </summary>
    public interface IAssociateService
    {
        Task<AssociatesIndexViewModel> BuildIndexAsync(
            bool puedeAdministrar,
            CancellationToken cancellationToken = default);

        Task<AssociateFormViewModel> BuildCreateFormAsync(CancellationToken cancellationToken = default);

        /// <summary>Detalle completo. Null si el asociado no existe en el tenant actual.</summary>
        Task<AssociateDetailViewModel?> BuildDetailAsync(
            int associateId,
            bool puedeAdministrar,
            CancellationToken cancellationToken = default);

        /// <summary>Rehidrata las ayudas contextuales de un formulario que volvió con errores.</summary>
        Task<AssociateFormViewModel> RehydrateFormAsync(
            AssociateFormViewModel form,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Crea el asociado, sus tipos y (si es inversionista) su participación. Devuelve el Id.
        /// El acceso al sistema NO se crea acá: se activa después, para que un fallo creando la
        /// cuenta Identity no deje a medias el registro del asociado.
        /// </summary>
        Task<int> CreateAsync(
            AssociateFormViewModel form,
            string? actorUserId,
            CancellationToken cancellationToken = default);

        /// <summary>Actualiza identidad y tipos. La participación se cambia por separado.</summary>
        Task UpdateAsync(
            int associateId,
            AssociateFormViewModel form,
            string? actorUserId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Baja/alta lógica. Desactivar conserva participación, estados de cuenta y auditoría, y
        /// bloquea el acceso si lo tenía.
        /// </summary>
        Task SetActivoAsync(
            int associateId,
            bool activo,
            string? actorUserId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Crea o versiona la participación del asociado. Un cambio de porcentaje NO reescribe el
        /// acuerdo anterior: lo cierra y crea una versión nueva desde el inicio de periodo.
        /// </summary>
        Task SaveParticipationAsync(
            AssociateParticipationFormViewModel form,
            string? actorUserId,
            CancellationToken cancellationToken = default);
    }
}
