using LuxuryApp.Models.Fiscal;
using LuxuryApp.Services.Tenant;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Fiscal
{
    public sealed class TenantFiscalConfigService : ITenantFiscalConfigService
    {
        private readonly ApplicationDbContext _context;
        private readonly ITenantProvider _tenantProvider;

        public TenantFiscalConfigService(ApplicationDbContext context, ITenantProvider tenantProvider)
        {
            _context = context;
            _tenantProvider = tenantProvider;
        }

        public async Task<TenantFiscalConfig> ObtenerAsync(CancellationToken cancellationToken = default)
        {
            var tenantId = _tenantProvider.GetTenantId();

            var config = await _context.Tenants
                .AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => new TenantFiscalConfig
                {
                    PreciosIncluyenIva = t.PreciosIncluyenIva,
                    TarifaIvaPorDefecto = t.TarifaIvaPorDefecto
                })
                .FirstOrDefaultAsync(cancellationToken);

            return config ?? TenantFiscalConfig.Default;
        }

        public TaxLineInput ResolverLinea(
            decimal monto,
            bool aplicaIva,
            decimal? tarifaOverride,
            bool? precioIncluyeIvaOverride,
            TenantFiscalConfig tenant)
        {
            tenant ??= TenantFiscalConfig.Default;

            return new TaxLineInput
            {
                TotalOrBase = monto,
                TaxRatePercent = tarifaOverride ?? tenant.TarifaIvaPorDefecto,
                PriceIncludesTax = precioIncluyeIvaOverride ?? tenant.PreciosIncluyenIva,
                Taxable = aplicaIva
            };
        }
    }
}
