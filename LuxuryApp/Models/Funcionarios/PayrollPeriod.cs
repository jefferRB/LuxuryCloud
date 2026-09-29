using System.Globalization;

namespace LuxuryApp.Models.Funcionarios
{
    /// <summary>Tipo de periodo de liquidación. Valores estables (viajan en la URL).</summary>
    public enum PayrollPeriodType
    {
        Semanal = 0,
        Quincenal = 1,
        Personalizado = 2
    }

    /// <summary>
    /// Periodo de liquidación resuelto (rango + navegación + etiquetas de presentación).
    /// El servicio de liquidación ya opera sobre un rango arbitrario, así que este tipo es la
    /// única fuente de la lógica de periodos (semana lunes–domingo, quincena 1–15 / 16–fin de mes).
    /// </summary>
    public sealed record PayrollPeriod
    {
        public PayrollPeriodType Tipo { get; init; }
        public DateTime Inicio { get; init; }
        public DateTime Fin { get; init; }

        /// <summary>Fecha de referencia para navegar al periodo anterior/siguiente.</summary>
        public DateTime ReferenciaAnterior { get; init; }
        public DateTime ReferenciaSiguiente { get; init; }

        /// <summary>Ej.: "29 jun. — 05 jul. 2026".</summary>
        public string Etiqueta { get; init; } = string.Empty;

        /// <summary>"Semanal" | "Quincenal".</summary>
        public string TipoLabel { get; init; } = string.Empty;

        /// <summary>"Pagar semana" | "Pagar quincena".</summary>
        public string CtaTexto { get; init; } = string.Empty;
    }

    /// <summary>
    /// Resuelve el rango de un periodo de liquidación a partir de un tipo y una fecha de referencia.
    /// Sin dependencias → testeable. NO contiene cálculo fiscal.
    /// </summary>
    public static class PayrollPeriodCalculator
    {
        private static readonly CultureInfo CrCulture = CultureInfo.GetCultureInfo("es-CR");

        /// <summary>Rango máximo permitido para un periodo personalizado (evita consultas pesadas).</summary>
        public const int MaxDiasPersonalizado = 62;

        public static PayrollPeriodType ParseTipo(string? valor)
        {
            if (string.Equals(valor, "quincenal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(valor, nameof(PayrollPeriodType.Quincenal), StringComparison.OrdinalIgnoreCase))
            {
                return PayrollPeriodType.Quincenal;
            }

            if (string.Equals(valor, "personalizado", StringComparison.OrdinalIgnoreCase)
                || string.Equals(valor, nameof(PayrollPeriodType.Personalizado), StringComparison.OrdinalIgnoreCase))
            {
                return PayrollPeriodType.Personalizado;
            }

            return PayrollPeriodType.Semanal;
        }

        public static PayrollPeriod Resolve(PayrollPeriodType tipo, DateTime referencia)
        {
            referencia = referencia.Date;

            var (inicio, fin) = tipo == PayrollPeriodType.Quincenal
                ? ResolveQuincena(referencia)
                : ResolveSemana(referencia);

            return new PayrollPeriod
            {
                Tipo = tipo,
                Inicio = inicio,
                Fin = fin,
                // Navegación uniforme: un día antes cae en el periodo anterior; un día después en el siguiente.
                ReferenciaAnterior = inicio.AddDays(-1),
                ReferenciaSiguiente = fin.AddDays(1),
                Etiqueta = BuildEtiqueta(inicio, fin),
                TipoLabel = tipo == PayrollPeriodType.Quincenal ? "Quincenal" : "Semanal",
                CtaTexto = tipo == PayrollPeriodType.Quincenal ? "Pagar quincena" : "Pagar semana"
            };
        }

        /// <summary>
        /// Resuelve un periodo personalizado a partir de fechas de inicio/fin (opcionales).
        /// Aplica valores por defecto seguros (mes en curso), corrige el orden si vienen invertidas
        /// y recorta el rango al máximo permitido. Devuelve un aviso amigable cuando ajusta algo.
        /// </summary>
        public static (PayrollPeriod Periodo, string? Aviso) ResolvePersonalizado(
            DateTime? desde, DateTime? hasta, DateTime hoy)
        {
            hoy = hoy.Date;
            var inicio = (desde?.Date) ?? new DateTime(hoy.Year, hoy.Month, 1);
            var fin = (hasta?.Date) ?? hoy;
            string? aviso = null;

            if (fin < inicio)
            {
                (inicio, fin) = (fin, inicio);
                aviso = "La fecha inicial era mayor que la final; se intercambiaron para mostrar el rango correctamente.";
            }

            var dias = (fin - inicio).Days + 1;
            if (dias > MaxDiasPersonalizado)
            {
                fin = inicio.AddDays(MaxDiasPersonalizado - 1);
                aviso = $"El rango máximo es de {MaxDiasPersonalizado} días; se ajustó la fecha final automáticamente.";
            }

            var periodo = new PayrollPeriod
            {
                Tipo = PayrollPeriodType.Personalizado,
                Inicio = inicio,
                Fin = fin,
                ReferenciaAnterior = inicio.AddDays(-1),
                ReferenciaSiguiente = fin.AddDays(1),
                Etiqueta = BuildEtiqueta(inicio, fin),
                TipoLabel = "Rango",
                CtaTexto = "Pagar rango"
            };

            return (periodo, aviso);
        }

        // Semana lunes–domingo que contiene la fecha (mismo criterio que la liquidación semanal previa).
        private static (DateTime Inicio, DateTime Fin) ResolveSemana(DateTime referencia)
        {
            var diff = (7 + (referencia.DayOfWeek - DayOfWeek.Monday)) % 7;
            var inicio = referencia.AddDays(-diff).Date;
            return (inicio, inicio.AddDays(6).Date);
        }

        // Quincena de calendario (convención CR): 1–15 y 16–último día del mes.
        private static (DateTime Inicio, DateTime Fin) ResolveQuincena(DateTime referencia)
        {
            if (referencia.Day <= 15)
            {
                var inicio = new DateTime(referencia.Year, referencia.Month, 1);
                return (inicio, new DateTime(referencia.Year, referencia.Month, 15));
            }

            var segundaInicio = new DateTime(referencia.Year, referencia.Month, 16);
            var ultimoDia = DateTime.DaysInMonth(referencia.Year, referencia.Month);
            return (segundaInicio, new DateTime(referencia.Year, referencia.Month, ultimoDia));
        }

        private static string BuildEtiqueta(DateTime inicio, DateTime fin)
        {
            // "29 jun. — 05 jul. 2026" (o con el año una sola vez si comparten año).
            return inicio.Year == fin.Year
                ? $"{inicio.ToString("dd MMM", CrCulture)} — {fin.ToString("dd MMM yyyy", CrCulture)}"
                : $"{inicio.ToString("dd MMM yyyy", CrCulture)} — {fin.ToString("dd MMM yyyy", CrCulture)}";
        }
    }
}
