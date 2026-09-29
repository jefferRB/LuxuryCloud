namespace LuxuryApp.Models.Fiscal
{
    /// <summary>
    /// Valores fiscales por defecto para Costa Rica. Se usan como default de columnas nuevas
    /// (Tenant/Servicio/Producto/Funcionario) y como respaldo cuando una entidad no define su
    /// propia configuración. Centralizados aquí para no repetir el 13% disperso por el código.
    /// </summary>
    public static class FiscalDefaults
    {
        /// <summary>Tarifa de IVA general de Costa Rica, en porcentaje (13 = 13%).</summary>
        public const decimal TarifaIvaPorDefecto = 13m;

        /// <summary>En CR los precios de servicios/productos se manejan con IVA incluido.</summary>
        public const bool PreciosIncluyenIvaPorDefecto = true;

        /// <summary>Por defecto los servicios/productos están sujetos a IVA.</summary>
        public const bool AplicaIvaPorDefecto = true;
    }
}
