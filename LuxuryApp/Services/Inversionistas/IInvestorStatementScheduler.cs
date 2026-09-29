namespace LuxuryApp.Services.Inversionistas
{
    public enum InvestorStatementScheduleOutcome
    {
        /// <summary>El interruptor maestro está apagado.</summary>
        SchedulerDisabled = 0,

        /// <summary>El negocio no activó la generación automática.</summary>
        NotEnabled = 1,

        /// <summary>Todavía no llegó la hora de generación configurada.</summary>
        NotDue = 2,

        /// <summary>No hay ningún período cerrado sin estado de cuenta.</summary>
        NothingPending = 3,

        /// <summary>Se generó al menos un estado de cuenta.</summary>
        Generated = 4,

        /// <summary>Se generó algo pero al menos un inversionista falló.</summary>
        PartiallyGenerated = 5,

        /// <summary>No se pudo generar nada por errores.</summary>
        Failed = 6
    }

    /// <summary>
    /// Resultado de una pasada por un tenant. <see cref="Enviados"/> y <see cref="EnviosFallidos"/>
    /// se cuentan aparte a propósito: un correo que no salió NO invalida el estado generado.
    /// </summary>
    public sealed record InvestorStatementScheduleResult(
        InvestorStatementScheduleOutcome Outcome,
        int Generados = 0,
        int Enviados = 0,
        int EnviosFallidos = 0,
        int Fallidos = 0)
    {
        public static InvestorStatementScheduleResult Nada(InvestorStatementScheduleOutcome outcome) =>
            new(outcome);
    }

    /// <summary>
    /// Cierre automático de estados de cuenta de inversionistas.
    ///
    /// <para>
    /// No pregunta "¿hoy es el día de corte?": eso perdería el corte si el worker estuvo caído.
    /// Resuelve el ÚLTIMO estado existente y avanza período por período generando todos los que
    /// falten, en orden. Reencontrar tres cortes perdidos y emitirlos en secuencia es normal.
    /// </para>
    ///
    /// <para>
    /// Generar y enviar son cosas distintas: se genera siempre, y el correo sale solo si el
    /// negocio (o ese acuerdo) lo pidió Y el interruptor de envíos está activo.
    /// </para>
    /// </summary>
    public interface IInvestorStatementScheduler
    {
        /// <summary>
        /// Procesa un tenant. <paramref name="nowLocal"/> es la hora LOCAL del negocio: el cierre
        /// de un período se mide en el calendario del negocio, nunca en UTC.
        /// </summary>
        Task<InvestorStatementScheduleResult> ProcessTenantAsync(
            Guid tenantId,
            DateTime nowLocal,
            CancellationToken cancellationToken = default);
    }
}
