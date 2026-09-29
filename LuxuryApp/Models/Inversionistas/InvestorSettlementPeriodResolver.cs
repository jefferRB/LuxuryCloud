namespace LuxuryApp.Models.Inversionistas
{
    /// <summary>
    /// Resuelve los periodos de liquidación de un acuerdo de inversionista.
    ///
    /// <para>
    /// Es la ÚNICA pieza que sabe qué significa un día de corte. Ni el controlador, ni la vista,
    /// ni el JavaScript, ni un worker calculan fechas de periodo: todos preguntan acá.
    /// </para>
    ///
    /// <para>Reglas:</para>
    /// <list type="bullet">
    ///   <item>Sin día de corte (<c>null</c>) se conserva el comportamiento histórico: mes
    ///   calendario para mensual, y las convenciones de <see cref="InvestorPeriodCalculator"/>
    ///   para semanal y quincenal.</item>
    ///   <item>Con corte el día N, un periodo va del día N+1 del mes anterior al día N de este mes.
    ///   El día de corte PERTENECE al periodo que cierra.</item>
    ///   <item>Si el mes no tiene el día N (corte 31 en febrero) se usa el último día real del mes,
    ///   y el periodo siguiente arranca el día 1.</item>
    /// </list>
    ///
    /// <para>Función pura y sin dependencias: se puede probar sin base de datos.</para>
    /// </summary>
    public static class InvestorSettlementPeriodResolver
    {
        /// <summary>Periodo que contiene la fecha indicada.</summary>
        public static InvestorPeriod Resolve(
            InvestorPayoutFrequency frecuencia,
            int? diaCorte,
            DateOnly referencia)
        {
            if (!UsaDiaDeCorte(frecuencia, diaCorte))
            {
                return InvestorPeriodCalculator.Resolve(frecuencia, referencia);
            }

            var corte = diaCorte!.Value;
            var corteDelMes = CorteDe(referencia.Year, referencia.Month, corte);

            // El día de corte cierra su periodo: una fecha igual al corte todavía pertenece al
            // periodo que termina ese día, no al siguiente.
            var fin = referencia <= corteDelMes
                ? corteDelMes
                : CorteDe(referencia.AddMonths(1).Year, referencia.AddMonths(1).Month, corte);

            return Construir(fin, corte);
        }

        /// <summary>Periodo que contiene la fecha indicada, según el acuerdo.</summary>
        public static InvestorPeriod Resolve(InvestorAgreement acuerdo, DateOnly referencia)
        {
            ArgumentNullException.ThrowIfNull(acuerdo);
            return Resolve(acuerdo.Frecuencia, acuerdo.DiaCorte, referencia);
        }

        /// <summary>Periodo en curso hoy. Su <c>Fin</c> es el próximo corte.</summary>
        public static InvestorPeriod CurrentPeriod(InvestorAgreement acuerdo, DateOnly hoy) =>
            Resolve(acuerdo, hoy);

        /// <summary>
        /// Último periodo COMPLETAMENTE cerrado. Un periodo cuyo corte es hoy todavía NO está
        /// cerrado: se cierra cuando el día de corte terminó, en la hora local del negocio. Por eso
        /// la fecha que se pasa acá tiene que ser "hoy" según la zona horaria operativa del tenant,
        /// nunca UTC.
        /// </summary>
        public static InvestorPeriod PreviousClosedPeriod(InvestorAgreement acuerdo, DateOnly hoy)
        {
            var actual = Resolve(acuerdo, hoy);
            return actual.Fin < hoy ? actual : Previous(acuerdo, actual);
        }

        /// <summary>Fecha del próximo corte a partir de hoy (inclusive: si hoy es corte, es hoy).</summary>
        public static DateOnly NextCutoff(InvestorAgreement acuerdo, DateOnly hoy) =>
            Resolve(acuerdo, hoy).Fin;

        /// <summary>Periodo inmediatamente anterior.</summary>
        public static InvestorPeriod Previous(InvestorAgreement acuerdo, InvestorPeriod periodo)
        {
            ArgumentNullException.ThrowIfNull(acuerdo);
            return Resolve(acuerdo.Frecuencia, acuerdo.DiaCorte, periodo.Inicio.AddDays(-1));
        }

        /// <summary>Periodo inmediatamente siguiente.</summary>
        public static InvestorPeriod Next(InvestorAgreement acuerdo, InvestorPeriod periodo)
        {
            ArgumentNullException.ThrowIfNull(acuerdo);
            return Resolve(acuerdo.Frecuencia, acuerdo.DiaCorte, periodo.Fin.AddDays(1));
        }

        /// <summary>
        /// True si la fecha es el PRIMER día de un periodo. Un cambio de acuerdo solo puede entrar
        /// en vigor ahí: de lo contrario partiría un periodo en dos acuerdos distintos y el estado
        /// de cuenta dejaría de ser explicable.
        /// </summary>
        public static bool EsInicioDePeriodo(
            InvestorPayoutFrequency frecuencia,
            int? diaCorte,
            DateOnly fecha) =>
            Resolve(frecuencia, diaCorte, fecha).Inicio == fecha;

        /// <summary>
        /// Primer día válido para que entre en vigor un cambio: hoy si hoy ya abre un periodo, o
        /// el arranque del periodo siguiente. Con corte 20, es el día 21.
        /// </summary>
        public static DateOnly ProximoInicioDePeriodo(
            InvestorPayoutFrequency frecuencia,
            int? diaCorte,
            DateOnly hoy)
        {
            var periodo = Resolve(frecuencia, diaCorte, hoy);
            return periodo.Inicio == hoy ? hoy : periodo.Fin.AddDays(1);
        }

        /// <summary>Etiqueta corta para tablas: "Corte mensual · día 20".</summary>
        public static string EtiquetaCorte(InvestorPayoutFrequency frecuencia, int? diaCorte)
        {
            var frecuenciaTexto = InvestorPeriodCalculator.FrecuenciaTexto(frecuencia).ToLowerInvariant();

            if (!UsaDiaDeCorte(frecuencia, diaCorte))
            {
                return frecuencia == InvestorPayoutFrequency.Mensual
                    ? "Corte mensual · fin de mes"
                    : $"Corte {frecuenciaTexto}";
            }

            return $"Corte {frecuenciaTexto} · día {diaCorte!.Value}";
        }

        /// <summary>Explicación en una frase, calculada a partir del corte configurado.</summary>
        public static string DescribirCorte(InvestorPayoutFrequency frecuencia, int? diaCorte)
        {
            if (frecuencia != InvestorPayoutFrequency.Mensual)
            {
                return frecuencia == InvestorPayoutFrequency.Semanal
                    ? "Cada período va de lunes a domingo."
                    : "Cada período va del 1 al 15 y del 16 al último día del mes.";
            }

            if (diaCorte is null)
            {
                return "Sin día de corte, cada período es el mes calendario completo: del día 1 al último día del mes.";
            }

            var corte = diaCorte.Value;

            // A partir del 28 el corte puede caer en el último día del mes, así que la frase no
            // puede prometer "el día N+1 del mes anterior": en febrero ese día no existe.
            return corte >= 28
                ? $"Con corte el día {corte}, cada período cierra ese día y el siguiente arranca al " +
                  $"día siguiente. Si el mes no llega al día {corte} (febrero, por ejemplo), cierra " +
                  "el último día real del mes."
                : $"Con corte el día {corte}, cada período va del día {corte + 1} del mes anterior al " +
                  $"día {corte} del mes actual. El propio día {corte} pertenece al período que cierra.";
        }

        /// <summary>El día de corte solo aplica a acuerdos mensuales.</summary>
        public static bool UsaDiaDeCorte(InvestorPayoutFrequency frecuencia, int? diaCorte) =>
            frecuencia == InvestorPayoutFrequency.Mensual &&
            diaCorte is >= 1 and <= 31;

        /// <summary>Normaliza lo que llega del formulario: fuera de rango o no mensual ⇒ null.</summary>
        public static int? NormalizarDiaCorte(InvestorPayoutFrequency frecuencia, int? diaCorte) =>
            UsaDiaDeCorte(frecuencia, diaCorte) ? diaCorte : null;

        // ─────────────── Interno ───────────────

        /// <summary>Fecha de corte de un mes concreto, recortada al último día real si hace falta.</summary>
        private static DateOnly CorteDe(int anio, int mes, int diaCorte) =>
            new(anio, mes, Math.Min(diaCorte, DateTime.DaysInMonth(anio, mes)));

        /// <summary>
        /// Arma el periodo que termina en <paramref name="fin"/>. El inicio es el día siguiente al
        /// corte del mes ANTERIOR, calculado sobre el mes (no sumando meses a una fecha recortada:
        /// con corte 31, febrero cierra el 28 y su mes anterior cierra el 31, no el 28).
        /// </summary>
        private static InvestorPeriod Construir(DateOnly fin, int diaCorte)
        {
            var (anioAnterior, mesAnterior) = fin.Month == 1
                ? (fin.Year - 1, 12)
                : (fin.Year, fin.Month - 1);

            var inicio = CorteDe(anioAnterior, mesAnterior, diaCorte).AddDays(1);

            return new InvestorPeriod
            {
                Frecuencia = InvestorPayoutFrequency.Mensual,
                Inicio = inicio,
                Fin = fin,
                Etiqueta = InvestorPeriodCalculator.BuildEtiqueta(
                    InvestorPayoutFrequency.Mensual,
                    inicio,
                    fin)
            };
        }
    }
}
