using LuxuryApp.Models.Calendar;

namespace LuxuryApp.Services.Calendar
{
    public interface IControlCobrosQueryService
    {
        /// <summary>
        /// Construye el modelo de la vista "Control de citas y cobros" para el tenant actual.
        /// Todas las consultas quedan filtradas por TenantId vía el global query filter.
        /// </summary>
        Task<ControlCitasCobrosViewModel> ObtenerAsync(
            ControlCitasCobrosFiltroViewModel filtro,
            bool hasWhatsAppAddon,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resuelve los datos de una cita para registrar su cobro (admin: cualquier cita del tenant).
        /// Devuelve null si la cita no existe en el tenant o no es de tipo CITA.
        /// La validación final de doble cobro y pertenencia la hace CobroService.
        /// </summary>
        Task<ControlCitaCobroContexto?> ObtenerCitaParaCobroAsync(
            int citaId,
            CancellationToken cancellationToken = default);
    }
}
