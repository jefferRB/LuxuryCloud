using LuxuryApp.Models.Inversionistas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Inversionistas
{
    /// <summary>Constante de auditoría: identifica los cierres disparados por el worker.</summary>
    public static class InvestorStatementTriggers
    {
        public const string Scheduler = "system:investor-statement-scheduler";
    }

    /// <summary>
    /// Cierre automático de estados de cuenta.
    ///
    /// <para>
    /// <b>Resiliencia (la razón por la que NO existe un <c>if (hoy.Day == diaCorte)</c>):</b>
    /// preguntar por el día exacto pierde el corte si el worker estuvo caído, si el proceso se
    /// reinició o si el negocio quedó sin acceso ese día. Acá se parte del último estado que
    /// EXISTE y se avanza período por período hasta hoy, generando todos los faltantes en orden.
    /// Si el último estado es de mayo y ya cerraron junio, julio y agosto, se generan los tres.
    /// </para>
    ///
    /// <para>
    /// <b>Nunca cierra al inicio del día de corte.</b> Un período se considera cerrado cuando su
    /// último día YA quedó atrás en la hora local del negocio (más los días de espera
    /// configurados). El día 20, con corte 20, todavía está abierto.
    /// </para>
    ///
    /// <para>
    /// <b>Generar ≠ enviar.</b> El estado se genera y se congela siempre; el correo sale solo si el
    /// negocio o ese acuerdo lo pidieron y el interruptor global de envíos está activo. Un correo
    /// que falla no deshace ni ensucia el estado ya emitido.
    /// </para>
    ///
    /// <para>
    /// La idempotencia real vive en el índice único filtrado
    /// <c>(TenantId, InvestorId, PeriodoInicio, PeriodoFin) WHERE Estado &lt;&gt; Voided</c>; acá se
    /// consulta antes para no pelear con la base en el caso normal.
    /// </para>
    /// </summary>
    public sealed class InvestorStatementScheduler : IInvestorStatementScheduler
    {
        /// <summary>Tope duro de vueltas del avance de períodos: red contra datos inconsistentes.</summary>
        private const int MaxIteraciones = 240;

        private readonly ApplicationDbContext _context;
        private readonly IInvestorService _investorService;
        private readonly IInvestorStatementService _statementService;
        private readonly IInvestorStatementEmailService _emailService;
        private readonly IOptionsMonitor<InvestorStatementSchedulerOptions> _options;
        private readonly ILogger<InvestorStatementScheduler> _logger;

        public InvestorStatementScheduler(
            ApplicationDbContext context,
            IInvestorService investorService,
            IInvestorStatementService statementService,
            IInvestorStatementEmailService emailService,
            IOptionsMonitor<InvestorStatementSchedulerOptions> options,
            ILogger<InvestorStatementScheduler> logger)
        {
            _context = context;
            _investorService = investorService;
            _statementService = statementService;
            _emailService = emailService;
            _options = options;
            _logger = logger;
        }

        public async Task<InvestorStatementScheduleResult> ProcessTenantAsync(
            Guid tenantId,
            DateTime nowLocal,
            CancellationToken cancellationToken = default)
        {
            var options = _options.CurrentValue;

            // Interruptor maestro: sin él no se genera nada, sin importar la configuración del tenant.
            if (!options.SchedulerEnabled)
            {
                return InvestorStatementScheduleResult.Nada(InvestorStatementScheduleOutcome.SchedulerDisabled);
            }

            var policy = await _investorService.GetPolicyAsync(cancellationToken);

            if (!policy.GeneracionAutomatica)
            {
                return InvestorStatementScheduleResult.Nada(InvestorStatementScheduleOutcome.NotEnabled);
            }

            if (nowLocal.Hour < policy.HoraGeneracion)
            {
                return InvestorStatementScheduleResult.Nada(InvestorStatementScheduleOutcome.NotDue);
            }

            var hoy = DateOnly.FromDateTime(nowLocal.Date);

            // Solo inversionistas activos: uno inactivo dejó de participar y sus acuerdos ya
            // deberían estar cerrados. Sus estados históricos se conservan intactos.
            var inversionistas = await _context.TenantInvestors
                .AsNoTracking()
                .Where(investor => investor.Activo)
                .OrderBy(investor => investor.Id)
                .Select(investor => new { investor.Id, investor.Nombre })
                .ToListAsync(cancellationToken);

            var generados = 0;
            var enviados = 0;
            var enviosFallidos = 0;
            var fallidos = 0;

            foreach (var inversionista in inversionistas)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var resultado = await ProcessInvestorAsync(
                        inversionista.Id,
                        policy,
                        hoy,
                        options,
                        cancellationToken);

                    generados += resultado.Generados;
                    enviados += resultado.Enviados;
                    enviosFallidos += resultado.EnviosFallidos;
                }
                catch (Exception ex)
                {
                    // Un inversionista que falle no puede frenar a los demás.
                    fallidos++;
                    _logger.LogError(
                        ex,
                        "Error cerrando estados de cuenta del inversionista {InvestorId} ({Nombre}) del tenant {TenantId}.",
                        inversionista.Id,
                        inversionista.Nombre,
                        tenantId);
                }
            }

            var outcome = (generados, fallidos) switch
            {
                (0, 0) => InvestorStatementScheduleOutcome.NothingPending,
                (0, _) => InvestorStatementScheduleOutcome.Failed,
                (_, 0) => InvestorStatementScheduleOutcome.Generated,
                _ => InvestorStatementScheduleOutcome.PartiallyGenerated
            };

            return new InvestorStatementScheduleResult(outcome, generados, enviados, enviosFallidos, fallidos);
        }

        /// <summary>
        /// Avanza por los períodos de UN inversionista desde donde quedó el último estado y genera
        /// todos los que ya cerraron.
        /// </summary>
        private async Task<(int Generados, int Enviados, int EnviosFallidos)> ProcessInvestorAsync(
            int investorId,
            InvestorProfitPolicy policy,
            DateOnly hoy,
            InvestorStatementSchedulerOptions options,
            CancellationToken cancellationToken)
        {
            var acuerdos = await _context.InvestorAgreements
                .AsNoTracking()
                .Where(agreement => agreement.InvestorId == investorId)
                .ToListAsync(cancellationToken);

            if (acuerdos.Count == 0)
            {
                return (0, 0, 0);
            }

            var primerInicio = acuerdos.Min(agreement => agreement.EffectiveFrom);

            // Punto de partida: el día siguiente al último período que YA tiene estado (aunque sea
            // borrador: ese período ya está tomado). Sin estados, el arranque del primer acuerdo.
            var ultimoFin = await _context.InvestorStatements
                .AsNoTracking()
                .Where(statement => statement.InvestorId == investorId &&
                                    statement.Estado != InvestorStatementStatus.Voided)
                .MaxAsync(statement => (DateOnly?)statement.PeriodoFin, cancellationToken);

            var cursor = ultimoFin.HasValue ? ultimoFin.Value.AddDays(1) : primerInicio;
            if (cursor < primerInicio)
            {
                cursor = primerInicio;
            }

            var maxPeriodos = Math.Clamp(options.MaxPeriodosPorInversionista, 1, 60);
            var generados = 0;
            var enviados = 0;
            var enviosFallidos = 0;

            for (var iteracion = 0; iteracion < MaxIteraciones && generados < maxPeriodos; iteracion++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (cursor > hoy)
                {
                    break;
                }

                var acuerdo = ResolveVigente(acuerdos, cursor);

                if (acuerdo is null)
                {
                    // Hueco sin acuerdo (participación terminada y retomada después). Se salta al
                    // arranque del siguiente acuerdo en vez de inventar un período.
                    var siguiente = acuerdos
                        .Where(current => current.EffectiveFrom > cursor)
                        .OrderBy(current => current.EffectiveFrom)
                        .FirstOrDefault();

                    if (siguiente is null)
                    {
                        break;
                    }

                    cursor = siguiente.EffectiveFrom;
                    continue;
                }

                var periodo = InvestorSettlementPeriodResolver.Resolve(acuerdo, cursor);

                if (periodo.Inicio < cursor)
                {
                    // El cursor cayó a mitad de un período (dato viejo o corte cambiado fuera del
                    // versionado). Generar acá pisaría un rango ya cubierto: se avanza al siguiente.
                    _logger.LogWarning(
                        "El inversionista {InvestorId} tiene un período desalineado en {Cursor:yyyy-MM-dd}; se salta al siguiente corte.",
                        investorId,
                        cursor);

                    cursor = periodo.Fin.AddDays(1);
                    continue;
                }

                // ¿Cerró de verdad? El día de corte pertenece al período que cierra, así que el
                // período recién está cerrado cuando ese día quedó atrás. Después se respetan los
                // días de espera configurados.
                var cerrado = periodo.Fin < hoy;
                var listoParaGenerar = cerrado && hoy >= periodo.Fin.AddDays(policy.DiasEsperaGeneracion);

                if (!listoParaGenerar)
                {
                    break;
                }

                var yaExiste = await _context.InvestorStatements
                    .AsNoTracking()
                    .AnyAsync(
                        statement => statement.InvestorId == investorId &&
                                     statement.PeriodoInicio == periodo.Inicio &&
                                     statement.PeriodoFin == periodo.Fin &&
                                     statement.Estado != InvestorStatementStatus.Voided,
                        cancellationToken);

                if (!yaExiste)
                {
                    var statementId = await GenerarAsync(investorId, periodo, cancellationToken);

                    if (statementId.HasValue)
                    {
                        generados++;

                        var (envio, fallo) = await EnviarSiCorrespondeAsync(
                            statementId.Value,
                            policy,
                            acuerdo,
                            options,
                            cancellationToken);

                        enviados += envio;
                        enviosFallidos += fallo;
                    }
                }

                cursor = periodo.Fin.AddDays(1);
            }

            return (generados, enviados, enviosFallidos);
        }

        /// <summary>
        /// Genera y congela el estado en un solo paso: el período ya cerró, así que dejarlo en
        /// borrador lo expondría a recalcularse con datos posteriores y dejaría de ser un snapshot.
        /// </summary>
        private async Task<int?> GenerarAsync(
            int investorId,
            InvestorPeriod periodo,
            CancellationToken cancellationToken)
        {
            int statementId;

            try
            {
                statementId = await _statementService.GenerateDraftAsync(
                    investorId,
                    periodo.Inicio,
                    InvestorStatementTriggers.Scheduler,
                    cancellationToken);
            }
            catch (InvestorValidationException ex)
            {
                _logger.LogWarning(
                    "No se generó el estado del inversionista {InvestorId} para {Inicio:yyyy-MM-dd}..{Fin:yyyy-MM-dd}: {Motivo}",
                    investorId,
                    periodo.Inicio,
                    periodo.Fin,
                    ex.Message);

                return null;
            }

            try
            {
                await _statementService.FinalizeAsync(
                    statementId,
                    InvestorStatementTriggers.Scheduler,
                    cancellationToken);
            }
            catch (InvestorValidationException ex)
            {
                // Alguien lo finalizó (o lo anuló) entre la generación y el congelado. El estado
                // existe igual: no se reintenta ni se rompe la pasada.
                _logger.LogWarning(
                    "El estado {StatementId} no se pudo finalizar automáticamente: {Motivo}",
                    statementId,
                    ex.Message);
            }

            _logger.LogInformation(
                "Estado de cuenta {StatementId} generado automáticamente para el inversionista {InvestorId} ({Inicio:yyyy-MM-dd} → {Fin:yyyy-MM-dd}).",
                statementId,
                investorId,
                periodo.Inicio,
                periodo.Fin);

            return statementId;
        }

        /// <summary>
        /// Envío opcional. Es un paso APARTE de la generación: su resultado no cambia el estado
        /// generado ni se reintenta acá (para eso está el reenvío manual).
        /// </summary>
        private async Task<(int Enviados, int Fallidos)> EnviarSiCorrespondeAsync(
            int statementId,
            InvestorProfitPolicy policy,
            InvestorAgreement acuerdo,
            InvestorStatementSchedulerOptions options,
            CancellationToken cancellationToken)
        {
            if (!options.SendEmails)
            {
                return (0, 0);
            }

            // El negocio puede activarlo para todos, o el acuerdo para ese inversionista.
            if (!policy.EnvioAutomatico && !acuerdo.EnvioAutomatico)
            {
                return (0, 0);
            }

            try
            {
                var resultado = await _emailService.SendAsync(
                    statementId,
                    InvestorStatementTriggers.Scheduler,
                    cancellationToken);

                return resultado.Outcome == InvestorStatementSendOutcome.Failed
                    ? (0, 1)
                    : (resultado.Success ? 1 : 0, 0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falló el envío automático del estado {StatementId}.", statementId);
                return (0, 1);
            }
        }

        /// <summary>
        /// Acuerdo que rige una fecha: activo, que la cubra, y ante empates el de arranque más
        /// reciente (misma regla que usa el módulo para "el acuerdo vigente").
        /// </summary>
        private static InvestorAgreement? ResolveVigente(
            IReadOnlyCollection<InvestorAgreement> acuerdos,
            DateOnly fecha) =>
            acuerdos
                .Where(agreement => agreement.CubreFecha(fecha))
                .OrderByDescending(agreement => agreement.EffectiveFrom)
                .ThenByDescending(agreement => agreement.Id)
                .FirstOrDefault();
    }
}
