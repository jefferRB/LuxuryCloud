namespace LuxuryApp.Models.Funcionarios
{
    public static class LiquidacionSemanalDefaults
    {
        /// <summary>
        /// Categoría de egreso para la SALIDA DE CAJA de una liquidación ordinaria ya devengada.
        /// El motor de ganancia la excluye de los gastos porque esa comisión ya se restó en la
        /// línea de "Liquidaciones del equipo": contarla otra vez sería doble conteo.
        /// </summary>
        public const string CategoriaPagoFuncionarios = "Pago Funcionarios";

        /// <summary>
        /// Categoría recomendada para el COSTO LABORAL EXTRAORDINARIO: vacaciones, bonos, pagos
        /// adicionales o ajustes que NO forman parte de una comisión devengada.
        ///
        /// <para>
        /// A diferencia de <see cref="CategoriaPagoFuncionarios"/>, es un gasto operativo NORMAL y
        /// por eso SÍ reduce la ganancia. No hay nada especial que registrar en el motor: basta con
        /// que no esté en las exclusiones estructurales de <c>PeriodProfitCalculationService</c>.
        /// La constante existe para que la ayuda de la UI y los tests nombren lo mismo.
        /// </para>
        /// </summary>
        public const string CategoriaCostoLaboralExtraordinario = "Costo laboral extraordinario";

        public const string EstadoPagada = "PAGADA";

        public static readonly IReadOnlyCollection<string> MetodosPagoPermitidos =
            new[] { "EFECTIVO", "SINPE", "TARJETA" };
    }
}
