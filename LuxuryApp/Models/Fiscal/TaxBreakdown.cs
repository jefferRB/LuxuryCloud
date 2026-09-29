namespace LuxuryApp.Models.Fiscal
{
    /// <summary>
    /// Desglose fiscal de un importe. Inmutable. Todos los montos en <see cref="decimal"/> y
    /// redondeados a 2 decimales por el motor. Invariante garantizada:
    /// <c>GrossTotal == NetBase + TaxAmount</c>.
    /// </summary>
    public sealed record TaxBreakdown
    {
        /// <summary>Total cobrado al cliente (IVA incluido).</summary>
        public decimal GrossTotal { get; init; }

        /// <summary>Base de la venta sin IVA.</summary>
        public decimal NetBase { get; init; }

        /// <summary>IVA contenido en la venta.</summary>
        public decimal TaxAmount { get; init; }

        /// <summary>Tarifa de IVA aplicada, en porcentaje (13 = 13%).</summary>
        public decimal TaxRatePercent { get; init; }

        /// <summary>Si el importe de entrada ya incluía el IVA.</summary>
        public bool PriceIncludesTax { get; init; }

        public static readonly TaxBreakdown Zero = new();
    }

    /// <summary>
    /// Línea de entrada para el cálculo por líneas del motor fiscal (regla: calcular por línea
    /// y luego sumar, para evitar diferencias de redondeo).
    /// </summary>
    public sealed record TaxLineInput
    {
        /// <summary>Total (si <see cref="PriceIncludesTax"/>) o base (si no) de la línea.</summary>
        public decimal TotalOrBase { get; init; }

        /// <summary>Tarifa de IVA, en porcentaje.</summary>
        public decimal TaxRatePercent { get; init; }

        /// <summary>Si <see cref="TotalOrBase"/> ya incluye IVA.</summary>
        public bool PriceIncludesTax { get; init; }

        /// <summary>Si la línea está sujeta a IVA. Si es false, todo es base y el IVA es 0.</summary>
        public bool Taxable { get; init; } = true;
    }
}
