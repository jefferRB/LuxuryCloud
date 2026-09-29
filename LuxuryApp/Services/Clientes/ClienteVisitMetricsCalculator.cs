using LuxuryApp.Models.DataBase;

namespace LuxuryApp.Services.Clientes
{
    /// <summary>
    /// Cálculo PURO de las métricas CRM de un cliente. No conoce EF, tenant ni HTTP:
    /// recibe las fechas de las citas del historial y devuelve el resultado. Toda la regla de
    /// negocio de "frecuencia", "última visita" y "días sin visitar" vive aquí y en ningún otro lado.
    /// </summary>
    public static class ClienteVisitMetricsCalculator
    {
        /// <param name="fechasCitas">
        /// <c>FechaHoraCita</c> de las citas del historial del cliente, en HORA LOCAL del negocio.
        /// Es el MISMO conjunto que pinta la tabla "Historial de citas".
        /// </param>
        /// <param name="hoyLocal">Día local del negocio usado como "hoy".</param>
        /// <param name="frecuenciaInicialDias">
        /// Frecuencia configurada al crear el cliente. Solo se usa como punto de partida mientras
        /// no haya dos días de visita distintos; en cuanto los hay, manda el promedio observado.
        /// </param>
        public static ClienteVisitMetrics Calculate(
            IEnumerable<DateTime> fechasCitas,
            DateTime hoyLocal,
            int frecuenciaInicialDias)
        {
            ArgumentNullException.ThrowIfNull(fechasCitas);

            // Un valor inicial corrupto (0 o negativo) no debe propagarse a la UI como
            // "0 días": se cae al default del dominio.
            var frecuenciaInicial = frecuenciaInicialDias > 0
                ? frecuenciaInicialDias
                : ClienteDefaults.InitialVisitFrequencyDays;

            var hoy = hoyLocal.Date;

            // Una cita que todavía no ocurrió no es una visita: no cuenta ni adelanta la última
            // visita. Es lo único que se descarta del historial; no hay ningún otro filtro.
            var citasOcurridas = fechasCitas
                .Select(fecha => fecha.Date)
                .Where(fecha => fecha <= hoy)
                .ToList();

            if (citasOcurridas.Count == 0)
            {
                return ClienteVisitMetrics.SinHistorial(frecuenciaInicial);
            }

            // Visitas atendidas cuenta CITAS: dos citas el mismo día son dos visitas atendidas.
            // La frecuencia, en cambio, mide la separación entre DÍAS distintos, para que esas
            // dos citas del mismo día no inventen un intervalo de 0 días.
            var dias = citasOcurridas
                .Distinct()
                .OrderBy(fecha => fecha)
                .ToList();

            var ultimaVisita = dias[^1];
            var frecuenciaPromedio = CalcularFrecuenciaPromedio(dias);

            // En cuanto hay patrón real, el valor inicial deja de importar.
            var frecuenciaEfectiva = frecuenciaPromedio ?? frecuenciaInicial;

            return new ClienteVisitMetrics
            {
                AttendedVisits = citasOcurridas.Count,
                LastVisitDate = ultimaVisita,
                AverageVisitFrequencyDays = frecuenciaPromedio,
                EffectiveFrequencyDays = frecuenciaEfectiva,
                DaysSinceLastVisit = (hoy - ultimaVisita).Days,
                ExpectedReturnDate = ultimaVisita.AddDays(frecuenciaEfectiva)
            };
        }

        /// <summary>
        /// Promedio de los intervalos entre días de visita consecutivos. Con menos de dos días
        /// distintos no hay ningún intervalo, así que la frecuencia todavía no existe
        /// (<c>null</c>) — no es "0 días".
        /// </summary>
        private static int? CalcularFrecuenciaPromedio(IReadOnlyList<DateTime> diasOrdenados)
        {
            if (diasOrdenados.Count < 2)
            {
                return null;
            }

            double sumaIntervalos = 0;
            for (var i = 1; i < diasOrdenados.Count; i++)
            {
                sumaIntervalos += (diasOrdenados[i] - diasOrdenados[i - 1]).TotalDays;
            }

            var promedio = sumaIntervalos / (diasOrdenados.Count - 1);

            // Redondeo explícito: 16.5 días se muestra como 17, no como 16. Es presentación
            // de días, no dinero, por eso NO usa el half-even de FiscalMath.
            return (int)Math.Round(promedio, MidpointRounding.AwayFromZero);
        }
    }
}
