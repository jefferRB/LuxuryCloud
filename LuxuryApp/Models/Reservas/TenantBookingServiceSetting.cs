using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using LuxuryApp.Models.Finanzas;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Reservas
{
    /// <summary>
    /// Configuración de publicación de un <see cref="Servicio"/> en el link público de reservas.
    /// Entidad separada del servicio interno para no ensuciar el catálogo del negocio.
    /// Índice único por TenantId + ServicioId. Si un tenant no tiene NINGÚN registro, por
    /// compatibilidad el público sigue mostrando todos los servicios activos (fallback), hasta
    /// que configure la lista desde el panel.
    /// </summary>
    public sealed class TenantBookingServiceSetting : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        public int Id { get; set; }

        public int ServicioId { get; set; }
        public Servicio? Servicio { get; set; }

        /// <summary>Si el servicio se publica en el link público de reservas.</summary>
        public bool IsVisibleOnline { get; set; } = true;

        /// <summary>Nombre mostrado al cliente. Si está vacío, se usa el nombre real del servicio.</summary>
        [MaxLength(120)]
        public string? PublicName { get; set; }

        /// <summary>Descripción corta pública. Si está vacía, no se muestra descripción.</summary>
        [MaxLength(300)]
        public string? PublicDescription { get; set; }

        /// <summary>Orden de aparición (ascendente). Empate → por nombre.</summary>
        public int DisplayOrder { get; set; }

        /// <summary>Si se muestra el precio del servicio en el link público.</summary>
        public bool ShowPrice { get; set; }

        /// <summary>Categoría pública opcional para agrupar/filtrar servicios.</summary>
        [MaxLength(80)]
        public string? Category { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
