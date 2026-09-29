using LuxuryApp.Models.Fiscal;

namespace LuxuryApp.Services.Fiscal
{
    /// <summary>
    /// Motor fiscal central. Única fuente de verdad para separar total, base sin IVA e IVA.
    /// Toda operación monetaria usa <see cref="decimal"/> y redondea a 2 decimales.
    /// </summary>
    public interface ITaxCalculationService
    {
        /// <summary>
        /// Calcula el desglose fiscal de un importe.
        /// </summary>
        /// <param name="totalOrBase">Total cobrado (si <paramref name="priceIncludesTax"/>) o base (si no).</param>
        /// <param name="taxRatePercent">Tarifa de IVA en porcentaje (13 = 13%).</param>
        /// <param name="priceIncludesTax">Si el importe ya incluye IVA.</param>
        /// <param name="taxable">Si el importe está sujeto a IVA.</param>
        TaxBreakdown Calcular(decimal totalOrBase, decimal taxRatePercent, bool priceIncludesTax, bool taxable);

        /// <summary>
        /// Calcula el desglose por líneas (una por elemento) y las suma. Cada línea se redondea
        /// antes de sumar para evitar diferencias por redondeo respecto al cálculo del total.
        /// El <see cref="TaxBreakdown.TaxRatePercent"/> y <see cref="TaxBreakdown.PriceIncludesTax"/>
        /// del resultado reflejan la primera línea (informativos cuando hay tarifas mixtas).
        /// </summary>
        TaxBreakdown Sumar(IEnumerable<TaxLineInput> lineas);
    }
}
