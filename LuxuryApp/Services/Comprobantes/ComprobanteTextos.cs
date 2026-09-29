using System.Globalization;

namespace LuxuryApp.Services.Comprobantes
{
    /// <summary>
    /// Textos y formatos centralizados del comprobante interno. Mantener aquí la leyenda
    /// legal evita inconsistencias entre correo, PDF y vista pública.
    /// </summary>
    public static class ComprobanteTextos
    {
        /// <summary>
        /// Leyenda legal obligatoria. Deja explícito que NO es un comprobante fiscal validado
        /// por Hacienda. Debe aparecer en UI, correo y PDF.
        /// </summary>
        public const string LeyendaLegal =
            "Comprobante interno no fiscal. No sustituye una factura electrónica autorizada por " +
            "el Ministerio de Hacienda.";

        public const string PieGenerado =
            "Este comprobante fue generado automáticamente por LuxuryCloud.";

        /// <summary>Cultura de Costa Rica para formateo de montos en colones.</summary>
        public static readonly CultureInfo CulturaCR = CultureInfo.GetCultureInfo("es-CR");

        /// <summary>Formatea un monto como "₡12 345,00" usando la cultura de Costa Rica.</summary>
        public static string Colones(decimal monto, string moneda = "CRC")
        {
            // Para CRC usamos el símbolo de colón; para otras monedas dejamos el código + monto.
            if (string.Equals(moneda, "CRC", StringComparison.OrdinalIgnoreCase))
            {
                return "₡" + monto.ToString("#,##0.00", CulturaCR);
            }

            return moneda + " " + monto.ToString("#,##0.00", CulturaCR);
        }

        public static string Fecha(DateTime fecha) =>
            fecha.ToString("dd/MM/yyyy HH:mm", CulturaCR);

        public static string FechaCorta(DateTime fecha) =>
            fecha.ToString("dd/MM/yyyy", CulturaCR);
    }
}
