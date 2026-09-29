using System.Globalization;
using LuxuryApp.Models.Funcionarios;

namespace LuxuryApp.Models.Inversionistas
{
    /// <summary>Periodo financiero resuelto de un acuerdo (rango cerrado, ambos extremos inclusive).</summary>
    public sealed record InvestorPeriod
    {
        public InvestorPayoutFrequency Frecuencia { get; init; }

        public DateOnly Inicio { get; init; }

        public DateOnly Fin { get; init; }

        /// <summary>Ej.: "Julio 2026" o "01 jul. — 15 jul. 2026".</summary>
        public string Etiqueta { get; init; } = string.Empty;

        public DateOnly SiguienteInicio => Fin.AddDays(1);

        public DateOnly AnteriorReferencia => Inicio.AddDays(-1);

        public bool Contiene(DateOnly fecha) => fecha >= Inicio && fecha <= Fin;
    }

    /// <summary>
    /// Resuelve periodos financieros para inversionistas. Reutiliza
    /// <see cref="PayrollPeriodCalculator"/> para semanal (lunes–domingo) y quincenal (1–15 / 16–fin)
    /// de modo que el módulo no invente una definición de periodo distinta a la de liquidaciones.
    /// Sin dependencias → testeable.
    /// </summary>
    public static class InvestorPeriodCalculator
    {
        private static readonly CultureInfo CrCulture = CultureInfo.GetCultureInfo("es-CR");

        public static InvestorPeriod Resolve(InvestorPayoutFrequency frecuencia, DateOnly referencia)
        {
            if (frecuencia == InvestorPayoutFrequency.Mensual)
            {
                var inicio = new DateOnly(referencia.Year, referencia.Month, 1);
                var fin = new DateOnly(
                    referencia.Year,
                    referencia.Month,
                    DateTime.DaysInMonth(referencia.Year, referencia.Month));

                return new InvestorPeriod
                {
                    Frecuencia = frecuencia,
                    Inicio = inicio,
                    Fin = fin,
                    Etiqueta = EtiquetaMes(inicio)
                };
            }

            var tipo = frecuencia == InvestorPayoutFrequency.Quincenal
                ? PayrollPeriodType.Quincenal
                : PayrollPeriodType.Semanal;

            var payroll = PayrollPeriodCalculator.Resolve(tipo, referencia.ToDateTime(TimeOnly.MinValue));

            return new InvestorPeriod
            {
                Frecuencia = frecuencia,
                Inicio = DateOnly.FromDateTime(payroll.Inicio),
                Fin = DateOnly.FromDateTime(payroll.Fin),
                Etiqueta = payroll.Etiqueta
            };
        }

        /// <summary>Periodo inmediatamente anterior al indicado.</summary>
        public static InvestorPeriod Previous(InvestorPeriod periodo) =>
            Resolve(periodo.Frecuencia, periodo.AnteriorReferencia);

        /// <summary>Periodo inmediatamente siguiente al indicado.</summary>
        public static InvestorPeriod Next(InvestorPeriod periodo) =>
            Resolve(periodo.Frecuencia, periodo.SiguienteInicio);

        /// <summary>
        /// Último periodo COMPLETAMENTE cerrado a la fecha indicada. Es el que puede facturarse:
        /// nunca se genera un estado de un periodo todavía en curso.
        /// </summary>
        public static InvestorPeriod LastClosed(InvestorPayoutFrequency frecuencia, DateOnly hoy)
        {
            var actual = Resolve(frecuencia, hoy);
            return actual.Fin < hoy ? actual : Previous(actual);
        }

        /// <summary>
        /// True si la fecha es el PRIMER día de un periodo de esa frecuencia. Un cambio de
        /// porcentaje solo puede entrar en vigor en ese punto: de lo contrario partiría un periodo
        /// en dos acuerdos distintos y el estado de cuenta dejaría de ser explicable.
        /// </summary>
        public static bool EsInicioDePeriodo(InvestorPayoutFrequency frecuencia, DateOnly fecha) =>
            Resolve(frecuencia, fecha).Inicio == fecha;

        /// <summary>Etiqueta legible de un rango arbitrario (para vistas y correos).</summary>
        public static string BuildEtiqueta(InvestorPayoutFrequency frecuencia, DateOnly inicio, DateOnly fin)
        {
            if (frecuencia == InvestorPayoutFrequency.Mensual &&
                inicio.Day == 1 &&
                fin.Day == DateTime.DaysInMonth(fin.Year, fin.Month) &&
                inicio.Month == fin.Month &&
                inicio.Year == fin.Year)
            {
                return EtiquetaMes(inicio);
            }

            return inicio.Year == fin.Year
                ? $"{inicio.ToString("dd MMM", CrCulture)} — {fin.ToString("dd MMM yyyy", CrCulture)}"
                : $"{inicio.ToString("dd MMM yyyy", CrCulture)} — {fin.ToString("dd MMM yyyy", CrCulture)}";
        }

        public static string FrecuenciaTexto(InvestorPayoutFrequency frecuencia) => frecuencia switch
        {
            InvestorPayoutFrequency.Semanal => "Semanal",
            InvestorPayoutFrequency.Quincenal => "Quincenal",
            _ => "Mensual"
        };

        private static string EtiquetaMes(DateOnly inicio)
        {
            var nombre = CrCulture.DateTimeFormat.GetMonthName(inicio.Month);
            if (string.IsNullOrEmpty(nombre))
            {
                return $"{inicio.Month:D2}/{inicio.Year}";
            }

            return $"{char.ToUpper(nombre[0], CrCulture)}{nombre[1..]} {inicio.Year}";
        }
    }
}
