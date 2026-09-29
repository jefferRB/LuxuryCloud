using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Funcionarios;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Finanzas
{
    /// <summary>
    /// Arma el Dashboard financiero del MES CALENDARIO.
    ///
    /// <para>
    /// La ganancia y sus componentes NO se calculan acá: salen de
    /// <see cref="IPeriodProfitCalculationService"/>, el mismo motor que usan los estados de cuenta
    /// de inversionistas. Antes este servicio tenía su propia aritmética (IVA plano Total/1.13,
    /// liquidaciones por lo pagado, sin excluir la categoría de distribución) y para el mismo mes
    /// podía dar un número distinto al del inversionista.
    /// </para>
    ///
    /// <para>
    /// Lo que sí sigue viviendo acá es todo lo que NO es la fórmula de ganancia: métodos de pago,
    /// inventario, clientes, citas y la vista de caja.
    /// </para>
    /// </summary>
    public sealed class DashboardFinancieroQueryService : IDashboardFinancieroQueryService
    {
        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly IPeriodProfitCalculationService _profitCalculationService;

        public DashboardFinancieroQueryService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider,
            IPeriodProfitCalculationService profitCalculationService)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
            _profitCalculationService = profitCalculationService;
        }

        public async Task<DashboardViewModel> BuildViewModelAsync(
            int? mes,
            int? anio,
            CancellationToken cancellationToken = default)
        {
            var selection = DashboardPeriodSelection.Resolve(mes, anio, _businessDateTimeProvider.Today());

            var cobroMetrics = await GetCobroMetricsAsync(
                    selection.MonthStart,
                    selection.MonthEnd,
                    cancellationToken)
                ?? new CobroMetricsProjection();

            var operationalMetrics = await GetOperationalMetricsAsync(
                selection.MonthStart,
                selection.MonthEnd,
                cancellationToken);

            var ingresosPorMes = await GetIngresosPorMesAsync(
                selection.YearStart,
                selection.YearEnd,
                cancellationToken);

            var egresosPorMes = await GetEgresosPorMesAsync(
                selection.YearStart,
                selection.YearEnd,
                cancellationToken);

            var pagosFuncionariosHistoricosCajaPorMes = await GetPagosFuncionariosHistoricosCajaPorMesAsync(
                selection.YearStart,
                selection.YearEnd,
                cancellationToken);

            var pagosFuncionariosCajaPorLiquidacionPorMes = await GetPagosFuncionariosCajaPorLiquidacionPorMesAsync(
                selection.YearStart,
                selection.YearEnd,
                cancellationToken);

            var egresosMesSeleccionado = egresosPorMes.GetValueOrDefault(
                selection.Month,
                EgresoMonthAggregateProjection.Empty);

            // ── Ganancia del negocio: motor único, mismo que usan los inversionistas ──
            // Una sola llamada devuelve los doce meses, así el número grande del mes seleccionado y
            // las barras del gráfico salen exactamente del mismo cálculo.
            // La interpretación es la ECONÓMICA, no la contractual del inversionista: ver
            // InvestorProfitPolicy.CreateForBusinessResult. El Dashboard responde "¿cuánto ganó el
            // negocio?", y eso no depende de cómo se pactó el reparto ni de si ya se pagó.
            var policy = InvestorProfitPolicy.CreateForBusinessResult();
            var desglosePorMes = await _profitCalculationService.CalculateMonthlyAsync(
                selection.Year,
                policy,
                cancellationToken);

            var desgloseMes = desglosePorMes[selection.Month];

            var totalSinImpuestos = desgloseMes.IngresosNetos;
            var totalImpuestos = desgloseMes.IvaCobrado;

            var totalPagadoFuncionariosCaja =
                pagosFuncionariosCajaPorLiquidacionPorMes.GetValueOrDefault(selection.Month)
                + pagosFuncionariosHistoricosCajaPorMes.GetValueOrDefault(selection.Month);

            // Liquidaciones del equipo tal como las define la política del negocio (devengado por
            // defecto). Es la MISMA línea que resta el estado de cuenta del inversionista.
            var totalPagadoFuncionariosAnalitico = desgloseMes.LiquidacionesEquipo;

            var totalEgresosAnaliticos =
                desgloseMes.GastosOperativos + desgloseMes.LiquidacionesEquipo;

            var resultadoCajaPorMes = new List<decimal>(12);
            var resultadoAnaliticoPorMes = new List<decimal>(12);

            for (var currentMonth = 1; currentMonth <= 12; currentMonth++)
            {
                var ingresosMes = ingresosPorMes.GetValueOrDefault(currentMonth);
                var totalSinImpuestosCajaMes = PagoFuncionarioDevengadoCalculator.CalcularBaseSinIvaIncluido(ingresosMes);

                var egresosMes = egresosPorMes.GetValueOrDefault(
                    currentMonth,
                    EgresoMonthAggregateProjection.Empty);

                // Vista de CAJA: lo que salió de la cuenta. No es la ganancia del negocio y por eso
                // conserva su propia aritmética simple.
                resultadoCajaPorMes.Add(totalSinImpuestosCajaMes - egresosMes.TotalEgresos);

                resultadoAnaliticoPorMes.Add(desglosePorMes[currentMonth].GananciaDistribuible);
            }

            return new DashboardViewModel
            {
                TotalServicios = cobroMetrics.TotalServicios,
                TotalProductos = cobroMetrics.TotalProductos,
                TotalGenerado = desgloseMes.TotalCobrado,
                TotalSinImpuestos = totalSinImpuestos,
                TotalImpuestos = totalImpuestos,
                TotalPagadoFuncionarios = totalPagadoFuncionariosCaja,
                TotalPagadoFuncionariosAnalitico = totalPagadoFuncionariosAnalitico,
                SalidasCajaMes = egresosMesSeleccionado.TotalEgresos,
                TotalEgresosAnaliticos = totalEgresosAnaliticos,
                IngresosEfectivo = cobroMetrics.IngresosEfectivo,
                IngresosSinpe = cobroMetrics.IngresosSinpe,
                IngresosTarjeta = cobroMetrics.IngresosTarjeta,
                GananciaPorMes = resultadoCajaPorMes,
                ResultadoAnaliticoPorMes = resultadoAnaliticoPorMes,
                Desglose = desgloseMes,
                CantidadClientes = operationalMetrics.CantidadClientes,
                CantidadCitasMes = operationalMetrics.CantidadCitasMes,
                ValorInventarioProductos = operationalMetrics.ValorInventarioProductos,
                TotalProductosInventario = operationalMetrics.TotalProductosInventario,
                MesSeleccionado = selection.Month,
                AnioSeleccionado = selection.Year
            };
        }

        private Task<CobroMetricsProjection?> GetCobroMetricsAsync(
            DateTime monthStart,
            DateTime monthEnd,
            CancellationToken cancellationToken) =>
            _context.Cobros
                .AsNoTracking()
                .Where(c => c.FechaCobro >= monthStart && c.FechaCobro < monthEnd)
                .GroupBy(_ => 1)
                .Select(group => new CobroMetricsProjection
                {
                    // Criterio canónico de "servicio" (mismo que CobroQueryService y
                    // LiquidacionSemanalService): un cobro de cita fuera de catálogo no tiene
                    // ServicioId, solo el nombre personalizado, y financieramente ES un servicio.
                    TotalServicios = group.Sum(x =>
                        x.ServicioId != null || x.ServicioNombrePersonalizado != null ? x.Monto : 0m),
                    TotalProductos = group.Sum(x => x.ProductoId != null ? x.Monto : 0m),
                    TotalGenerado = group.Sum(x => x.Monto),
                    IngresosEfectivo = group.Sum(x => x.MetodoPago == "EFECTIVO" ? x.Monto : 0m),
                    IngresosSinpe = group.Sum(x => x.MetodoPago == "SINPE" ? x.Monto : 0m),
                    IngresosTarjeta = group.Sum(x => x.MetodoPago == "TARJETA" ? x.Monto : 0m)
                })
                .SingleOrDefaultAsync(cancellationToken);

        private async Task<OperationalMetricsProjection> GetOperationalMetricsAsync(
            DateTime monthStart,
            DateTime monthEnd,
            CancellationToken cancellationToken)
        {
            var cantidadClientes = await _context.Clientes
                .AsNoTracking()
                .CountAsync(cancellationToken);

            var cantidadCitasMes = await _context.Citas
                .AsNoTracking()
                .CountAsync(
                    c => c.FechaHoraCita >= monthStart && c.FechaHoraCita < monthEnd,
                    cancellationToken);

            var inventario = await _context.Productos
                .AsNoTracking()
                .Where(p => p.Activo)
                .GroupBy(_ => 1)
                .Select(group => new OperationalMetricsProjection
                {
                    ValorInventarioProductos = group.Sum(x => x.PrecioProducto * x.CantidadProducto),
                    TotalProductosInventario = group.Count()
                })
                .SingleOrDefaultAsync(cancellationToken)
                ?? new OperationalMetricsProjection();

            inventario.CantidadClientes = cantidadClientes;
            inventario.CantidadCitasMes = cantidadCitasMes;

            return inventario;
        }

        private async Task<Dictionary<int, decimal>> GetIngresosPorMesAsync(
            DateTime yearStart,
            DateTime yearEnd,
            CancellationToken cancellationToken)
        {
            var rows = await _context.Cobros
                .AsNoTracking()
                .Where(c => c.FechaCobro >= yearStart && c.FechaCobro < yearEnd)
                .GroupBy(c => c.FechaCobro.Month)
                .Select(group => new MonthAmountProjection
                {
                    Month = group.Key,
                    Amount = group.Sum(x => x.Monto)
                })
                .ToListAsync(cancellationToken);

            return rows.ToDictionary(x => x.Month, x => x.Amount);
        }

        private async Task<Dictionary<int, EgresoMonthAggregateProjection>> GetEgresosPorMesAsync(
            DateTime yearStart,
            DateTime yearEnd,
            CancellationToken cancellationToken)
        {
            var rows = await _context.Egresos
                .AsNoTracking()
                .Where(e => e.FechaEgreso >= yearStart && e.FechaEgreso < yearEnd)
                .GroupBy(e => e.FechaEgreso.Month)
                .Select(group => new EgresoMonthAggregateProjection
                {
                    Month = group.Key,
                    TotalEgresos = group.Sum(x => x.Monto)
                })
                .ToListAsync(cancellationToken);

            return rows.ToDictionary(x => x.Month, x => x);
        }

        private async Task<Dictionary<int, decimal>> GetPagosFuncionariosHistoricosCajaPorMesAsync(
            DateTime yearStart,
            DateTime yearEnd,
            CancellationToken cancellationToken)
        {
            var rows = await _context.Egresos
                .AsNoTracking()
                .Where(e => e.FechaEgreso >= yearStart && e.FechaEgreso < yearEnd)
                // Identidad estructural, no nombre: ver SystemCategoryCodes.
                .Where(e => e.Categoria != null &&
                            (e.Categoria.SystemCode != null
                                ? e.Categoria.SystemCode == SystemCategoryCodes.EmployeeSettlement
                                : e.Categoria.Nombre == SystemCategoryCodes.NombreLegacyEmployeeSettlement))
                .Where(e => !_context.LiquidacionesSemanales.Any(l => l.EgresoId == e.IdEgreso))
                .GroupBy(e => e.FechaEgreso.Month)
                .Select(group => new MonthAmountProjection
                {
                    Month = group.Key,
                    Amount = group.Sum(x => x.Monto)
                })
                .ToListAsync(cancellationToken);

            return rows.ToDictionary(x => x.Month, x => x.Amount);
        }

        private async Task<Dictionary<int, decimal>> GetPagosFuncionariosCajaPorLiquidacionPorMesAsync(
            DateTime yearStart,
            DateTime yearEnd,
            CancellationToken cancellationToken)
        {
            var rows = await _context.LiquidacionesSemanales
                .AsNoTracking()
                .Where(l => l.FechaPago >= yearStart && l.FechaPago < yearEnd)
                .GroupBy(l => l.FechaPago.Month)
                .Select(group => new MonthAmountProjection
                {
                    Month = group.Key,
                    Amount = group.Sum(x => x.MontoTotal)
                })
                .ToListAsync(cancellationToken);

            return rows.ToDictionary(x => x.Month, x => x.Amount);
        }

        private sealed class DashboardPeriodSelection
        {
            public int Month { get; init; }
            public int Year { get; init; }
            public DateTime MonthStart { get; init; }
            public DateTime MonthEnd { get; init; }
            public DateTime YearStart { get; init; }
            public DateTime YearEnd { get; init; }

            public static DashboardPeriodSelection Resolve(int? mes, int? anio, DateTime today)
            {
                var year = anio ?? today.Year;
                var month = mes ?? today.Month;
                var monthStart = new DateTime(year, month, 1);

                return new DashboardPeriodSelection
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

        private sealed class CobroMetricsProjection
        {
            public decimal TotalServicios { get; init; }
            public decimal TotalProductos { get; init; }
            public decimal TotalGenerado { get; init; }
            public decimal IngresosEfectivo { get; init; }
            public decimal IngresosSinpe { get; init; }
            public decimal IngresosTarjeta { get; init; }
        }

        private sealed class OperationalMetricsProjection
        {
            public int CantidadClientes { get; set; }
            public int CantidadCitasMes { get; set; }
            public decimal ValorInventarioProductos { get; init; }
            public int TotalProductosInventario { get; init; }
        }

        private sealed class MonthAmountProjection
        {
            public int Month { get; init; }
            public decimal Amount { get; init; }
        }

        private sealed class EgresoMonthAggregateProjection
        {
            public static EgresoMonthAggregateProjection Empty { get; } = new();

            public int Month { get; init; }
            public decimal TotalEgresos { get; init; }
        }

    }
}
