using LuxuryApp.Models.Platform;
using LuxuryApp.Models.Reports;

namespace LuxuryApp.Services.Platform
{
    /// <summary>Resultado de guardar la configuración de un tenant desde Plataforma.</summary>
    public sealed record PlatformSaveSettingsResult(bool TenantFound, bool Saved)
    {
        public static readonly PlatformSaveSettingsResult NotFound = new(false, false);
    }

    /// <summary>
    /// Consola de Plataforma (SuperAdmin) para el Resumen Ejecutivo Mensual: agregados y acciones
    /// cross-tenant. Todas las lecturas filtran por el <c>tenantId</c> explícito de cada fila; los
    /// envíos y el guardado de configuración se ejecutan dentro del scope del tenant destino para
    /// respetar el aislamiento y los guards de RLS.
    /// </summary>
    public interface IPlatformMonthlyReportService
    {
        Task<PlatformMonthlyReportOverview> GetOverviewAsync(CancellationToken cancellationToken = default);

        Task<PlatformMonthlyReportDetailViewModel?> GetTenantDetailAsync(
            Guid tenantId,
            int take = 50,
            CancellationToken cancellationToken = default);

        /// <summary>Crea o actualiza la configuración del tenant (dentro de su scope).</summary>
        Task<PlatformSaveSettingsResult> SaveSettingsAsync(
            Guid tenantId,
            PlatformMonthlyReportSettingsForm form,
            CancellationToken cancellationToken = default);

        /// <summary>Dispara un envío de PRUEBA a un correo interno, dentro del scope del tenant.</summary>
        Task<MonthlyReportSendResult> SendTestAsync(
            Guid tenantId,
            int year,
            int month,
            string recipientEmail,
            CancellationToken cancellationToken = default);

        /// <summary>Dispara el envío REAL del tenant (idempotente), dentro de su scope.</summary>
        Task<MonthlyReportSendResult> SendRealAsync(
            Guid tenantId,
            int year,
            int month,
            CancellationToken cancellationToken = default);
    }
}
