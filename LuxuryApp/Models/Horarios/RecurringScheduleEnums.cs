namespace LuxuryApp.Models.Horarios
{
    /// <summary>
    /// Tipo de regla recurrente. En esta fase solo existe <see cref="UnavailableBlock"/>;
    /// el enum queda abierto para futuras reglas de disponibilidad sin obligar a construir
    /// hoy un motor genérico que nadie necesita.
    /// </summary>
    public enum RecurringScheduleRuleType
    {
        /// <summary>Bloque de indisponibilidad (almuerzo, limpieza, capacitación...).</summary>
        UnavailableBlock = 0
    }

    /// <summary>A quién aplica la regla.</summary>
    public enum RecurringScheduleScope
    {
        /// <summary>Todos los colaboradores activos, evaluado dinámicamente en cada consulta.</summary>
        TodosLosColaboradores = 0,

        /// <summary>Solo los colaboradores listados en <c>RecurringScheduleRuleTarget</c>.</summary>
        ColaboradoresSeleccionados = 1
    }

    /// <summary>Qué hace una excepción sobre una fecha concreta.</summary>
    public enum RecurringScheduleExceptionType
    {
        /// <summary>Ese día no hay bloqueo (para el colaborador indicado, o para todos si es null).</summary>
        Omitir = 0,

        /// <summary>Ese día el bloqueo corre en otro horario.</summary>
        CambiarHorario = 1,

        /// <summary>Ese colaborador queda excluido de la regla ese día.</summary>
        ExcluirColaborador = 2
    }
}
