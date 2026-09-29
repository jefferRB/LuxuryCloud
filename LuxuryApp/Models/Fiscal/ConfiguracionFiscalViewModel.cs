using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Fiscal
{
    /// <summary>Ajustes fiscales del negocio (tenant) editables por el administrador.</summary>
    public sealed class ConfiguracionFiscalViewModel
    {
        [Display(Name = "Los precios ya incluyen IVA")]
        public bool PreciosIncluyenIva { get; set; } = FiscalDefaults.PreciosIncluyenIvaPorDefecto;

        [Display(Name = "Tarifa de IVA por defecto (%)")]
        [Range(0, 100, ErrorMessage = "La tarifa de IVA debe estar entre 0 y 100.")]
        public decimal TarifaIvaPorDefecto { get; set; } = FiscalDefaults.TarifaIvaPorDefecto;
    }
}
