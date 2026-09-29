using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Fiscal;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.Funcionarios;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Finanzas
{
    /// <summary>
    /// Calcula la ganancia del negocio para un rango REUTILIZANDO los servicios que ya alimentan
    /// las pantallas. No se duplica ni una fórmula fiscal:
    ///
    /// <list type="bullet">
    ///   <item>Ingresos e IVA salen de <see cref="ILiquidacionSemanalService"/>, que aplica el motor
    ///   fiscal (<c>ITaxCalculationService</c> + <c>ITenantFiscalConfigService</c>) línea por línea.
    ///   Por eso un servicio exento de IVA no se "des-IVA-iza" con una división plana.</item>
    ///   <item>Las liquidaciones del equipo salen del mismo resumen, sin recalcular comisiones.</item>
    ///   <item>Los gastos se leen de Egresos con las exclusiones estructurales del dominio.</item>
    /// </list>
    ///
    /// <para>Redondeo: <see cref="FiscalMath.Redondear"/> (2 decimales, half-even), igual que el
    /// resto de la aplicación.</para>
    ///
    /// <para>
    /// Este servicio nació dentro del módulo de inversionistas. Se promovió a Finanzas cuando el
    /// Dashboard pasó a consumirlo: antes el Dashboard tenía su propia aritmética (IVA plano
    /// Total/1.13, liquidaciones por lo pagado y sin excluir la categoría de distribución), así que
    /// los dos números podían diferir de verdad para el mismo mes.
    /// </para>
    /// </summary>
    public sealed class PeriodProfitCalculationService : IPeriodProfitCalculationService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILiquidacionSemanalService _liquidacionService;

        public PeriodProfitCalculationService(
            ApplicationDbContext context,
            ILiquidacionSemanalService liquidacionService)
        {
            _context = context;
            _liquidacionService = liquidacionService;
        }

        public async Task<PeriodProfitBreakdown> CalculateAsync(
            DateOnly periodoInicio,
            DateOnly periodoFin,
            InvestorProfitPolicy policy,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(policy);

            if (periodoFin < periodoInicio)
            {
                throw new ArgumentException("El periodo indicado no es válido.", nameof(periodoFin));
            }

            var inicio = periodoInicio.ToDateTime(TimeOnly.MinValue);
            var fin = periodoFin.ToDateTime(TimeOnly.MinValue);

            // Ingresos + IVA + liquidaciones: una sola pasada del servicio de liquidación, que
            // opera sobre rangos arbitrarios y aplica el motor fiscal por línea.
            var resumen = await _liquidacionService.ObtenerResumenSemanaAsync(inicio, fin, cancellationToken);

            var totalCobrado = FiscalMath.Redondear(resumen.TotalGeneradoGeneral);

            // Sin exclusión de IVA la base es el total cobrado tal cual (caso raro, pero configurable).
            var ingresosNetos = policy.ExcluirIva
                ? FiscalMath.Redondear(resumen.TotalBaseVentaSinIvaGeneral)
                : totalCobrado;

            var ivaCobrado = policy.ExcluirIva
                ? FiscalMath.Redondear(totalCobrado - ingresosNetos)
                : 0m;

            var liquidaciones = 0m;
            if (policy.IncluirLiquidaciones)
            {
                liquidaciones = policy.BaseLiquidaciones == InvestorSettlementBasis.Pagado
                    ? FiscalMath.Redondear(resumen.TotalPagadoAplicadoGeneral)
                    : FiscalMath.Redondear(resumen.TotalAPagarColaboradoresGeneral);
            }

            var gastos = await CalcularGastosAsync(periodoInicio, periodoFin, policy, cancellationToken);

            // Hoy no existen ajustes a nivel de negocio; ver PeriodProfitBreakdown.AjustesAutorizados.
            const decimal ajustesAutorizados = 0m;

            var distribuible = FiscalMath.Redondear(
                ingresosNetos - gastos.Total - liquidaciones + ajustesAutorizados);

            return new PeriodProfitBreakdown
            {
                PeriodoInicio = periodoInicio,
                PeriodoFin = periodoFin,
                TotalCobrado = totalCobrado,
                IvaCobrado = ivaCobrado,
                IngresosNetos = ingresosNetos,
                GastosOperativos = gastos.Total,
                LiquidacionesEquipo = liquidaciones,
                AjustesAutorizados = ajustesAutorizados,
                GananciaDistribuible = distribuible,
                PoliticaVersion = policy.BuildVersionDescription(),
                GastosPorCategoria = gastos.Detalle
            };
        }

        public async Task<IReadOnlyDictionary<int, PeriodProfitBreakdown>> CalculateMonthlyAsync(
            int anio,
            InvestorProfitPolicy policy,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(policy);

            var resultado = new Dictionary<int, PeriodProfitBreakdown>(12);

            for (var mes = 1; mes <= 12; mes++)
            {
                var inicio = new DateOnly(anio, mes, 1);
                var fin = new DateOnly(anio, mes, DateTime.DaysInMonth(anio, mes));

                resultado[mes] = await CalculateAsync(inicio, fin, policy, cancellationToken);
            }

            return resultado;
        }

        private async Task<(decimal Total, IReadOnlyList<PeriodExpenseCategoryBreakdown> Detalle)> CalcularGastosAsync(
            DateOnly periodoInicio,
            DateOnly periodoFin,
            InvestorProfitPolicy policy,
            CancellationToken cancellationToken)
        {
            var desde = periodoInicio.ToDateTime(TimeOnly.MinValue);
            var hastaExclusive = periodoFin.AddDays(1).ToDateTime(TimeOnly.MinValue);

            var filas = await _context.Egresos
                .AsNoTracking()
                .Where(egreso => egreso.FechaEgreso >= desde && egreso.FechaEgreso < hastaExclusive)
                .GroupBy(egreso => new
                {
                    egreso.CategoriaId,
                    Nombre = egreso.Categoria != null ? egreso.Categoria.Nombre : null,
                    SystemCode = egreso.Categoria != null ? egreso.Categoria.SystemCode : null
                })
                .Select(group => new
                {
                    group.Key.CategoriaId,
                    group.Key.Nombre,
                    group.Key.SystemCode,
                    Monto = group.Sum(egreso => egreso.Monto)
                })
                .ToListAsync(cancellationToken);

            var seleccionadas = policy.CategoriasSeleccionadas
                .Select(link => link.CategoriaId)
                .ToHashSet();

            var detalle = new List<PeriodExpenseCategoryBreakdown>(filas.Count);
            var total = 0m;

            foreach (var fila in filas.OrderBy(row => row.Nombre ?? string.Empty, StringComparer.CurrentCultureIgnoreCase))
            {
                var nombre = string.IsNullOrWhiteSpace(fila.Nombre) ? "Sin categoría" : fila.Nombre!;
                var motivo = ResolverMotivoExclusion(fila.CategoriaId, nombre, fila.SystemCode, policy, seleccionadas);
                var incluido = motivo is null;
                var monto = FiscalMath.Redondear(fila.Monto);

                if (incluido)
                {
                    total += monto;
                }

                detalle.Add(new PeriodExpenseCategoryBreakdown(
                    fila.CategoriaId,
                    nombre,
                    monto,
                    incluido,
                    motivo));
            }

            return (FiscalMath.Redondear(total), detalle);
        }

        /// <summary>
        /// Devuelve el motivo por el que una categoría NO cuenta como gasto elegible, o null si sí
        /// cuenta. Las dos exclusiones estructurales son obligatorias y no dependen de la configuración.
        /// </summary>
        private static string? ResolverMotivoExclusion(
            int categoriaId,
            string nombre,
            string? systemCode,
            InvestorProfitPolicy policy,
            IReadOnlySet<int> seleccionadas)
        {
            // Las dos exclusiones estructurales se deciden por IDENTIDAD (SystemCode), no por el
            // nombre visible: renombrar la etiqueta no puede cambiar la ganancia. El nombre solo
            // decide en categorías que todavía no tienen código (ver SystemCategoryCodes).

            // 1) Pago a colaboradores: ya se resta como "Liquidaciones". Contarlo también como gasto
            //    lo restaría dos veces.
            if (SystemCategoryCodes.EsLiquidacionDeColaboradores(systemCode, nombre))
            {
                return "Los pagos a colaboradores ya se restan en la línea de liquidaciones.";
            }

            // 2) Distribución a inversionistas: si contara como gasto, pagarle al inversionista
            //    reduciría la ganancia distribuible y con ella su propia participación (recursividad).
            if (SystemCategoryCodes.EsDistribucionAInversionistas(systemCode, nombre))
            {
                return "Los pagos a inversionistas no reducen la ganancia distribuible.";
            }

            return policy.ModoCategoriasGasto switch
            {
                InvestorExpenseCategoryMode.SoloSeleccionadas when !seleccionadas.Contains(categoriaId) =>
                    "La categoría no está marcada como elegible.",
                InvestorExpenseCategoryMode.TodasExceptoSeleccionadas when seleccionadas.Contains(categoriaId) =>
                    "La categoría está excluida por configuración.",
                _ => null
            };
        }
    }
}
