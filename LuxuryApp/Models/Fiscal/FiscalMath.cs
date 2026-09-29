namespace LuxuryApp.Models.Fiscal
{
    /// <summary>
    /// Redondeo monetario único para todo el motor fiscal y de liquidación.
    /// 2 decimales, <see cref="MidpointRounding.ToEven"/> (banca). Ver justificación en
    /// <c>TaxCalculationService</c>: reproduce los cuadres limpios del negocio en CR.
    /// </summary>
    public static class FiscalMath
    {
        public static decimal Redondear(decimal valor) =>
            Math.Round(valor, 2, MidpointRounding.ToEven);
    }
}
