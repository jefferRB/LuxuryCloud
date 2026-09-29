namespace LuxuryApp.Models.Inversionistas
{
    /// <summary>
    /// Constantes compartidas del módulo de inversionistas. Centralizadas para que la regla
    /// anti-recursividad (un pago al inversionista NO puede volver a reducir la ganancia)
    /// tenga un solo nombre en todo el sistema.
    /// </summary>
    public static class InvestorDefaults
    {
        /// <summary>
        /// Categoría de egreso reservada para registrar salidas de dinero hacia inversionistas.
        /// SIEMPRE se excluye del cálculo de la ganancia distribuible: si contara como gasto,
        /// pagar al inversionista reduciría la ganancia y por tanto su propia participación
        /// (recursividad). Ver <c>InvestorProfitCalculationService</c>.
        /// </summary>
        public const string CategoriaDistribucionInversionistas = "Distribución a inversionistas";

        /// <summary>Máximo de participación acumulada entre acuerdos activos que se solapan.</summary>
        public const decimal MaxParticipacionAcumulada = 100m;

        /// <summary>Descripción de la versión de la fórmula que queda congelada en cada snapshot.</summary>
        public const string PolicyVersion = "v1";

        public static readonly IReadOnlyCollection<string> MetodosPagoPermitidos =
            new[] { "EFECTIVO", "SINPE", "TARJETA", "TRANSFERENCIA" };

        /// <summary>Normaliza el método de pago o devuelve null si no es válido.</summary>
        public static string? NormalizeMetodoPago(string? metodoPago)
        {
            if (string.IsNullOrWhiteSpace(metodoPago))
            {
                return null;
            }

            var normalized = metodoPago.Trim().ToUpperInvariant();
            return MetodosPagoPermitidos.Contains(normalized) ? normalized : null;
        }
    }
}
