using LuxuryApp.Models.DataBase;

namespace LuxuryApp.Services.Clientes
{
    /// <summary>
    /// Fuente ÚNICA de las métricas CRM de un cliente (visitas atendidas, última visita,
    /// frecuencia efectiva, días sin visitar). Cualquier módulo que necesite estos números
    /// —perfil, listado, campañas de recuperación, recordatorios de WhatsApp— debe consumir este
    /// servicio en lugar de reimplementar el cálculo o leer las columnas legacy del cliente.
    /// </summary>
    public interface IClienteVisitMetricsService
    {
        /// <summary>
        /// Métricas del cliente dentro del tenant actual. Un cliente de otro tenant devuelve
        /// métricas vacías: las consultas están filtradas por tenant, nunca cruzan datos.
        /// </summary>
        Task<ClienteVisitMetrics> GetForClienteAsync(int clienteId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Métricas de varios clientes en DOS consultas, sin importar cuántos sean: la versión
        /// para listados paginados. Devuelve una entrada por cada id pedido que exista en el
        /// tenant actual.
        /// </summary>
        Task<IReadOnlyDictionary<int, ClienteVisitMetrics>> GetForClientesAsync(
            IReadOnlyCollection<int> clienteIds,
            CancellationToken cancellationToken = default);
    }
}
