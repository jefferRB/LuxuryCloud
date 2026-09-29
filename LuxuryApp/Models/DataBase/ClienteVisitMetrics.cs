namespace LuxuryApp.Models.DataBase
{
    /// <summary>
    /// Métricas CRM derivadas del historial real de atenciones de un cliente.
    /// SIEMPRE se calculan a partir de la fuente autoritativa (ver
    /// <c>IClienteVisitMetricsService</c>); nunca se persisten, para que no puedan
    /// quedar desactualizadas respecto al historial.
    /// </summary>
    /// <remarks>
    /// Todas salen de las CITAS del historial del cliente, el mismo conjunto que pinta la tabla
    /// "Historial de citas". Ni cobros, ni notas, ni campos persistidos en <c>Cliente</c>.
    /// </remarks>
    public sealed record ClienteVisitMetrics
    {
        /// <summary>
        /// Citas del historial que ya ocurrieron. Dos citas el mismo día son dos visitas
        /// atendidas; lo que se agrupa por día es la frecuencia, no este contador.
        /// </summary>
        public int AttendedVisits { get; init; }

        /// <summary>
        /// Fecha de la cita más reciente que ya ocurrió. <c>null</c> si no hay ninguna.
        /// La fecha de un cobro NO la mueve.
        /// </summary>
        public DateTime? LastVisitDate { get; init; }

        /// <summary>
        /// Promedio OBSERVADO de días entre días de visita consecutivos. <c>null</c> mientras
        /// haya menos de dos días distintos: dos citas del mismo día no generan un intervalo de
        /// 0 días. Sirve para saber si ya hay patrón real; para mostrar o decidir se usa
        /// <see cref="EffectiveFrequencyDays"/>.
        /// </summary>
        public int? AverageVisitFrequencyDays { get; init; }

        /// <summary>
        /// Frecuencia que el sistema realmente usa: el promedio observado cuando existe, y si no
        /// la frecuencia inicial del cliente. Nunca es <c>null</c>, así que Detalle, listado y el
        /// futuro CRM muestran y deciden con el mismo número.
        /// </summary>
        public int EffectiveFrequencyDays { get; init; } = ClienteDefaults.InitialVisitFrequencyDays;

        /// <summary>Días transcurridos desde la última visita. <c>null</c> si nunca fue atendido.</summary>
        public int? DaysSinceLastVisit { get; init; }

        /// <summary>
        /// Fecha en la que estadísticamente se espera que regrese
        /// (<see cref="LastVisitDate"/> + <see cref="EffectiveFrequencyDays"/>).
        /// NO es una cita agendada y no se muestra como tal: es la base para futuras campañas
        /// CRM/WhatsApp.
        /// </summary>
        public DateTime? ExpectedReturnDate { get; init; }

        /// <summary>
        /// Cliente sin historial, con la frecuencia inicial que se le haya configurado.
        /// </summary>
        public static ClienteVisitMetrics SinHistorial(int frecuenciaInicialDias) =>
            new() { EffectiveFrequencyDays = frecuenciaInicialDias };
    }
}
