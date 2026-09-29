using LuxuryApp.Models.PublicPages;

namespace LuxuryApp.Services.PublicPages
{
    /// <summary>
    /// Convierte entre el horario estructurado (<see cref="BusinessSchedule"/>), su forma
    /// serializada (JSON en <c>TenantPublicPage.BusinessHoursJson</c>), la entrada del editor
    /// interno (<see cref="BusinessScheduleDayInput"/>) y el estado calculado para la landing
    /// (<see cref="BusinessScheduleStatusViewModel"/>, estilo Google Maps).
    /// </summary>
    public interface IBusinessScheduleService
    {
        /// <summary>Deserializa el JSON persistido. Devuelve null si es nulo/invalido/vacio.</summary>
        BusinessSchedule? TryDeserialize(string? json);

        /// <summary>
        /// Construye un horario validado a partir de la entrada del editor. Lanza
        /// <see cref="TenantPublicPageValidationException"/> si algun tramo es invalido.
        /// Devuelve null si no hay ningun dia abierto (equivale a "sin horario").
        /// </summary>
        BusinessSchedule? BuildFromInputs(IEnumerable<BusinessScheduleDayInput>? inputs);

        /// <summary>Serializa a JSON. Devuelve null si el horario es nulo o sin dias abiertos.</summary>
        string? Serialize(BusinessSchedule? schedule);

        /// <summary>
        /// Devuelve siempre 7 filas (Lunes-Domingo) para poblar el editor interno a partir
        /// del horario guardado (o vacio si no hay).
        /// </summary>
        IReadOnlyList<BusinessScheduleDayInput> BuildInputs(BusinessSchedule? schedule);

        /// <summary>
        /// Calcula el estado "Abierto/Cerrado" y el detalle de la landing usando la hora local
        /// del negocio. Si no hay horario, <c>HasSchedule = false</c>.
        /// </summary>
        BusinessScheduleStatusViewModel BuildStatus(BusinessSchedule? schedule, DateTime businessLocalNow);
    }
}
