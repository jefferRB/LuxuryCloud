using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Notifications
{
    /// <summary>
    /// Notificación interna del negocio para el Centro de Notificaciones (burbuja flotante).
    /// Es estrictamente tenant-scoped: nunca se muestran notificaciones de otro <see cref="TenantId"/>.
    /// No reemplaza a los módulos Reservas/Calendario; solo actúa como acceso rápido a eventos.
    /// </summary>
    public sealed class TenantNotification : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        public int Id { get; set; }

        /// <summary>Tipo de evento. Ver <see cref="NotificationTypes"/>.</summary>
        [Required]
        [MaxLength(60)]
        public string Type { get; set; } = string.Empty;

        [Required]
        [MaxLength(150)]
        public string Title { get; set; } = string.Empty;

        [Required]
        [MaxLength(400)]
        public string Message { get; set; } = string.Empty;

        /// <summary>Ruta interna a la que lleva la acción primaria (p. ej. /Reservas o /Calendar).</summary>
        [MaxLength(300)]
        public string? ActionUrl { get; set; }

        /// <summary>Tipo de entidad de origen. Ver <see cref="NotificationEntityTypes"/>.</summary>
        [MaxLength(60)]
        public string? EntityType { get; set; }

        /// <summary>Id de la entidad de origen (BookingRequestId, CitaId, ...). Parte de la llave anti-duplicados.</summary>
        public int? EntityId { get; set; }

        /// <summary>Metadata opcional serializada en JSON (teléfono, funcionario, estados, etc.).</summary>
        public string? MetadataJson { get; set; }

        public bool IsRead { get; set; }

        public DateTime? ReadAtUtc { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Origen que la generó (auditoría ligera). Ver <see cref="NotificationSources"/>.</summary>
        [MaxLength(40)]
        public string Source { get; set; } = NotificationSources.System;
    }
}
