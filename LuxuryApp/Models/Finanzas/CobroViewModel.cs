using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace LuxuryApp.Models.Finanzas
{
    public class CobroViewModel
    {
        public Cobro Cobro { get; set; } = new();
        [ValidateNever]
        public List<SelectListItem> Funcionarios { get; set; } = new();
        [ValidateNever]
        public List<SelectListItem> Servicios { get; set; } = new();
        [ValidateNever]
        public List<SelectListItem> MetodosPago { get; set; } = new();
        [ValidateNever]
        public List<SelectListItem> Productos { get; set; } = new();
        [ValidateNever]
        public List<DetalleCobroProducto> ProductosVendidos { get; set; } = new();
    }
}
