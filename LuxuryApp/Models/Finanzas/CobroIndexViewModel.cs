using Microsoft.AspNetCore.Mvc.Rendering;

namespace LuxuryApp.Models.Finanzas
{
    public class CobroIndexViewModel
    {
        public List<CobroIndexItemViewModel> Cobros { get; set; } = new();

        public CobroFiltroViewModel Filtros { get; set; } = new();

        public List<SelectListItem> Funcionarios { get; set; } = new();
        public List<SelectListItem> MetodosPago { get; set; } = new();
        public decimal TotalCobrado { get; set; }
        public int CantidadServicios { get; set; }

        public decimal TotalServicios { get; set; }

        public decimal TotalProductos { get; set; }

        public decimal TotalGenerado { get; set; }

        public decimal TotalSinImpuestos { get; set; }

        public decimal TotalImpuestos { get; set; }

        public decimal PagoColaboradores { get; set; }

        public decimal GananciaNegocio { get; set; }

        public decimal GananciaEfectivo { get; set; }

        public decimal GananciaTarjeta { get; set; }

        public decimal GananciaSinpe { get; set; }

        // ─────────────── Paginación (la tabla muestra una página; los KPIs son de todo el filtro) ───────────────
        public int Page { get; set; } = 1;

        public int PageSize { get; set; } = 20;

        /// <summary>Total de registros que cumplen el filtro (independiente de la página).</summary>
        public int TotalRegistros { get; set; }

        public int TotalPaginas { get; set; }

        /// <summary>Índice (1-based) del primer registro mostrado en la página actual.</summary>
        public int Desde => TotalRegistros == 0 ? 0 : ((Page - 1) * PageSize) + 1;

        /// <summary>Índice (1-based) del último registro mostrado en la página actual.</summary>
        public int Hasta => Math.Min(Page * PageSize, TotalRegistros);

        public static readonly int[] PageSizeOptions = { 20, 50, 100 };
    }

}
