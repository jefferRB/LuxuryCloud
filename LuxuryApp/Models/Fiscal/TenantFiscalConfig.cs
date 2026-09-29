namespace LuxuryApp.Models.Fiscal
{
    /// <summary>
    /// Configuración fiscal efectiva del negocio (tenant). Los servicios/productos pueden
    /// sobreescribir tarifa e "incluye IVA"; si no lo hacen, heredan estos valores.
    /// </summary>
    public sealed record TenantFiscalConfig
    {
        public bool PreciosIncluyenIva { get; init; } = FiscalDefaults.PreciosIncluyenIvaPorDefecto;
        public decimal TarifaIvaPorDefecto { get; init; } = FiscalDefaults.TarifaIvaPorDefecto;

        public static readonly TenantFiscalConfig Default = new();
    }
}
