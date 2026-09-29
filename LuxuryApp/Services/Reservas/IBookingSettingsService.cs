using LuxuryApp.Models.Reservas;

namespace LuxuryApp.Services.Reservas
{
    /// <summary>
    /// Contexto público resuelto a partir del slug. Solo contiene datos no sensibles
    /// necesarios para renderizar la página y aplicar las reglas de reserva.
    /// </summary>
    public sealed class PublicBookingTenantContext
    {
        public Guid TenantId { get; init; }
        public string NombreNegocio { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string? MensajeBienvenida { get; init; }
        public string? MensajeConfirmacion { get; init; }
        public bool PermiteElegirFuncionario { get; init; }
        public bool PermiteCualquierFuncionario { get; init; }
        public bool MostrarFotosFuncionarios { get; init; } = true;
        public int MinAdvanceMinutes { get; init; }
        public int MaxDaysAhead { get; init; }
    }

    public interface IBookingSettingsService
    {
        /// <summary>Construye el VM de configuración para el tenant actual (crea defaults en memoria si no existe).</summary>
        Task<BookingSettingsViewModel> BuildSettingsViewModelAsync(CancellationToken cancellationToken = default);

        /// <summary>Persiste la configuración del tenant actual. Valida y normaliza el slug.</summary>
        Task SaveSettingsAsync(BookingSettingsViewModel input, string? userId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Resuelve un slug público a su tenant SIN contexto de tenant (ignora el query filter).
        /// Devuelve null si el slug no existe, las reservas están desactivadas o el tenant está inactivo.
        /// </summary>
        Task<PublicBookingTenantContext?> ResolvePublicBySlugAsync(string slug, CancellationToken cancellationToken = default);

        /// <summary>Slug del tenant actual (para construir el link en el panel privado).</summary>
        Task<string?> GetCurrentSlugAsync(CancellationToken cancellationToken = default);
    }
}
