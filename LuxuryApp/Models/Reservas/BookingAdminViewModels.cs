using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Models.Reservas
{
    /// <summary>Página del panel privado de solicitudes de reserva.</summary>
    public sealed class BookingRequestsPageViewModel
    {
        public BookingRequestStatusFilter EstadoFiltro { get; init; } = BookingRequestFilters.DefaultStatus;
        public BookingRequestDateRange RangoFiltro { get; init; } = BookingRequestFilters.DefaultRange;

        /// <summary>Tokens del query string, para que la vista no traduzca enums a mano.</summary>
        public string EstadoFiltroValor => EstadoFiltro.ToToken();
        public string RangoFiltroValor => RangoFiltro.ToToken();

        /// <summary>
        /// El período no limita las pendientes (son backlog), así que con esa pestaña activa el
        /// selector no cambiaría nada y la vista lo deshabilita.
        /// </summary>
        public bool RangoAplicaAlListado => EstadoFiltro != BookingRequestStatusFilter.Pending;

        /// <summary>
        /// Conteos por estado. Las pendientes son TODO el backlog del tenant; los estados ya
        /// resueltos corresponden al rango seleccionado.
        /// </summary>
        public int PendientesCount { get; init; }
        public int ConfirmadasCount { get; init; }
        public int RechazadasCount { get; init; }

        /// <summary>Total de solicitudes recibidas en el rango, sin importar el estado.</summary>
        public int TotalCount { get; init; }

        public bool ReservasActivas { get; init; }

        /// <summary>
        /// Si el negocio tiene el complemento de WhatsApp. Cuando es false, la pantalla no habla de
        /// WhatsApp en cada solicitud: solo ofrece activarlo una vez, arriba.
        /// </summary>
        public bool WhatsAppActivo { get; init; }
        public string? Slug { get; init; }
        public string? LinkPublico { get; init; }

        public IReadOnlyList<BookingRequestListItemViewModel> Solicitudes { get; init; } =
            Array.Empty<BookingRequestListItemViewModel>();

        public bool EsEstadoActivo(BookingRequestStatusFilter estado) => EstadoFiltro == estado;

        public bool EsRangoActivo(BookingRequestDateRange rango) => RangoFiltro == rango;

        /// <summary>Contador que acompaña a cada pestaña.</summary>
        public int ConteoDe(BookingRequestStatusFilter estado) => estado switch
        {
            BookingRequestStatusFilter.Pending => PendientesCount,
            BookingRequestStatusFilter.Confirmed => ConfirmadasCount,
            BookingRequestStatusFilter.Rejected => RechazadasCount,
            _ => TotalCount
        };
    }

    public sealed class BookingRequestListItemViewModel
    {
        public int Id { get; set; }
        public string NombreCliente { get; set; } = string.Empty;
        public string TelefonoCliente { get; set; } = string.Empty;
        public string? CorreoCliente { get; set; }
        public string ServicioNombre { get; set; } = string.Empty;
        /// <summary>Lo que pidió el CLIENTE: un profesional concreto o "Cualquier profesional".</summary>
        public string FuncionarioNombre { get; set; } = "Cualquier profesional";

        /// <summary>
        /// Profesional que el SISTEMA reservó para sostener el espacio mientras la solicitud está
        /// pendiente. Null en solicitudes anteriores a esta función (no reservaban agenda).
        /// </summary>
        public string? FuncionarioAsignadoNombre { get; set; }

        public bool SolicitoCualquierFuncionario { get; set; }
        public DateTime FechaHoraInicioSolicitada { get; set; }
        public int DuracionMinutos { get; set; }
        public string? NotasCliente { get; set; }
        public string Estado { get; set; } = BookingRequestStates.Pending;
        public DateTime CreatedAtUtc { get; set; }

        /// <summary>
        /// <see cref="CreatedAtUtc"/> convertido a la hora local del negocio. Lo calcula el
        /// servicio con el offset del reloj de negocio: la vista solo lo formatea.
        /// </summary>
        public DateTime RecibidaLocal { get; set; }
        public string? RejectedReason { get; set; }
        public int? ConvertedCitaId { get; set; }
        public bool AceptaWhatsApp { get; set; }

        /// <summary>UTC del envío de la confirmación por WhatsApp de la cita creada (null si no se envió).</summary>
        public DateTime? ConfirmacionWhatsAppEnviadaUtc { get; set; }

        /// <summary>Estado de confirmación WhatsApp de la cita creada (ErrorEnvio/NoEnviada/Pendiente/Confirmada).</summary>
        public string? ConfirmacionWhatsAppEstado { get; set; }

        /// <summary>True si la confirmación por WhatsApp de la cita creada ya fue enviada.</summary>
        public bool ConfirmacionWhatsAppEnviada => ConfirmacionWhatsAppEnviadaUtc.HasValue;
    }

    /// <summary>Configuración "Reservas online" (vista privada del negocio).</summary>
    public sealed class BookingSettingsViewModel
    {
        public bool PublicBookingEnabled { get; set; }

        [MaxLength(80)]
        public string? PublicBookingSlug { get; set; }

        public bool PublicBookingAllowEmployeeSelection { get; set; }

        public bool PublicBookingAllowAnyEmployee { get; set; } = true;

        /// <summary>Interruptor maestro: mostrar fotos de funcionarios en el link público.</summary>
        public bool PublicBookingShowEmployeePhotos { get; set; } = true;

        [Range(0, 43200)]
        public int PublicBookingMinAdvanceMinutes { get; set; } = TenantBookingSettings.DefaultMinAdvanceMinutes;

        [Range(1, 365)]
        public int PublicBookingMaxDaysAhead { get; set; } = TenantBookingSettings.DefaultMaxDaysAhead;

        [MaxLength(500)]
        public string? PublicBookingWelcomeMessage { get; set; }

        [MaxLength(500)]
        public string? PublicBookingConfirmationMessage { get; set; }

        [Range(5, 240)]
        public int SlotIntervalMinutes { get; set; } = TenantBookingSettings.DefaultSlotIntervalMinutes;

        /// <summary>
        /// Jornada del negocio día por día. Siempre siete entradas; el día concreto lo lleva cada
        /// fila en <see cref="BookingDayScheduleViewModel.DiaSemana"/>, no la posición del índice,
        /// para que el orden de presentación (lunes primero) no sea parte del contrato.
        /// </summary>
        public List<BookingDayScheduleViewModel> Horario { get; set; } = new();

        // Solo lectura, para la UI
        public string NombreNegocio { get; set; } = string.Empty;
        public string? LinkPublico { get; set; }

        /// <summary>Ruta del QR del enlace público. Null si todavía no hay enlace.</summary>
        public string? QrPngUrl { get; set; }
        public string? QrDescargaUrl { get; set; }

        /// <summary>
        /// Bloqueos recurrentes vigentes (almuerzo, limpieza...). Se muestran acá como resumen de
        /// solo lectura: el alta/edición vive en su propio módulo, que es la fuente de verdad.
        /// </summary>
        public IReadOnlyList<BookingRecurringBlockSummary> BloqueosRecurrentes { get; set; } =
            Array.Empty<BookingRecurringBlockSummary>();
    }

    /// <summary>Jornada de UN día en la pantalla de configuración.</summary>
    public sealed class BookingDayScheduleViewModel
    {
        /// <summary>0 = domingo .. 6 = sábado. Se valida en el servidor (rango y sin repetidos).</summary>
        public int DiaSemana { get; set; }

        public bool Abierto { get; set; }

        public TimeOnly Apertura { get; set; } = TenantBookingSettings.DefaultOpenTime;

        public TimeOnly Cierre { get; set; } = TenantBookingSettings.DefaultCloseTime;

        public string NombreDia => BookingDayNames.Largo(DiaSemana);
    }

    /// <summary>Fila de solo lectura del resumen de bloqueos recurrentes.</summary>
    public sealed record BookingRecurringBlockSummary(int Id, string Nombre, string Horario, string Dias);

    /// <summary>Nombres de día en español, en un solo lugar.</summary>
    public static class BookingDayNames
    {
        private static readonly string[] Largos =
            ["Domingo", "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado"];

        /// <summary>Orden de presentación: lunes primero, domingo al final (uso local del negocio).</summary>
        public static readonly int[] OrdenSemana = [1, 2, 3, 4, 5, 6, 0];

        public static string Largo(int diaSemana) =>
            diaSemana is >= 0 and <= 6 ? Largos[diaSemana] : string.Empty;
    }
}
