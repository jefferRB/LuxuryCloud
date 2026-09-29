namespace LuxuryApp.Services.PublicSite
{
    /// <summary>
    /// Proyección liviana e inmutable de un plan público. Se usa como unidad cacheable:
    /// NO contiene entidades EF rastreadas ni el grafo de navegación, por lo que es seguro
    /// mantenerla en memoria durante el TTL sin arrastrar el <c>DbContext</c>.
    /// </summary>
    internal sealed record PublicPlanSnapshot
    {
        public Guid Id { get; init; }
        public string? Codigo { get; init; }
        public string Nombre { get; init; } = string.Empty;
        public decimal PrecioMensual { get; init; }
        public string? Moneda { get; init; }
        public int? MaxFuncionarios { get; init; }
        public int? LimiteMensajesMensual { get; init; }
        public bool EsPlanValidacion { get; init; }
        public IReadOnlyList<PublicPlanFeatureSnapshot> Features { get; init; } =
            Array.Empty<PublicPlanFeatureSnapshot>();
    }

    /// <summary>
    /// Proyección de una característica del plan (nombre + límite opcional) lista para
    /// convertirse en un "highlight" comercial sin exponer la entidad EF original.
    /// </summary>
    internal sealed record PublicPlanFeatureSnapshot(string? Nombre, int? Limite);
}
