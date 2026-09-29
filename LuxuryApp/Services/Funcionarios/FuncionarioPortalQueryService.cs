using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Services.BusinessTime;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Funcionarios
{
    public sealed class FuncionarioPortalQueryService : IFuncionarioPortalQueryService
    {
        private const int PagosPageSize = 20;
        private const int ProximasCitasLimit = 10;

        private readonly ApplicationDbContext _context;
        private readonly ILiquidacionSemanalService _liquidacionSemanalService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;

        public FuncionarioPortalQueryService(
            ApplicationDbContext context,
            ILiquidacionSemanalService liquidacionSemanalService,
            IBusinessDateTimeProvider businessDateTimeProvider)
        {
            _context = context;
            _liquidacionSemanalService = liquidacionSemanalService;
            _businessDateTimeProvider = businessDateTimeProvider;
        }

        public async Task<PortalFuncionario?> ResolverFuncionarioAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default)
        {
            // Tenant-safe por el global query filter: solo devuelve funcionarios del tenant actual.
            return await _context.Funcionarios
                .AsNoTracking()
                .Where(f => f.IdFuncionario == funcionarioId)
                .Select(f => new PortalFuncionario
                {
                    IdFuncionario = f.IdFuncionario,
                    Nombre = f.Nombre,
                    Activo = f.Activo,
                    ColorCalendario = f.ColorCalendario
                })
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<MiPanelViewModel> ObtenerPanelAsync(
            int funcionarioId,
            bool puedeRegistrarCobros,
            CancellationToken cancellationToken = default)
        {
            var hoy = _businessDateTimeProvider.Today();
            var inicioManana = hoy.AddDays(1);

            // Solo enriquecemos estado de cobro cuando hay permiso (evita query innecesaria).
            var citasHoy = await ConsultarCitasAsync(
                funcionarioId, hoy, inicioManana, null, puedeRegistrarCobros, cancellationToken);

            var proximasCitas = await ConsultarCitasAsync(
                funcionarioId, inicioManana, null, ProximasCitasLimit, false, cancellationToken);

            // Reutiliza la fórmula canónica de liquidaciones (respeta RebajarImpuestosAntesDeComision).
            var resumenSemana = await _liquidacionSemanalService.ObtenerResumenSemanaAsync(hoy, cancellationToken);
            var meSemana = resumenSemana.Funcionarios.FirstOrDefault(f => f.FuncionarioId == funcionarioId);

            // "Hoy" se calcula pidiendo el resumen para un rango de un solo día.
            var resumenHoy = await _liquidacionSemanalService.ObtenerResumenSemanaAsync(hoy, hoy, cancellationToken);
            var meHoy = resumenHoy.Funcionarios.FirstOrDefault(f => f.FuncionarioId == funcionarioId);

            var nombre = meSemana?.Nombre
                ?? meHoy?.Nombre
                ?? (await ResolverFuncionarioAsync(funcionarioId, cancellationToken))?.Nombre
                ?? string.Empty;

            return new MiPanelViewModel
            {
                Nombre = nombre,
                Hoy = hoy,
                CitasHoy = citasHoy,
                ProximasCitas = proximasCitas,
                ResumenHoy = MapResumen(meHoy),
                ProduccionSemana = meSemana?.TotalServicios + meSemana?.TotalProductos ?? 0m,
                ComisionSemana = meSemana?.PagoFinal ?? 0m,
                PagadoSemana = meSemana?.MontoPagado ?? 0m,
                PendienteSemana = meSemana?.MontoPendiente ?? 0m,
                InicioSemana = resumenSemana.InicioSemana,
                FinSemana = resumenSemana.FinSemana,
                PuedeRegistrarCobros = puedeRegistrarCobros,
                KpisHoy = await ObtenerKpisHoyAsync(funcionarioId, cancellationToken)
            };
        }

        public Task<MisGananciasViewModel> ObtenerGananciasAsync(
            int funcionarioId,
            CancellationToken cancellationToken = default)
            => ObtenerGananciasAsync(funcionarioId, null, null, 6, cancellationToken);

        public async Task<MisGananciasViewModel> ObtenerGananciasAsync(
            int funcionarioId,
            DateTime? semanaAnchor,
            DateTime? mesAnchor,
            int mesesEvolucion,
            CancellationToken cancellationToken = default)
        {
            // El gráfico de evolución solo admite 6 ó 12 meses.
            mesesEvolucion = mesesEvolucion == 12 ? 12 : 6;

            var hoy = _businessDateTimeProvider.Today();

            // Semana y mes navegables (independientes). Default = actual.
            var anchorSemana = (semanaAnchor ?? hoy).Date;
            var anchorMes = (mesAnchor ?? hoy).Date;
            var inicioMes = new DateTime(anchorMes.Year, anchorMes.Month, 1);
            var finMes = inicioMes.AddMonths(1).AddDays(-1);

            var resumenHoy = await _liquidacionSemanalService.ObtenerResumenSemanaAsync(hoy, hoy, cancellationToken);
            var resumenSemana = await _liquidacionSemanalService.ObtenerResumenSemanaAsync(anchorSemana, cancellationToken);
            var resumenMes = await _liquidacionSemanalService.ObtenerResumenSemanaAsync(inicioMes, finMes, cancellationToken);

            var meHoy = resumenHoy.Funcionarios.FirstOrDefault(f => f.FuncionarioId == funcionarioId);
            var meSemana = resumenSemana.Funcionarios.FirstOrDefault(f => f.FuncionarioId == funcionarioId);
            var meMes = resumenMes.Funcionarios.FirstOrDefault(f => f.FuncionarioId == funcionarioId);

            var nombre = meSemana?.Nombre
                ?? (await ResolverFuncionarioAsync(funcionarioId, cancellationToken))?.Nombre
                ?? string.Empty;

            var inicioMesActual = new DateTime(hoy.Year, hoy.Month, 1);

            // ── Series para gráficos (Fase 2) ──
            // Se reutiliza la MISMA fórmula canónica por periodo: cero divergencia con los KPIs.
            // El nº de llamadas está acotado por semanas/meses (no por filas) → no es N+1.
            var semanasDelMes = await ConstruirSemanasDelMesAsync(funcionarioId, inicioMes, finMes, cancellationToken);
            var evolucionMensual = await ConstruirEvolucionMensualAsync(funcionarioId, inicioMesActual, mesesEvolucion, cancellationToken);

            return new MisGananciasViewModel
            {
                Nombre = nombre,
                Hoy = MapResumen(meHoy),
                Semana = MapResumen(meSemana),
                Mes = MapResumen(meMes),
                InicioSemana = resumenSemana.InicioSemana,
                FinSemana = resumenSemana.FinSemana,
                InicioMes = inicioMes,
                FinMes = finMes,
                PagadoSemana = meSemana?.MontoPagado ?? 0m,
                PendienteSemana = meSemana?.MontoPendiente ?? 0m,
                PagadoMes = meMes?.MontoPagado ?? 0m,
                PendienteMes = meMes?.MontoPendiente ?? 0m,
                EsSemanaActual = hoy >= resumenSemana.InicioSemana && hoy <= resumenSemana.FinSemana,
                EsMesActual = inicioMes == inicioMesActual,
                DetalleDiasSemana = meSemana?.DetalleDias ?? new List<DetalleDiaVM>(),
                SemanasDelMes = semanasDelMes,
                EvolucionMensual = evolucionMensual,
                MesesEvolucion = mesesEvolucion
            };
        }

        /// <summary>
        /// Comisión/pagado/pendiente por cada semana (lunes→domingo, recortada al mes) del mes indicado.
        /// Reutiliza la fórmula canónica de liquidaciones por cada segmento.
        /// </summary>
        private async Task<IReadOnlyList<GananciaPeriodoPunto>> ConstruirSemanasDelMesAsync(
            int funcionarioId, DateTime inicioMes, DateTime finMes, CancellationToken cancellationToken)
        {
            var puntos = new List<GananciaPeriodoPunto>();
            var cursor = inicioMes;
            var idx = 1;
            while (cursor <= finMes)
            {
                // Domingo de la semana del cursor (semana lunes-primero); recortado al fin de mes.
                var dow = (int)cursor.DayOfWeek;            // domingo = 0
                var aDomingo = dow == 0 ? 0 : 7 - dow;
                var finSegmento = cursor.AddDays(aDomingo);
                if (finSegmento > finMes) finSegmento = finMes;

                var resumen = await _liquidacionSemanalService
                    .ObtenerResumenSemanaAsync(cursor, finSegmento, cancellationToken);
                var me = resumen.Funcionarios.FirstOrDefault(f => f.FuncionarioId == funcionarioId);
                puntos.Add(MapPunto(me, $"Sem {idx}", cursor, finSegmento));

                cursor = finSegmento.AddDays(1);
                idx++;
            }
            return puntos;
        }

        /// <summary>
        /// Comisión/pagado/pendiente de los últimos <paramref name="meses"/> meses terminando en el mes actual.
        /// </summary>
        private async Task<IReadOnlyList<GananciaPeriodoPunto>> ConstruirEvolucionMensualAsync(
            int funcionarioId, DateTime inicioMesActual, int meses, CancellationToken cancellationToken)
        {
            var puntos = new List<GananciaPeriodoPunto>();
            for (var i = meses - 1; i >= 0; i--)
            {
                var inicio = inicioMesActual.AddMonths(-i);
                var fin = inicio.AddMonths(1).AddDays(-1);
                var resumen = await _liquidacionSemanalService
                    .ObtenerResumenSemanaAsync(inicio, fin, cancellationToken);
                var me = resumen.Funcionarios.FirstOrDefault(f => f.FuncionarioId == funcionarioId);
                puntos.Add(MapPunto(me, inicio.ToString("MMM yy", new System.Globalization.CultureInfo("es-CR")), inicio, fin));
            }
            return puntos;
        }

        private static GananciaPeriodoPunto MapPunto(
            PagoFuncionarioVM? me, string etiqueta, DateTime desde, DateTime hasta) => new()
        {
            Etiqueta = etiqueta,
            Desde = desde,
            Hasta = hasta,
            Produccion = (me?.TotalServicios ?? 0m) + (me?.TotalProductos ?? 0m),
            Comision = me?.PagoFinal ?? 0m,
            Pagado = me?.MontoPagado ?? 0m,
            Pendiente = me?.MontoPendiente ?? 0m
        };

        public async Task<MisPagosViewModel> ObtenerPagosAsync(
            int funcionarioId,
            int pagina,
            CancellationToken cancellationToken = default)
        {
            if (pagina < 1)
            {
                pagina = 1;
            }

            // Fuente moderna: detalle de liquidaciones semanales.
            var liquidaciones = await _context.LiquidacionesSemanalesDetalle
                .AsNoTracking()
                .Where(d => d.FuncionarioId == funcionarioId && d.LiquidacionSemanal != null)
                .Select(d => new PortalPagoItem
                {
                    FechaPago = d.LiquidacionSemanal!.FechaPago,
                    Monto = d.MontoPagado,
                    MetodoPago = d.LiquidacionSemanal.Egreso != null
                        ? d.LiquidacionSemanal.Egreso.MetodoPago
                        : null,
                    InicioSemana = d.LiquidacionSemanal.SemanaInicio,
                    FinSemana = d.LiquidacionSemanal.SemanaFin,
                    Observacion = d.LiquidacionSemanal.Observacion
                })
                .ToListAsync(cancellationToken);

            // Fuente legacy: pagos antiguos.
            var legacy = await _context.PagosFuncionarios
                .AsNoTracking()
                .Where(p => p.FuncionarioId == funcionarioId)
                .Select(p => new PortalPagoItem
                {
                    FechaPago = p.FechaPago,
                    Monto = p.MontoPagado,
                    MetodoPago = null,
                    InicioSemana = p.InicioSemana,
                    FinSemana = p.FinSemana,
                    Observacion = p.Observacion
                })
                .ToListAsync(cancellationToken);

            var todos = liquidaciones
                .Concat(legacy)
                .OrderByDescending(p => p.FechaPago)
                .ToList();

            var totalRegistros = todos.Count;
            var totalPaginas = Math.Max(1, (int)Math.Ceiling(totalRegistros / (double)PagosPageSize));
            if (pagina > totalPaginas)
            {
                pagina = totalPaginas;
            }

            var pagos = todos
                .Skip((pagina - 1) * PagosPageSize)
                .Take(PagosPageSize)
                .ToList();

            var nombre = (await ResolverFuncionarioAsync(funcionarioId, cancellationToken))?.Nombre ?? string.Empty;

            var hoy = _businessDateTimeProvider.Today();
            var inicioMes = new DateTime(hoy.Year, hoy.Month, 1);
            var finMesExcl = inicioMes.AddMonths(1);

            return new MisPagosViewModel
            {
                Nombre = nombre,
                Pagos = pagos,
                TotalPagadoHistorico = todos.Sum(p => p.Monto),
                RecibidoMes = todos.Where(p => p.FechaPago >= inicioMes && p.FechaPago < finMesExcl).Sum(p => p.Monto),
                UltimoPago = todos.Count > 0 ? todos.Max(p => p.FechaPago) : (DateTime?)null,
                PagosRegistrados = totalRegistros,
                TotalRegistros = totalRegistros,
                PageSize = PagosPageSize,
                Pagina = pagina,
                TotalPaginas = totalPaginas
            };
        }

        public async Task<MiCalendarioViewModel> ObtenerCalendarioAsync(
            int funcionarioId,
            DateTime fecha,
            string rango,
            bool puedeCrearCitas,
            bool puedeEditarCitas,
            bool puedeCancelarCitas,
            bool puedeRegistrarCobros,
            CancellationToken cancellationToken = default)
        {
            var dia = fecha.Date;
            var siguiente = dia.AddDays(1);

            var citas = await ConsultarCitasAsync(
                funcionarioId, dia, siguiente, null, true, cancellationToken);

            // Panel derecho: solo citas restantes/próximas.
            var hoyDate = _businessDateTimeProvider.Today();
            var ahora = _businessDateTimeProvider.Now();
            var diaEsPasado = dia < hoyDate;
            IReadOnlyList<PortalCitaItem> citasRestantes;
            if (diaEsPasado)
            {
                citasRestantes = Array.Empty<PortalCitaItem>();
            }
            else if (dia == hoyDate)
            {
                citasRestantes = citas.Where(c => c.EsCita && c.FechaHora >= ahora).ToList();
            }
            else
            {
                citasRestantes = citas.Where(c => c.EsCita).ToList();
            }

            var control = await BuildControlAsync(funcionarioId, dia, rango, cancellationToken);

            var funcionario = await ResolverFuncionarioAsync(funcionarioId, cancellationToken);

            var servicios = (puedeCrearCitas || puedeEditarCitas)
                ? await LoadServiciosActivosAsync(cancellationToken)
                : Array.Empty<CalendarServiceOptionResponse>();

            // Conteo mensual de citas del funcionario (estilo calendario principal).
            var inicioMes = new DateTime(dia.Year, dia.Month, 1);
            var finMes = inicioMes.AddMonths(1);
            var conteo = await _context.Citas
                .AsNoTracking()
                .Where(c => c.FuncionarioId == funcionarioId &&
                            c.Tipo == "CITA" &&
                            c.FechaHoraCita >= inicioMes &&
                            c.FechaHoraCita < finMes)
                .GroupBy(c => c.FechaHoraCita.Day)
                .Select(g => new { Dia = g.Key, Total = g.Count() })
                .ToDictionaryAsync(x => x.Dia, x => x.Total, cancellationToken);

            // KPI "Citas hoy" (siempre la fecha real de hoy, sin importar el mes navegado).
            var hoy = _businessDateTimeProvider.Today();
            var mananaHoy = hoy.AddDays(1);
            var citasHoyCount = await _context.Citas
                .AsNoTracking()
                .CountAsync(c => c.FuncionarioId == funcionarioId &&
                                 c.Tipo == "CITA" &&
                                 c.FechaHoraCita >= hoy &&
                                 c.FechaHoraCita < mananaHoy,
                            cancellationToken);

            return new MiCalendarioViewModel
            {
                Nombre = funcionario?.Nombre ?? string.Empty,
                ColorCalendario = funcionario?.ColorCalendario ?? "#111111",
                Fecha = dia,
                EsHoy = dia == _businessDateTimeProvider.Today(),
                HoyNegocio = _businessDateTimeProvider.Today(),
                Citas = citas,
                PuedeCrearCitas = puedeCrearCitas,
                PuedeEditarCitas = puedeEditarCitas,
                PuedeCancelarCitas = puedeCancelarCitas,
                PuedeRegistrarCobros = puedeRegistrarCobros,
                Servicios = servicios,
                CitasRestantes = citasRestantes,
                DiaEsPasado = diaEsPasado,
                KpisHoy = await ObtenerKpisHoyAsync(funcionarioId, cancellationToken),
                Year = dia.Year,
                Month = dia.Month,
                CitasHoyCount = citasHoyCount,
                ConteoPorDia = conteo,
                Control = control
            };
        }

        /// <summary>Citas del funcionario para un día (para la vista diaria por horas del portal).</summary>
        public async Task<IReadOnlyList<PortalCitaItem>> ObtenerCitasDiaAsync(
            int funcionarioId,
            DateTime fecha,
            CancellationToken cancellationToken = default)
        {
            var dia = fecha.Date;
            return await ConsultarCitasAsync(funcionarioId, dia, dia.AddDays(1), null, true, cancellationToken);
        }

        /// <summary>KPIs operativos del día actual del funcionario (citas, pendiente, cobrado, próxima).</summary>
        private async Task<PortalKpisDia> ObtenerKpisHoyAsync(int funcionarioId, CancellationToken cancellationToken)
        {
            var hoy = _businessDateTimeProvider.Today();
            var manana = hoy.AddDays(1);
            var ahora = _businessDateTimeProvider.Now();

            var citas = await _context.Citas
                .AsNoTracking()
                .Where(c => c.FuncionarioId == funcionarioId && c.Tipo == "CITA" &&
                            c.FechaHoraCita >= hoy && c.FechaHoraCita < manana)
                .OrderBy(c => c.FechaHoraCita)
                .Select(c => new
                {
                    c.Id,
                    c.FechaHoraCita,
                    c.ServicioId,
                    Precio = c.Servicio != null ? (decimal?)c.Servicio.Precio : null,
                    Cliente = c.Cliente != null ? c.Cliente.Nombre : (c.NombreCliente ?? "Cliente")
                })
                .ToListAsync(cancellationToken);

            var ids = citas.Select(c => c.Id).ToList();
            var cobradas = ids.Count == 0
                ? new HashSet<int>()
                : (await _context.Cobros.AsNoTracking()
                    .Where(co => co.CitaId != null && ids.Contains(co.CitaId.Value))
                    .Select(co => co.CitaId!.Value).ToListAsync(cancellationToken)).ToHashSet();

            var pendientes = citas.Where(c => c.ServicioId.HasValue && !cobradas.Contains(c.Id)).ToList();

            var cobradoHoy = await _context.Cobros.AsNoTracking()
                .Where(c => c.FuncionarioId == funcionarioId && c.FechaCobro >= hoy && c.FechaCobro < manana)
                .SumAsync(c => (decimal?)c.Monto, cancellationToken) ?? 0m;

            var proxima = citas.FirstOrDefault(c => c.FechaHoraCita >= ahora) ?? citas.FirstOrDefault();

            return new PortalKpisDia
            {
                CitasHoy = citas.Count,
                PendientesHoy = pendientes.Count,
                PendienteCobrarHoy = pendientes.Sum(c => c.Precio ?? 0m),
                CobradoHoy = cobradoHoy,
                ProximaFechaHora = proxima?.FechaHoraCita,
                ProximaCliente = proxima?.Cliente
            };
        }

        public Task<PortalControlCitas> ObtenerControlAsync(
            int funcionarioId,
            DateTime fecha,
            string rango,
            CancellationToken cancellationToken = default)
            => BuildControlAsync(funcionarioId, fecha.Date, rango, cancellationToken);

        private async Task<PortalControlCitas> BuildControlAsync(
            int funcionarioId,
            DateTime dia,
            string rango,
            CancellationToken cancellationToken)
        {
            rango = rango?.ToLowerInvariant() switch
            {
                "semana" => "semana",
                "mes" => "mes",
                _ => "dia"
            };

            DateTime desde, hastaExcl;
            switch (rango)
            {
                case "semana":
                    var diff = ((int)dia.DayOfWeek + 6) % 7; // lunes primero
                    desde = dia.AddDays(-diff);
                    hastaExcl = desde.AddDays(7);
                    break;
                case "mes":
                    desde = new DateTime(dia.Year, dia.Month, 1);
                    hastaExcl = desde.AddMonths(1);
                    break;
                default:
                    desde = dia;
                    hastaExcl = dia.AddDays(1);
                    break;
            }

            // Citas (solo CITA) del funcionario en el rango. Tenant-safe + filtro funcionario.
            var citas = await _context.Citas
                .AsNoTracking()
                .Where(c => c.FuncionarioId == funcionarioId &&
                            c.Tipo == "CITA" &&
                            c.FechaHoraCita >= desde &&
                            c.FechaHoraCita < hastaExcl)
                .OrderBy(c => c.FechaHoraCita)
                .Select(c => new
                {
                    c.Id,
                    c.FechaHoraCita,
                    Cliente = c.Cliente != null ? c.Cliente.Nombre : (c.NombreCliente ?? "Cliente"),
                    Servicio = c.Servicio != null ? c.Servicio.Nombre : (c.ServicioNombrePersonalizado ?? "Servicio"),
                    c.ServicioId,
                    PrecioServicio = c.Servicio != null ? (decimal?)c.Servicio.Precio : null,
                    CorreoCliente = c.Cliente != null ? c.Cliente.CorreoElectronico : null
                })
                .ToListAsync(cancellationToken);

            // Montos cobrados por cita (cobros ligados a estas citas).
            var ids = citas.Select(c => c.Id).ToList();
            var cobrosPorCita = ids.Count == 0
                ? new Dictionary<int, decimal>()
                : await _context.Cobros
                    .AsNoTracking()
                    .Where(co => co.CitaId != null && ids.Contains(co.CitaId.Value))
                    .GroupBy(co => co.CitaId!.Value)
                    .Select(g => new { CitaId = g.Key, Monto = g.Sum(x => x.Monto) })
                    .ToDictionaryAsync(x => x.CitaId, x => x.Monto, cancellationToken);

            var items = citas.Select(c =>
            {
                var cobrada = cobrosPorCita.TryGetValue(c.Id, out var monto);
                return new PortalControlCitaItem
                {
                    Id = c.Id,
                    FechaHora = c.FechaHoraCita,
                    Cliente = c.Cliente,
                    Servicio = c.Servicio,
                    ServicioId = c.ServicioId,
                    PrecioServicio = c.PrecioServicio,
                    MontoCobrado = cobrada ? monto : (decimal?)null,
                    YaCobrada = cobrada,
                    CorreoCliente = c.CorreoCliente
                };
            }).ToList();

            var cobradas = items.Count(i => i.YaCobrada);

            return new PortalControlCitas
            {
                Rango = rango,
                Desde = desde,
                Hasta = hastaExcl.AddDays(-1),
                Total = items.Count,
                Cobradas = cobradas,
                Pendientes = items.Count - cobradas,
                MontoCobrado = items.Where(i => i.YaCobrada).Sum(i => i.MontoCobrado ?? 0m),
                MontoPendienteEstimado = items.Where(i => !i.YaCobrada).Sum(i => i.PrecioServicio ?? 0m),
                Items = items
            };
        }

        public async Task<MisCobrosViewModel> ObtenerCobrosAsync(
            int funcionarioId,
            int pagina,
            bool puedeRegistrarCobros,
            CancellationToken cancellationToken = default)
            => await ObtenerCobrosAsync(funcionarioId, pagina, "mes", "", "", puedeRegistrarCobros, false, cancellationToken);

        public async Task<MisCobrosViewModel> ObtenerCobrosAsync(
            int funcionarioId,
            int pagina,
            string rango,
            string? metodo,
            string? origen,
            bool puedeRegistrarCobros,
            bool puedeRegistrarManual,
            CancellationToken cancellationToken = default)
        {
            if (pagina < 1) pagina = 1;

            rango = rango?.ToLowerInvariant() switch
            {
                "dia" => "dia",
                "semana" => "semana",
                "todos" => "todos",
                _ => "mes"
            };
            metodo = (metodo ?? "").Trim().ToUpperInvariant();
            if (metodo != "EFECTIVO" && metodo != "TARJETA" && metodo != "SINPE") metodo = "";
            origen = (origen ?? "").Trim().ToLowerInvariant();
            if (origen != "cita" && origen != "manual") origen = "";

            var hoy = _businessDateTimeProvider.Today();
            var inicioManana = hoy.AddDays(1);
            var diff = (7 + (hoy.DayOfWeek - DayOfWeek.Monday)) % 7;
            var inicioSemana = hoy.AddDays(-diff).Date;
            var finSemanaExclusivo = inicioSemana.AddDays(7);
            var inicioMes = new DateTime(hoy.Year, hoy.Month, 1);
            var finMesExclusivo = inicioMes.AddMonths(1);

            // Solo cobros del funcionario autenticado (tenant-safe + filtro FuncionarioId).
            var baseQuery = _context.Cobros.AsNoTracking().Where(c => c.FuncionarioId == funcionarioId);

            // KPIs (siempre)
            var totalHoy = await baseQuery.Where(c => c.FechaCobro >= hoy && c.FechaCobro < inicioManana)
                .SumAsync(c => (decimal?)c.Monto, cancellationToken) ?? 0m;
            var totalSemana = await baseQuery.Where(c => c.FechaCobro >= inicioSemana && c.FechaCobro < finSemanaExclusivo)
                .SumAsync(c => (decimal?)c.Monto, cancellationToken) ?? 0m;
            var totalMes = await baseQuery.Where(c => c.FechaCobro >= inicioMes && c.FechaCobro < finMesExclusivo)
                .SumAsync(c => (decimal?)c.Monto, cancellationToken) ?? 0m;
            var cobrosRegistrados = await baseQuery.CountAsync(cancellationToken);

            // Rango seleccionado
            DateTime? desde = null, hastaExcl = null;
            switch (rango)
            {
                case "dia": desde = hoy; hastaExcl = inicioManana; break;
                case "semana": desde = inicioSemana; hastaExcl = finSemanaExclusivo; break;
                case "mes": desde = inicioMes; hastaExcl = finMesExclusivo; break;
                // "todos": sin rango
            }

            var rangoQuery = baseQuery;
            if (desde.HasValue) rangoQuery = rangoQuery.Where(c => c.FechaCobro >= desde.Value && c.FechaCobro < hastaExcl!.Value);

            // Desglose por método sobre el rango (sin aplicar el filtro de método)
            var metodoEfectivo = await rangoQuery.Where(c => c.MetodoPago == "EFECTIVO").SumAsync(c => (decimal?)c.Monto, cancellationToken) ?? 0m;
            var metodoTarjeta = await rangoQuery.Where(c => c.MetodoPago == "TARJETA").SumAsync(c => (decimal?)c.Monto, cancellationToken) ?? 0m;
            var metodoSinpe = await rangoQuery.Where(c => c.MetodoPago == "SINPE").SumAsync(c => (decimal?)c.Monto, cancellationToken) ?? 0m;

            // Tabla filtrada (rango + método + origen)
            var filtered = rangoQuery;
            if (metodo.Length > 0) filtered = filtered.Where(c => c.MetodoPago == metodo);
            if (origen == "cita") filtered = filtered.Where(c => c.CitaId != null);
            else if (origen == "manual") filtered = filtered.Where(c => c.CitaId == null);

            var totalRegistros = await filtered.CountAsync(cancellationToken);
            var totalPaginas = Math.Max(1, (int)Math.Ceiling(totalRegistros / (double)PagosPageSize));
            if (pagina > totalPaginas) pagina = totalPaginas;

            var cobros = await filtered
                .OrderByDescending(c => c.FechaCobro)
                .ThenByDescending(c => c.IdCobro)
                .Skip((pagina - 1) * PagosPageSize)
                .Take(PagosPageSize)
                .Select(c => new PortalCobroItem
                {
                    IdCobro = c.IdCobro,
                    FechaCobro = c.FechaCobro,
                    Cliente = c.Cliente != null ? c.Cliente.Nombre : c.NombreCliente,
                    Servicio = c.Servicio != null
                        ? c.Servicio.Nombre
                        : (c.Producto != null ? c.Producto.NombreProducto : "Servicio"),
                    Monto = c.Monto,
                    MetodoPago = c.MetodoPago,
                    DesdeCita = c.CitaId != null,
                    // Comprobante "vivo" más reciente del cobro (OUTER APPLY: una sola consulta).
                    ComprobanteId = _context.ComprobantesCobro
                        .Where(cc => cc.CobroId == c.IdCobro && cc.EstadoEnvio != LuxuryApp.Models.Comprobantes.ComprobanteEstadoEnvio.Cancelled)
                        .OrderByDescending(cc => cc.Id)
                        .Select(cc => (int?)cc.Id)
                        .FirstOrDefault(),
                    ComprobanteEstado = _context.ComprobantesCobro
                        .Where(cc => cc.CobroId == c.IdCobro && cc.EstadoEnvio != LuxuryApp.Models.Comprobantes.ComprobanteEstadoEnvio.Cancelled)
                        .OrderByDescending(cc => cc.Id)
                        .Select(cc => (LuxuryApp.Models.Comprobantes.ComprobanteEstadoEnvio?)cc.EstadoEnvio)
                        .FirstOrDefault(),
                    ComprobanteToken = _context.ComprobantesCobro
                        .Where(cc => cc.CobroId == c.IdCobro && cc.EstadoEnvio != LuxuryApp.Models.Comprobantes.ComprobanteEstadoEnvio.Cancelled)
                        .OrderByDescending(cc => cc.Id)
                        .Select(cc => cc.TokenPublico)
                        .FirstOrDefault()
                })
                .ToListAsync(cancellationToken);

            var nombre = (await ResolverFuncionarioAsync(funcionarioId, cancellationToken))?.Nombre ?? string.Empty;

            var servicios = puedeRegistrarManual
                ? await LoadServiciosActivosAsync(cancellationToken)
                : Array.Empty<CalendarServiceOptionResponse>();

            return new MisCobrosViewModel
            {
                Nombre = nombre,
                PuedeRegistrarCobros = puedeRegistrarCobros,
                PuedeRegistrarManual = puedeRegistrarManual,
                Servicios = servicios,
                TotalHoy = totalHoy,
                TotalSemana = totalSemana,
                TotalMes = totalMes,
                CobrosRegistrados = cobrosRegistrados,
                Rango = rango,
                Metodo = metodo,
                Origen = origen,
                RangoDesde = desde,
                RangoHasta = hastaExcl?.AddDays(-1),
                MetodoEfectivo = metodoEfectivo,
                MetodoTarjeta = metodoTarjeta,
                MetodoSinpe = metodoSinpe,
                Cobros = cobros,
                TotalRegistros = totalRegistros,
                PageSize = PagosPageSize,
                Pagina = pagina,
                TotalPaginas = totalPaginas
            };
        }

        public async Task<PortalCitaItem?> ObtenerCitaCobrableAsync(
            int funcionarioId,
            int citaId,
            CancellationToken cancellationToken = default)
        {
            // Tenant-safe (global filter) + filtro explícito por funcionario.
            var cita = await _context.Citas
                .AsNoTracking()
                .Where(c => c.Id == citaId && c.FuncionarioId == funcionarioId)
                .Select(c => new
                {
                    c.Id,
                    c.FechaHoraCita,
                    c.ClienteId,
                    Cliente = c.Cliente != null ? c.Cliente.Nombre : (c.NombreCliente ?? "Cliente"),
                    Servicio = c.Servicio != null ? c.Servicio.Nombre : c.ServicioNombrePersonalizado,
                    c.Tipo,
                    c.ServicioId,
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

            return new PortalCitaItem
            {
                Id = cita.Id,
                FechaHora = cita.FechaHoraCita,
                Cliente = cita.Cliente,
                Servicio = cita.Servicio ?? "Servicio",
                Tipo = cita.Tipo,
                ServicioId = cita.ServicioId,
                PrecioServicio = cita.PrecioServicio,
                YaCobrada = yaCobrada,
                ClienteId = cita.ClienteId
            };
        }

        public async Task<bool> ClienteExisteAsync(int clienteId, CancellationToken cancellationToken = default)
        {
            if (clienteId <= 0) return false;
            // Tenant-safe por global query filter.
            return await _context.Clientes.AsNoTracking().AnyAsync(c => c.Id == clienteId, cancellationToken);
        }

        public async Task<IReadOnlyList<PortalClienteOption>> BuscarClientesAsync(
            string? term,
            CancellationToken cancellationToken = default)
        {
            term = term?.Trim();
            if (string.IsNullOrWhiteSpace(term) || term.Length < 3)
            {
                return Array.Empty<PortalClienteOption>();
            }

            // Tenant-safe por global query filter. Clientes son del negocio, no del funcionario.
            return await _context.Clientes
                .AsNoTracking()
                .Where(c => EF.Functions.Like(c.Nombre, $"%{term}%") ||
                            (c.NumeroTelefono != null && c.NumeroTelefono.Contains(term)))
                .OrderBy(c => c.Nombre)
                .ThenBy(c => c.Id)
                .Take(10)
                .Select(c => new PortalClienteOption
                {
                    Id = c.Id,
                    Nombre = c.Nombre,
                    Telefono = c.NumeroTelefono,
                    Correo = c.CorreoElectronico
                })
                .ToListAsync(cancellationToken);
        }

        private async Task<IReadOnlyList<CalendarServiceOptionResponse>> LoadServiciosActivosAsync(
            CancellationToken cancellationToken)
        {
            return await _context.Servicios
                .AsNoTracking()
                .Where(s => s.Activo)
                .OrderBy(s => s.Nombre)
                .Select(s => new CalendarServiceOptionResponse
                {
                    Id = s.Id,
                    Nombre = s.Nombre,
                    DuracionMinutos = s.DuracionMinutos ?? 30,
                    Precio = s.Precio
                })
                .ToListAsync(cancellationToken);
        }

        private async Task<IReadOnlyList<PortalCitaItem>> ConsultarCitasAsync(
            int funcionarioId,
            DateTime desde,
            DateTime? hasta,
            int? limite,
            bool incluirEstadoCobro,
            CancellationToken cancellationToken)
        {
            // Tenant-safe + filtro explícito por funcionario.
            var query = _context.Citas
                .AsNoTracking()
                .Where(c => c.FuncionarioId == funcionarioId && c.FechaHoraCita >= desde);

            if (hasta.HasValue)
            {
                var limiteSuperior = hasta.Value;
                query = query.Where(c => c.FechaHoraCita < limiteSuperior);
            }

            query = query.OrderBy(c => c.FechaHoraCita);

            if (limite.HasValue)
            {
                query = query.Take(limite.Value);
            }

            var citas = await query
                .Select(c => new
                {
                    c.Id,
                    c.FechaHoraCita,
                    Cliente = c.Cliente != null ? c.Cliente.Nombre : (c.NombreCliente ?? "Cliente"),
                    Servicio = c.Servicio != null
                        ? c.Servicio.Nombre
                        : (c.ServicioNombrePersonalizado ?? (c.Tipo == "CITA" ? "Servicio" : c.Tipo)),
                    c.Tipo,
                    Telefono = c.Cliente != null ? c.Cliente.NumeroTelefono : c.TelefonoCliente,
                    CorreoCliente = c.Cliente != null ? c.Cliente.CorreoElectronico : null,
                    c.DuracionMinutos,
                    DuracionServicio = c.Servicio != null ? c.Servicio.DuracionMinutos : null,
                    c.ServicioId,
                    PrecioServicio = c.Servicio != null ? (decimal?)c.Servicio.Precio : null,
                    c.ClienteId,
                    c.NombreCliente,
                    c.ServicioNombrePersonalizado
                })
                .ToListAsync(cancellationToken);

            // Estado de cobro: una sola consulta para todas las citas con servicio de catálogo.
            var cobradas = new HashSet<int>();
            if (incluirEstadoCobro)
            {
                var idsConServicio = citas
                    .Where(c => c.ServicioId.HasValue)
                    .Select(c => c.Id)
                    .ToList();

                if (idsConServicio.Count > 0)
                {
                    cobradas = (await _context.Cobros
                            .AsNoTracking()
                            .Where(co => co.CitaId != null && idsConServicio.Contains(co.CitaId.Value))
                            .Select(co => co.CitaId!.Value)
                            .ToListAsync(cancellationToken))
                        .ToHashSet();
                }
            }

            return citas
                .Select(c => new PortalCitaItem
                {
                    Id = c.Id,
                    FechaHora = c.FechaHoraCita,
                    Cliente = c.Cliente,
                    Servicio = c.Servicio,
                    Tipo = c.Tipo,
                    Telefono = c.Telefono,
                    DuracionMinutos = c.DuracionMinutos,
                    DuracionEfectiva = c.DuracionMinutos ?? c.DuracionServicio ?? 30,
                    ServicioId = c.ServicioId,
                    PrecioServicio = c.PrecioServicio,
                    YaCobrada = cobradas.Contains(c.Id),
                    ClienteId = c.ClienteId,
                    NombreClienteRaw = c.NombreCliente,
                    ServicioPersonalizado = c.ServicioNombrePersonalizado,
                    CorreoCliente = c.CorreoCliente
                })
                .ToList();
        }

        private static PortalResumenProduccion MapResumen(PagoFuncionarioVM? me)
        {
            if (me is null)
            {
                return new PortalResumenProduccion();
            }

            // Comisión de productos: suma de la ganancia ya calculada por producto
            // (misma base/impuestos del servicio canónico). El resto de PagoFinal es servicios.
            var comisionProductos = me.ProductosVendidos.Sum(p => p.GananciaFuncionario);
            var comisionServicios = me.PagoFinal - comisionProductos;
            if (comisionServicios < 0)
            {
                comisionServicios = 0;
            }

            return new PortalResumenProduccion
            {
                ProduccionServicios = me.TotalServicios,
                ProduccionProductos = me.TotalProductos,
                ComisionProductos = comisionProductos,
                ComisionServicios = comisionServicios,
                TotalEstimado = me.PagoFinal
            };
        }
    }
}
