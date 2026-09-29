using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Reservas
{
    /// <summary>
    /// Datos públicos y seguros para renderizar la página /reservar/{slug}.
    /// No expone TenantId, ids internos sensibles ni datos financieros.
    /// </summary>
    public sealed class PublicBookingPageViewModel
    {
        public string Slug { get; set; } = string.Empty;
        public string NombreNegocio { get; set; } = string.Empty;
        public string? MensajeBienvenida { get; set; }
        public bool PermiteElegirFuncionario { get; set; }
        public bool PermiteCualquierFuncionario { get; set; }
        public bool MostrarWhatsApp { get; set; }
        public int MinAdvanceMinutes { get; set; }
        public int MaxDaysAhead { get; set; }

        /// <summary>Fecha mínima seleccionable (hoy en hora del negocio), formato yyyy-MM-dd.</summary>
        public string MinDateIso { get; set; } = string.Empty;

        /// <summary>Fecha máxima seleccionable, formato yyyy-MM-dd.</summary>
        public string MaxDateIso { get; set; } = string.Empty;

        /// <summary>Token de idempotencia único de esta carga del formulario.</summary>
        public string SubmissionToken { get; set; } = string.Empty;

        public int? PreselectedServiceId { get; set; }

        public string? PreselectedServiceName { get; set; }

        public IReadOnlyList<PublicBookingServiceOption> Servicios { get; set; } =
            Array.Empty<PublicBookingServiceOption>();

        public IReadOnlyList<PublicBookingEmployeeOption> Funcionarios { get; set; } =
            Array.Empty<PublicBookingEmployeeOption>();
    }

    public sealed class PublicBookingServiceOption
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = string.Empty;
        public int DuracionMinutos { get; set; }

        /// <summary>Precio a mostrar. Null cuando el negocio decidió no mostrar precio.</summary>
        public decimal? Precio { get; set; }

        public string? Descripcion { get; set; }

        public string? Categoria { get; set; }

        /// <summary>Ids de funcionarios que pueden atender este servicio (para filtrar el paso 2).</summary>
        public IReadOnlyList<int> FuncionarioIds { get; set; } = Array.Empty<int>();
    }

    public sealed class PublicBookingEmployeeOption
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = string.Empty;
        public string? Puesto { get; set; }
        public string? FotoUrl { get; set; }

        /// <summary>Color de respaldo para el avatar cuando no hay foto.</summary>
        public string ColorAvatar { get; set; } = "#6366f1";
    }

    /// <summary>Resultado JSON del endpoint de disponibilidad. Solo expone datos públicos y seguros.</summary>
    public sealed class BookingAvailabilityResult
    {
        public bool Success { get; set; } = true;

        public string Fecha { get; set; } = string.Empty;

        /// <summary>Horas disponibles (HH:mm) para la fecha consultada.</summary>
        public IReadOnlyList<string> Horas { get; set; } = Array.Empty<string>();

        /// <summary>Mensaje a mostrar cuando no hay horas (mensaje inteligente por caso).</summary>
        public string? Mensaje { get; set; }

        public int DurationMinutes { get; set; }

        public string? ServiceName { get; set; }

        /// <summary>Nombre del profesional elegido (si se eligió uno específico).</summary>
        public string? SelectedEmployeeName { get; set; }

        /// <summary>True si el profesional elegido no tiene espacio pero otros compatibles sí.</summary>
        public bool HasAvailabilityWithOtherEmployees { get; set; }

        /// <summary>Próximos espacios disponibles (cuando la fecha elegida no tiene).</summary>
        public IReadOnlyList<NextAvailableSlot> NextAvailableSlots { get; set; } =
            Array.Empty<NextAvailableSlot>();
    }

    /// <summary>
    /// Respuesta del endpoint de próximos espacios (/reservar/{slug}/proximos). Mínima a
    /// propósito: el navegador solo necesita poder pintar y elegir un espacio. No viaja
    /// ocupación, ni citas, ni clientes, ni la agenda interna del negocio.
    /// </summary>
    public sealed class BookingNextSlotsResult
    {
        public bool Success { get; set; } = true;

        /// <summary>Mensaje neutro cuando no se puede resolver la consulta. Nunca detalle interno.</summary>
        public string? Mensaje { get; set; }

        public IReadOnlyList<NextAvailableSlot> NextAvailableSlots { get; set; } =
            Array.Empty<NextAvailableSlot>();
    }

    /// <summary>Sugerencia de próximo espacio disponible. Solo datos públicos.</summary>
    public sealed class NextAvailableSlot
    {
        /// <summary>Fecha en formato yyyy-MM-dd (para reenviar en la solicitud).</summary>
        public string Fecha { get; set; } = string.Empty;

        /// <summary>Etiqueta amigable, ej. "Jueves 03/07".</summary>
        public string FechaLabel { get; set; } = string.Empty;

        /// <summary>Hora en formato HH:mm (para reenviar en la solicitud).</summary>
        public string Hora { get; set; } = string.Empty;

        /// <summary>Etiqueta amigable, ej. "10:30 a. m.".</summary>
        public string HoraLabel { get; set; } = string.Empty;

        public int? FuncionarioId { get; set; }

        public string? FuncionarioNombre { get; set; }
    }

    /// <summary>Payload del formulario público de solicitud (/reservar/{slug}/solicitar).</summary>
    public sealed class PublicBookingRequestInput
    {
        public int ServicioId { get; set; }

        public int? FuncionarioId { get; set; }

        /// <summary>Fecha solicitada, formato yyyy-MM-dd.</summary>
        public string? Fecha { get; set; }

        /// <summary>Hora de inicio solicitada, formato HH:mm.</summary>
        public string? Hora { get; set; }

        [MaxLength(120)]
        public string? Nombre { get; set; }

        [MaxLength(30)]
        public string? Telefono { get; set; }

        public bool AceptaWhatsApp { get; set; }

        /// <summary>Token de idempotencia generado al cargar el formulario. Evita solicitudes duplicadas.</summary>
        [MaxLength(64)]
        public string? SubmissionToken { get; set; }

        /// <summary>Honeypot anti-bot: debe llegar vacío. Si trae valor, se descarta la solicitud.</summary>
        public string? Website { get; set; }
    }
}
