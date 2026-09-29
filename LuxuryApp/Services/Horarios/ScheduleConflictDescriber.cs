using System.Globalization;

namespace LuxuryApp.Services.Horarios
{
    /// <summary>
    /// Traduce un <see cref="ScheduleConflict"/> (dato) a la frase que lee el usuario (presentación).
    ///
    /// <para>
    /// Existe para que el mapa "tipo de bloque → palabra en español" viva en UN solo lugar: lo usan
    /// el mensaje de error al guardar y la vista previa de fechas del calendario. Sin esto, el
    /// navegador tendría su propia copia del mapa y podría desincronizarse.
    /// </para>
    ///
    /// <para>
    /// Deliberadamente NO menciona al cliente ni al servicio de la cita que estorba: para explicar
    /// por qué un horario no se puede tomar basta el colaborador, la franja y el tipo de bloque.
    /// </para>
    /// </summary>
    public static class ScheduleConflictDescriber
    {
        /// <summary>"una cita", "un descanso", "un bloqueo de horario", "una reserva pendiente".</summary>
        public static string DescribeType(string? tipo) => tipo switch
        {
            BusyIntervalSources.Descanso => "un descanso",
            BusyIntervalSources.BloqueoRecurrente => "un bloqueo de horario",
            BusyIntervalSources.SolicitudPendiente => "una reserva pendiente",
            _ => "una cita"
        };

        /// <summary>Ej.: <c>Drew tiene una cita de 10:00 a 11:00</c>.</summary>
        public static string Describe(ScheduleConflict conflicto)
        {
            ArgumentNullException.ThrowIfNull(conflicto);

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} tiene {1} de {2:HH:mm} a {3:HH:mm}",
                string.IsNullOrWhiteSpace(conflicto.FuncionarioNombre) ? "El colaborador" : conflicto.FuncionarioNombre,
                DescribeType(conflicto.Tipo),
                conflicto.Inicio,
                conflicto.Fin);
        }

        /// <summary>La misma frase precedida por el día, para listas de varias fechas.</summary>
        public static string DescribeWithDate(ScheduleConflict conflicto)
        {
            ArgumentNullException.ThrowIfNull(conflicto);

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:dd/MM}: {1}",
                conflicto.Fecha,
                Describe(conflicto));
        }
    }
}
