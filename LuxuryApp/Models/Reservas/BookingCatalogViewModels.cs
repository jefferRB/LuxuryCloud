namespace LuxuryApp.Models.Reservas
{
    /// <summary>VM del panel privado "Servicios publicados" (Fase 1 + Fase 2).</summary>
    public sealed class BookingCatalogViewModel
    {
        public IReadOnlyList<BookingCatalogServiceItem> Servicios { get; set; } =
            Array.Empty<BookingCatalogServiceItem>();

        /// <summary>Todos los funcionarios activos (para asignar por servicio).</summary>
        public IReadOnlyList<BookingCatalogFuncionarioOption> Funcionarios { get; set; } =
            Array.Empty<BookingCatalogFuncionarioOption>();

        /// <summary>
        /// True si el tenant aún no ha configurado ningún servicio: por compatibilidad se muestran
        /// todos en el link público hasta que guarde la lista.
        /// </summary>
        public bool UsandoCompatibilidad { get; set; }
    }

    public sealed class BookingCatalogServiceItem
    {
        public int ServicioId { get; set; }
        public string NombreServicio { get; set; } = string.Empty;
        public int DuracionMinutos { get; set; }
        public decimal Precio { get; set; }

        public bool IsVisibleOnline { get; set; }

        /// <summary>
        /// Descripción corta opcional que ve el cliente. Sigue siendo editable (la página pública
        /// la muestra), pero en segundo plano: el nombre y el precio salen del servicio real.
        /// </summary>
        public string? PublicDescription { get; set; }

        public bool ShowPrice { get; set; }

        /// <summary>Ids de funcionarios habilitados explícitamente para este servicio.</summary>
        public List<int> FuncionarioIds { get; set; } = new();

        /// <summary>True si no hay asignación explícita: lo atienden todos los funcionarios activos.</summary>
        public bool AtiendenTodos { get; set; }
    }

    public sealed class BookingCatalogFuncionarioOption
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = string.Empty;
        public string? Puesto { get; set; }
        public string? FotoUrl { get; set; }
        public string ColorCalendario { get; set; } = "#6366f1";
    }

    /// <summary>Payload de guardado del panel "Servicios publicados".</summary>
    public sealed class BookingCatalogSaveInput
    {
        public List<BookingCatalogServiceSaveItem> Servicios { get; set; } = new();
    }

    /// <summary>
    /// Lo que el panel puede cambiar de un servicio publicado. Deliberadamente NO viajan el nombre
    /// público, la categoría ni el orden: el nombre es el del servicio real (una sola fuente), el
    /// orden es alfabético y la categoría dejó de ser editable. Sus columnas siguen en la base con
    /// los valores históricos; simplemente este guardado no las toca.
    /// </summary>
    public sealed class BookingCatalogServiceSaveItem
    {
        public int ServicioId { get; set; }
        public bool IsVisibleOnline { get; set; }
        public string? PublicDescription { get; set; }
        public bool ShowPrice { get; set; }
        public List<int> FuncionarioIds { get; set; } = new();
    }
}
