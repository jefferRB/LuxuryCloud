namespace LuxuryApp.Models.Funcionarios
{
    /// <summary>Funcionario validado para el portal (resuelto desde el claim).</summary>
    public sealed class PortalFuncionario
    {
        public int IdFuncionario { get; init; }
        public string Nombre { get; init; } = string.Empty;
        public bool Activo { get; init; }
        public string ColorCalendario { get; init; } = string.Empty;
    }

    /// <summary>Una cita del funcionario para el portal (solo lectura).</summary>
    public sealed class PortalCitaItem
    {
        public int Id { get; init; }
        public DateTime FechaHora { get; init; }
        public string Cliente { get; init; } = string.Empty;
        public string Servicio { get; init; } = string.Empty;
        public string Tipo { get; init; } = "CITA";
        public string? Telefono { get; init; }
        public int? DuracionMinutos { get; init; }

        public int? ServicioId { get; init; }
        public decimal? PrecioServicio { get; init; }
        public bool YaCobrada { get; init; }

        /// <summary>
        /// Duración real para render/validación: snapshot de la cita, si no la del servicio,
        /// si no un default razonable. (DuracionMinutos crudo es null en citas de catálogo.)
        /// </summary>
        public int DuracionEfectiva { get; init; } = 30;

        // Datos para precargar el modal de edición (sin exponer otros funcionarios).
        public int? ClienteId { get; init; }
        public string? NombreClienteRaw { get; init; }
        public string? ServicioPersonalizado { get; init; }
        /// <summary>Correo del cliente registrado (para precargar el comprobante). Null si no tiene.</summary>
        public string? CorreoCliente { get; init; }

        public bool EsCita => string.Equals(Tipo, "CITA", System.StringComparison.OrdinalIgnoreCase);
        public string FechaHoraInput => FechaHora.ToString("yyyy-MM-ddTHH:mm");

        public string Hora => FechaHora.ToString("hh:mm tt");

        /// <summary>True si esta cita puede generar un cobro (servicio de catálogo y aún sin cobro).</summary>
        public bool EsCobrable =>
            string.Equals(Tipo, "CITA", System.StringComparison.OrdinalIgnoreCase) &&
            ServicioId.HasValue &&
            !YaCobrada;
    }

    /// <summary>Resumen de producción y comisión estimada para un periodo.</summary>
    public sealed class PortalResumenProduccion
    {
        public decimal ProduccionServicios { get; init; }
        public decimal ComisionServicios { get; init; }
        public decimal ProduccionProductos { get; init; }
        public decimal ComisionProductos { get; init; }

        /// <summary>Comisión total estimada (servicios + productos).</summary>
        public decimal TotalEstimado { get; init; }

        public decimal ProduccionTotal => ProduccionServicios + ProduccionProductos;
        public bool TieneProductos => ProduccionProductos > 0 || ComisionProductos > 0;
    }

    /// <summary>KPIs operativos del día (compartidos por Mi Portal y Mi Calendario).</summary>
    public sealed class PortalKpisDia
    {
        public int CitasHoy { get; init; }
        public int PendientesHoy { get; init; }
        public decimal PendienteCobrarHoy { get; init; }
        public decimal CobradoHoy { get; init; }
        public DateTime? ProximaFechaHora { get; init; }
        public string? ProximaCliente { get; init; }
        public string ProximaHora => ProximaFechaHora?.ToString("hh:mm tt") ?? "—";
    }

    public sealed class MiPanelViewModel
    {
        public string Nombre { get; init; } = string.Empty;
        public DateTime Hoy { get; init; }

        public IReadOnlyList<PortalCitaItem> CitasHoy { get; init; } = Array.Empty<PortalCitaItem>();
        public IReadOnlyList<PortalCitaItem> ProximasCitas { get; init; } = Array.Empty<PortalCitaItem>();

        public PortalResumenProduccion ResumenHoy { get; init; } = new();
        public PortalKpisDia KpisHoy { get; init; } = new();

        // Semana
        public decimal ProduccionSemana { get; init; }
        public decimal ComisionSemana { get; init; }
        public decimal PagadoSemana { get; init; }
        public decimal PendienteSemana { get; init; }
        public DateTime InicioSemana { get; init; }
        public DateTime FinSemana { get; init; }

        // Permisos relevantes para la vista
        public bool PuedeRegistrarCobros { get; init; }
    }

    public sealed class MisGananciasViewModel
    {
        public string Nombre { get; init; } = string.Empty;

        public PortalResumenProduccion Hoy { get; init; } = new();
        public PortalResumenProduccion Semana { get; init; } = new();
        public PortalResumenProduccion Mes { get; init; } = new();

        public DateTime InicioSemana { get; init; }
        public DateTime FinSemana { get; init; }
        public DateTime InicioMes { get; init; }
        public DateTime FinMes { get; init; }

        public decimal PagadoSemana { get; init; }
        public decimal PendienteSemana { get; init; }
        public decimal PagadoMes { get; init; }
        public decimal PendienteMes { get; init; }

        // Navegación de periodos (server-render por querystring)
        public bool EsSemanaActual { get; init; }
        public bool EsMesActual { get; init; }
        public DateTime SemanaAnterior => InicioSemana.AddDays(-7);
        public DateTime SemanaSiguiente => InicioSemana.AddDays(7);
        public DateTime MesAnterior => InicioMes.AddMonths(-1);
        public DateTime MesSiguiente => InicioMes.AddMonths(1);

        /// <summary>Producción diaria de la semana (para mini desglose).</summary>
        public IReadOnlyList<DetalleDiaVM> DetalleDiasSemana { get; init; } = Array.Empty<DetalleDiaVM>();

        // ── Series para gráficos (Fase 2) ──
        // Todas vienen ya agregadas del backend, reutilizando la fórmula canónica de liquidaciones.
        /// <summary>Comisión/pagado/pendiente por cada semana del mes seleccionado.</summary>
        public IReadOnlyList<GananciaPeriodoPunto> SemanasDelMes { get; init; } = Array.Empty<GananciaPeriodoPunto>();
        /// <summary>Comisión/pagado/pendiente de los últimos N meses (evolución).</summary>
        public IReadOnlyList<GananciaPeriodoPunto> EvolucionMensual { get; init; } = Array.Empty<GananciaPeriodoPunto>();
        /// <summary>Rango del gráfico mensual (6 ó 12).</summary>
        public int MesesEvolucion { get; init; } = 6;
    }

    /// <summary>
    /// Punto de una serie de ganancias para gráficos (semana del mes o mes).
    /// Solo presentación: agregado en backend desde la fórmula canónica de liquidaciones.
    /// Comision = Pagado + Pendiente (comisión estimada total del periodo).
    /// </summary>
    public sealed class GananciaPeriodoPunto
    {
        public string Etiqueta { get; init; } = string.Empty;
        public DateTime Desde { get; init; }
        public DateTime Hasta { get; init; }
        public decimal Produccion { get; init; }
        public decimal Comision { get; init; }
        public decimal Pagado { get; init; }
        public decimal Pendiente { get; init; }
    }

    public sealed class PortalPagoItem
    {
        public DateTime FechaPago { get; init; }
        public decimal Monto { get; init; }
        public string? MetodoPago { get; init; }
        public DateTime InicioSemana { get; init; }
        public DateTime FinSemana { get; init; }
        public string? Observacion { get; init; }
    }

    public sealed class MisPagosViewModel
    {
        public string Nombre { get; init; } = string.Empty;
        public IReadOnlyList<PortalPagoItem> Pagos { get; init; } = Array.Empty<PortalPagoItem>();
        public decimal TotalPagadoHistorico { get; init; }

        // KPIs (blueprint)
        public decimal RecibidoMes { get; init; }
        public DateTime? UltimoPago { get; init; }
        public int PagosRegistrados { get; init; }

        public int TotalRegistros { get; init; }
        public int PageSize { get; init; }
        public int Pagina { get; init; }
        public int TotalPaginas { get; init; }
        public bool HayMas => Pagina < TotalPaginas;
        public int DesdeRegistro => TotalRegistros == 0 ? 0 : ((Pagina - 1) * PageSize) + 1;
        public int HastaRegistro => System.Math.Min(Pagina * PageSize, TotalRegistros);
    }

    public sealed class MiCalendarioViewModel
    {
        public string Nombre { get; init; } = string.Empty;
        public string ColorCalendario { get; init; } = string.Empty;
        public DateTime Fecha { get; init; }
        public DateTime FechaAnterior => Fecha.AddDays(-1);
        public DateTime FechaSiguiente => Fecha.AddDays(1);
        public bool EsHoy { get; init; }
        public DateTime HoyNegocio { get; init; }

        public IReadOnlyList<PortalCitaItem> Citas { get; init; } = Array.Empty<PortalCitaItem>();

        // Permisos y datos para acciones
        public bool PuedeCrearCitas { get; init; }
        public bool PuedeEditarCitas { get; init; }
        public bool PuedeCancelarCitas { get; init; }
        public bool PuedeRegistrarCobros { get; init; }
        public IReadOnlyList<LuxuryApp.Models.Calendar.CalendarServiceOptionResponse> Servicios { get; init; }
            = Array.Empty<LuxuryApp.Models.Calendar.CalendarServiceOptionResponse>();

        // Panel derecho: solo citas restantes/próximas del día seleccionado.
        public IReadOnlyList<PortalCitaItem> CitasRestantes { get; init; } = Array.Empty<PortalCitaItem>();
        public bool DiaEsPasado { get; init; }

        // Vista mensual (estilo calendario principal)
        public PortalKpisDia KpisHoy { get; init; } = new();
        public int Year { get; init; }
        public int Month { get; init; }
        public int CitasHoyCount { get; init; }
        public IReadOnlyDictionary<int, int> ConteoPorDia { get; init; } = new Dictionary<int, int>();
        public DateTime MesActual => new DateTime(Year, Month, 1);
        public DateTime MesAnterior => MesActual.AddMonths(-1);
        public DateTime MesSiguiente => MesActual.AddMonths(1);

        // Sección inferior: control de citas y cobros (Día / Semana / Mes)
        public PortalControlCitas Control { get; init; } = new();
    }

    public sealed class PortalControlCitaItem
    {
        public int Id { get; init; }
        public DateTime FechaHora { get; init; }
        public string Cliente { get; init; } = string.Empty;
        public string Servicio { get; init; } = string.Empty;
        public int? ServicioId { get; init; }
        public decimal? PrecioServicio { get; init; }
        public decimal? MontoCobrado { get; init; }
        public bool YaCobrada { get; init; }
        public string? CorreoCliente { get; init; }

        public bool EsCobrable => ServicioId.HasValue && !YaCobrada;
        public decimal? MontoMostrado => YaCobrada ? MontoCobrado : PrecioServicio;
    }

    public sealed class PortalControlCitas
    {
        public string Rango { get; init; } = "dia"; // dia | semana | mes
        public DateTime Desde { get; init; }
        public DateTime Hasta { get; init; }

        public int Total { get; init; }
        public int Cobradas { get; init; }
        public int Pendientes { get; init; }
        public decimal MontoCobrado { get; init; }
        public decimal MontoPendienteEstimado { get; init; }

        public IReadOnlyList<PortalControlCitaItem> Items { get; init; } = Array.Empty<PortalControlCitaItem>();
    }

    public sealed class PortalClienteOption
    {
        public int Id { get; init; }
        public string Nombre { get; init; } = string.Empty;
        public string? Telefono { get; init; }
        public string? Correo { get; init; }
    }

    public sealed class PortalCobroItem
    {
        public int IdCobro { get; init; }
        public DateTime FechaCobro { get; init; }
        public string Cliente { get; init; } = string.Empty;
        public string Servicio { get; init; } = string.Empty;
        public decimal Monto { get; init; }
        public string MetodoPago { get; init; } = string.Empty;
        public bool DesdeCita { get; init; }

        public string Origen => DesdeCita ? "Desde cita" : "Manual";
        public string Estado => "Cobrado";

        // Estado del comprobante asociado (null = sin comprobante).
        public int? ComprobanteId { get; init; }
        public LuxuryApp.Models.Comprobantes.ComprobanteEstadoEnvio? ComprobanteEstado { get; init; }
        public string? ComprobanteToken { get; init; }

        public bool TieneComprobante => ComprobanteId.HasValue;
        public bool ComprobanteEnviado => ComprobanteEstado == LuxuryApp.Models.Comprobantes.ComprobanteEstadoEnvio.Sent;
        public bool ComprobantePendiente =>
            ComprobanteEstado == LuxuryApp.Models.Comprobantes.ComprobanteEstadoEnvio.Pending ||
            ComprobanteEstado == LuxuryApp.Models.Comprobantes.ComprobanteEstadoEnvio.Failed;
    }

    public sealed class MisCobrosViewModel
    {
        public string Nombre { get; init; } = string.Empty;
        public bool PuedeRegistrarCobros { get; init; }
        public bool PuedeRegistrarManual { get; init; }
        public IReadOnlyList<LuxuryApp.Models.Calendar.CalendarServiceOptionResponse> Servicios { get; init; }
            = Array.Empty<LuxuryApp.Models.Calendar.CalendarServiceOptionResponse>();

        // KPIs (siempre, independientes del filtro)
        public decimal TotalHoy { get; init; }
        public decimal TotalSemana { get; init; }
        public decimal TotalMes { get; init; }
        public int CobrosRegistrados { get; init; }

        // Filtros
        public string Rango { get; init; } = "mes"; // dia | semana | mes | todos
        public string Metodo { get; init; } = "";    // "" | EFECTIVO | TARJETA | SINPE
        public string Origen { get; init; } = "";     // "" | cita | manual
        public DateTime? RangoDesde { get; init; }
        public DateTime? RangoHasta { get; init; }

        // Desglose por método (sobre el rango seleccionado, sin filtrar por método)
        public decimal MetodoEfectivo { get; init; }
        public decimal MetodoTarjeta { get; init; }
        public decimal MetodoSinpe { get; init; }

        public IReadOnlyList<PortalCobroItem> Cobros { get; init; } = Array.Empty<PortalCobroItem>();

        public int TotalRegistros { get; init; }
        public int PageSize { get; init; }
        public int Pagina { get; init; }
        public int TotalPaginas { get; init; }
        public bool HayMas => Pagina < TotalPaginas;
        public int DesdeRegistro => TotalRegistros == 0 ? 0 : ((Pagina - 1) * PageSize) + 1;
        public int HastaRegistro => System.Math.Min(Pagina * PageSize, TotalRegistros);
    }
}
