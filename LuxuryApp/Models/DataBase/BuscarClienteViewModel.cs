namespace LuxuryApp.Models.DataBase
{
    public sealed class BuscarClienteViewModel
    {
        public string? Criterio { get; set; }
        public string? Mensaje { get; set; }
        public bool EsBusquedaTelefonica { get; set; }
        public bool ResultadosLimitados { get; set; }
        public ClienteSummaryViewModel? ClienteSeleccionado { get; set; }
        public IReadOnlyList<ClienteSummaryViewModel> ClientesEncontrados { get; set; } = Array.Empty<ClienteSummaryViewModel>();
        public IReadOnlyList<CitaVisitaItemViewModel> HistorialVisitas { get; set; } = Array.Empty<CitaVisitaItemViewModel>();

        /// <summary>
        /// Métricas CRM derivadas del historial real. Reemplazan a los campos persistidos
        /// <c>FrecuenciaVisita</c>/<c>FechaUltimaVisita</c> en las tarjetas del perfil.
        /// </summary>
        public ClienteVisitMetrics Metricas { get; set; } =
            ClienteVisitMetrics.SinHistorial(ClienteDefaults.InitialVisitFrequencyDays);

        public int TotalCitasHistorial { get; set; }

        /// <summary>
        /// Nota heredada: el texto libre que guardan "Registrar servicios" y el flujo de cobro
        /// en <c>Cliente.DescripcionServiciosRealizados</c>. Se conserva visible para no perder
        /// lo ya escrito; las notas nuevas van a <see cref="Notas"/>.
        /// </summary>
        public string? NotasServicio { get; set; }

        /// <summary>Notas de servicio, de la más reciente a la más antigua.</summary>
        public IReadOnlyList<ClienteServicioRealizadoItemViewModel> Notas { get; set; } =
            Array.Empty<ClienteServicioRealizadoItemViewModel>();

        public IReadOnlyList<CobroClienteHistorialItemViewModel> HistorialPagos { get; set; } = Array.Empty<CobroClienteHistorialItemViewModel>();
    }
}
