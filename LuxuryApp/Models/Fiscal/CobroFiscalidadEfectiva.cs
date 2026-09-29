namespace LuxuryApp.Models.Fiscal
{
    /// <summary>
    /// Fiscalidad con la que se interpreta un cobro: la que quedó CONGELADA cuando se registró
    /// (snapshot) o, para cobros anteriores al sistema de snapshots, la que dice hoy el catálogo.
    ///
    /// <para>
    /// Esta es la ÚNICA regla de resolución del sistema. Dashboard, Ingresos, Excel, Liquidaciones,
    /// comprobantes y reportes la usan toda; si cada uno decidiera por su cuenta cuándo mirar el
    /// snapshot y cuándo el catálogo, volveríamos a tener módulos que reportan cifras distintas
    /// para el mismo cobro.
    /// </para>
    ///
    /// <para>
    /// La regla es deliberadamente simple: <b>manda el snapshot cuando existe</b>. Un cobro nuevo
    /// es historia, no una interpretación dinámica del catálogo actual: cambiar mañana el IVA de
    /// "Corte" afecta las ventas de mañana, nunca las de hoy.
    /// </para>
    ///
    /// <para>
    /// <see cref="AplicaIvaSnapshot"/> es el discriminante. No existe el snapshot parcial: o los
    /// tres valores están, o ninguno (lo garantiza el CHECK constraint CK_Cobros_SnapshotFiscal).
    /// </para>
    /// </summary>
    /// <param name="AplicaIva">Si la línea estaba gravada.</param>
    /// <param name="TarifaIva">Tarifa efectiva; null = heredar la del tenant (solo legacy).</param>
    /// <param name="PrecioIncluyeIva">Si el monto ya traía el IVA dentro; null = heredar (solo legacy).</param>
    /// <param name="DesdeSnapshot">true = el cobro trae su propia historia fiscal.</param>
    public readonly record struct CobroFiscalidadEfectiva(
        bool AplicaIva,
        decimal? TarifaIva,
        bool? PrecioIncluyeIva,
        bool DesdeSnapshot)
    {
        /// <summary>
        /// Resuelve la fiscalidad efectiva. Los parámetros <c>*Catalogo</c> son los que el sistema
        /// venía usando siempre (servicio/producto, o herencia del tenant cuando no hay catálogo),
        /// así que un cobro legacy se comporta EXACTAMENTE igual que antes de esta versión.
        /// </summary>
        public static CobroFiscalidadEfectiva Resolver(
            bool? aplicaIvaSnapshot,
            decimal? tarifaIvaSnapshot,
            bool? precioIncluyeIvaSnapshot,
            bool aplicaIvaCatalogo,
            decimal? tarifaIvaCatalogo,
            bool? precioIncluyeIvaCatalogo) =>
            aplicaIvaSnapshot.HasValue
                ? new CobroFiscalidadEfectiva(
                    aplicaIvaSnapshot.Value,
                    tarifaIvaSnapshot,
                    precioIncluyeIvaSnapshot,
                    DesdeSnapshot: true)
                : new CobroFiscalidadEfectiva(
                    aplicaIvaCatalogo,
                    tarifaIvaCatalogo,
                    precioIncluyeIvaCatalogo,
                    DesdeSnapshot: false);
    }

    /// <summary>
    /// Proyección que sabe de dónde sale su fiscalidad. La implementan las filas que leen cobros
    /// desde la base (liquidaciones, ingresos, Excel): traen las dos fuentes y dejan que
    /// <see cref="CobroFiscalidadEfectiva.Resolver"/> decida, una sola vez y en un solo lugar.
    /// </summary>
    public interface ICobroFiscalSnapshotOrigen
    {
        bool? AplicaIvaSnapshot { get; }
        decimal? TarifaIvaSnapshot { get; }
        bool? PrecioIncluyeIvaSnapshot { get; }

        /// <summary>Lo que diría hoy el catálogo. Es el fallback de los cobros legacy.</summary>
        bool AplicaIvaCatalogo { get; }
        decimal? TarifaIvaCatalogo { get; }
        bool? PrecioIncluyeIvaCatalogo { get; }
    }

    public static class CobroFiscalSnapshotExtensions
    {
        public static CobroFiscalidadEfectiva Fiscalidad(this ICobroFiscalSnapshotOrigen origen) =>
            CobroFiscalidadEfectiva.Resolver(
                origen.AplicaIvaSnapshot,
                origen.TarifaIvaSnapshot,
                origen.PrecioIncluyeIvaSnapshot,
                origen.AplicaIvaCatalogo,
                origen.TarifaIvaCatalogo,
                origen.PrecioIncluyeIvaCatalogo);
    }
}
