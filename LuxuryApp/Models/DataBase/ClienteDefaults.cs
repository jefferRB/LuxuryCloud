namespace LuxuryApp.Models.DataBase
{
    /// <summary>
    /// Valores iniciales de un cliente nuevo. Viven aquí para que el alta manual, la que crea
    /// el calendario y la que crea una reserva arranquen todas con el mismo número: es una regla
    /// estable del dominio, no configuración por tenant.
    /// </summary>
    public static class ClienteDefaults
    {
        /// <summary>
        /// Frecuencia de visita INICIAL, en días. Es solo el punto de partida mientras el cliente
        /// no tiene historial suficiente; en cuanto hay dos días de visita distintos manda el
        /// promedio observado (ver <c>ClienteVisitMetrics.EffectiveFrequencyDays</c>).
        /// </summary>
        public const int InitialVisitFrequencyDays = 15;
    }
}
