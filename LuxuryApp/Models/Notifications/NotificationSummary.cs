namespace LuxuryApp.Models.Notifications
{
    /// <summary>Respuesta del endpoint de resumen consumido por la burbuja flotante.</summary>
    public sealed class NotificationSummary
    {
        public int UnreadCount { get; set; }

        public IReadOnlyList<NotificationItem> Notifications { get; set; } = new List<NotificationItem>();
    }

    /// <summary>Notificación lista para mostrar. El texto ya viene saneado por el dominio.</summary>
    public sealed class NotificationItem
    {
        public int Id { get; set; }

        public string Type { get; set; } = string.Empty;

        /// <summary>Clave visual para escoger ícono/acento en el frontend (no es HTML).</summary>
        public string Icon { get; set; } = "bell";

        public string Title { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        /// <summary>Etiqueta de tiempo relativo ya formateada en español ("Hace 3 min", "Hoy 12:30").</summary>
        public string CreatedAtLabel { get; set; } = string.Empty;

        public string? ActionUrl { get; set; }

        public bool IsRead { get; set; }
    }
}
