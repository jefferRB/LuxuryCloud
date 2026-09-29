using LuxuryApp.Models.Reports;

namespace LuxuryApp.Services.Reports
{
    /// <summary>
    /// Resuelve los destinatarios reales del resumen mensual de un tenant, devolviendo
    /// incluidos y excluidos con motivo. Es la fuente única usada por el envío (real/prueba),
    /// por la vista previa de la UI y por el panel de Plataforma.
    /// <para>
    /// No aplica guard de "tenant actual": filtra siempre por el <c>tenantId</c> explícito
    /// recibido, para que el super admin pueda previsualizar cualquier tenant. Nunca incluye
    /// funcionarios ni cuentas con <c>FuncionarioId</c>.
    /// </para>
    /// </summary>
    public interface IMonthlyReportRecipientResolver
    {
        Task<MonthlyReportRecipientResolution> ResolveAsync(
            Guid tenantId,
            TenantMonthlyReportSettings settings,
            CancellationToken cancellationToken = default);
    }
}
