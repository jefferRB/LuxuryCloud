using LuxuryApp.Models.DataBase;
using LuxuryApp.Services.BusinessTime;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services
{
    public class VisitasAutomaticasService
    {
        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;

        public VisitasAutomaticasService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
        }

        public async Task ProcesarCitasFinalizadas(CancellationToken cancellationToken = default)
        {
            var ahora = _businessDateTimeProvider.Now();

            var citas = await _context.Citas
                .Include(c => c.Servicio)
                .Where(c => !c.VisitaProcesada && c.ServicioId != null && c.ClienteId != null)
                .ToListAsync(cancellationToken);

            foreach (var cita in citas)
            {
                if (cita.Servicio == null)
                    continue;
                var duracion = cita.Servicio?.DuracionMinutos ?? 30;
                var fin = cita.FechaHoraCita.AddMinutes(duracion);

                if (fin <= ahora)
                {
                    var cliente = await _context.Clientes
                        .FirstOrDefaultAsync(c => c.Id == cita.ClienteId, cancellationToken);

                    if (cliente == null)
                        continue;

                    // Evitar duplicados
                    bool existe = await _context.ClienteVisitas
                        .AnyAsync(v =>
                            v.ClienteId == cliente.Id &&
                            v.FechaVisita == fin,
                            cancellationToken);

                    if (!existe)
                    {
                        if (fin > cliente.FechaUltimaVisita)
                        {
                            cliente.FechaUltimaVisita = fin;
                        }

                        _context.ClienteVisitas.Add(new ClienteVisitas
                        {
                            ClienteId = cliente.Id,
                            NumeroTelefono = cliente.NumeroTelefono,
                            FechaVisita = fin
                        });
                    }

                    cita.VisitaProcesada = true;
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
