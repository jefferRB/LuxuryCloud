using LuxuryApp.Models.Reservas;

namespace LuxuryApp.Services.Reservas
{
    /// <summary>
    /// Catálogo publicable de reservas online: qué servicios se muestran y qué funcionarios pueden
    /// atender cada uno. Todo tenant-scoped (global query filter). Compatibilidad: si el tenant no
    /// tiene configuración, se comporta como antes (todos los servicios/funcionarios activos).
    /// </summary>
    public interface IBookingCatalogService
    {
        /// <summary>Servicios visibles en el link público, con nombre/descr/precio/categoría resueltos.</summary>
        Task<IReadOnlyList<PublicBookingServiceOption>> GetPublicServicesAsync(
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Ids de funcionarios activos que pueden atender el servicio. Si no hay asignación explícita
        /// (o ninguna habilitada quedó activa), devuelve todos los funcionarios activos (fallback).
        /// </summary>
        Task<IReadOnlyList<int>> GetCompatibleFuncionarioIdsAsync(
            int servicioId,
            CancellationToken cancellationToken = default);

        /// <summary>True si el servicio está activo y publicable online (respeta el fallback).</summary>
        Task<bool> IsServiceVisibleOnlineAsync(
            int servicioId,
            CancellationToken cancellationToken = default);

        /// <summary>VM del panel privado "Servicios publicados".</summary>
        Task<BookingCatalogViewModel> BuildManagementAsync(CancellationToken cancellationToken = default);

        /// <summary>Persiste la configuración de servicios publicados y sus funcionarios.</summary>
        Task SaveAsync(BookingCatalogSaveInput input, string? userId, CancellationToken cancellationToken = default);
    }
}
