using LuxuryApp.Models.Fiscal;

namespace LuxuryApp.Services.Fiscal
{
    /// <summary>
    /// Provee la configuración fiscal del tenant actual y resuelve la configuración efectiva
    /// de una línea (servicio/producto con overrides, o herencia del tenant).
    /// </summary>
    public interface ITenantFiscalConfigService
    {
        /// <summary>Config fiscal del tenant actual. Si no hay fila, retorna los defaults CR.</summary>
        Task<TenantFiscalConfig> ObtenerAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Construye la línea fiscal efectiva combinando los overrides del servicio/producto con
        /// la configuración del tenant.
        /// </summary>
        TaxLineInput ResolverLinea(
            decimal monto,
            bool aplicaIva,
            decimal? tarifaOverride,
            bool? precioIncluyeIvaOverride,
            TenantFiscalConfig tenant);
    }
}
