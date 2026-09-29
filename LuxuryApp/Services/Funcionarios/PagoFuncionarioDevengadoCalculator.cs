using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Fiscal;
using LuxuryApp.Models.Funcionarios;

namespace LuxuryApp.Services.Funcionarios
{
    public static class PagoFuncionarioDevengadoCalculator
    {
        /// <summary>Tarifa de IVA por defecto como factor (0.13). Compatibilidad; ver <see cref="FiscalDefaults"/>.</summary>
        public const decimal TasaImpuesto = FiscalDefaults.TarifaIvaPorDefecto / 100m;

        /// <summary>
        /// Base de venta sin IVA a partir de un total que YA incluye IVA (contexto CR).
        /// </summary>
        private static decimal BaseSinIva(decimal totalConIva) =>
            FiscalMath.Redondear(totalConIva / (1m + (FiscalDefaults.TarifaIvaPorDefecto / 100m)));

        /// <summary>Base sin IVA (a la tarifa por defecto) de un total con IVA incluido.</summary>
        public static decimal CalcularBaseSinIvaIncluido(decimal totalConIva) => BaseSinIva(totalConIva);

        /// <summary>IVA contenido (a la tarifa por defecto) en un total con IVA incluido.</summary>
        public static decimal CalcularIvaIncluido(decimal totalConIva) => totalConIva - BaseSinIva(totalConIva);

        /// <summary>
        /// Reparte un pago entre los meses de la producción que lo generó, proporcionalmente al
        /// devengado de cada mes.
        ///
        /// <para>
        /// Recibe el devengado YA CALCULADO. Antes lo resolvía acá dentro con la configuración
        /// actual del colaborador y una división plana entre 1,13, lo que lo convertía en una
        /// segunda implementación del devengado que podía contradecir a la liquidación. Ahora el
        /// único dueño de ese cálculo es <c>LiquidacionSemanalService.Devengado</c>, que respeta
        /// el snapshot del cobro y el motor fiscal canónico.
        /// </para>
        ///
        /// <para>
        /// El reparto en sí NO cambió: mismo prorrateo, mismo redondeo AwayFromZero y el último mes
        /// se lleva el residuo para que la suma cierre exacta contra el monto pagado.
        /// </para>
        /// </summary>
        public static IReadOnlyList<PagoFuncionarioDistribucionMensual> DistribuirMontoPagadoPorMes(
            IEnumerable<(DateTime Fecha, decimal Devengado)> produccion,
            decimal montoPagado)
        {
            var baseMensual = produccion
                .Where(x => x.Devengado > 0)
                .GroupBy(x => new { x.Fecha.Year, x.Fecha.Month })
                .Select(group => new PagoFuncionarioDistribucionMensual
                {
                    Anio = group.Key.Year,
                    Mes = group.Key.Month,
                    MontoAsignado = group.Sum(x => x.Devengado),
                    DiasAplicados = group
                        .Select(x => x.Fecha.Date)
                        .Distinct()
                        .Count()
                })
                .OrderBy(x => x.Anio)
                .ThenBy(x => x.Mes)
                .ToList();

            if (baseMensual.Count == 0 || montoPagado <= 0)
            {
                return Array.Empty<PagoFuncionarioDistribucionMensual>();
            }

            var totalBase = baseMensual.Sum(x => x.MontoAsignado);
            if (totalBase <= 0)
            {
                return Array.Empty<PagoFuncionarioDistribucionMensual>();
            }

            var distribucionFinal = new List<PagoFuncionarioDistribucionMensual>(baseMensual.Count);
            decimal montoAcumulado = 0;

            for (var index = 0; index < baseMensual.Count; index++)
            {
                var actual = baseMensual[index];
                var esUltimo = index == baseMensual.Count - 1;

                var montoAsignado = esUltimo
                    ? montoPagado - montoAcumulado
                    : Math.Round(
                        montoPagado * actual.MontoAsignado / totalBase,
                        2,
                        MidpointRounding.AwayFromZero);

                montoAcumulado += montoAsignado;

                distribucionFinal.Add(new PagoFuncionarioDistribucionMensual
                {
                    Anio = actual.Anio,
                    Mes = actual.Mes,
                    MontoAsignado = montoAsignado,
                    DiasAplicados = actual.DiasAplicados
                });
            }

            return distribucionFinal;
        }
    }

    public sealed class PagoFuncionarioDistribucionMensual
    {
        public int Anio { get; init; }
        public int Mes { get; init; }
        public decimal MontoAsignado { get; init; }
        public int DiasAplicados { get; init; }
    }
}
