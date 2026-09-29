using System.Text.Json.Serialization;

namespace LuxuryApp.Models.PublicPages
{
    /// <summary>
    /// Horario estructurado del negocio para la landing publica. Se serializa a JSON y se
    /// guarda en <see cref="TenantPublicPage.BusinessHoursJson"/>. Modela hasta dos turnos
    /// por dia (ej. 8:00-12:00 y 14:00-18:00) para negocios que cierran a mediodia.
    /// </summary>
    public sealed class BusinessSchedule
    {
        /// <summary>Siempre 7 elementos, ordenados de Lunes (0) a Domingo (6).</summary>
        [JsonPropertyName("days")]
        public List<BusinessScheduleDay> Days { get; set; } = new();

        public bool HasAnyOpenDay =>
            Days.Any(day => !day.Closed && day.Ranges.Count > 0);
    }

    public sealed class BusinessScheduleDay
    {
        [JsonPropertyName("closed")]
        public bool Closed { get; set; }

        /// <summary>0 a 2 tramos horarios del dia.</summary>
        [JsonPropertyName("ranges")]
        public List<BusinessScheduleRange> Ranges { get; set; } = new();
    }

    public sealed class BusinessScheduleRange
    {
        /// <summary>Hora de apertura en formato "HH:mm" (24h).</summary>
        [JsonPropertyName("open")]
        public string Open { get; set; } = string.Empty;

        /// <summary>Hora de cierre en formato "HH:mm" (24h).</summary>
        [JsonPropertyName("close")]
        public string Close { get; set; } = string.Empty;
    }

    /// <summary>Estado calculado del horario para render en la landing (estilo Google Maps).</summary>
    public sealed class BusinessScheduleStatusViewModel
    {
        public bool HasSchedule { get; init; }
        public bool IsOpenNow { get; init; }

        /// <summary>Texto corto del resumen, ej. "Abierto ahora" o "Cerrado".</summary>
        public string StatusLabel { get; init; } = string.Empty;

        /// <summary>Detalle del resumen, ej. "Cierra a las 7:00 p. m." o "Abre mañana a las 8:00 a. m.".</summary>
        public string? StatusDetail { get; init; }

        public IReadOnlyList<BusinessScheduleDayRowViewModel> Days { get; init; } =
            Array.Empty<BusinessScheduleDayRowViewModel>();
    }

    public sealed class BusinessScheduleDayRowViewModel
    {
        public string DayName { get; init; } = string.Empty;
        public bool IsToday { get; init; }
        public bool Closed { get; init; }

        /// <summary>Ej. "8:00 a. m. – 12:00 p. m., 2:00 p. m. – 6:00 p. m." o "Cerrado".</summary>
        public string HoursText { get; init; } = string.Empty;
    }
}
