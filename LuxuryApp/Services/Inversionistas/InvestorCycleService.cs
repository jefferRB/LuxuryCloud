using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Finanzas;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Inversionistas
{
    /// <summary>
    /// Arma la lectura operacional del inversionista respetando la separación del modelo mental:
    /// ciclo abierto (live), corte emitido (snapshot) y saldo (solo de cortes emitidos).
    ///
    /// <para>
    /// Reglas que sostiene esta clase y que no se negocian:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Último corte = estado realmente emitido.</b> Un borrador todavía puede cambiar de
    ///   monto: no es un corte y no entra en el saldo. Si no hay ninguno emitido no se muestra un
    ///   último corte ficticio derivado de <c>PreviousClosedPeriod</c>: se muestra el PRIMER corte
    ///   que va a existir.</item>
    ///   <item><b>El ciclo actual se calcula hasta <c>min(hoy, fin del período)</c>.</b> Nunca se
    ///   suman días futuros, aunque el rango contractual los contenga.</item>
    ///   <item><b>La estimación del ciclo jamás entra en el saldo pendiente.</b></item>
    /// </list>
    ///
    /// <para>
    /// El dinero sale de <see cref="IPeriodProfitCalculationService"/> (la única fórmula) y las
    /// reglas de pérdida/participación de <see cref="InvestorStatementService"/>. Acá no hay
    /// aritmética financiera propia.
    /// </para>
    /// </summary>
    public sealed class InvestorCycleService : IInvestorCycleService
    {
        /// <summary>Cortes que se muestran en el bloque "Cortes recientes" del detalle.</summary>
        private const int CortesRecientes = 5;

        private readonly ApplicationDbContext _context;
        private readonly IInvestorService _investorService;
        private readonly IInvestorStatementService _statementService;
        private readonly IPeriodProfitCalculationService _calculationService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;

        public InvestorCycleService(
            ApplicationDbContext context,
            IInvestorService investorService,
            IInvestorStatementService statementService,
            IPeriodProfitCalculationService calculationService,
            IBusinessDateTimeProvider businessDateTimeProvider)
        {
            _context = context;
            _investorService = investorService;
            _statementService = statementService;
            _calculationService = calculationService;
            _businessDateTimeProvider = businessDateTimeProvider;
        }

        public async Task<InvestorFinancialSummaryViewModel?> BuildSummaryAsync(
            int investorId,
            CancellationToken cancellationToken = default)
        {
            var investor = await _context.TenantInvestors
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == investorId, cancellationToken);

            if (investor is null)
            {
                return null;
            }

            var hoy = Today();
            var acuerdo = await _investorService.GetAgreementForDateAsync(investorId, hoy, cancellationToken);
            var policy = await _investorService.GetPolicyAsync(cancellationToken);

            var ultimo = await LoadUltimoCorteAsync(investorId, cancellationToken);
            var saldos = await LoadSaldosAsync(investorId, cancellationToken);
            var recientes = await LoadCortesRecientesAsync(investorId, cancellationToken);

            var ciclo = acuerdo is null
                ? null
                : await BuildCurrentCycleAsync(investor.Id, investor.Nombre, acuerdo, policy, hoy, cancellationToken);

            return new InvestorFinancialSummaryViewModel
            {
                InvestorId = investor.Id,
                InvestorNombre = investor.Nombre,
                PorcentajeVigente = acuerdo?.ParticipacionPorcentaje,
                CorteTexto = acuerdo is null
                    ? InvestorSettlementPeriodResolver.EtiquetaCorte(policy.FrecuenciaPorDefecto, null)
                    : InvestorSettlementPeriodResolver.EtiquetaCorte(acuerdo.Frecuencia, acuerdo.DiaCorte),
                UltimoCorte = ultimo,
                // Solo cuando NO hay cortes emitidos: es la respuesta honesta a "¿y el último corte?".
                PrimerCorte = ultimo is null ? ResolvePrimerCorte(acuerdo, hoy) : null,
                CicloActual = ciclo,
                SaldoPendienteTotal = saldos.Saldo,
                EstadosConSaldo = saldos.ConSaldo,
                EstadosEmitidos = saldos.Emitidos,
                Borradores = saldos.Borradores,
                GeneracionAutomatica = policy.GeneracionAutomatica,
                CortesRecientes = recientes
            };
        }

        public async Task<InvestorCurrentCycleViewModel?> BuildCurrentCycleAsync(
            int investorId,
            CancellationToken cancellationToken = default)
        {
            var investor = await _context.TenantInvestors
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == investorId, cancellationToken);

            if (investor is null)
            {
                return null;
            }

            var hoy = Today();
            var policy = await _investorService.GetPolicyAsync(cancellationToken);
            var acuerdo = await _investorService.GetAgreementForDateAsync(investorId, hoy, cancellationToken);

            if (acuerdo is null)
            {
                // Sin acuerdo vigente no hay ciclo: se devuelve el período que correspondería según
                // la configuración por defecto, con la participación en cero y la bandera abajo,
                // para que la pantalla explique el vacío en vez de romperse.
                var referencia = new InvestorAgreement
                {
                    Frecuencia = policy.FrecuenciaPorDefecto,
                    DiaCorte = null
                };

                var periodoSinAcuerdo = InvestorSettlementPeriodResolver.CurrentPeriod(referencia, hoy);

                return new InvestorCurrentCycleViewModel
                {
                    InvestorId = investor.Id,
                    InvestorNombre = investor.Nombre,
                    PeriodoInicio = periodoSinAcuerdo.Inicio,
                    PeriodoFin = periodoSinAcuerdo.Fin,
                    PeriodoEtiqueta = periodoSinAcuerdo.Etiqueta,
                    CalculadoAl = Min(hoy, periodoSinAcuerdo.Fin),
                    CorteTexto = InvestorSettlementPeriodResolver.EtiquetaCorte(policy.FrecuenciaPorDefecto, null),
                    TieneAcuerdoVigente = false
                };
            }

            return await BuildCurrentCycleAsync(investor.Id, investor.Nombre, acuerdo, policy, hoy, cancellationToken);
        }

        // ─────────────── Ciclo en curso ───────────────

        private async Task<InvestorCurrentCycleViewModel> BuildCurrentCycleAsync(
            int investorId,
            string investorNombre,
            InvestorAgreement acuerdo,
            InvestorProfitPolicy policy,
            DateOnly hoy,
            CancellationToken cancellationToken)
        {
            var periodo = InvestorSettlementPeriodResolver.CurrentPeriod(acuerdo, hoy);

            // Tope del cálculo: hoy. Sumar los días que faltan del período daría un "acumulado"
            // que incluye un futuro que todavía no ocurrió.
            var calculadoAl = Min(hoy, periodo.Fin);

            var breakdown = await _calculationService.CalculateAsync(
                periodo.Inicio,
                calculadoAl,
                policy,
                cancellationToken);

            var perdidaPrevia = await _statementService.GetCarryForwardLossAsync(
                investorId,
                periodo.Inicio,
                acuerdo,
                cancellationToken);

            var resultado = InvestorStatementService.ApplyProfitRules(
                breakdown.GananciaDistribuible,
                ajustesPositivos: 0m,
                ajustesNegativos: 0m,
                perdidaPrevia,
                acuerdo.TratamientoPerdidas);

            var porcentaje = acuerdo.ParticipacionPorcentaje;
            var participacion = InvestorStatementService.CalcularParticipacion(resultado.Distribuible, porcentaje);

            var existente = await _context.InvestorStatements
                .AsNoTracking()
                .Where(statement => statement.InvestorId == investorId &&
                                    statement.PeriodoInicio == periodo.Inicio &&
                                    statement.PeriodoFin == periodo.Fin &&
                                    statement.Estado != InvestorStatementStatus.Voided)
                .Select(statement => (int?)statement.Id)
                .FirstOrDefaultAsync(cancellationToken);

            return new InvestorCurrentCycleViewModel
            {
                InvestorId = investorId,
                InvestorNombre = investorNombre,
                PeriodoInicio = periodo.Inicio,
                PeriodoFin = periodo.Fin,
                PeriodoEtiqueta = periodo.Etiqueta,
                CalculadoAl = calculadoAl,
                CorteTexto = InvestorSettlementPeriodResolver.EtiquetaCorte(acuerdo.Frecuencia, acuerdo.DiaCorte),
                GananciaDistribuible = resultado.Distribuible,
                ParticipacionPorcentaje = porcentaje,
                ParticipacionEstimada = participacion,
                TieneAcuerdoVigente = true,
                EstadoExistenteId = existente,
                Desglose = new InvestorCalculationBreakdownViewModel
                {
                    InvestorNombre = investorNombre,
                    PeriodoInicio = periodo.Inicio,
                    // El desglose enlaza a Ingresos/Egresos con el rango REALMENTE calculado.
                    PeriodoFin = calculadoAl,
                    IngresosCobrados = breakdown.TotalCobrado,
                    IvaExcluido = breakdown.IvaCobrado,
                    IngresosNetos = breakdown.IngresosNetos,
                    GastosElegibles = breakdown.GastosOperativos,
                    Liquidaciones = breakdown.LiquidacionesEquipo,
                    PerdidaArrastrada = resultado.PerdidaAplicada,
                    PerdidaPendiente = resultado.PerdidaPendiente,
                    GananciaDistribuible = resultado.Distribuible,
                    ParticipacionPorcentaje = porcentaje,
                    ParticipacionCalculada = participacion,
                    PoliticaVersion = breakdown.PoliticaVersion,
                    GastosPorCategoria = breakdown.GastosPorCategoria
                        .Select(linea => new InvestorExpenseLineViewModel(
                            linea.CategoriaNombre,
                            linea.Monto,
                            linea.Incluido,
                            linea.MotivoExclusion))
                        .ToList()
                }
            };
        }

        // ─────────────── Cortes emitidos ───────────────

        /// <summary>
        /// Último estado EMITIDO (finalizado en adelante). Un borrador no cuenta: sus montos
        /// todavía pueden cambiar y no forman parte del saldo.
        /// </summary>
        private async Task<InvestorLastStatementViewModel?> LoadUltimoCorteAsync(
            int investorId,
            CancellationToken cancellationToken)
        {
            var statement = await _context.InvestorStatements
                .AsNoTracking()
                .Where(current => current.InvestorId == investorId &&
                                  current.Estado != InvestorStatementStatus.Voided &&
                                  current.Estado != InvestorStatementStatus.Draft)
                .OrderByDescending(current => current.PeriodoFin)
                .ThenByDescending(current => current.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (statement is null)
            {
                return null;
            }

            return new InvestorLastStatementViewModel
            {
                StatementId = statement.Id,
                FechaCorte = statement.FechaCorte,
                PeriodoInicio = statement.PeriodoInicio,
                PeriodoFin = statement.PeriodoFin,
                PeriodoEtiqueta = InvestorPeriodCalculator.BuildEtiqueta(
                    statement.Frecuencia,
                    statement.PeriodoInicio,
                    statement.PeriodoFin),
                GananciaDistribuible = statement.GananciaDistribuible,
                ParticipacionPorcentaje = statement.ParticipacionPorcentaje,
                ParticipacionCalculada = statement.ParticipacionCalculada,
                TotalPagado = statement.TotalPagado,
                SaldoPendiente = statement.SaldoPendiente,
                Estado = statement.Estado,
                EnviadoAtUtc = statement.EnviadoAtUtc
            };
        }

        private async Task<(decimal Saldo, int ConSaldo, int Emitidos, int Borradores)> LoadSaldosAsync(
            int investorId,
            CancellationToken cancellationToken)
        {
            var filas = await _context.InvestorStatements
                .AsNoTracking()
                .Where(statement => statement.InvestorId == investorId &&
                                    statement.Estado != InvestorStatementStatus.Voided)
                .Select(statement => new
                {
                    statement.Estado,
                    statement.SaldoPendiente
                })
                .ToListAsync(cancellationToken);

            // El saldo SOLO viene de estados emitidos. La estimación del ciclo en curso no es deuda.
            var emitidos = filas.Where(fila => fila.Estado != InvestorStatementStatus.Draft).ToList();

            return (
                emitidos.Sum(fila => fila.SaldoPendiente),
                emitidos.Count(fila => fila.SaldoPendiente > 0m),
                emitidos.Count,
                filas.Count(fila => fila.Estado == InvestorStatementStatus.Draft));
        }

        private async Task<IReadOnlyList<InvestorStatementListItemViewModel>> LoadCortesRecientesAsync(
            int investorId,
            CancellationToken cancellationToken)
        {
            var filas = await _context.InvestorStatements
                .AsNoTracking()
                .Include(statement => statement.Investor)
                .Where(statement => statement.InvestorId == investorId &&
                                    statement.Estado != InvestorStatementStatus.Voided)
                .OrderByDescending(statement => statement.PeriodoFin)
                .ThenByDescending(statement => statement.Id)
                .Take(CortesRecientes)
                .ToListAsync(cancellationToken);

            return filas
                .Select(statement => new InvestorStatementListItemViewModel
                {
                    Id = statement.Id,
                    InvestorId = statement.InvestorId,
                    InvestorNombre = statement.Investor?.Nombre ?? "—",
                    PeriodoInicio = statement.PeriodoInicio,
                    PeriodoFin = statement.PeriodoFin,
                    FechaCorte = statement.FechaCorte,
                    PeriodoEtiqueta = InvestorPeriodCalculator.BuildEtiqueta(
                        statement.Frecuencia,
                        statement.PeriodoInicio,
                        statement.PeriodoFin),
                    Estado = statement.Estado,
                    EstadoTexto = statement.EstadoTexto,
                    GananciaDistribuible = statement.GananciaDistribuible,
                    ParticipacionPorcentaje = statement.ParticipacionPorcentaje,
                    ParticipacionCalculada = statement.ParticipacionCalculada,
                    TotalPagado = statement.TotalPagado,
                    SaldoPendiente = statement.SaldoPendiente,
                    EnviadoAtUtc = statement.EnviadoAtUtc
                })
                .ToList();
        }

        // ─────────────── Helpers ───────────────

        /// <summary>
        /// Fecha del primer corte que se va a emitir. Se calcula sobre el período que contiene a
        /// hoy, pero NUNCA antes del arranque del acuerdo: un acuerdo vigente desde el 21/08 con
        /// corte 20 cierra por primera vez el 20/09, no el 20/08.
        /// </summary>
        private static DateOnly? ResolvePrimerCorte(InvestorAgreement? acuerdo, DateOnly hoy)
        {
            if (acuerdo is null)
            {
                return null;
            }

            var referencia = hoy < acuerdo.EffectiveFrom ? acuerdo.EffectiveFrom : hoy;
            return InvestorSettlementPeriodResolver.Resolve(acuerdo, referencia).Fin;
        }

        private static DateOnly Min(DateOnly left, DateOnly right) => left <= right ? left : right;

        private DateOnly Today() => DateOnly.FromDateTime(_businessDateTimeProvider.Today());
    }
}
