using LuxuryApp.Models.DataBase;
using LuxuryApp.Services.BusinessTime;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Clientes
{
    /// <summary>
    /// Obtiene las citas del historial del cliente y delega el cálculo en
    /// <see cref="ClienteVisitMetricsCalculator"/>. Esta clase solo hace I/O; la regla de
    /// negocio no se duplica aquí.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FUENTE ÚNICA: las CITAS del cliente — exactamente el mismo criterio que alimenta la tabla
    /// "Historial de citas" del perfil (<c>Citas.Where(c =&gt; c.ClienteId == clienteId)</c>).
    /// Si el historial muestra dos citas, las métricas hablan de esas dos citas.
    /// </para>
    /// <para>
    /// NO intervienen los cobros, ni <c>ClienteVisitas</c>, ni <c>ClienteServicioRealizado</c>:
    /// cobrar el día 16 no convierte el 16 en una visita, y una nota no es una atención. De
    /// <c>Cliente</c> solo se lee <c>FrecuenciaVisita</c>, y únicamente como valor INICIAL
    /// mientras no haya historial suficiente.
    /// </para>
    /// <para>
    /// <c>FechaHoraCita</c> ya se guarda en HORA LOCAL del negocio, así que truncarla a día
    /// equivale a agrupar por día local. Convertirla desde UTC aquí introduciría el error.
    /// </para>
    /// </remarks>
    public sealed class ClienteVisitMetricsService : IClienteVisitMetricsService
    {
        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;

        public ClienteVisitMetricsService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
        }

        public async Task<ClienteVisitMetrics> GetForClienteAsync(
            int clienteId,
            CancellationToken cancellationToken = default)
        {
            if (clienteId <= 0)
            {
                return ClienteVisitMetrics.SinHistorial(ClienteDefaults.InitialVisitFrequencyDays);
            }

            var metricas = await GetForClientesAsync(new[] { clienteId }, cancellationToken);

            return metricas.TryGetValue(clienteId, out var resultado)
                ? resultado
                : ClienteVisitMetrics.SinHistorial(ClienteDefaults.InitialVisitFrequencyDays);
        }

        public async Task<IReadOnlyDictionary<int, ClienteVisitMetrics>> GetForClientesAsync(
            IReadOnlyCollection<int> clienteIds,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(clienteIds);

            var ids = clienteIds.Where(id => id > 0).Distinct().ToArray();

            if (ids.Length == 0)
            {
                return new Dictionary<int, ClienteVisitMetrics>();
            }

            // DOS consultas para toda la página, no una por cliente: las frecuencias iniciales
            // y las fechas de las citas. El filtro global por tenant se aplica a ambas, así que
            // un id de otro negocio simplemente no devuelve filas.
            var frecuenciasIniciales = await _context.Clientes
                .AsNoTracking()
                .Where(cliente => ids.Contains(cliente.Id))
                .Select(cliente => new { cliente.Id, cliente.FrecuenciaVisita })
                .ToDictionaryAsync(x => x.Id, x => x.FrecuenciaVisita, cancellationToken);

            // Solo ClienteId + fecha: ni servicios, ni funcionarios, ni la cita completa.
            var citas = await _context.Citas
                .AsNoTracking()
                .Where(cita => cita.ClienteId != null && ids.Contains(cita.ClienteId.Value))
                .Select(cita => new { ClienteId = cita.ClienteId!.Value, cita.FechaHoraCita })
                .ToListAsync(cancellationToken);

            var fechasPorCliente = citas
                .GroupBy(cita => cita.ClienteId)
                .ToDictionary(grupo => grupo.Key, grupo => grupo.Select(cita => cita.FechaHoraCita).ToArray());

            var hoy = _businessDateTimeProvider.Today();
            var resultado = new Dictionary<int, ClienteVisitMetrics>(frecuenciasIniciales.Count);

            foreach (var (clienteId, frecuenciaInicial) in frecuenciasIniciales)
            {
                var fechas = fechasPorCliente.TryGetValue(clienteId, out var propias)
                    ? propias
                    : Array.Empty<DateTime>();

                resultado[clienteId] = ClienteVisitMetricsCalculator.Calculate(
                    fechas,
                    hoy,
                    frecuenciaInicial);
            }

            return resultado;
        }
    }
}
