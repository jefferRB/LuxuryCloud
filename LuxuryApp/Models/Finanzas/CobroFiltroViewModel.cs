namespace LuxuryApp.Models.Finanzas
{
    public class CobroFiltroViewModel
    {
        public string VistaTiempo { get; set; } = "dia";
        // dia / semana / mes / año

        public int? FuncionarioId { get; set; }

        public string MetodoPago { get; set; } = string.Empty;

        public DateTime? FechaInicio { get; set; }

        public DateTime? FechaFin { get; set; }

        public bool MostrarServicios { get; set; } = true;
        public bool MostrarProductos { get; set; } = true;

        // ─────────────── Paginación (tabla) ───────────────
        public int Page { get; set; } = 1;

        public int PageSize { get; set; } = 20;
    }
}
