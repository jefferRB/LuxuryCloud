using LuxuryApp.Models.WhatsApp;

namespace LuxuryApp.Models.Calendar
{
    /// <summary>
    /// Estado de los filtros de la vista "Control de citas y cobros".
    /// Se reutiliza tanto en el render inicial como en las peticiones AJAX.
    /// </summary>
    public sealed class ControlCitasCobrosFiltroViewModel
    {
        /// <summary>"dia", "semana" o "mes". Cualquier otro valor se trata como "dia".</summary>
        public string Rango { get; set; } = "dia";

        /// <summary>Fecha ancla del rango (día seleccionado / día dentro de la semana o mes).</summary>
        public DateTime Fecha { get; set; }

        /// <summary>Funcionario específico, o null = todos. El acceso siempre se valida en backend.</summary>
        public int? FuncionarioId { get; set; }

        /// <summary>"todos", "cobradas", "pendientes" o "canceladas".</summary>
        public string EstadoPago { get; set; } = "todos";

        /// <summary>Texto libre para buscar por nombre de cliente o teléfono.</summary>
        public string? Buscar { get; set; }
    }

    /// <summary>KPIs resumidos del rango filtrado.</summary>
    public sealed class ControlCitasCobrosKpiViewModel
    {
        public int TotalCitas { get; set; }
        public int Cobradas { get; set; }
        public int Pendientes { get; set; }
        public decimal MontoCobrado { get; set; }
        public decimal PendienteEstimado { get; set; }
        public int Canceladas { get; set; }
    }

    /// <summary>Una cita con su estado de pago global para la tabla / cards.</summary>
    public sealed class CitaCobroItemViewModel
    {
        public int CitaId { get; set; }
        public DateTime FechaHora { get; set; }
        public string Cliente { get; set; } = "Cliente";
        public string? Telefono { get; set; }
        public string? CorreoCliente { get; set; }
        public int? ClienteId { get; set; }
        public int FuncionarioId { get; set; }
        public string Funcionario { get; set; } = string.Empty;
        public string Servicio { get; set; } = "Servicio";
        public int? ServicioId { get; set; }
        public decimal? PrecioServicio { get; set; }

        // Estado de cobro (global, calculado en backend a partir de los cobros ligados).
        public bool YaCobrada { get; set; }
        public decimal? MontoCobrado { get; set; }
        public string? MetodoPago { get; set; }
        public int? CobroId { get; set; }

        // Estado del comprobante del cobro (si ya se cobró). Null = sin comprobante.
        public Comprobantes.ComprobanteEstadoEnvio? ComprobanteEstado { get; set; }
        public string? ComprobanteToken { get; set; }
        public int? ComprobanteId { get; set; }
        public bool TieneComprobante => ComprobanteId.HasValue;
        public bool ComprobanteEnviado => ComprobanteEstado == Comprobantes.ComprobanteEstadoEnvio.Sent;

        /// <summary>Cita cancelada por el cliente vía WhatsApp (solo aplica con add-on).</summary>
        public bool EsCancelada { get; set; }

        public string EstadoConfirmacionWhatsApp { get; set; } = WhatsAppConfirmationStates.Pendiente;

        /// <summary>
        /// Una cita es cobrable si no está cobrada ni cancelada. El monto base puede venir del
        /// catálogo, ser 0 o no estar definido (servicio personalizado): el monto final se captura
        /// en el modal. La colección ya está acotada a Tipo == "CITA" (sin descansos).
        /// </summary>
        public bool EsCobrable => !YaCobrada && !EsCancelada;

        /// <summary>"Cobrada", "Cancelada" o "Pendiente".</summary>
        public string EstadoPago => YaCobrada
            ? "Cobrada"
            : (EsCancelada ? "Cancelada" : "Pendiente");

        /// <summary>Monto a mostrar: el cobrado si existe, si no el precio esperado del servicio.</summary>
        public decimal? MontoMostrado => YaCobrada ? MontoCobrado : PrecioServicio;
    }

    /// <summary>Opción de funcionario para el selector del filtro.</summary>
    public sealed class ControlFuncionarioOption
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = string.Empty;
    }

    /// <summary>
    /// Datos mínimos de una cita necesarios para registrar su cobro desde el control admin.
    /// El FuncionarioId es el de la cita (quien realizó el servicio), no el del usuario.
    /// </summary>
    public sealed class ControlCitaCobroContexto
    {
        public int CitaId { get; set; }
        public int FuncionarioId { get; set; }
        public int? ServicioId { get; set; }

        /// <summary>Nombre del servicio personalizado cuando la cita no usa catálogo (ServicioId == null).</summary>
        public string? ServicioNombrePersonalizado { get; set; }

        public int? ClienteId { get; set; }
        public string NombreCliente { get; set; } = "Cliente";
        public decimal? PrecioServicio { get; set; }
        public bool YaCobrada { get; set; }
    }

    /// <summary>Modelo completo de la vista "Control de citas y cobros".</summary>
    public sealed class ControlCitasCobrosViewModel
    {
        public ControlCitasCobrosFiltroViewModel Filtro { get; set; } = new();
        public ControlCitasCobrosKpiViewModel Kpis { get; set; } = new();
        public IReadOnlyList<CitaCobroItemViewModel> Items { get; set; } = new List<CitaCobroItemViewModel>();
        public IReadOnlyList<ControlFuncionarioOption> Funcionarios { get; set; } = new List<ControlFuncionarioOption>();

        /// <summary>Cuando el tenant no tiene add-on de WhatsApp se ocultan columnas/acciones de WhatsApp.</summary>
        public bool HasWhatsAppAddon { get; set; }

        /// <summary>Límites del rango (inclusivos en pantalla).</summary>
        public DateTime Desde { get; set; }
        public DateTime Hasta { get; set; }
    }
}
