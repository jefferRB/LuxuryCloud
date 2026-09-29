using Microsoft.AspNetCore.Mvc.Rendering;

namespace LuxuryApp.Models.Finanzas
{
    public class EgresoIndexViewModel
    {
        public List<EgresoIndexItemViewModel> Egresos { get; set; } = new();

        public EgresoFiltroViewModel Filtros { get; set; } = new();

        public List<SelectListItem> MetodosPago { get; set; } = new();

        public List<SelectListItem> Categorias { get; set; } = new();


        public decimal TotalEgresos { get; set; }

        public int CantidadRegistros { get; set; }
    }
}
