namespace LuxuryApp.Services.Inversionistas
{
    /// <summary>
    /// Configuración global del cierre automático de estados de cuenta (sección
    /// "InvestorStatements").
    ///
    /// <para>
    /// <see cref="SchedulerEnabled"/> es el interruptor maestro. Mientras esté en <c>false</c>
    /// (default) el worker no genera NADA, sin importar la configuración por tenant. Es deliberado:
    /// <c>InvestorProfitPolicy.GeneracionAutomatica</c> se viene guardando desde antes de que
    /// existiera un worker que la leyera, así que puede haber negocios con la casilla marcada sin
    /// haber elegido nunca un cierre automático real. Activar el interruptor es una decisión
    /// explícita de producción.
    /// </para>
    ///
    /// <para>
    /// <see cref="SendEmails"/> separa GENERAR de ENVIAR: con él apagado el worker cierra los
    /// períodos y deja los estados listos, pero el correo lo manda una persona. Es el modo con el
    /// que conviene estrenar la función.
    /// </para>
    /// </summary>
    public sealed class InvestorStatementSchedulerOptions
    {
        public const string SectionName = "InvestorStatements";

        /// <summary>Interruptor maestro del cierre automático. Default false (seguro).</summary>
        public bool SchedulerEnabled { get; set; }

        /// <summary>Cada cuántos minutos revisa el worker si hay períodos cerrados. Default 30.</summary>
        public int PollingIntervalMinutes { get; set; } = 30;

        /// <summary>
        /// Si es false, el worker genera pero NUNCA envía correos, aunque el tenant o el acuerdo
        /// tengan el envío automático activado. Default false.
        /// </summary>
        public bool SendEmails { get; set; }

        /// <summary>
        /// Tope de períodos que se generan por inversionista en una sola pasada. Protege contra un
        /// acuerdo con fecha efectiva muy vieja que dispararía decenas de cálculos de golpe; lo que
        /// quede pendiente se genera en la pasada siguiente.
        /// </summary>
        public int MaxPeriodosPorInversionista { get; set; } = 12;
    }
}
