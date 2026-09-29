using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Finanzas
{
    /// <summary>
    /// Implementación de <see cref="ILegacyFinancialImpactService"/>. Son conteos simples sobre
    /// Cobros, siempre dentro del filtro de tenant del contexto.
    /// </summary>
    public sealed class LegacyFinancialImpactService : ILegacyFinancialImpactService
    {
        private readonly ApplicationDbContext _context;

        public LegacyFinancialImpactService(ApplicationDbContext context) => _context = context;

        public Task<int> ContarCobrosLegacyDeServicioAsync(int servicioId, CancellationToken cancellationToken = default) =>
            _context.Cobros
                .AsNoTracking()
                .CountAsync(c => c.ServicioId == servicioId && c.AplicaIvaSnapshot == null, cancellationToken);

        public Task<int> ContarCobrosLegacyDeProductoAsync(int productoId, CancellationToken cancellationToken = default) =>
            _context.Cobros
                .AsNoTracking()
                .CountAsync(c => c.ProductoId == productoId && c.AplicaIvaSnapshot == null, cancellationToken);

        public Task<int> ContarCobrosLegacyDelNegocioAsync(CancellationToken cancellationToken = default) =>
            _context.Cobros
                .AsNoTracking()
                .CountAsync(c => c.AplicaIvaSnapshot == null, cancellationToken);

        public Task<int> ContarProduccionHistoricaDeColaboradorAsync(int funcionarioId, CancellationToken cancellationToken = default) =>
            _context.Cobros
                .AsNoTracking()
                .CountAsync(
                    c => c.FuncionarioId == funcionarioId && c.PorcentajeServicioSnapshot == null,
                    cancellationToken);
    }
}
