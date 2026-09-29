using LuxuryApp.Models.Fiscal;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Fiscal
{
    public sealed class CobroFiscalPreviewService : ICobroFiscalPreviewService
    {
        private readonly ApplicationDbContext _context;
        private readonly ITenantFiscalConfigService _fiscalConfig;
        private readonly ITaxCalculationService _taxService;

        public CobroFiscalPreviewService(
            ApplicationDbContext context,
            ITenantFiscalConfigService fiscalConfig,
            ITaxCalculationService taxService)
        {
            _context = context;
            _fiscalConfig = fiscalConfig;
            _taxService = taxService;
        }

        public async Task<CobroFiscalPreview?> PreviewCitaAsync(
            int citaId,
            decimal monto,
            CancellationToken cancellationToken = default)
        {
            if (citaId <= 0)
            {
                return null;
            }

            if (monto < 0)
            {
                monto = 0m;
            }

            // Config fiscal efectiva del servicio de la cita (o del tenant si es personalizada).
            // Filtro global de tenant aplica: si la cita no es del tenant, no se encuentra.
            var cita = await _context.Citas
                .AsNoTracking()
                .Where(c => c.Id == citaId)
                .Select(c => new
                {
                    TieneServicio = c.Servicio != null,
                    AplicaIva = c.Servicio != null ? c.Servicio.AplicaIva : (bool?)null,
                    TarifaIva = c.Servicio != null ? c.Servicio.TarifaIva : null,
                    PrecioIncluyeIva = c.Servicio != null ? c.Servicio.PrecioIncluyeIva : null
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (cita is null)
            {
                return null;
            }

            var tenant = await _fiscalConfig.ObtenerAsync(cancellationToken);
            var aplicaIva = cita.AplicaIva ?? true;

            var linea = _fiscalConfig.ResolverLinea(monto, aplicaIva, cita.TarifaIva, cita.PrecioIncluyeIva, tenant);
            var b = _taxService.Calcular(linea.TotalOrBase, linea.TaxRatePercent, linea.PriceIncludesTax, linea.Taxable);

            return new CobroFiscalPreview
            {
                Total = b.GrossTotal,
                BaseSinIva = b.NetBase,
                IvaIncluido = b.TaxAmount,
                TarifaIva = b.TaxRatePercent,
                PrecioIncluyeIva = b.PriceIncludesTax,
                AplicaIva = aplicaIva && b.TaxRatePercent > 0m,
                TipoLinea = "Servicio"
            };
        }
    }
}
