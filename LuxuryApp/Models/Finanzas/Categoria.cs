using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;

namespace LuxuryApp.Models.Finanzas
{
    public class Categoria : ITenantEntity
    {
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }
        public int Id { get; set; }

        [Required]
        [Display(Name = "Nombre categoria")]
        [StringLength(150)]
        public string? Nombre { get; set; }

        [Required]
        [Display(Name = "Detalle")]
        [StringLength(500)]
        public string? Detalle { get; set; }

        public bool Activo { get; set; } = true;

        /// <summary>
        /// Identidad ESTRUCTURAL de la categoría (ver <see cref="SystemCategoryCodes"/>).
        /// <c>null</c> = categoría del usuario, se comporta como gasto operativo normal.
        ///
        /// <para>
        /// La lógica financiera consulta este campo, nunca <see cref="Nombre"/>: renombrar la
        /// etiqueta visible no puede cambiar una fórmula. Único por tenant (índice filtrado).
        /// </para>
        /// </summary>
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        [StringLength(SystemCategoryCodes.MaxLength)]
        public string? SystemCode { get; set; }
    }
}
