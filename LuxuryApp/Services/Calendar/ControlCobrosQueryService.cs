using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.BusinessTime;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Calendar
{
    /// <summary>
    /// Consulta de solo lectura para la vista admin "Control de citas y cobros".
    /// El estado de pago es global: una cita está "Cobrada" si tiene un cobro ligado
    /// (Cobro.CitaId), sin importar qué usuario consulta. Tenant-safe por global filter.
    /// </summary>
    public sealed class ControlCobrosQueryService : IControlCobrosQueryService
    {
        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;

        public ControlCobrosQueryService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
        }

        public async Task<ControlCitasCobrosViewModel> ObtenerAsync(
            ControlCitasCobrosFiltroViewModel filtro,
            bool hasWhatsAppAddon,
            CancellationToken cancellationToken = default)
        {
            filtro = Sanitizar(filtro, hasWhatsAppAddon);

            var (desde, hastaExcl) = ResolverRango(filtro.Rango, filtro.Fecha);

            // Citas (solo CITA, no descansos) del rango. Filtro por funcionario y búsqueda
            // se aplican en SQL. Tenant-safe por el global query filter de ITenantEntity.
            var query = _context.Citas
                .AsNoTracking()
                .Where(c => c.Tipo == "CITA" &&
                            c.FechaHoraCita >= desde &&
                            c.FechaHoraCita < hastaExcl);

            if (filtro.FuncionarioId.HasValue && filtro.FuncionarioId.Value > 0)
            {
                query = query.Where(c => c.FuncionarioId == filtro.FuncionarioId.Value);
            }

            if (!string.IsNullOrWhiteSpace(filtro.Buscar))
            {
                var termino = filtro.Buscar.Trim();
                query = query.Where(c =>
                    (c.Cliente != null && EF.Functions.Like(c.Cliente.Nombre, $"%{termino}%")) ||
                    (c.NombreCliente != null && EF.Functions.Like(c.NombreCliente, $"%{termino}%")) ||
                    (c.Cliente != null && c.Cliente.NumeroTelefono != null && c.Cliente.NumeroTelefono.Contains(termino)) ||
                    (c.TelefonoCliente != null && c.TelefonoCliente.Contains(termino)));
            }

            var citas = await query
                .OrderBy(c => c.FechaHoraCita)
                .Select(c => new CitaProjection
                {
                    Id = c.Id,
                    FechaHora = c.FechaHoraCita,
                    Cliente = c.Cliente != null ? c.Cliente.Nombre : (c.NombreCliente ?? "Cliente"),
                    Telefono = c.Cliente != null ? c.Cliente.NumeroTelefono : c.TelefonoCliente,
                    CorreoCliente = c.Cliente != null ? c.Cliente.CorreoElectronico : null,
                    ClienteId = c.ClienteId,
                    FuncionarioId = c.FuncionarioId,
                    Funcionario = c.Funcionario != null ? c.Funcionario.Nombre : string.Empty,
                    Servicio = c.Servicio != null ? c.Servicio.Nombre : (c.ServicioNombrePersonalizado ?? "Servicio"),
                    ServicioId = c.ServicioId,
                    PrecioServicio = c.Servicio != null ? (decimal?)c.Servicio.Precio : null,
                    EstadoConfirmacionWhatsApp = c.EstadoConfirmacionWhatsApp
                })
                .ToListAsync(cancellationToken);

            // Cobros ligados a estas citas (estado de pago global). Una sola consulta.
            var ids = citas.Select(c => c.Id).ToList();
            var cobrosPorCita = ids.Count == 0
                ? new Dictionary<int, CobroResumen>()
                : await _context.Cobros
                    .AsNoTracking()
                    .Where(co => co.CitaId != null && ids.Contains(co.CitaId.Value))
                    .GroupBy(co => co.CitaId!.Value)
                    .Select(g => new
                    {
                        CitaId = g.Key,
                        Monto = g.Sum(x => x.Monto),
                        // En la práctica hay un único cobro por cita (índice único filtrado).
                        IdCobro = g.Max(x => x.IdCobro),
                        Metodo = g.Max(x => x.MetodoPago)
                    })
                    .ToDictionaryAsync(
                        x => x.CitaId,
                        x => new CobroResumen { Monto = x.Monto, IdCobro = x.IdCobro, Metodo = x.Metodo },
                        cancellationToken);

            var items = citas.Select(c =>
            {
                var cobrada = cobrosPorCita.TryGetValue(c.Id, out var cobro);
                var esCancelada = !cobrada
                    && hasWhatsAppAddon
                    && string.Equals(c.EstadoConfirmacionWhatsApp, WhatsAppConfirmationStates.Cancelada, StringComparison.Ordinal);

                return new CitaCobroItemViewModel
                {
                    CitaId = c.Id,
                    FechaHora = c.FechaHora,
                    Cliente = c.Cliente,
                    Telefono = c.Telefono,
                    CorreoCliente = c.CorreoCliente,
                    ClienteId = c.ClienteId,
                    FuncionarioId = c.FuncionarioId,
                    Funcionario = c.Funcionario,
                    Servicio = c.Servicio,
                    ServicioId = c.ServicioId,
                    PrecioServicio = c.PrecioServicio,
                    YaCobrada = cobrada,
                    MontoCobrado = cobrada ? cobro!.Monto : (decimal?)null,
                    MetodoPago = cobrada ? cobro!.Metodo : null,
                    CobroId = cobrada ? cobro!.IdCobro : (int?)null,
                    EsCancelada = esCancelada,
                    EstadoConfirmacionWhatsApp = c.EstadoConfirmacionWhatsApp
                };
            }).ToList();

            // Estado del comprobante por cobro (una sola consulta para todas las citas cobradas).
            var cobroIds = items.Where(i => i.CobroId.HasValue).Select(i => i.CobroId!.Value).ToList();
            if (cobroIds.Count > 0)
            {
                var comprobantesRaw = await _context.ComprobantesCobro
                    .AsNoTracking()
                    .Where(cc => cobroIds.Contains(cc.CobroId) &&
                                 cc.EstadoEnvio != LuxuryApp.Models.Comprobantes.ComprobanteEstadoEnvio.Cancelled)
                    .Select(cc => new { cc.Id, cc.CobroId, cc.EstadoEnvio, cc.TokenPublico })
                    .ToListAsync(cancellationToken);

                // Un comprobante "vivo" por cobro (índice único); agrupamos en memoria por seguridad.
                var comprobantes = comprobantesRaw
                    .GroupBy(c => c.CobroId)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Id).First());

                foreach (var item in items)
                {
                    if (item.CobroId.HasValue && comprobantes.TryGetValue(item.CobroId.Value, out var comp))
                    {
                        item.ComprobanteId = comp.Id;
                        item.ComprobanteEstado = comp.EstadoEnvio;
                        item.ComprobanteToken = comp.TokenPublico;
                    }
                }
            }

            // KPIs sobre el alcance (rango + funcionario + búsqueda), independientes del
            // filtro de estado de pago, para que los totales no "desaparezcan" al filtrar.
            var kpis = new ControlCitasCobrosKpiViewModel
            {
                TotalCitas = items.Count,
                Cobradas = items.Count(i => i.YaCobrada),
                Canceladas = items.Count(i => i.EsCancelada),
                MontoCobrado = items.Where(i => i.YaCobrada).Sum(i => i.MontoCobrado ?? 0m),
                PendienteEstimado = items.Where(i => !i.YaCobrada && !i.EsCancelada).Sum(i => i.PrecioServicio ?? 0m)
            };
            kpis.Pendientes = kpis.TotalCitas - kpis.Cobradas - kpis.Canceladas;

            // El filtro de estado solo angosta la tabla.
            IEnumerable<CitaCobroItemViewModel> visibles = items;
            visibles = filtro.EstadoPago switch
            {
                "cobradas" => visibles.Where(i => i.YaCobrada),
                "pendientes" => visibles.Where(i => !i.YaCobrada && !i.EsCancelada),
                "canceladas" => visibles.Where(i => i.EsCancelada),
                _ => visibles
            };

            var funcionarios = await _context.Funcionarios
                .AsNoTracking()
                .Where(f => f.Activo)
                .OrderBy(f => f.Nombre)
                .Select(f => new ControlFuncionarioOption { Id = f.IdFuncionario, Nombre = f.Nombre })
                .ToListAsync(cancellationToken);

            return new ControlCitasCobrosViewModel
            {
                Filtro = filtro,
                Kpis = kpis,
                Items = visibles.ToList(),
                Funcionarios = funcionarios,
                HasWhatsAppAddon = hasWhatsAppAddon,
                Desde = desde,
                Hasta = hastaExcl.AddDays(-1)
            };
        }

        public async Task<ControlCitaCobroContexto?> ObtenerCitaParaCobroAsync(
            int citaId,
            CancellationToken cancellationToken = default)
        {
            // Tenant-safe por global query filter. No se filtra por funcionario:
            // el admin puede cobrar cualquier cita de su negocio.
            var cita = await _context.Citas
                .AsNoTracking()
                .Where(c => c.Id == citaId)
                .Select(c => new
                {
                    c.Id,
                    c.Tipo,
                    c.FuncionarioId,
                    c.ServicioId,
                    c.ServicioNombrePersonalizado,
                    c.ClienteId,
                    Nombre = c.Cliente != null ? c.Cliente.Nombre : (c.NombreCliente ?? "Cliente"),
                    PrecioServicio = c.Servicio != null ? (decimal?)c.Servicio.Precio : null
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (cita is null || !string.Equals(cita.Tipo, "CITA", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var yaCobrada = await _context.Cobros
                .AsNoTracking()
                .AnyAsync(co => co.CitaId == citaId, cancellationToken);

            return new ControlCitaCobroContexto
            {
                CitaId = cita.Id,
                FuncionarioId = cita.FuncionarioId,
                ServicioId = cita.ServicioId,
                // Para una cita sin catálogo se usa su nombre personalizado; si por algún motivo
                // viniera vacío, un texto neutro evita que el cobro quede sin descripción.
                ServicioNombrePersonalizado = cita.ServicioId.HasValue
                    ? null
                    : (string.IsNullOrWhiteSpace(cita.ServicioNombrePersonalizado)
                        ? "Servicio personalizado"
                        : cita.ServicioNombrePersonalizado),
                ClienteId = cita.ClienteId,
                NombreCliente = string.IsNullOrWhiteSpace(cita.Nombre) ? "Cliente" : cita.Nombre,
                PrecioServicio = cita.PrecioServicio,
                YaCobrada = yaCobrada
            };
        }

        private ControlCitasCobrosFiltroViewModel Sanitizar(
            ControlCitasCobrosFiltroViewModel filtro,
            bool hasWhatsAppAddon)
        {
            var rango = filtro.Rango?.ToLowerInvariant() switch
            {
                "semana" => "semana",
                "mes" => "mes",
                _ => "dia"
            };

            var estado = filtro.EstadoPago?.ToLowerInvariant() switch
            {
                "cobradas" => "cobradas",
                "pendientes" => "pendientes",
                // "canceladas" solo tiene sentido con add-on de WhatsApp.
                "canceladas" => hasWhatsAppAddon ? "canceladas" : "todos",
                _ => "todos"
            };

            var fecha = filtro.Fecha == default
                ? _businessDateTimeProvider.Today()
                : filtro.Fecha.Date;

            return new ControlCitasCobrosFiltroViewModel
            {
                Rango = rango,
                Fecha = fecha,
                FuncionarioId = filtro.FuncionarioId.HasValue && filtro.FuncionarioId.Value > 0
                    ? filtro.FuncionarioId
                    : null,
                EstadoPago = estado,
                Buscar = string.IsNullOrWhiteSpace(filtro.Buscar) ? null : filtro.Buscar.Trim()
            };
        }

        private static (DateTime Desde, DateTime HastaExcl) ResolverRango(string rango, DateTime fecha)
        {
            var dia = fecha.Date;
            switch (rango)
            {
                case "semana":
                    var diff = ((int)dia.DayOfWeek + 6) % 7; // lunes primero
                    var inicioSemana = dia.AddDays(-diff);
                    return (inicioSemana, inicioSemana.AddDays(7));
                case "mes":
                    var inicioMes = new DateTime(dia.Year, dia.Month, 1);
                    return (inicioMes, inicioMes.AddMonths(1));
                default:
                    return (dia, dia.AddDays(1));
            }
        }

        private sealed class CitaProjection
        {
            public int Id { get; init; }
            public DateTime FechaHora { get; init; }
            public string Cliente { get; init; } = "Cliente";
            public string? Telefono { get; init; }
            public string? CorreoCliente { get; init; }
            public int? ClienteId { get; init; }
            public int FuncionarioId { get; init; }
            public string Funcionario { get; init; } = string.Empty;
            public string Servicio { get; init; } = "Servicio";
            public int? ServicioId { get; init; }
            public decimal? PrecioServicio { get; init; }
            public string EstadoConfirmacionWhatsApp { get; init; } = WhatsAppConfirmationStates.Pendiente;
        }

        private sealed class CobroResumen
        {
            public decimal Monto { get; init; }
            public int IdCobro { get; init; }
            public string? Metodo { get; init; }
        }
    }
}
