using LuxuryApp.Models.Reservas;

namespace LuxuryApp.Services.Reservas
{
    public sealed class PublicBookingSubmitResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;

        public static PublicBookingSubmitResult Ok(string message) =>
            new() { Success = true, Message = message };

        public static PublicBookingSubmitResult Fail(string message) =>
            new() { Success = false, Message = message };
    }

    public interface IPublicBookingService
    {
        /// <summary>
        /// Resuelve el slug a su tenant y fija el contexto de tenant para el resto del request.
        /// Devuelve null si no existe, está desactivado o el tenant está inactivo.
        /// </summary>
        Task<PublicBookingTenantContext?> ResolveContextAsync(string slug, CancellationToken cancellationToken = default);

        /// <summary>Construye el modelo de la página pública (servicios, funcionarios, rango de fechas).</summary>
        Task<PublicBookingPageViewModel> BuildPageAsync(
            PublicBookingTenantContext context,
            int? preselectedServiceId = null,
            CancellationToken cancellationToken = default);

        /// <summary>Horarios disponibles para la página pública (valida fecha/servicio internamente).</summary>
        Task<BookingAvailabilityResult> GetAvailabilityAsync(
            PublicBookingTenantContext context,
            int servicioId,
            string? fecha,
            int? funcionarioId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Próximos espacios disponibles para un servicio (y opcionalmente un profesional), SIN
        /// que el cliente elija fecha. El punto de partida y el horizonte los fija el servidor.
        /// Comparte el motor y el formato con <see cref="GetAvailabilityAsync"/>.
        /// </summary>
        Task<BookingNextSlotsResult> GetNextSlotsAsync(
            PublicBookingTenantContext context,
            int servicioId,
            int? funcionarioId,
            CancellationToken cancellationToken = default);

        /// <summary>Crea una solicitud Pending tras revalidar todo en backend.</summary>
        Task<PublicBookingSubmitResult> SubmitAsync(
            PublicBookingTenantContext context,
            PublicBookingRequestInput input,
            CancellationToken cancellationToken = default);
    }
}
