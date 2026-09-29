using LuxuryApp.Models.Fiscal;

namespace LuxuryApp.Services.Fiscal
{
    /// <summary>
    /// Implementación del motor fiscal. Sin estado → seguro para singleton y testeable con
    /// los ejemplos del negocio.
    ///
    /// Redondeo: 2 decimales con <see cref="MidpointRounding.ToEven"/> (banca). Esta elección
    /// es deliberada: reproduce exactamente los totales limpios del negocio en CR. Ej. base de
    /// ₡36.000 = 31.858,41; comisión 50% = 15.929,205 que con half-even da 15.929,20 y hace que
    /// el pago del independiente con factura IVA cuadre en ₡18.000 (= 50% del total). Con
    /// half-up daría 15.929,21 y rompería el cuadre.
    /// </summary>
    public sealed class TaxCalculationService : ITaxCalculationService
    {
        private static decimal Redondear(decimal valor) => FiscalMath.Redondear(valor);

        public TaxBreakdown Calcular(decimal totalOrBase, decimal taxRatePercent, bool priceIncludesTax, bool taxable)
        {
            // Exento / no sujeto: todo es base, IVA 0.
            if (!taxable || taxRatePercent <= 0m)
            {
                var monto = Redondear(totalOrBase);
                return new TaxBreakdown
                {
                    GrossTotal = monto,
                    NetBase = monto,
                    TaxAmount = 0m,
                    TaxRatePercent = taxable ? taxRatePercent : 0m,
                    PriceIncludesTax = priceIncludesTax
                };
            }

            var factor = taxRatePercent / 100m;

            if (priceIncludesTax)
            {
                // El precio YA incluye IVA (caso por defecto en CR).
                var gross = Redondear(totalOrBase);
                var net = Redondear(gross / (1m + factor));
                var tax = gross - net; // por diferencia → garantiza Gross == Net + Tax exacto.
                return new TaxBreakdown
                {
                    GrossTotal = gross,
                    NetBase = net,
                    TaxAmount = tax,
                    TaxRatePercent = taxRatePercent,
                    PriceIncludesTax = true
                };
            }

            // El IVA se suma encima de la base.
            var netBase = Redondear(totalOrBase);
            var taxAmount = Redondear(netBase * factor);
            return new TaxBreakdown
            {
                GrossTotal = netBase + taxAmount,
                NetBase = netBase,
                TaxAmount = taxAmount,
                TaxRatePercent = taxRatePercent,
                PriceIncludesTax = false
            };
        }

        public TaxBreakdown Sumar(IEnumerable<TaxLineInput> lineas)
        {
            ArgumentNullException.ThrowIfNull(lineas);

            decimal gross = 0m, net = 0m, tax = 0m;
            decimal tarifaInformativa = 0m;
            bool incluyeInformativo = true;
            var primera = true;

            foreach (var linea in lineas)
            {
                var b = Calcular(linea.TotalOrBase, linea.TaxRatePercent, linea.PriceIncludesTax, linea.Taxable);
                gross += b.GrossTotal;
                net += b.NetBase;
                tax += b.TaxAmount;

                if (primera)
                {
                    tarifaInformativa = b.TaxRatePercent;
                    incluyeInformativo = b.PriceIncludesTax;
                    primera = false;
                }
            }

            return new TaxBreakdown
            {
                GrossTotal = gross,
                NetBase = net,
                TaxAmount = tax,
                TaxRatePercent = tarifaInformativa,
                PriceIncludesTax = incluyeInformativo
            };
        }
    }
}
