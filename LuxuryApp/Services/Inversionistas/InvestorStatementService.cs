using System.Data;
using System.Text.Json;
using LuxuryApp.Models.Fiscal;
using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.Finanzas;
using LuxuryApp.Models.Platform;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Platform;
using LuxuryApp.Services.Tenant;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Inversionistas
{
    /// <summary>
    /// Ciclo de vida del estado de cuenta del inversionista.
    ///
    /// <para>Invariantes:</para>
    /// <list type="number">
    ///   <item>Solo un borrador puede recalcularse o recibir ajustes.</item>
    ///   <item>Al finalizar, todos los montos quedan congelados; cambiar un cobro histórico no los mueve.</item>
    ///   <item>La generación es idempotente por (tenant, inversionista, periodo).</item>
    ///   <item>No se puede pagar más que el saldo pendiente salvo por una reversión explícita.</item>
    ///   <item>Los pagos al inversionista nunca vuelven a entrar en la fórmula.</item>
    /// </list>
    /// </summary>
    public sealed class InvestorStatementService : IInvestorStatementService
    {
        private readonly ApplicationDbContext _context;
        private readonly IInvestorService _investorService;
        private readonly IPeriodProfitCalculationService _calculationService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ITenantDisplayNameService _tenantDisplayNameService;
        private readonly IPlatformAuditService _auditService;
        private readonly ILogger<InvestorStatementService> _logger;

        public InvestorStatementService(
            ApplicationDbContext context,
            IInvestorService investorService,
            IPeriodProfitCalculationService calculationService,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ITenantDisplayNameService tenantDisplayNameService,
            IPlatformAuditService auditService,
            ILogger<InvestorStatementService> logger)
        {
            _context = context;
            _investorService = investorService;
            _calculationService = calculationService;
            _businessDateTimeProvider = businessDateTimeProvider;
            _tenantDisplayNameService = tenantDisplayNameService;
            _auditService = auditService;
            _logger = logger;
        }

        // ─────────────── Vista previa ───────────────

        public async Task<InvestorStatementPreviewViewModel> PreviewAsync(
            int investorId,
            DateOnly? referencia,
            CancellationToken cancellationToken = default)
        {
            var investor = await _context.TenantInvestors
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == investorId, cancellationToken)
                ?? throw new InvestorValidationException("El inversionista indicado no existe o no pertenece a este negocio.");

            var policy = await _investorService.GetPolicyAsync(cancellationToken);
            var hoy = Today();

            // El periodo lo define el ACUERDO que rige la fecha consultada: su frecuencia y su día
            // de corte. Nunca se calcula con la frecuencia de "hoy" sobre un periodo del pasado,
            // porque un cambio de corte movería las fechas de periodos ya cerrados.
            var acuerdoReferencia =
                await _investorService.GetAgreementForDateAsync(investorId, referencia ?? hoy, cancellationToken)
                ?? await _investorService.GetAgreementForDateAsync(investorId, hoy, cancellationToken)
                ?? AcuerdoPorDefecto(policy);

            var periodo = referencia.HasValue
                ? InvestorSettlementPeriodResolver.Resolve(acuerdoReferencia, referencia.Value)
                : InvestorSettlementPeriodResolver.PreviousClosedPeriod(acuerdoReferencia, hoy);

            var frecuencia = acuerdoReferencia.Frecuencia;

            var acuerdo = await _investorService.GetAgreementForDateAsync(investorId, periodo.Inicio, cancellationToken);

            var breakdown = await _calculationService.CalculateAsync(periodo.Inicio, periodo.Fin, policy, cancellationToken);
            var perdidaPrevia = await ResolvePerdidaArrastradaAsync(investorId, periodo.Inicio, acuerdo, cancellationToken);

            var resultado = ApplyProfitRules(
                breakdown.GananciaDistribuible,
                ajustesPositivos: 0m,
                ajustesNegativos: 0m,
                perdidaPrevia,
                acuerdo?.TratamientoPerdidas ?? policy.TratamientoPerdidasPorDefecto);

            var porcentaje = acuerdo?.ParticipacionPorcentaje ?? 0m;
            var participacion = CalcularParticipacion(resultado.Distribuible, porcentaje);

            var existente = await _context.InvestorStatements
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    statement => statement.InvestorId == investorId &&
                                 statement.PeriodoInicio == periodo.Inicio &&
                                 statement.PeriodoFin == periodo.Fin &&
                                 statement.Estado != InvestorStatementStatus.Voided,
                    cancellationToken);

            // Un periodo está CERRADO cuando su último día quedó atrás en hora local del negocio.
            // El propio día de corte todavía no cierra: se cierra cuando ese día terminó.
            var periodoCerrado = periodo.Fin < hoy;

            string? advertencia = null;
            if (acuerdo is null)
            {
                advertencia = "Este inversionista no tiene un acuerdo de participación vigente en el periodo seleccionado.";
            }
            else if (!periodoCerrado)
            {
                advertencia = "El período todavía está abierto: es un estimado y los montos pueden cambiar hasta el cierre.";
            }

            return new InvestorStatementPreviewViewModel
            {
                InvestorId = investor.Id,
                InvestorNombre = investor.Nombre,
                PeriodoInicio = periodo.Inicio,
                PeriodoFin = periodo.Fin,
                PeriodoEtiqueta = periodo.Etiqueta,
                Frecuencia = frecuencia,
                DiaCorte = acuerdo?.DiaCorte ?? acuerdoReferencia.DiaCorte,
                FechaCorte = periodo.Fin,
                CorteTexto = InvestorSettlementPeriodResolver.EtiquetaCorte(
                    frecuencia,
                    acuerdo?.DiaCorte ?? acuerdoReferencia.DiaCorte),
                TieneAcuerdoVigente = acuerdo is not null,
                PeriodoCerrado = periodoCerrado,
                EstadoExistenteId = existente?.Id,
                EstadoExistenteTexto = existente?.EstadoTexto,
                Advertencia = advertencia,
                Desglose = new InvestorCalculationBreakdownViewModel
                {
                    InvestorNombre = investor.Nombre,
                    PeriodoInicio = periodo.Inicio,
                    PeriodoFin = periodo.Fin,
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

        // ─────────────── Generación y recálculo ───────────────

        public async Task<int> GenerateDraftAsync(
            int investorId,
            DateOnly referencia,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            var investor = await _context.TenantInvestors
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == investorId, cancellationToken)
                ?? throw new InvestorValidationException("El inversionista indicado no existe o no pertenece a este negocio.");

            var policy = await _investorService.GetPolicyAsync(cancellationToken);

            // El periodo sale del acuerdo que rige la fecha de referencia (frecuencia + día de
            // corte), no de la frecuencia vigente hoy.
            var acuerdoReferencia =
                await _investorService.GetAgreementForDateAsync(investorId, referencia, cancellationToken)
                ?? await _investorService.GetAgreementForDateAsync(investorId, Today(), cancellationToken)
                ?? AcuerdoPorDefecto(policy);

            var periodo = InvestorSettlementPeriodResolver.Resolve(acuerdoReferencia, referencia);

            var acuerdo = await _investorService.GetAgreementForDateAsync(investorId, periodo.Inicio, cancellationToken)
                ?? throw new InvestorValidationException(
                    $"El inversionista no tiene un acuerdo de participación vigente en el periodo {periodo.Etiqueta}.");

            // Idempotencia (1/2): si ya hay un estado vivo para el periodo, se devuelve ese.
            var existente = await _context.InvestorStatements
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    statement => statement.InvestorId == investorId &&
                                 statement.PeriodoInicio == periodo.Inicio &&
                                 statement.PeriodoFin == periodo.Fin &&
                                 statement.Estado != InvestorStatementStatus.Voided,
                    cancellationToken);

            if (existente is not null)
            {
                return existente.Id;
            }

            var statement = new InvestorStatement
            {
                InvestorId = investorId,
                AgreementId = acuerdo.Id,
                PeriodoInicio = periodo.Inicio,
                PeriodoFin = periodo.Fin,
                Frecuencia = acuerdo.Frecuencia,
                // Se congelan junto al porcentaje: cambiar el corte después no reescribe este estado.
                DiaCorte = acuerdo.DiaCorte,
                FechaCorte = periodo.Fin,
                ParticipacionPorcentaje = acuerdo.ParticipacionPorcentaje,
                Estado = InvestorStatementStatus.Draft,
                GeneradoPorUserId = userId,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            await ApplyCalculationAsync(statement, acuerdo, policy, cancellationToken);

            _context.InvestorStatements.Add(statement);

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                // Idempotencia (2/2): dos generaciones concurrentes. El índice único filtrado
                // (TenantId, InvestorId, PeriodoInicio, PeriodoFin) WHERE Estado <> Voided garantiza
                // que solo una gane; la otra adopta el estado ya creado en vez de fallar.
                _logger.LogWarning(
                    ex,
                    "Generación concurrente del estado de cuenta del inversionista {InvestorId} para {Inicio:yyyy-MM-dd}.",
                    investorId,
                    periodo.Inicio);

                _context.Entry(statement).State = EntityState.Detached;

                var ganador = await _context.InvestorStatements
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        current => current.InvestorId == investorId &&
                                   current.PeriodoInicio == periodo.Inicio &&
                                   current.PeriodoFin == periodo.Fin &&
                                   current.Estado != InvestorStatementStatus.Voided,
                        cancellationToken);

                if (ganador is null)
                {
                    throw;
                }

                return ganador.Id;
            }

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.InvestorStatementGenerated,
                    EntityType = PlatformAuditEntityTypes.InvestorStatement,
                    EntityId = statement.Id.ToString(),
                    TenantId = statement.TenantId,
                    AfterJson = SerializeSnapshot(statement, investor.Nombre)
                },
                cancellationToken);

            return statement.Id;
        }

        public async Task RecalculateAsync(
            int statementId,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            var statement = await LoadTrackedAsync(statementId, cancellationToken);

            if (!statement.EsEditable)
            {
                throw new InvestorValidationException(
                    "Solo un borrador puede recalcularse. Un estado finalizado conserva sus valores congelados: usá un ajuste, una anulación o una reapertura.");
            }

            var policy = await _investorService.GetPolicyAsync(cancellationToken);
            var acuerdo = await _investorService.GetAgreementForDateAsync(
                statement.InvestorId,
                statement.PeriodoInicio,
                cancellationToken);

            if (acuerdo is not null)
            {
                statement.AgreementId = acuerdo.Id;
                statement.ParticipacionPorcentaje = acuerdo.ParticipacionPorcentaje;
                statement.Frecuencia = acuerdo.Frecuencia;
            }

            await ApplyCalculationAsync(statement, acuerdo, policy, cancellationToken);

            statement.GeneradoPorUserId = userId;
            statement.UpdatedAtUtc = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.InvestorStatementRecalculated,
                    EntityType = PlatformAuditEntityTypes.InvestorStatement,
                    EntityId = statement.Id.ToString(),
                    TenantId = statement.TenantId,
                    AfterJson = SerializeSnapshot(statement, null)
                },
                cancellationToken);
        }

        // ─────────────── Finalización / anulación / reapertura ───────────────

        public async Task FinalizeAsync(
            int statementId,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            var executionStrategy = _context.Database.CreateExecutionStrategy();
            InvestorStatement? finalizado = null;

            await executionStrategy.ExecuteAsync(async () =>
            {
                // Serializable + relectura dentro de la transacción: dos usuarios finalizando el
                // mismo estado a la vez no pueden congelarlo dos veces con números distintos.
                await using var transaction = await _context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);

                var statement = await _context.InvestorStatements
                    .FirstOrDefaultAsync(current => current.Id == statementId, cancellationToken)
                    ?? throw new InvestorValidationException("El estado de cuenta indicado no existe o no pertenece a este negocio.");

                if (statement.Estado == InvestorStatementStatus.Voided)
                {
                    throw new InvestorValidationException("El estado de cuenta está anulado y no puede finalizarse.");
                }

                if (statement.Estado != InvestorStatementStatus.Draft)
                {
                    throw new InvestorValidationException("El estado de cuenta ya fue finalizado por otra persona.");
                }

                statement.Estado = InvestorStatementStatus.Finalized;
                statement.FinalizadoAtUtc = DateTime.UtcNow;
                statement.FinalizadoPorUserId = userId;
                statement.UpdatedAtUtc = DateTime.UtcNow;

                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                finalizado = statement;
            });

            if (finalizado is not null)
            {
                await _auditService.TryLogAsync(
                    new PlatformAuditEntry
                    {
                        Action = PlatformAuditActions.InvestorStatementFinalized,
                        EntityType = PlatformAuditEntityTypes.InvestorStatement,
                        EntityId = finalizado.Id.ToString(),
                        TenantId = finalizado.TenantId,
                        AfterJson = SerializeSnapshot(finalizado, null)
                    },
                    cancellationToken);
            }
        }

        public async Task VoidAsync(
            int statementId,
            string motivo,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            var razon = NormalizeRequiredText(motivo, 500)
                ?? throw new InvestorValidationException("Explicá el motivo de la anulación.");

            var statement = await LoadTrackedAsync(statementId, cancellationToken);

            if (statement.EstaAnulado)
            {
                throw new InvestorValidationException("El estado de cuenta ya está anulado.");
            }

            if (statement.TotalPagado != 0m)
            {
                throw new InvestorValidationException(
                    "No se puede anular un estado con pagos registrados. Revertí primero los pagos con una corrección auditada.");
            }

            statement.Estado = InvestorStatementStatus.Voided;
            statement.AnuladoAtUtc = DateTime.UtcNow;
            statement.AnuladoPorUserId = userId;
            statement.MotivoAnulacion = razon;
            statement.UpdatedAtUtc = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.InvestorStatementVoided,
                    EntityType = PlatformAuditEntityTypes.InvestorStatement,
                    EntityId = statement.Id.ToString(),
                    TenantId = statement.TenantId,
                    Reason = razon,
                    AfterJson = SerializeSnapshot(statement, null)
                },
                cancellationToken);
        }

        public async Task ReopenAsync(
            int statementId,
            string motivo,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            var razon = NormalizeRequiredText(motivo, 500)
                ?? throw new InvestorValidationException("Explicá el motivo de la reapertura.");

            var statement = await LoadTrackedAsync(statementId, cancellationToken);

            if (statement.EstaAnulado)
            {
                throw new InvestorValidationException("Un estado anulado no puede reabrirse. Generá uno nuevo para ese periodo.");
            }

            if (statement.EsEditable)
            {
                throw new InvestorValidationException("El estado ya es un borrador editable.");
            }

            if (statement.TotalPagado != 0m)
            {
                throw new InvestorValidationException(
                    "No se puede reabrir un estado con pagos registrados. Revertí primero los pagos con una corrección auditada.");
            }

            var antes = SerializeSnapshot(statement, null);

            statement.Estado = InvestorStatementStatus.Draft;
            statement.FinalizadoAtUtc = null;
            statement.FinalizadoPorUserId = null;
            statement.ReabiertoAtUtc = DateTime.UtcNow;
            statement.ReabiertoPorUserId = userId;
            statement.MotivoReapertura = razon;
            statement.UpdatedAtUtc = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.InvestorStatementReopened,
                    EntityType = PlatformAuditEntityTypes.InvestorStatement,
                    EntityId = statement.Id.ToString(),
                    TenantId = statement.TenantId,
                    Reason = razon,
                    BeforeJson = antes,
                    AfterJson = SerializeSnapshot(statement, null)
                },
                cancellationToken);
        }

        public async Task MarkAsSentAsync(int statementId, CancellationToken cancellationToken = default)
        {
            var statement = await _context.InvestorStatements
                .FirstOrDefaultAsync(current => current.Id == statementId, cancellationToken);

            if (statement is null || statement.EsEditable || statement.EstaAnulado)
            {
                return;
            }

            statement.EnviadoAtUtc ??= DateTime.UtcNow;

            if (statement.Estado == InvestorStatementStatus.Finalized)
            {
                statement.Estado = InvestorStatementStatus.Sent;
            }

            statement.UpdatedAtUtc = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }

        // ─────────────── Ajustes ───────────────

        public async Task AddAdjustmentAsync(
            InvestorAdjustmentFormViewModel form,
            string? userId,
            string? userEmail,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(form);

            var descripcion = NormalizeRequiredText(form.Descripcion, 300)
                ?? throw new InvestorValidationException(
                    "Explicá el motivo del ajuste.",
                    nameof(form.Descripcion));

            if (form.Monto == 0m)
            {
                throw new InvestorValidationException(
                    "El ajuste no puede ser cero.",
                    nameof(form.Monto));
            }

            var statement = await LoadTrackedAsync(form.StatementId, cancellationToken);

            if (!statement.EsEditable)
            {
                throw new InvestorValidationException(
                    "Solo un borrador admite ajustes. Reabrí el estado o anulalo si necesitás corregir un periodo ya finalizado.");
            }

            var ajuste = new InvestorStatementAdjustment
            {
                StatementId = statement.Id,
                Monto = FiscalMath.Redondear(form.Monto),
                Descripcion = descripcion,
                CreadoPorUserId = userId,
                CreadoPorEmail = userEmail,
                CreatedAtUtc = DateTime.UtcNow
            };

            _context.InvestorStatementAdjustments.Add(ajuste);
            await _context.SaveChangesAsync(cancellationToken);

            await RefreshDraftTotalsAsync(statement, cancellationToken);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.InvestorStatementAdjustmentAdded,
                    EntityType = PlatformAuditEntityTypes.InvestorStatement,
                    EntityId = statement.Id.ToString(),
                    TenantId = statement.TenantId,
                    Reason = descripcion,
                    AfterJson = JsonSerializer.Serialize(new { ajuste.Monto, ajuste.Descripcion })
                },
                cancellationToken);
        }

        public async Task RemoveAdjustmentAsync(
            int adjustmentId,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            var ajuste = await _context.InvestorStatementAdjustments
                .FirstOrDefaultAsync(current => current.Id == adjustmentId, cancellationToken)
                ?? throw new InvestorValidationException("El ajuste indicado no existe o no pertenece a este negocio.");

            var statement = await LoadTrackedAsync(ajuste.StatementId, cancellationToken);

            if (!statement.EsEditable)
            {
                throw new InvestorValidationException("Solo se pueden quitar ajustes de un borrador.");
            }

            var descripcion = ajuste.Descripcion;
            var monto = ajuste.Monto;

            _context.InvestorStatementAdjustments.Remove(ajuste);
            await _context.SaveChangesAsync(cancellationToken);

            await RefreshDraftTotalsAsync(statement, cancellationToken);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.InvestorStatementAdjustmentRemoved,
                    EntityType = PlatformAuditEntityTypes.InvestorStatement,
                    EntityId = statement.Id.ToString(),
                    TenantId = statement.TenantId,
                    BeforeJson = JsonSerializer.Serialize(new { Monto = monto, Descripcion = descripcion })
                },
                cancellationToken);
        }

        // ─────────────── Pagos ───────────────

        public async Task RegisterPaymentAsync(
            InvestorPaymentFormViewModel form,
            string? userId,
            string? userEmail,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(form);

            var metodo = InvestorDefaults.NormalizeMetodoPago(form.MetodoPago)
                ?? throw new InvestorValidationException(
                    "El método de pago indicado no es válido.",
                    nameof(form.MetodoPago));

            var monto = FiscalMath.Redondear(form.Monto);
            if (monto <= 0m)
            {
                throw new InvestorValidationException(
                    "El monto del pago debe ser mayor a cero.",
                    nameof(form.Monto));
            }

            var statement = await LoadTrackedAsync(form.StatementId, cancellationToken);

            if (statement.EstaAnulado)
            {
                throw new InvestorValidationException("No se pueden registrar pagos sobre un estado anulado.");
            }

            if (!statement.AdmitePagos)
            {
                throw new InvestorValidationException(
                    "Finalizá el estado de cuenta antes de registrar pagos: un borrador todavía puede cambiar de monto.");
            }

            if (monto > statement.SaldoPendiente)
            {
                throw new InvestorValidationException(
                    $"El pago de ₡{monto:N2} supera el saldo pendiente de ₡{statement.SaldoPendiente:N2}.",
                    nameof(form.Monto));
            }

            var pago = new InvestorDistributionPayment
            {
                StatementId = statement.Id,
                Fecha = DateOnly.FromDateTime(form.Fecha.Date),
                Monto = monto,
                MetodoPago = metodo,
                Referencia = NormalizeOptionalText(form.Referencia, 120),
                Notas = NormalizeOptionalText(form.Notas, 500),
                EsReversion = false,
                RegistradoPorUserId = userId,
                RegistradoPorEmail = userEmail,
                CreatedAtUtc = DateTime.UtcNow
            };

            _context.InvestorDistributionPayments.Add(pago);
            await _context.SaveChangesAsync(cancellationToken);

            await RefreshPaymentTotalsAsync(statement, cancellationToken);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.InvestorPaymentRegistered,
                    EntityType = PlatformAuditEntityTypes.InvestorStatement,
                    EntityId = statement.Id.ToString(),
                    TenantId = statement.TenantId,
                    AfterJson = JsonSerializer.Serialize(new
                    {
                        pago.Monto,
                        pago.MetodoPago,
                        Fecha = pago.Fecha.ToString("yyyy-MM-dd"),
                        statement.TotalPagado,
                        statement.SaldoPendiente
                    })
                },
                cancellationToken);
        }

        public async Task ReversePaymentAsync(
            int paymentId,
            string motivo,
            string? userId,
            string? userEmail,
            CancellationToken cancellationToken = default)
        {
            var razon = NormalizeRequiredText(motivo, 300)
                ?? throw new InvestorValidationException("Explicá el motivo de la corrección del pago.");

            var pago = await _context.InvestorDistributionPayments
                .FirstOrDefaultAsync(current => current.Id == paymentId, cancellationToken)
                ?? throw new InvestorValidationException("El pago indicado no existe o no pertenece a este negocio.");

            if (pago.EsReversion)
            {
                throw new InvestorValidationException("Una corrección no puede revertirse: registrá un pago nuevo si corresponde.");
            }

            var yaRevertido = await _context.InvestorDistributionPayments
                .AsNoTracking()
                .AnyAsync(
                    current => current.StatementId == pago.StatementId &&
                               current.EsReversion &&
                               current.Referencia == BuildReversalReference(pago.Id),
                    cancellationToken);

            if (yaRevertido)
            {
                throw new InvestorValidationException("Ese pago ya fue corregido.");
            }

            var statement = await LoadTrackedAsync(pago.StatementId, cancellationToken);

            _context.InvestorDistributionPayments.Add(new InvestorDistributionPayment
            {
                StatementId = statement.Id,
                Fecha = DateOnly.FromDateTime(_businessDateTimeProvider.Today()),
                // Movimiento compensatorio: el pago original NO se borra, para conservar la traza.
                Monto = -pago.Monto,
                MetodoPago = pago.MetodoPago,
                Referencia = BuildReversalReference(pago.Id),
                Notas = $"Corrección del pago #{pago.Id}.",
                EsReversion = true,
                Motivo = razon,
                RegistradoPorUserId = userId,
                RegistradoPorEmail = userEmail,
                CreatedAtUtc = DateTime.UtcNow
            });

            await _context.SaveChangesAsync(cancellationToken);
            await RefreshPaymentTotalsAsync(statement, cancellationToken);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.InvestorPaymentReversed,
                    EntityType = PlatformAuditEntityTypes.InvestorStatement,
                    EntityId = statement.Id.ToString(),
                    TenantId = statement.TenantId,
                    Reason = razon,
                    AfterJson = JsonSerializer.Serialize(new
                    {
                        PagoOriginalId = pago.Id,
                        pago.Monto,
                        statement.TotalPagado,
                        statement.SaldoPendiente
                    })
                },
                cancellationToken);
        }

        // ─────────────── Consultas ───────────────

        public async Task<InvestorStatementsPageViewModel> BuildStatementsPageAsync(
            InvestorStatementFilter filter,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(filter);

            var query = _context.InvestorStatements
                .AsNoTracking()
                .Include(statement => statement.Investor)
                .AsQueryable();

            if (filter.InvestorId.HasValue)
            {
                query = query.Where(statement => statement.InvestorId == filter.InvestorId.Value);
            }

            if (filter.Estado.HasValue)
            {
                query = query.Where(statement => statement.Estado == filter.Estado.Value);
            }

            if (filter.Desde.HasValue)
            {
                query = query.Where(statement => statement.PeriodoFin >= filter.Desde.Value);
            }

            if (filter.Hasta.HasValue)
            {
                query = query.Where(statement => statement.PeriodoInicio <= filter.Hasta.Value);
            }

            var filas = await query
                .OrderByDescending(statement => statement.PeriodoInicio)
                .ThenBy(statement => statement.InvestorId)
                .Take(300)
                .ToListAsync(cancellationToken);

            var inversionistas = await _context.TenantInvestors
                .AsNoTracking()
                .OrderBy(investor => investor.Nombre)
                .Select(investor => new InvestorCategoriaOption(investor.Id, investor.Nombre))
                .ToListAsync(cancellationToken);

            var items = filas
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

            var vivos = items.Where(item => item.Estado != InvestorStatementStatus.Voided).ToList();

            return new InvestorStatementsPageViewModel
            {
                Estados = items,
                Inversionistas = inversionistas,
                FiltroInversionistaId = filter.InvestorId,
                FiltroEstado = filter.Estado,
                FiltroDesde = filter.Desde,
                FiltroHasta = filter.Hasta,
                TotalParticipaciones = vivos.Sum(item => item.ParticipacionCalculada),
                TotalPagado = vivos.Sum(item => item.TotalPagado),
                TotalPendiente = vivos.Sum(item => item.SaldoPendiente)
            };
        }

        public async Task<InvestorStatementDetailViewModel?> BuildDetailAsync(
            int statementId,
            CancellationToken cancellationToken = default)
        {
            var statement = await _context.InvestorStatements
                .AsNoTracking()
                .Include(current => current.Investor)
                .Include(current => current.Ajustes)
                .Include(current => current.Pagos)
                .FirstOrDefaultAsync(current => current.Id == statementId, cancellationToken);

            if (statement is null)
            {
                return null;
            }

            var envios = await _context.InvestorStatementEmailLogs
                .AsNoTracking()
                .Where(log => log.StatementId == statementId)
                .OrderByDescending(log => log.CreatedAtUtc)
                .Take(50)
                .ToListAsync(cancellationToken);

            var nombreNegocio = await _tenantDisplayNameService.GetCurrentTenantDisplayNameAsync(cancellationToken);
            var navegacion = await BuildNavigationAsync(statement, cancellationToken);

            return new InvestorStatementDetailViewModel
            {
                Navegacion = navegacion,
                Id = statement.Id,
                InvestorId = statement.InvestorId,
                InvestorNombre = statement.Investor?.Nombre ?? "—",
                InvestorEmail = statement.Investor?.Email ?? string.Empty,
                NombreNegocio = nombreNegocio,
                PeriodoInicio = statement.PeriodoInicio,
                PeriodoFin = statement.PeriodoFin,
                PeriodoEtiqueta = InvestorPeriodCalculator.BuildEtiqueta(
                    statement.Frecuencia,
                    statement.PeriodoInicio,
                    statement.PeriodoFin),
                Frecuencia = statement.Frecuencia,
                DiaCorte = statement.DiaCorte,
                FechaCorte = statement.FechaCorte,
                CorteTexto = InvestorSettlementPeriodResolver.EtiquetaCorte(
                    statement.Frecuencia,
                    statement.DiaCorte),
                Estado = statement.Estado,
                EstadoTexto = statement.EstadoTexto,
                TotalPagado = statement.TotalPagado,
                SaldoPendiente = statement.SaldoPendiente,
                FechaCalculoUtc = statement.FechaCalculoUtc,
                FinalizadoAtUtc = statement.FinalizadoAtUtc,
                EnviadoAtUtc = statement.EnviadoAtUtc,
                AnuladoAtUtc = statement.AnuladoAtUtc,
                MotivoAnulacion = statement.MotivoAnulacion,
                Desglose = BuildBreakdownViewModel(statement),
                Ajustes = statement.Ajustes
                    .OrderBy(ajuste => ajuste.CreatedAtUtc)
                    .Select(ajuste => new InvestorAdjustmentRowViewModel(
                        ajuste.Id,
                        ajuste.Monto,
                        ajuste.Descripcion,
                        ajuste.CreadoPorEmail,
                        ajuste.CreatedAtUtc))
                    .ToList(),
                Pagos = statement.Pagos
                    .OrderByDescending(pago => pago.Fecha)
                    .ThenByDescending(pago => pago.Id)
                    .Select(pago => new InvestorPaymentRowViewModel(
                        pago.Id,
                        pago.Fecha,
                        pago.Monto,
                        pago.MetodoPago,
                        pago.Referencia,
                        pago.Notas,
                        pago.EsReversion,
                        pago.Motivo,
                        pago.RegistradoPorEmail,
                        pago.CreatedAtUtc))
                    .ToList(),
                Envios = envios
                    .Select(log => new InvestorEmailRowViewModel(
                        log.Id,
                        log.RecipientEmail,
                        log.Subject,
                        log.Status,
                        log.IsTest,
                        log.ErrorMessage,
                        log.SentAtUtc,
                        log.CreatedAtUtc))
                    .ToList()
            };
        }

        /// <summary>
        /// Corte anterior y siguiente del MISMO inversionista, para navegar por ciclos reales en
        /// vez de escribir fechas. Se ordena por la fecha de corte, que es como el dueño piensa el
        /// historial; los anulados quedan fuera porque no representan un corte válido.
        /// </summary>
        private async Task<InvestorStatementNavigation> BuildNavigationAsync(
            InvestorStatement statement,
            CancellationToken cancellationToken)
        {
            if (statement.EstaAnulado)
            {
                return InvestorStatementNavigation.Vacia;
            }

            var vecinos = await _context.InvestorStatements
                .AsNoTracking()
                .Where(current => current.InvestorId == statement.InvestorId &&
                                  current.Id != statement.Id &&
                                  current.Estado != InvestorStatementStatus.Voided)
                .Select(current => new
                {
                    current.Id,
                    current.FechaCorte,
                    current.PeriodoFin
                })
                .ToListAsync(cancellationToken);

            var anterior = vecinos
                .Where(current => current.PeriodoFin < statement.PeriodoFin)
                .OrderByDescending(current => current.PeriodoFin)
                .ThenByDescending(current => current.Id)
                .FirstOrDefault();

            var siguiente = vecinos
                .Where(current => current.PeriodoFin > statement.PeriodoFin)
                .OrderBy(current => current.PeriodoFin)
                .ThenBy(current => current.Id)
                .FirstOrDefault();

            return new InvestorStatementNavigation(
                anterior?.Id,
                anterior?.FechaCorte,
                siguiente?.Id,
                siguiente?.FechaCorte);
        }

        /// <summary>Desglose visible construido SOLO desde el snapshot (nunca recalcula).</summary>
        public static InvestorCalculationBreakdownViewModel BuildBreakdownViewModel(InvestorStatement statement) =>
            new()
            {
                InvestorNombre = statement.Investor?.Nombre ?? string.Empty,
                PeriodoInicio = statement.PeriodoInicio,
                PeriodoFin = statement.PeriodoFin,
                IngresosCobrados = statement.IngresosCobrados,
                IvaExcluido = statement.IvaExcluido,
                IngresosNetos = statement.IngresosNetos,
                GastosElegibles = statement.GastosElegibles,
                Liquidaciones = statement.Liquidaciones,
                AjustesPositivos = statement.AjustesPositivos,
                AjustesNegativos = statement.AjustesNegativos,
                PerdidaArrastrada = statement.PerdidaArrastrada,
                PerdidaPendiente = statement.PerdidaPendiente,
                GananciaDistribuible = statement.GananciaDistribuible,
                ParticipacionPorcentaje = statement.ParticipacionPorcentaje,
                ParticipacionCalculada = statement.ParticipacionCalculada,
                PoliticaVersion = statement.PoliticaVersion,
                TotalPagado = statement.TotalPagado,
                SaldoPendiente = statement.SaldoPendiente
            };

        // ─────────────── Núcleo del cálculo ───────────────

        /// <summary>
        /// Aplica la fórmula al estado. Es el ÚNICO punto donde se escriben los montos del
        /// snapshot; finalizar simplemente deja de llamarlo.
        /// </summary>
        private async Task ApplyCalculationAsync(
            InvestorStatement statement,
            InvestorAgreement? acuerdo,
            InvestorProfitPolicy policy,
            CancellationToken cancellationToken)
        {
            var breakdown = await _calculationService.CalculateAsync(
                statement.PeriodoInicio,
                statement.PeriodoFin,
                policy,
                cancellationToken);

            var ajustes = statement.Id == 0
                ? new List<InvestorStatementAdjustment>()
                : await _context.InvestorStatementAdjustments
                    .AsNoTracking()
                    .Where(ajuste => ajuste.StatementId == statement.Id)
                    .ToListAsync(cancellationToken);

            var ajustesPositivos = FiscalMath.Redondear(ajustes.Where(a => a.Monto > 0m).Sum(a => a.Monto));
            var ajustesNegativos = FiscalMath.Redondear(Math.Abs(ajustes.Where(a => a.Monto < 0m).Sum(a => a.Monto)));

            var tratamiento = acuerdo?.TratamientoPerdidas ?? policy.TratamientoPerdidasPorDefecto;
            var perdidaPrevia = await ResolvePerdidaArrastradaAsync(
                statement.InvestorId,
                statement.PeriodoInicio,
                acuerdo,
                cancellationToken);

            var resultado = ApplyProfitRules(
                breakdown.GananciaDistribuible,
                ajustesPositivos,
                ajustesNegativos,
                perdidaPrevia,
                tratamiento);

            statement.IngresosCobrados = breakdown.TotalCobrado;
            statement.IvaExcluido = breakdown.IvaCobrado;
            statement.IngresosNetos = breakdown.IngresosNetos;
            statement.GastosElegibles = breakdown.GastosOperativos;
            statement.Liquidaciones = breakdown.LiquidacionesEquipo;
            statement.AjustesPositivos = ajustesPositivos;
            statement.AjustesNegativos = ajustesNegativos;
            statement.PerdidaArrastrada = resultado.PerdidaAplicada;
            statement.PerdidaPendiente = resultado.PerdidaPendiente;
            statement.GananciaDistribuible = resultado.Distribuible;
            statement.ParticipacionCalculada = CalcularParticipacion(
                resultado.Distribuible,
                statement.ParticipacionPorcentaje);
            statement.PoliticaVersion = Truncate(breakdown.PoliticaVersion, 300)!;
            statement.FechaCalculoUtc = DateTime.UtcNow;

            RecalculateBalance(statement);
        }

        /// <summary>
        /// Reglas de ganancia y pérdida. Función pura: es la regla de negocio en una sola pieza.
        /// </summary>
        public static (decimal Distribuible, decimal PerdidaAplicada, decimal PerdidaPendiente) ApplyProfitRules(
            decimal resultadoOperativo,
            decimal ajustesPositivos,
            decimal ajustesNegativos,
            decimal perdidaPrevia,
            InvestorLossTreatment tratamiento)
        {
            var neto = FiscalMath.Redondear(resultadoOperativo + ajustesPositivos - ajustesNegativos);

            if (tratamiento != InvestorLossTreatment.CarryForward)
            {
                // NoDistribution: una pérdida deja el distribuible en cero y NO pasa al periodo siguiente.
                return (Math.Max(neto, 0m), 0m, 0m);
            }

            var aplicada = Math.Max(perdidaPrevia, 0m);
            var conArrastre = FiscalMath.Redondear(neto - aplicada);

            return conArrastre >= 0m
                ? (conArrastre, aplicada, 0m)
                : (0m, aplicada, FiscalMath.Redondear(-conArrastre));
        }

        public static decimal CalcularParticipacion(decimal distribuible, decimal porcentaje) =>
            FiscalMath.Redondear(distribuible * (porcentaje / 100m));

        /// <summary>
        /// Pérdida pendiente que deja el último estado NO anulado anterior al periodo. Solo se
        /// arrastra cuando el acuerdo lo pide; con NoDistribution siempre es cero.
        /// </summary>
        public async Task<decimal> GetCarryForwardLossAsync(
            int investorId,
            DateOnly periodoInicio,
            InvestorAgreement? acuerdo,
            CancellationToken cancellationToken = default) =>
            await ResolvePerdidaArrastradaAsync(investorId, periodoInicio, acuerdo, cancellationToken);

        private async Task<decimal> ResolvePerdidaArrastradaAsync(
            int investorId,
            DateOnly periodoInicio,
            InvestorAgreement? acuerdo,
            CancellationToken cancellationToken)
        {
            if (acuerdo?.TratamientoPerdidas != InvestorLossTreatment.CarryForward)
            {
                return 0m;
            }

            var anterior = await _context.InvestorStatements
                .AsNoTracking()
                .Where(statement => statement.InvestorId == investorId &&
                                    statement.PeriodoFin < periodoInicio &&
                                    statement.Estado != InvestorStatementStatus.Voided)
                .OrderByDescending(statement => statement.PeriodoFin)
                .ThenByDescending(statement => statement.Id)
                .FirstOrDefaultAsync(cancellationToken);

            return anterior?.PerdidaPendiente ?? 0m;
        }

        /// <summary>Recalcula un borrador tras agregar/quitar ajustes.</summary>
        private async Task RefreshDraftTotalsAsync(
            InvestorStatement statement,
            CancellationToken cancellationToken)
        {
            var policy = await _investorService.GetPolicyAsync(cancellationToken);
            var acuerdo = await _investorService.GetAgreementForDateAsync(
                statement.InvestorId,
                statement.PeriodoInicio,
                cancellationToken);

            await ApplyCalculationAsync(statement, acuerdo, policy, cancellationToken);
            statement.UpdatedAtUtc = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
        }

        /// <summary>
        /// Recalcula pagado/pendiente y ajusta el estado. NO toca los montos del snapshot:
        /// un pago nunca cambia la participación calculada.
        /// </summary>
        private async Task RefreshPaymentTotalsAsync(
            InvestorStatement statement,
            CancellationToken cancellationToken)
        {
            var totalPagado = await _context.InvestorDistributionPayments
                .AsNoTracking()
                .Where(pago => pago.StatementId == statement.Id)
                .SumAsync(pago => (decimal?)pago.Monto, cancellationToken) ?? 0m;

            statement.TotalPagado = FiscalMath.Redondear(totalPagado);
            RecalculateBalance(statement);
            statement.UpdatedAtUtc = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
        }

        private static void RecalculateBalance(InvestorStatement statement)
        {
            statement.SaldoPendiente = FiscalMath.Redondear(
                Math.Max(statement.ParticipacionCalculada - statement.TotalPagado, 0m));

            if (statement.EsEditable || statement.EstaAnulado)
            {
                return;
            }

            if (statement.TotalPagado <= 0m)
            {
                // Sin pagos vivos: vuelve al estado previo al primer pago.
                statement.Estado = statement.EnviadoAtUtc.HasValue
                    ? InvestorStatementStatus.Sent
                    : InvestorStatementStatus.Finalized;
                return;
            }

            statement.Estado = statement.SaldoPendiente <= 0m
                ? InvestorStatementStatus.Paid
                : InvestorStatementStatus.PartiallyPaid;
        }

        // ─────────────── Helpers ───────────────

        private DateOnly Today() => DateOnly.FromDateTime(_businessDateTimeProvider.Today());

        /// <summary>
        /// Acuerdo "de mentira" con la configuración por defecto del negocio, solo para poder
        /// resolver un periodo cuando el inversionista todavía no tiene acuerdo. Nunca se guarda:
        /// sirve para que la vista previa muestre un rango razonable en vez de fallar.
        /// </summary>
        private static InvestorAgreement AcuerdoPorDefecto(InvestorProfitPolicy policy) => new()
        {
            Frecuencia = policy.FrecuenciaPorDefecto,
            DiaCorte = null
        };

        private async Task<InvestorStatement> LoadTrackedAsync(int statementId, CancellationToken cancellationToken) =>
            await _context.InvestorStatements
                .FirstOrDefaultAsync(statement => statement.Id == statementId, cancellationToken)
            ?? throw new InvestorValidationException("El estado de cuenta indicado no existe o no pertenece a este negocio.");

        private static string BuildReversalReference(int paymentId) => $"REV-{paymentId}";

        private static string SerializeSnapshot(InvestorStatement statement, string? investorNombre) =>
            JsonSerializer.Serialize(new
            {
                statement.Id,
                statement.InvestorId,
                Inversionista = investorNombre,
                Periodo = $"{statement.PeriodoInicio:yyyy-MM-dd}..{statement.PeriodoFin:yyyy-MM-dd}",
                statement.IngresosNetos,
                statement.GastosElegibles,
                statement.Liquidaciones,
                statement.AjustesPositivos,
                statement.AjustesNegativos,
                statement.PerdidaArrastrada,
                statement.GananciaDistribuible,
                statement.ParticipacionPorcentaje,
                statement.ParticipacionCalculada,
                Estado = statement.Estado.ToString()
            });

        private static bool IsUniqueConstraintViolation(DbUpdateException ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("UX_InvestorStatements", StringComparison.OrdinalIgnoreCase);
        }

        private static string? NormalizeRequiredText(string? value, int maxLength)
        {
            var normalized = NormalizeOptionalText(value, maxLength);
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static string? NormalizeOptionalText(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return value.Length <= maxLength ? value : value[..maxLength];
        }
    }
}
