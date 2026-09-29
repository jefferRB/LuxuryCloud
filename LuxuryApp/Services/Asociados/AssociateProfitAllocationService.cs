using LuxuryApp.Models.Asociados;
using LuxuryApp.Models.Fiscal;
using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Services.Inversionistas;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Asociados
{
    /// <summary>
    /// Implementación del KPI de participación.
    /// Ver <see cref="IAssociateProfitAllocationService"/> para la regla de fuente única.
    /// </summary>
    public sealed class AssociateProfitAllocationService : IAssociateProfitAllocationService
    {
        private readonly ApplicationDbContext _context;
        private readonly IPeriodProfitCalculationService _calculationService;
        private readonly LuxuryApp.Services.BusinessTime.IBusinessDateTimeProvider _businessDateTimeProvider;

        public AssociateProfitAllocationService(
            ApplicationDbContext context,
            IPeriodProfitCalculationService calculationService,
            LuxuryApp.Services.BusinessTime.IBusinessDateTimeProvider businessDateTimeProvider)
        {
            _context = context;
            _calculationService = calculationService;
            _businessDateTimeProvider = businessDateTimeProvider;
        }

        public async Task<AssociateAllocationKpiViewModel?> BuildMonthlyKpiAsync(
            int mes,
            int anio,
            CancellationToken cancellationToken = default)
        {
            if (mes < 1 || mes > 12 || anio < 1)
            {
                return null;
            }

            var inicio = new DateOnly(anio, mes, 1);
            var fin = new DateOnly(anio, mes, DateTime.DaysInMonth(anio, mes));

            // Consulta barata primero: si nadie participa en el periodo, el KPI no existe y NO se
            // ejecuta el cálculo pesado de la ganancia. El dashboard se abre muchas veces al día.
            var participaciones = await ResolverParticipacionesVigentesAsync(inicio, fin, cancellationToken);

            if (participaciones.Count == 0)
            {
                return null;
            }

            var porcentajeTotal = participaciones.Sum(fila => fila.Porcentaje);
            if (porcentajeTotal <= 0m)
            {
                return null;
            }

            // MISMA ganancia que el titular del Dashboard: este KPI se muestra en la misma pantalla,
            // así que no puede repartir sobre un número distinto del que el dueño está viendo.
            var policy = InvestorProfitPolicy.CreateForBusinessResult();
            var desglose = await _calculationService.CalculateAsync(inicio, fin, policy, cancellationToken);

            // Ganancia del negocio en el mes calendario, tal cual la devuelve el motor único.
            // Los ajustes de un estado de cuenta y las pérdidas arrastradas pertenecen a ese estado
            // concreto, no a la foto del mes; por eso no entran en el KPI.
            var distribuible = desglose.GananciaDistribuible;

            // En pérdida no se reparte nada: mostrar una "participación negativa" sería inventar
            // una deuda del asociado que el módulo no contempla.
            var baseReparto = distribuible > 0m ? distribuible : 0m;
            var monto = FiscalMath.Redondear(baseReparto * porcentajeTotal / 100m);

            // El Dashboard habla del MES CALENDARIO; el estado de cuenta habla del periodo
            // contractual. Si no coinciden, el KPI se presenta como estimación y ofrece la fecha
            // real del próximo corte, para que nadie lea este monto como "lo que voy a pagar".
            var hoy = DateOnly.FromDateTime(_businessDateTimeProvider.Today());
            var cortes = participaciones
                .Select(fila => InvestorSettlementPeriodResolver.NextCutoff(fila.Acuerdo, hoy))
                .Distinct()
                .ToArray();

            var difiere = participaciones.Any(fila =>
            {
                var periodo = InvestorSettlementPeriodResolver.Resolve(fila.Acuerdo, fin);
                return periodo.Inicio != inicio || periodo.Fin != fin;
            });

            return new AssociateAllocationKpiViewModel
            {
                GananciaDistribuible = distribuible,
                ParticipacionPorcentaje = porcentajeTotal,
                ParticipacionMonto = monto,
                CantidadAsociados = participaciones.Count,
                PeriodoEtiqueta = InvestorPeriodCalculator.BuildEtiqueta(
                    InvestorPayoutFrequency.Mensual,
                    inicio,
                    fin),
                // Una sola fecha común, o ninguna: si cada asociado corta un día distinto, mostrar
                // "el" próximo corte sería inventar un dato.
                ProximoCorte = cortes.Length == 1 ? cortes[0] : null,
                PeriodoDifiereDelCorte = difiere
            };
        }

        /// <summary>
        /// Porcentaje vigente por inversionista dentro del periodo. Si un inversionista cambió de
        /// porcentaje a mitad de mes (solo posible con frecuencias más cortas), gana la versión
        /// vigente al CIERRE del periodo: es la que explica el estado de cuenta del mes.
        /// </summary>
        private async Task<IReadOnlyList<(int InvestorId, decimal Porcentaje, InvestorAgreement Acuerdo)>>
            ResolverParticipacionesVigentesAsync(
                DateOnly inicio,
                DateOnly fin,
                CancellationToken cancellationToken)
        {
            var acuerdos = await _context.InvestorAgreements
                .AsNoTracking()
                .Where(agreement => agreement.Activo)
                .Where(agreement => agreement.Investor != null && agreement.Investor.Activo)
                .Where(agreement => agreement.EffectiveFrom <= fin &&
                                    (agreement.EffectiveTo == null || agreement.EffectiveTo >= inicio))
                .ToListAsync(cancellationToken);

            return acuerdos
                .GroupBy(agreement => agreement.InvestorId)
                .Select(grupo =>
                {
                    var elegido = grupo
                        .OrderByDescending(agreement => agreement.EffectiveFrom)
                        .ThenByDescending(agreement => agreement.Id)
                        .First();

                    return (grupo.Key, elegido.ParticipacionPorcentaje, elegido);
                })
                .ToArray();
        }
    }
}
