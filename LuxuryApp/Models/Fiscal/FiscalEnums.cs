namespace LuxuryApp.Models.Fiscal
{
    /// <summary>
    /// Tipo de relación del colaborador con el negocio. Determina, junto con
    /// <see cref="ComisionCalculadaSobre"/> y la configuración de facturación, cómo se liquida
    /// su pago. Los valores numéricos son estables (persistidos en BD): NO reordenar.
    /// </summary>
    public enum TipoRelacionColaborador
    {
        /// <summary>Empleado del negocio (planilla). No factura IVA por su cuenta.</summary>
        Empleado = 0,

        /// <summary>Profesional independiente que puede facturar IVA sobre su comisión.</summary>
        Independiente = 1,

        /// <summary>Alquiler de silla / espacio. Normalmente comisión sobre total.</summary>
        AlquilerSilla = 2
    }

    /// <summary>
    /// Base sobre la que se aplica el porcentaje de comisión del colaborador.
    /// Es la fuente de verdad; el flag histórico <c>RebajarImpuestosAntesDeComision</c> se mapea
    /// a este enum (true → <see cref="BaseSinIva"/>, false → <see cref="TotalCobrado"/>).
    /// Los valores numéricos son estables (persistidos en BD): NO reordenar.
    /// </summary>
    public enum ComisionCalculadaSobre
    {
        /// <summary>La comisión se calcula sobre el total cobrado al cliente (IVA incluido).</summary>
        TotalCobrado = 0,

        /// <summary>La comisión se calcula sobre la base de la venta sin IVA.</summary>
        BaseSinIva = 1
    }

    /// <summary>
    /// Cómo se trata el IVA del colaborador independiente al liquidarlo. Solo tiene efecto para
    /// <see cref="TipoRelacionColaborador.Independiente"/>. Valores estables (persistidos): NO reordenar.
    /// </summary>
    public enum ModalidadIvaColaborador
    {
        /// <summary>A) No factura IVA: recibe solo su comisión; IVA colaborador = 0.</summary>
        NoFactura = 0,

        /// <summary>
        /// B) Factura IVA incluido dentro de su parte (caso principal): el monto del colaborador
        /// (% acordado) YA incluye su IVA; se descompone en base + IVA sin aumentar el total.
        /// </summary>
        IvaIncluido = 1,

        /// <summary>
        /// C) Factura IVA adicional sobre su comisión: el IVA se SUMA por encima de la comisión
        /// (total a pagar = base comisión + IVA). Solo si se selecciona explícitamente.
        /// </summary>
        IvaAdicional = 2
    }
}
