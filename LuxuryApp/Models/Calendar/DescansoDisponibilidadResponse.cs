namespace LuxuryApp.Models.Calendar
{
    /// <summary>
    /// Conflicto de una fecha tal como lo consume el calendario. Contiene el dato (colaborador,
    /// franja, tipo) y la frase ya compuesta por el servidor, para que el navegador no tenga su
    /// propia traducción de los tipos de bloque.
    /// </summary>
    public sealed class DescansoConflictoResponse
    {
        public int FuncionarioId { get; init; }

        public string FuncionarioNombre { get; init; } = string.Empty;

        /// <summary>Hora local de inicio del bloque que estorba, en formato HH:mm.</summary>
        public string Inicio { get; init; } = string.Empty;

        /// <summary>Hora local de fin del bloque que estorba, en formato HH:mm.</summary>
        public string Fin { get; init; } = string.Empty;

        /// <summary>Token estable: CITA, DESCANSO, BLOQUEO_RECURRENTE o SOLICITUD_PENDIENTE.</summary>
        public string Tipo { get; init; } = string.Empty;

        /// <summary>Ej.: <c>Drew tiene una cita de 10:00 a 11:00</c>.</summary>
        public string Descripcion { get; init; } = string.Empty;
    }

    /// <summary>
    /// Disponibilidad de UNA fecha para TODO el conjunto de colaboradores seleccionado. Es una
    /// vista previa: el guardado vuelve a validar con los datos de ese momento.
    /// </summary>
    public sealed class DescansoDisponibilidadResponse
    {
        /// <summary>Fecha local en formato yyyy-MM-dd.</summary>
        public string Fecha { get; init; } = string.Empty;

        public bool Disponible { get; init; }

        public IReadOnlyList<DescansoConflictoResponse> Conflictos { get; init; } =
            Array.Empty<DescansoConflictoResponse>();
    }
}
