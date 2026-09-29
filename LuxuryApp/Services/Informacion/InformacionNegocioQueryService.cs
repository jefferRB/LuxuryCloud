using System.Globalization;
using LuxuryApp.Models.Informacion;
using LuxuryApp.Models.Reservas;
using LuxuryApp.Services.BusinessTime;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Informacion
{
    public sealed class InformacionNegocioQueryService : IInformacionNegocioQueryService
    {
        private static readonly string[] FullWeekLabels =
        {
            "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado", "Domingo"
        };

        private static readonly string[] ShortWeekLabels =
        {
            "Lun", "Mar", "Mié", "Jue", "Vie", "Sáb", "Dom"
        };

        private static readonly HashSet<int> AllowedTopValues = new() { 5, 10, 20, 50 };
        private static readonly CultureInfo SpanishCulture = CultureInfo.GetCultureInfo("es-CR");

        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;

        public InformacionNegocioQueryService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
        }

        public async Task<InformacionViewModel> BuildViewModelAsync(
            int? mes,
            int? anio,
            int top,
            CancellationToken cancellationToken = default)
        {
            var selection = PeriodSelection.Resolve(mes, anio, _businessDateTimeProvider.Today());
            var normalizedTop = NormalizeTop(top);

            var citas = _context.Citas
                .AsNoTracking()
                .Where(c => c.Tipo == "CITA");

            var citasMes = citas
                .Where(c => c.FechaHoraCita >= selection.MonthStart && c.FechaHoraCita < selection.MonthEnd);

            var cobrosMes = _context.Cobros
                .AsNoTracking()
                .Where(c => c.FechaCobro >= selection.MonthStart && c.FechaCobro < selection.MonthEnd);

            var citasPorMesHistorico = await citas
                .GroupBy(c => c.FechaHoraCita.Month)
                .Select(group => new CountByMonthProjection
                {
                    Month = group.Key,
                    Total = group.Count()
                })
                .ToListAsync(cancellationToken);

            var citasHistoricas = await citas
                .Select(c => c.FechaHoraCita)
                .ToListAsync(cancellationToken);

            var serviciosMes = await citasMes
                .Where(c => c.ServicioId != null)
                .GroupBy(c => c.Servicio!.Nombre)
                .Select(group => new NamedCountProjection
                {
                    Name = group.Key,
                    Total = group.Count()
                })
                .OrderByDescending(x => x.Total)
                .ThenBy(x => x.Name)
                .ToListAsync(cancellationToken);

            var productosMes = await cobrosMes
                .Where(c => c.ProductoId != null)
                .GroupBy(c => c.Producto!.NombreProducto)
                .Select(group => new NamedCountProjection
                {
                    Name = group.Key,
                    Total = group.Count()
                })
                .OrderByDescending(x => x.Total)
                .ThenBy(x => x.Name)
                .ToListAsync(cancellationToken);

            var funcionariosMes = await citasMes
                .GroupBy(c => c.Funcionario!.Nombre)
                .Select(group => new NamedCountProjection
                {
                    Name = group.Key,
                    Total = group.Count()
                })
                .OrderByDescending(x => x.Total)
                .ThenBy(x => x.Name)
                .ToListAsync(cancellationToken);

            var citasAnuales = await citas
                .Where(c => c.FechaHoraCita >= selection.YearStart && c.FechaHoraCita < selection.YearEnd)
                .GroupBy(c => c.FechaHoraCita.Month)
                .Select(group => new CountByMonthProjection
                {
                    Month = group.Key,
                    Total = group.Count()
                })
                .ToListAsync(cancellationToken);

            var cobrosAnuales = await _context.Cobros
                .AsNoTracking()
                .Where(c => c.ProductoId != null
                         && c.FechaCobro >= selection.YearStart
                         && c.FechaCobro < selection.YearEnd)
                .GroupBy(c => c.FechaCobro.Month)
                .Select(group => new CountByMonthProjection
                {
                    Month = group.Key,
                    Total = group.Count()
                })
                .ToListAsync(cancellationToken);

            var topClientes = await _context.Clientes
                .AsNoTracking()
                .Where(c => c.Visitas.Any())
                .Select(c => new TopClienteVM
                {
                    Nombre = c.Nombre,
                    Telefono = c.NumeroTelefono,
                    TotalVisitas = c.Visitas.Count()
                })
                .OrderByDescending(x => x.TotalVisitas)
                .ThenBy(x => x.Nombre)
                .ThenBy(x => x.Telefono)
                .Take(normalizedTop)
                .ToListAsync(cancellationToken);

            var reservasOnlineMes = await _context.BookingRequests
                .AsNoTracking()
                .Where(r => r.Estado == BookingRequestStates.Confirmed
                         && r.FechaHoraInicioSolicitada >= selection.MonthStart
                         && r.FechaHoraInicioSolicitada < selection.MonthEnd)
                .CountAsync(cancellationToken);

            var semanaActual = await BuildWeekSeriesInternalAsync(
                GetStartOfWeek(_businessDateTimeProvider.Today()),
                FullWeekLabels,
                cancellationToken);

            var vm = new InformacionViewModel
            {
                MesSeleccionado = selection.Month,
                AnioSeleccionado = selection.Year,
                TopCantidad = normalizedTop,
                TopClientes = topClientes,
                SemanaDias = semanaActual.Dias,
                SemanaCitas = semanaActual.Citas,
                FuncionariosNombres = funcionariosMes.Select(x => x.Name).ToList(),
                FuncionariosCitas = funcionariosMes.Select(x => x.Total).ToList(),
                ServiciosNombres = serviciosMes.Select(x => x.Name).ToList(),
                ServiciosCantidad = serviciosMes.Select(x => x.Total).ToList(),
                ProductosVendidosNombres = productosMes
                    .Select(x => string.IsNullOrEmpty(x.Name) ? "Producto sin nombre" : x.Name)
                    .ToList(),
                ProductosVendidosCantidad = productosMes.Select(x => x.Total).ToList(),
                CitasPorMes = ComposeYearSeries(citasAnuales),
                ProductosVendidosPorMes = ComposeYearSeries(cobrosAnuales),
                CantidadServiciosMes = serviciosMes.Sum(x => x.Total),
                CantidadProductosMes = productosMes.Sum(x => x.Total),
                ReservasOnlineMes = reservasOnlineMes
            };

            ApplyHistoricalMonthExtremes(vm, citasPorMesHistorico);
            ApplyHistoricalDayAndHourExtremes(vm, citasHistoricas);
            ApplyServiceMetrics(vm, serviciosMes);
            ApplyProductMetrics(vm, productosMes);
            ApplyFuncionarioMetrics(vm, funcionariosMes);

            return vm;
        }

        public Task<CitasSemanaResponse> BuildCitasSemanaAsync(
            DateTime semana,
            CancellationToken cancellationToken = default) =>
            BuildWeekSeriesInternalAsync(GetStartOfWeek(semana.Date), ShortWeekLabels, cancellationToken);

        private async Task<CitasSemanaResponse> BuildWeekSeriesInternalAsync(
            DateTime weekStart,
            IReadOnlyList<string> labels,
            CancellationToken cancellationToken)
        {
            var weekEnd = weekStart.AddDays(7);

            var rows = await _context.Citas
                .AsNoTracking()
                .Where(c => c.Tipo == "CITA")
                .Where(c => c.FechaHoraCita >= weekStart && c.FechaHoraCita < weekEnd)
                .GroupBy(c => new
                {
                    c.FechaHoraCita.Year,
                    c.FechaHoraCita.Month,
                    c.FechaHoraCita.Day
                })
                .Select(group => new WeekDayCountProjection
                {
                    Year = group.Key.Year,
                    Month = group.Key.Month,
                    Day = group.Key.Day,
                    Total = group.Count()
                })
                .ToListAsync(cancellationToken);

            var countsByDate = rows.ToDictionary(
                row => new DateTime(row.Year, row.Month, row.Day),
                row => row.Total);

            var counts = Enumerable.Range(0, 7)
                .Select(offset => countsByDate.GetValueOrDefault(weekStart.AddDays(offset).Date))
                .ToList();

            return new CitasSemanaResponse
            {
                Dias = labels.ToList(),
                Citas = counts,
                Inicio = weekStart.ToString("dd"),
                Fin = weekStart.AddDays(6).ToString("dd")
            };
        }

        private static void ApplyHistoricalMonthExtremes(
            InformacionViewModel vm,
            IReadOnlyCollection<CountByMonthProjection> citasPorMesHistorico)
        {
            var mesMas = citasPorMesHistorico
                .OrderByDescending(x => x.Total)
                .ThenBy(x => x.Month)
                .FirstOrDefault();

            var mesMenos = citasPorMesHistorico
                .OrderBy(x => x.Total)
                .ThenBy(x => x.Month)
                .FirstOrDefault();

            if (mesMas is not null)
            {
                vm.MesMasCitas = SpanishCulture.DateTimeFormat.GetMonthName(mesMas.Month);
                vm.TotalMesMasCitas = mesMas.Total;
            }

            if (mesMenos is not null)
            {
                vm.MesMenosCitas = SpanishCulture.DateTimeFormat.GetMonthName(mesMenos.Month);
                vm.TotalMesMenosCitas = mesMenos.Total;
            }
        }

        private static void ApplyHistoricalDayAndHourExtremes(
            InformacionViewModel vm,
            IReadOnlyCollection<DateTime> citasHistoricas)
        {
            var dias = citasHistoricas
                .GroupBy(fecha => fecha.DayOfWeek)
                .Select(group => new DayOfWeekCountProjection
                {
                    DayOfWeek = group.Key,
                    Total = group.Count()
                })
                .ToList();

            var diaMas = dias
                .OrderByDescending(x => x.Total)
                .ThenBy(x => GetMondayBasedDayOrder(x.DayOfWeek))
                .FirstOrDefault();

            var diaMenos = dias
                .OrderBy(x => x.Total)
                .ThenBy(x => GetMondayBasedDayOrder(x.DayOfWeek))
                .FirstOrDefault();

            if (diaMas is not null)
            {
                vm.DiaMasOcupado = SpanishCulture.DateTimeFormat.GetDayName(diaMas.DayOfWeek);
                vm.TotalDiaMasOcupado = diaMas.Total;
            }

            if (diaMenos is not null)
            {
                vm.DiaMasLibre = SpanishCulture.DateTimeFormat.GetDayName(diaMenos.DayOfWeek);
                vm.TotalDiaMasLibre = diaMenos.Total;
            }

            var horas = citasHistoricas
                .GroupBy(fecha => fecha.Hour)
                .Select(group => new HourCountProjection
                {
                    Hour = group.Key,
                    Total = group.Count()
                })
                .ToList();

            var horaMas = horas
                .OrderByDescending(x => x.Total)
                .ThenBy(x => x.Hour)
                .FirstOrDefault();

            var horaMenos = horas
                .OrderBy(x => x.Total)
                .ThenBy(x => x.Hour)
                .FirstOrDefault();

            if (horaMas is not null)
            {
                vm.HoraMasOcupada = $"{horaMas.Hour}:00";
                vm.PromedioHoraMasOcupada = horaMas.Total;
            }

            if (horaMenos is not null)
            {
                vm.HoraMasLibre = $"{horaMenos.Hour}:00";
                vm.PromedioHoraMasLibre = horaMenos.Total;
            }
        }

        private static void ApplyServiceMetrics(
            InformacionViewModel vm,
            IReadOnlyList<NamedCountProjection> serviciosMes)
        {
            var servicioMasSolicitado = serviciosMes.FirstOrDefault();
            if (servicioMasSolicitado is null)
            {
                return;
            }

            vm.ServicioMasSolicitado = servicioMasSolicitado.Name;
            vm.TotalServicioMasSolicitado = servicioMasSolicitado.Total;
        }

        private static void ApplyProductMetrics(
            InformacionViewModel vm,
            IReadOnlyList<NamedCountProjection> productosMes)
        {
            var productoMasVendido = productosMes
                .OrderByDescending(x => x.Total)
                .ThenBy(x => x.Name)
                .FirstOrDefault();

            var productoMenosVendido = productosMes
                .OrderBy(x => x.Total)
                .ThenBy(x => x.Name)
                .FirstOrDefault();

            if (productoMasVendido is not null)
            {
                vm.ProductoMasVendido = productoMasVendido.Name;
                vm.TotalProductoMasVendido = productoMasVendido.Total;
            }

            if (productoMenosVendido is not null)
            {
                vm.ProductoMenosVendido = productoMenosVendido.Name;
                vm.TotalProductoMenosVendido = productoMenosVendido.Total;
            }
        }

        private static void ApplyFuncionarioMetrics(
            InformacionViewModel vm,
            IReadOnlyList<NamedCountProjection> funcionariosMes)
        {
            var funcionarioMasCitas = funcionariosMes.FirstOrDefault();
            if (funcionarioMasCitas is null)
            {
                return;
            }

            vm.FuncionarioMasCitas = funcionarioMasCitas.Name;
            vm.TotalFuncionarioCitas = funcionarioMasCitas.Total;
        }

        private static List<int> ComposeYearSeries(
            IReadOnlyCollection<CountByMonthProjection> citasAnuales)
        {
            var countsByMonth = citasAnuales.ToDictionary(row => row.Month, row => row.Total);

            return Enumerable.Range(1, 12)
                .Select(month => countsByMonth.GetValueOrDefault(month))
                .ToList();
        }

        private static int NormalizeTop(int top) =>
            AllowedTopValues.Contains(top) ? top : 10;

        private static DateTime GetStartOfWeek(DateTime date)
        {
            var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.AddDays(-diff).Date;
        }

        private static int GetMondayBasedDayOrder(DayOfWeek dayOfWeek) =>
            ((int)dayOfWeek + 6) % 7;

        private sealed class PeriodSelection
        {
            public int Month { get; init; }
            public int Year { get; init; }
            public DateTime MonthStart { get; init; }
            public DateTime MonthEnd { get; init; }
            public DateTime YearStart { get; init; }
            public DateTime YearEnd { get; init; }

            public static PeriodSelection Resolve(int? mes, int? anio, DateTime today)
            {
                var year = anio ?? today.Year;
                var month = mes ?? today.Month;
                var monthStart = new DateTime(year, month, 1);

                return new PeriodSelection
                {
                    Month = month,
                    Year = year,
                    MonthStart = monthStart,
                    MonthEnd = monthStart.AddMonths(1),
                    YearStart = new DateTime(year, 1, 1),
                    YearEnd = new DateTime(year + 1, 1, 1)
                };
            }
        }

        private sealed class CountByMonthProjection
        {
            public int Month { get; init; }
            public int Total { get; init; }
        }

        private sealed class NamedCountProjection
        {
            public string Name { get; init; } = string.Empty;
            public int Total { get; init; }
        }

        private sealed class DayOfWeekCountProjection
        {
            public DayOfWeek DayOfWeek { get; init; }
            public int Total { get; init; }
        }

        private sealed class HourCountProjection
        {
            public int Hour { get; init; }
            public int Total { get; init; }
        }

        private sealed class WeekDayCountProjection
        {
            public int Year { get; init; }
            public int Month { get; init; }
            public int Day { get; init; }
            public int Total { get; init; }
        }
    }
}
