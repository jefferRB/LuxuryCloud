namespace LuxuryApp.Models.Horarios
{
    /// <summary>
    /// Regla ÚNICA de intersección de intervalos de tiempo del sistema.
    ///
    /// <para>
    /// Convención: los intervalos son <c>[inicio, fin)</c> — el fin NO pertenece al intervalo. Por
    /// eso un bloque de 12:00 a 13:00 y otro de 13:00 a 14:00 NO entran en conflicto, mientras que
    /// 12:30–13:30 sí. La misma convención rige citas, descansos, bloqueos recurrentes, solicitudes
    /// pendientes y la jornada del negocio; tener una sola implementación es lo que garantiza que
    /// el calendario y las reservas públicas nunca respondan distinto sobre el mismo horario.
    /// </para>
    /// </summary>
    public static class TimeIntervalMath
    {
        /// <summary>True si <c>[inicioA, finA)</c> y <c>[inicioB, finB)</c> comparten algún instante.</summary>
        public static bool Overlaps(DateTime inicioA, DateTime finA, DateTime inicioB, DateTime finB) =>
            inicioA < finB && inicioB < finA;

        /// <summary>
        /// Versión para horas de pared del mismo día. La usa la jornada del negocio, donde el
        /// bloque debe caber COMPLETO dentro de la ventana del día.
        /// </summary>
        public static bool Contains(TimeOnly ventanaInicio, TimeOnly ventanaFin, TimeOnly inicio, TimeOnly fin) =>
            inicio >= ventanaInicio && fin <= ventanaFin && fin > inicio;
    }
}
