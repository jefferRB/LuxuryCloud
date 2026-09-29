using System.Text.Json;
using LuxuryApp.Models.Horarios;
using LuxuryApp.Models.Platform;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Platform;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Horarios
{
    /// <summary>
    /// Gestión de reglas recurrentes.
    ///
    /// <para>Reglas duras:</para>
    /// <list type="number">
    ///   <item>Las citas existentes NUNCA se mueven, cancelan ni borran. Solo se informan.</item>
    ///   <item>Los cambios aplican hacia el futuro: editar una regla vigente la versiona.</item>
    ///   <item>Las horas son locales del negocio (America/Costa_Rica), nunca UTC.</item>
    ///   <item>Una excepción altera un día concreto y jamás la regla general.</item>
    /// </list>
    /// </summary>
    public sealed class RecurringScheduleService : IRecurringScheduleService
    {
        /// <summary>Ventana de búsqueda de conflictos hacia adelante. Suficiente para ver el impacto real.</summary>
        private const int DiasVentanaConflictos = 60;

        private const int MaxConflictosReportados = 100;

        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly IPlatformAuditService _auditService;
        private readonly ILogger<RecurringScheduleService> _logger;

        public RecurringScheduleService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider,
            IPlatformAuditService auditService,
            ILogger<RecurringScheduleService> logger)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
            _auditService = auditService;
            _logger = logger;
        }

        // ─────────────── Consultas para la UI ───────────────

        public async Task<RecurringSchedulePageViewModel> BuildPageAsync(CancellationToken cancellationToken = default)
        {
            var hoy = Today();

            var reglas = await _context.RecurringScheduleRules
                .AsNoTracking()
                .Include(regla => regla.Colaboradores)
                .OrderByDescending(regla => regla.Activa)
                .ThenBy(regla => regla.HoraInicio)
                .ThenBy(regla => regla.Nombre)
                .ToListAsync(cancellationToken);

            var excepcionesProximas = await _context.RecurringScheduleExceptions
                .AsNoTracking()
                .Where(exception => exception.Fecha >= hoy)
                .GroupBy(exception => exception.RuleId)
                .Select(group => new { RuleId = group.Key, Total = group.Count() })
                .ToListAsync(cancellationToken);

            var excepcionesPorRegla = excepcionesProximas.ToDictionary(row => row.RuleId, row => row.Total);

            return new RecurringSchedulePageViewModel
            {
                Reglas = reglas
                    .Select(regla => new RecurringScheduleRuleListItemViewModel
                    {
                        Id = regla.Id,
                        Nombre = regla.Nombre,
                        Horario = FormatHorario(regla.HoraInicio, regla.HoraFin),
                        Dias = RecurringScheduleOccurrenceCalculator.DescribirDias(regla.DiasSemanaMask),
                        Activa = regla.Activa,
                        Alcance = regla.Alcance,
                        AlcanceTexto = DescribirAlcance(regla.Alcance, regla.Colaboradores.Count),
                        ColaboradoresCount = regla.Colaboradores.Count,
                        VigenteDesde = regla.VigenteDesde,
                        VigenteHasta = regla.VigenteHasta,
                        ExcepcionesProximas = excepcionesPorRegla.GetValueOrDefault(regla.Id),
                        Motivo = regla.Motivo,
                        EsVersion = regla.ReglaOrigenId.HasValue
                    })
                    .ToList(),
                Funcionarios = await LoadFuncionariosAsync(cancellationToken)
            };
        }

        public async Task<RecurringScheduleRuleFormViewModel> BuildCreateFormAsync(
            CancellationToken cancellationToken = default) =>
            new()
            {
                VigenteDesde = _businessDateTimeProvider.Today(),
                FuncionariosDisponibles = await LoadFuncionariosAsync(cancellationToken)
            };

        public async Task<RecurringScheduleRuleFormViewModel?> BuildEditFormAsync(
            int ruleId,
            CancellationToken cancellationToken = default)
        {
            var regla = await _context.RecurringScheduleRules
                .AsNoTracking()
                .Include(current => current.Colaboradores)
                .FirstOrDefaultAsync(current => current.Id == ruleId, cancellationToken);

            if (regla is null)
            {
                return null;
            }

            return new RecurringScheduleRuleFormViewModel
            {
                Id = regla.Id,
                Nombre = regla.Nombre,
                HoraInicio = regla.HoraInicio,
                HoraFin = regla.HoraFin,
                Dias = Enumerable.Range(0, 7)
                    .Where(dia => (regla.DiasSemanaMask & (1 << dia)) != 0)
                    .ToList(),
                VigenteDesde = regla.VigenteDesde.ToDateTime(TimeOnly.MinValue),
                VigenteHasta = regla.VigenteHasta?.ToDateTime(TimeOnly.MinValue),
                Activa = regla.Activa,
                Alcance = regla.Alcance,
                IncluirNuevosColaboradores = regla.IncluirNuevosColaboradores,
                FuncionarioIds = regla.Colaboradores.Select(target => target.FuncionarioId).ToList(),
                EtiquetaCalendario = regla.EtiquetaCalendario,
                Motivo = regla.Motivo,
                FuncionariosDisponibles = await LoadFuncionariosAsync(cancellationToken)
            };
        }

        public async Task<RecurringScheduleRuleDetailViewModel?> BuildDetailAsync(
            int ruleId,
            CancellationToken cancellationToken = default)
        {
            var regla = await _context.RecurringScheduleRules
                .AsNoTracking()
                .Include(current => current.Colaboradores)
                    .ThenInclude(target => target.Funcionario)
                .Include(current => current.Excepciones)
                    .ThenInclude(exception => exception.Funcionario)
                .FirstOrDefaultAsync(current => current.Id == ruleId, cancellationToken);

            if (regla is null)
            {
                return null;
            }

            return new RecurringScheduleRuleDetailViewModel
            {
                Id = regla.Id,
                Nombre = regla.Nombre,
                Horario = FormatHorario(regla.HoraInicio, regla.HoraFin),
                Dias = RecurringScheduleOccurrenceCalculator.DescribirDias(regla.DiasSemanaMask),
                AlcanceTexto = DescribirAlcance(regla.Alcance, regla.Colaboradores.Count),
                Activa = regla.Activa,
                VigenteDesde = regla.VigenteDesde,
                VigenteHasta = regla.VigenteHasta,
                Motivo = regla.Motivo,
                EtiquetaCalendario = regla.EtiquetaCalendario,
                Colaboradores = regla.Colaboradores
                    .Select(target => target.Funcionario?.Nombre ?? $"#{target.FuncionarioId}")
                    .OrderBy(nombre => nombre, StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),
                Excepciones = regla.Excepciones
                    .OrderBy(exception => exception.Fecha)
                    .Select(exception => new RecurringScheduleExceptionRowViewModel(
                        exception.Id,
                        exception.Fecha,
                        exception.Funcionario?.Nombre,
                        exception.Tipo,
                        exception.HoraInicioAlternativa,
                        exception.HoraFinAlternativa,
                        exception.Motivo))
                    .ToList(),
                FuncionariosDisponibles = await LoadFuncionariosAsync(cancellationToken)
            };
        }

        // ─────────────── Conflictos ───────────────

        public async Task<RecurringScheduleConflictSummaryViewModel> DetectConflictsAsync(
            RecurringScheduleRuleFormViewModel form,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(form);

            var normalizado = await NormalizeAsync(form, cancellationToken);
            return await DetectConflictsAsync(normalizado, form.Id, cancellationToken);
        }

        /// <summary>
        /// Busca citas reales (Tipo = CITA) que caigan dentro de las futuras ocurrencias del bloqueo.
        /// Solo mira hacia adelante: bloquear el pasado no tiene sentido y no se toca el historial.
        /// </summary>
        private async Task<RecurringScheduleConflictSummaryViewModel> DetectConflictsAsync(
            NormalizedRule regla,
            int? ruleIdExcluida,
            CancellationToken cancellationToken)
        {
            var hoy = Today();
            var desde = regla.VigenteDesde > hoy ? regla.VigenteDesde : hoy;
            var hastaLimite = desde.AddDays(DiasVentanaConflictos);
            var hasta = regla.VigenteHasta.HasValue && regla.VigenteHasta.Value < hastaLimite
                ? regla.VigenteHasta.Value
                : hastaLimite;

            if (hasta < desde)
            {
                return new RecurringScheduleConflictSummaryViewModel();
            }

            var funcionarios = await ResolveFuncionariosAlcanzadosAsync(regla, cancellationToken);
            if (funcionarios.Count == 0)
            {
                return new RecurringScheduleConflictSummaryViewModel();
            }

            // Ocurrencias teóricas de la regla propuesta, aplicando sus excepciones ya guardadas
            // (si es una edición) para no reportar conflictos en días que igual estarán exentos.
            var excepciones = ruleIdExcluida.HasValue
                ? await _context.RecurringScheduleExceptions
                    .AsNoTracking()
                    .Where(exception => exception.RuleId == ruleIdExcluida.Value &&
                                        exception.Fecha >= desde &&
                                        exception.Fecha <= hasta)
                    .ToListAsync(cancellationToken)
                : new List<RecurringScheduleException>();

            var candidata = new RecurringScheduleRule
            {
                Id = ruleIdExcluida ?? 0,
                Nombre = regla.Nombre,
                HoraInicio = regla.HoraInicio,
                HoraFin = regla.HoraFin,
                DiasSemanaMask = regla.DiasSemanaMask,
                VigenteDesde = regla.VigenteDesde,
                VigenteHasta = regla.VigenteHasta,
                Activa = true,
                Alcance = regla.Alcance,
                EtiquetaCalendario = regla.EtiquetaCalendario,
                Motivo = regla.Motivo,
                Colaboradores = regla.FuncionarioIds
                    .Select(id => new RecurringScheduleRuleTarget { FuncionarioId = id })
                    .ToList(),
                Excepciones = excepciones
            };

            var ocurrencias = RecurringScheduleOccurrenceCalculator.Expand(
                new[] { candidata },
                funcionarios,
                desde,
                hasta);

            if (ocurrencias.Count == 0)
            {
                return new RecurringScheduleConflictSummaryViewModel();
            }

            var rangoInicio = desde.ToDateTime(TimeOnly.MinValue);
            var rangoFin = hasta.AddDays(1).ToDateTime(TimeOnly.MinValue);

            var citas = await _context.Citas
                .AsNoTracking()
                .Where(cita =>
                    cita.Tipo == "CITA" &&
                    funcionarios.Contains(cita.FuncionarioId) &&
                    cita.FechaHoraCita >= rangoInicio &&
                    cita.FechaHoraCita < rangoFin)
                .Select(cita => new
                {
                    cita.Id,
                    cita.FuncionarioId,
                    cita.FechaHoraCita,
                    FuncionarioNombre = cita.Funcionario != null ? cita.Funcionario.Nombre : null,
                    Detalle = cita.ServicioNombrePersonalizado
                              ?? (cita.Servicio != null ? cita.Servicio.Nombre : null),
                    Duracion = cita.DuracionMinutos
                               ?? (cita.Servicio != null ? cita.Servicio.DuracionMinutos : null)
                               ?? FuncionarioAvailabilityService.DefaultDurationMinutes
                })
                .ToListAsync(cancellationToken);

            if (citas.Count == 0)
            {
                return new RecurringScheduleConflictSummaryViewModel();
            }

            var ocurrenciasPorFuncionario = RecurringScheduleOccurrenceCalculator.GroupByFuncionario(ocurrencias);
            var conflictos = new List<RecurringScheduleConflictViewModel>();

            foreach (var cita in citas.OrderBy(cita => cita.FechaHoraCita))
            {
                if (!ocurrenciasPorFuncionario.TryGetValue(cita.FuncionarioId, out var bloques))
                {
                    continue;
                }

                var fin = cita.FechaHoraCita.AddMinutes(cita.Duracion);
                if (!bloques.Any(bloque => bloque.Solapa(cita.FechaHoraCita, fin)))
                {
                    continue;
                }

                conflictos.Add(new RecurringScheduleConflictViewModel(
                    cita.Id,
                    cita.FechaHoraCita,
                    cita.Duracion,
                    cita.FuncionarioId,
                    cita.FuncionarioNombre ?? $"#{cita.FuncionarioId}",
                    cita.Detalle));

                if (conflictos.Count >= MaxConflictosReportados)
                {
                    break;
                }
            }

            return new RecurringScheduleConflictSummaryViewModel { Conflictos = conflictos };
        }

        // ─────────────── Alta / edición ───────────────

        public async Task<RecurringScheduleSaveResult> CreateAsync(
            RecurringScheduleRuleFormViewModel form,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(form);

            var normalizado = await NormalizeAsync(form, cancellationToken);
            await EnsureNoDuplicateAsync(normalizado, ruleIdExcluida: null, cancellationToken);

            var conflictos = await DetectConflictsAsync(normalizado, ruleIdExcluida: null, cancellationToken);

            // Las citas existentes se conservan siempre; solo se exige que el usuario vea el impacto.
            if (conflictos.TieneConflictos && !form.ConfirmarConflictos)
            {
                return RecurringScheduleSaveResult.NeedsConfirmation(conflictos);
            }

            var regla = new RecurringScheduleRule
            {
                Nombre = normalizado.Nombre,
                Tipo = RecurringScheduleRuleType.UnavailableBlock,
                HoraInicio = normalizado.HoraInicio,
                HoraFin = normalizado.HoraFin,
                DiasSemanaMask = normalizado.DiasSemanaMask,
                VigenteDesde = normalizado.VigenteDesde,
                VigenteHasta = normalizado.VigenteHasta,
                Activa = form.Activa,
                Alcance = normalizado.Alcance,
                IncluirNuevosColaboradores = normalizado.Alcance == RecurringScheduleScope.TodosLosColaboradores
                    && form.IncluirNuevosColaboradores,
                EtiquetaCalendario = normalizado.EtiquetaCalendario,
                Motivo = normalizado.Motivo,
                CreadoPorUserId = userId,
                ActualizadoPorUserId = userId,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            _context.RecurringScheduleRules.Add(regla);
            await _context.SaveChangesAsync(cancellationToken);

            await ReplaceTargetsAsync(regla.Id, normalizado, cancellationToken);

            await AuditRuleAsync(
                PlatformAuditActions.RecurringScheduleRuleCreated,
                regla,
                normalizado,
                conflictos,
                userId,
                cancellationToken);

            _logger.LogInformation(
                "Bloqueo recurrente {RuleId} creado ({Horario}, {Dias}).",
                regla.Id,
                FormatHorario(regla.HoraInicio, regla.HoraFin),
                RecurringScheduleOccurrenceCalculator.DescribirDias(regla.DiasSemanaMask));

            return RecurringScheduleSaveResult.Saved(regla.Id, conflictos);
        }

        public async Task<RecurringScheduleSaveResult> UpdateAsync(
            int ruleId,
            RecurringScheduleRuleFormViewModel form,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(form);

            var regla = await _context.RecurringScheduleRules
                .Include(current => current.Colaboradores)
                .FirstOrDefaultAsync(current => current.Id == ruleId, cancellationToken)
                ?? throw new RecurringScheduleValidationException(
                    "La regla indicada no existe o no pertenece a este negocio.");

            var normalizado = await NormalizeAsync(form, cancellationToken);
            await EnsureNoDuplicateAsync(normalizado, ruleId, cancellationToken);

            var conflictos = await DetectConflictsAsync(normalizado, ruleId, cancellationToken);
            if (conflictos.TieneConflictos && !form.ConfirmarConflictos)
            {
                return RecurringScheduleSaveResult.NeedsConfirmation(conflictos);
            }

            var hoy = Today();
            var antes = SerializeRule(regla);

            // ¿Cambia lo que define el bloqueo en sí?
            var cambioEstructural =
                regla.HoraInicio != normalizado.HoraInicio ||
                regla.HoraFin != normalizado.HoraFin ||
                regla.DiasSemanaMask != normalizado.DiasSemanaMask ||
                regla.Alcance != normalizado.Alcance ||
                !regla.Colaboradores
                    .Select(target => target.FuncionarioId)
                    .OrderBy(id => id)
                    .SequenceEqual(normalizado.FuncionarioIds.OrderBy(id => id));

            // Versionado: si la regla ya estuvo vigente, no se reescribe el pasado. Se cierra ayer
            // y la nueva versión arranca hoy (o en la fecha efectiva pedida si es futura).
            var yaEstuvoVigente = regla.VigenteDesde <= hoy;

            if (cambioEstructural && yaEstuvoVigente)
            {
                var efectiva = normalizado.VigenteDesde > hoy ? normalizado.VigenteDesde : hoy;

                regla.VigenteHasta = efectiva.AddDays(-1);
                regla.Activa = false;
                regla.ActualizadoPorUserId = userId;
                regla.UpdatedAtUtc = DateTime.UtcNow;

                var nueva = new RecurringScheduleRule
                {
                    Nombre = normalizado.Nombre,
                    Tipo = regla.Tipo,
                    HoraInicio = normalizado.HoraInicio,
                    HoraFin = normalizado.HoraFin,
                    DiasSemanaMask = normalizado.DiasSemanaMask,
                    VigenteDesde = efectiva,
                    VigenteHasta = normalizado.VigenteHasta,
                    Activa = form.Activa,
                    Alcance = normalizado.Alcance,
                    IncluirNuevosColaboradores = normalizado.Alcance == RecurringScheduleScope.TodosLosColaboradores
                        && form.IncluirNuevosColaboradores,
                    EtiquetaCalendario = normalizado.EtiquetaCalendario,
                    Motivo = normalizado.Motivo,
                    ReglaOrigenId = regla.Id,
                    CreadoPorUserId = userId,
                    ActualizadoPorUserId = userId,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                };

                _context.RecurringScheduleRules.Add(nueva);
                await _context.SaveChangesAsync(cancellationToken);

                await ReplaceTargetsAsync(nueva.Id, normalizado, cancellationToken);

                await AuditRuleAsync(
                    PlatformAuditActions.RecurringScheduleRuleVersioned,
                    nueva,
                    normalizado,
                    conflictos,
                    userId,
                    cancellationToken,
                    antes);

                _logger.LogInformation(
                    "Bloqueo recurrente {RuleId} versionado: la versión anterior queda cerrada el {Cierre:yyyy-MM-dd} y la nueva es {NuevaId}.",
                    regla.Id,
                    regla.VigenteHasta,
                    nueva.Id);

                return RecurringScheduleSaveResult.Saved(nueva.Id, conflictos);
            }

            // Regla futura o cambios cosméticos: se edita en el sitio.
            regla.Nombre = normalizado.Nombre;
            regla.HoraInicio = normalizado.HoraInicio;
            regla.HoraFin = normalizado.HoraFin;
            regla.DiasSemanaMask = normalizado.DiasSemanaMask;
            regla.VigenteDesde = normalizado.VigenteDesde;
            regla.VigenteHasta = normalizado.VigenteHasta;
            regla.Activa = form.Activa;
            regla.Alcance = normalizado.Alcance;
            regla.IncluirNuevosColaboradores = normalizado.Alcance == RecurringScheduleScope.TodosLosColaboradores
                && form.IncluirNuevosColaboradores;
            regla.EtiquetaCalendario = normalizado.EtiquetaCalendario;
            regla.Motivo = normalizado.Motivo;
            regla.ActualizadoPorUserId = userId;
            regla.UpdatedAtUtc = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
            await ReplaceTargetsAsync(regla.Id, normalizado, cancellationToken);

            await AuditRuleAsync(
                PlatformAuditActions.RecurringScheduleRuleUpdated,
                regla,
                normalizado,
                conflictos,
                userId,
                cancellationToken,
                antes);

            return RecurringScheduleSaveResult.Saved(regla.Id, conflictos);
        }

        public async Task SetActivaAsync(
            int ruleId,
            bool activa,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            var regla = await _context.RecurringScheduleRules
                .FirstOrDefaultAsync(current => current.Id == ruleId, cancellationToken)
                ?? throw new RecurringScheduleValidationException(
                    "La regla indicada no existe o no pertenece a este negocio.");

            regla.Activa = activa;
            regla.ActualizadoPorUserId = userId;
            regla.UpdatedAtUtc = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = activa
                        ? PlatformAuditActions.RecurringScheduleRuleResumed
                        : PlatformAuditActions.RecurringScheduleRulePaused,
                    EntityType = PlatformAuditEntityTypes.RecurringScheduleRule,
                    EntityId = regla.Id.ToString(),
                    TenantId = regla.TenantId,
                    AfterJson = SerializeRule(regla)
                },
                cancellationToken);
        }

        public async Task EndAsync(int ruleId, string? userId, CancellationToken cancellationToken = default)
        {
            var regla = await _context.RecurringScheduleRules
                .FirstOrDefaultAsync(current => current.Id == ruleId, cancellationToken)
                ?? throw new RecurringScheduleValidationException(
                    "La regla indicada no existe o no pertenece a este negocio.");

            var hoy = Today();
            var antes = SerializeRule(regla);

            // Baja LÓGICA: la fila se conserva para poder explicar por qué una agenda estuvo
            // bloqueada. Si la regla ni empezó, se cierra en su propia fecha de inicio.
            regla.VigenteHasta = regla.VigenteDesde > hoy ? regla.VigenteDesde : hoy;
            regla.Activa = false;
            regla.ActualizadoPorUserId = userId;
            regla.UpdatedAtUtc = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.RecurringScheduleRuleEnded,
                    EntityType = PlatformAuditEntityTypes.RecurringScheduleRule,
                    EntityId = regla.Id.ToString(),
                    TenantId = regla.TenantId,
                    BeforeJson = antes,
                    AfterJson = SerializeRule(regla)
                },
                cancellationToken);
        }

        // ─────────────── Excepciones ───────────────

        public async Task AddExceptionAsync(
            RecurringScheduleExceptionFormViewModel form,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(form);

            var regla = await _context.RecurringScheduleRules
                .AsNoTracking()
                .Include(current => current.Colaboradores)
                .FirstOrDefaultAsync(current => current.Id == form.RuleId, cancellationToken)
                ?? throw new RecurringScheduleValidationException(
                    "La regla indicada no existe o no pertenece a este negocio.");

            var fecha = DateOnly.FromDateTime(form.Fecha.Date);

            if (fecha < regla.VigenteDesde ||
                (regla.VigenteHasta.HasValue && fecha > regla.VigenteHasta.Value))
            {
                throw new RecurringScheduleValidationException(
                    "La fecha de la excepción queda fuera de la vigencia de la regla.",
                    nameof(form.Fecha));
            }

            if (!regla.AplicaDia(fecha.DayOfWeek))
            {
                throw new RecurringScheduleValidationException(
                    "Ese día de la semana no está incluido en la regla, así que no hay bloqueo que modificar.",
                    nameof(form.Fecha));
            }

            int? funcionarioId = null;
            if (form.FuncionarioId is > 0)
            {
                funcionarioId = form.FuncionarioId.Value;

                var existe = await _context.Funcionarios
                    .AsNoTracking()
                    .AnyAsync(funcionario => funcionario.IdFuncionario == funcionarioId.Value, cancellationToken);

                if (!existe)
                {
                    throw new RecurringScheduleValidationException(
                        "El colaborador indicado no existe o no pertenece a este negocio.",
                        nameof(form.FuncionarioId));
                }

                if (regla.Alcance == RecurringScheduleScope.ColaboradoresSeleccionados &&
                    regla.Colaboradores.All(target => target.FuncionarioId != funcionarioId.Value))
                {
                    throw new RecurringScheduleValidationException(
                        "Ese colaborador no está incluido en la regla.",
                        nameof(form.FuncionarioId));
                }
            }

            TimeOnly? inicioAlternativo = null;
            TimeOnly? finAlternativo = null;

            if (form.Tipo == RecurringScheduleExceptionType.CambiarHorario)
            {
                if (!form.HoraInicioAlternativa.HasValue || !form.HoraFinAlternativa.HasValue)
                {
                    throw new RecurringScheduleValidationException(
                        "Indicá el horario alternativo para esa fecha.",
                        nameof(form.HoraInicioAlternativa));
                }

                if (form.HoraFinAlternativa.Value <= form.HoraInicioAlternativa.Value)
                {
                    throw new RecurringScheduleValidationException(
                        "La hora final del horario alternativo debe ser mayor que la inicial.",
                        nameof(form.HoraFinAlternativa));
                }

                inicioAlternativo = form.HoraInicioAlternativa;
                finAlternativo = form.HoraFinAlternativa;
            }

            if (form.Tipo == RecurringScheduleExceptionType.ExcluirColaborador && funcionarioId is null)
            {
                throw new RecurringScheduleValidationException(
                    "Para excluir a un colaborador hay que indicar cuál.",
                    nameof(form.FuncionarioId));
            }

            var duplicada = await _context.RecurringScheduleExceptions
                .AsNoTracking()
                .AnyAsync(
                    exception => exception.RuleId == form.RuleId &&
                                 exception.Fecha == fecha &&
                                 exception.FuncionarioId == funcionarioId,
                    cancellationToken);

            if (duplicada)
            {
                throw new RecurringScheduleValidationException(
                    "Ya existe una excepción para esa regla, fecha y colaborador.");
            }

            var excepcion = new RecurringScheduleException
            {
                RuleId = form.RuleId,
                FuncionarioId = funcionarioId,
                Fecha = fecha,
                Tipo = form.Tipo,
                HoraInicioAlternativa = inicioAlternativo,
                HoraFinAlternativa = finAlternativo,
                Motivo = NormalizeText(form.Motivo, 200),
                CreadoPorUserId = userId,
                CreatedAtUtc = DateTime.UtcNow
            };

            _context.RecurringScheduleExceptions.Add(excepcion);
            await _context.SaveChangesAsync(cancellationToken);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.RecurringScheduleExceptionCreated,
                    EntityType = PlatformAuditEntityTypes.RecurringScheduleRule,
                    EntityId = form.RuleId.ToString(),
                    TenantId = excepcion.TenantId,
                    Reason = excepcion.Motivo,
                    AfterJson = JsonSerializer.Serialize(new
                    {
                        Fecha = fecha.ToString("yyyy-MM-dd"),
                        FuncionarioId = funcionarioId,
                        Tipo = form.Tipo.ToString(),
                        HoraInicio = inicioAlternativo?.ToString("HH:mm"),
                        HoraFin = finAlternativo?.ToString("HH:mm")
                    })
                },
                cancellationToken);
        }

        public async Task RemoveExceptionAsync(
            int exceptionId,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            var excepcion = await _context.RecurringScheduleExceptions
                .FirstOrDefaultAsync(current => current.Id == exceptionId, cancellationToken)
                ?? throw new RecurringScheduleValidationException(
                    "La excepción indicada no existe o no pertenece a este negocio.");

            var snapshot = JsonSerializer.Serialize(new
            {
                excepcion.RuleId,
                Fecha = excepcion.Fecha.ToString("yyyy-MM-dd"),
                excepcion.FuncionarioId,
                Tipo = excepcion.Tipo.ToString()
            });

            var ruleId = excepcion.RuleId;
            var tenantId = excepcion.TenantId;

            _context.RecurringScheduleExceptions.Remove(excepcion);
            await _context.SaveChangesAsync(cancellationToken);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.RecurringScheduleExceptionRemoved,
                    EntityType = PlatformAuditEntityTypes.RecurringScheduleRule,
                    EntityId = ruleId.ToString(),
                    TenantId = tenantId,
                    BeforeJson = snapshot
                },
                cancellationToken);
        }

        // ─────────────── Normalización y validación ───────────────

        /// <summary>Regla ya validada y lista para persistir.</summary>
        private sealed record NormalizedRule
        {
            public string Nombre { get; init; } = string.Empty;
            public TimeOnly HoraInicio { get; init; }
            public TimeOnly HoraFin { get; init; }
            public int DiasSemanaMask { get; init; }
            public DateOnly VigenteDesde { get; init; }
            public DateOnly? VigenteHasta { get; init; }
            public RecurringScheduleScope Alcance { get; init; }
            public List<int> FuncionarioIds { get; init; } = new();
            public string? EtiquetaCalendario { get; init; }
            public string? Motivo { get; init; }
        }

        private async Task<NormalizedRule> NormalizeAsync(
            RecurringScheduleRuleFormViewModel form,
            CancellationToken cancellationToken)
        {
            var nombre = NormalizeText(form.Nombre, 100)
                ?? throw new RecurringScheduleValidationException(
                    "Indicá un nombre para la regla.",
                    nameof(form.Nombre));

            if (form.HoraFin <= form.HoraInicio)
            {
                throw new RecurringScheduleValidationException(
                    "La hora final debe ser mayor que la hora inicial.",
                    nameof(form.HoraFin));
            }

            var duracion = (int)(form.HoraFin.ToTimeSpan() - form.HoraInicio.ToTimeSpan()).TotalMinutes;
            if (duracion < RecurringScheduleRule.MinDurationMinutes ||
                duracion > RecurringScheduleRule.MaxDurationMinutes)
            {
                throw new RecurringScheduleValidationException(
                    $"La duración del bloqueo debe estar entre {RecurringScheduleRule.MinDurationMinutes} y {RecurringScheduleRule.MaxDurationMinutes} minutos.",
                    nameof(form.HoraFin));
            }

            var mask = form.DiasSemanaMask;
            if (mask == 0)
            {
                throw new RecurringScheduleValidationException(
                    "Seleccioná al menos un día de la semana.",
                    nameof(form.Dias));
            }

            var vigenteDesde = DateOnly.FromDateTime(form.VigenteDesde.Date);
            var vigenteHasta = form.VigenteHasta.HasValue
                ? DateOnly.FromDateTime(form.VigenteHasta.Value.Date)
                : (DateOnly?)null;

            if (vigenteHasta.HasValue && vigenteHasta.Value < vigenteDesde)
            {
                throw new RecurringScheduleValidationException(
                    "La fecha final debe ser mayor o igual a la fecha inicial.",
                    nameof(form.VigenteHasta));
            }

            var funcionarioIds = new List<int>();

            if (form.Alcance == RecurringScheduleScope.ColaboradoresSeleccionados)
            {
                var solicitados = (form.FuncionarioIds ?? new List<int>())
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();

                if (solicitados.Count == 0)
                {
                    throw new RecurringScheduleValidationException(
                        "Seleccioná al menos un colaborador o aplicá la regla a todo el equipo.",
                        nameof(form.FuncionarioIds));
                }

                // Se validan contra el tenant actual: un Id de otro negocio no se encuentra.
                funcionarioIds = await _context.Funcionarios
                    .AsNoTracking()
                    .Where(funcionario => solicitados.Contains(funcionario.IdFuncionario))
                    .Select(funcionario => funcionario.IdFuncionario)
                    .ToListAsync(cancellationToken);

                if (funcionarioIds.Count == 0)
                {
                    throw new RecurringScheduleValidationException(
                        "Los colaboradores seleccionados no existen o no pertenecen a este negocio.",
                        nameof(form.FuncionarioIds));
                }
            }

            return new NormalizedRule
            {
                Nombre = nombre,
                HoraInicio = TruncateToMinute(form.HoraInicio),
                HoraFin = TruncateToMinute(form.HoraFin),
                DiasSemanaMask = mask,
                VigenteDesde = vigenteDesde,
                VigenteHasta = vigenteHasta,
                Alcance = form.Alcance,
                FuncionarioIds = funcionarioIds,
                EtiquetaCalendario = NormalizeText(form.EtiquetaCalendario, 60),
                Motivo = NormalizeText(form.Motivo, 60)
            };
        }

        /// <summary>
        /// Rechaza duplicados exactos (mismo horario, mismos días, misma vigencia y mismo alcance)
        /// entre reglas activas: dos bloqueos idénticos solo generan confusión.
        /// </summary>
        private async Task EnsureNoDuplicateAsync(
            NormalizedRule regla,
            int? ruleIdExcluida,
            CancellationToken cancellationToken)
        {
            var candidatas = await _context.RecurringScheduleRules
                .AsNoTracking()
                .Include(current => current.Colaboradores)
                .Where(current =>
                    current.Activa &&
                    current.HoraInicio == regla.HoraInicio &&
                    current.HoraFin == regla.HoraFin &&
                    current.DiasSemanaMask == regla.DiasSemanaMask &&
                    current.VigenteDesde == regla.VigenteDesde &&
                    current.VigenteHasta == regla.VigenteHasta &&
                    current.Alcance == regla.Alcance &&
                    (!ruleIdExcluida.HasValue || current.Id != ruleIdExcluida.Value))
                .ToListAsync(cancellationToken);

            var esperados = regla.FuncionarioIds.OrderBy(id => id).ToList();

            var duplicada = candidatas.Any(current =>
                current.Colaboradores
                    .Select(target => target.FuncionarioId)
                    .OrderBy(id => id)
                    .SequenceEqual(esperados));

            if (duplicada)
            {
                throw new RecurringScheduleValidationException(
                    "Ya existe una regla activa idéntica (mismo horario, días, vigencia y colaboradores).");
            }
        }

        private async Task<List<int>> ResolveFuncionariosAlcanzadosAsync(
            NormalizedRule regla,
            CancellationToken cancellationToken)
        {
            if (regla.Alcance == RecurringScheduleScope.ColaboradoresSeleccionados)
            {
                return regla.FuncionarioIds;
            }

            return await _context.Funcionarios
                .AsNoTracking()
                .Where(funcionario => funcionario.Activo)
                .Select(funcionario => funcionario.IdFuncionario)
                .ToListAsync(cancellationToken);
        }

        /// <summary>
        /// Sincroniza los colaboradores explícitos de la regla. En alcance global se dejan CERO
        /// filas a propósito: la pertenencia se evalúa dinámicamente y así un colaborador nuevo
        /// entra sin tocar nada. La operación es idempotente.
        /// </summary>
        private async Task ReplaceTargetsAsync(
            int ruleId,
            NormalizedRule regla,
            CancellationToken cancellationToken)
        {
            var actuales = await _context.RecurringScheduleRuleTargets
                .Where(target => target.RuleId == ruleId)
                .ToListAsync(cancellationToken);

            var deseados = regla.Alcance == RecurringScheduleScope.ColaboradoresSeleccionados
                ? regla.FuncionarioIds.ToHashSet()
                : new HashSet<int>();

            foreach (var target in actuales.Where(target => !deseados.Contains(target.FuncionarioId)))
            {
                _context.RecurringScheduleRuleTargets.Remove(target);
            }

            foreach (var funcionarioId in deseados.Where(id => actuales.All(target => target.FuncionarioId != id)))
            {
                _context.RecurringScheduleRuleTargets.Add(new RecurringScheduleRuleTarget
                {
                    RuleId = ruleId,
                    FuncionarioId = funcionarioId
                });
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        // ─────────────── Helpers ───────────────

        private DateOnly Today() => DateOnly.FromDateTime(_businessDateTimeProvider.Today());

        private async Task<IReadOnlyList<RecurringScheduleFuncionarioOption>> LoadFuncionariosAsync(
            CancellationToken cancellationToken) =>
            await _context.Funcionarios
                .AsNoTracking()
                .Where(funcionario => funcionario.Activo)
                .OrderBy(funcionario => funcionario.Nombre)
                .Select(funcionario => new RecurringScheduleFuncionarioOption(
                    funcionario.IdFuncionario,
                    funcionario.Nombre,
                    funcionario.ColorCalendario))
                .ToListAsync(cancellationToken);

        private async Task AuditRuleAsync(
            string action,
            RecurringScheduleRule regla,
            NormalizedRule normalizado,
            RecurringScheduleConflictSummaryViewModel conflictos,
            string? userId,
            CancellationToken cancellationToken,
            string? antes = null)
        {
            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = action,
                    EntityType = PlatformAuditEntityTypes.RecurringScheduleRule,
                    EntityId = regla.Id.ToString(),
                    TenantId = regla.TenantId,
                    BeforeJson = antes,
                    AfterJson = SerializeRule(regla)
                },
                cancellationToken);

            // Señal separada y explícita: la regla se activó sabiendo que hay citas que coinciden.
            // Las citas NO se tocaron; queda constancia de la decisión.
            if (conflictos.TieneConflictos)
            {
                await _auditService.TryLogAsync(
                    new PlatformAuditEntry
                    {
                        Action = PlatformAuditActions.RecurringScheduleRuleActivatedWithConflicts,
                        EntityType = PlatformAuditEntityTypes.RecurringScheduleRule,
                        EntityId = regla.Id.ToString(),
                        TenantId = regla.TenantId,
                        Reason = conflictos.Mensaje,
                        AfterJson = JsonSerializer.Serialize(new
                        {
                            Total = conflictos.Total,
                            CitasAfectadas = conflictos.Conflictos
                                .Take(25)
                                .Select(conflicto => new
                                {
                                    conflicto.CitaId,
                                    Fecha = conflicto.FechaHoraCita.ToString("yyyy-MM-dd HH:mm"),
                                    conflicto.FuncionarioId
                                })
                        })
                    },
                    cancellationToken);
            }
        }

        private static string SerializeRule(RecurringScheduleRule regla) =>
            JsonSerializer.Serialize(new
            {
                regla.Id,
                regla.Nombre,
                Horario = FormatHorario(regla.HoraInicio, regla.HoraFin),
                Dias = RecurringScheduleOccurrenceCalculator.DescribirDias(regla.DiasSemanaMask),
                VigenteDesde = regla.VigenteDesde.ToString("yyyy-MM-dd"),
                VigenteHasta = regla.VigenteHasta?.ToString("yyyy-MM-dd"),
                regla.Activa,
                Alcance = regla.Alcance.ToString(),
                regla.IncluirNuevosColaboradores
            });

        private static string FormatHorario(TimeOnly inicio, TimeOnly fin) =>
            $"{inicio:HH\\:mm} – {fin:HH\\:mm}";

        private static string DescribirAlcance(RecurringScheduleScope alcance, int colaboradores) =>
            alcance == RecurringScheduleScope.TodosLosColaboradores
                ? "Todos los colaboradores"
                : colaboradores == 1
                    ? "1 colaborador"
                    : $"{colaboradores} colaboradores";

        private static TimeOnly TruncateToMinute(TimeOnly value) => new(value.Hour, value.Minute);

        private static string? NormalizeText(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = string.Join(
                ' ',
                value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
        }
    }
}
