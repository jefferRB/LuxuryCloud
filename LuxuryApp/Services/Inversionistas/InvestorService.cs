using System.Net.Mail;
using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Models.Platform;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Platform;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Inversionistas
{
    /// <summary>
    /// Gestión de inversionistas y acuerdos.
    ///
    /// <para>Reglas duras que se validan acá y en ningún otro lado:</para>
    /// <list type="number">
    ///   <item>La suma de participaciones de acuerdos activos que se solapan no puede pasar de 100 %.</item>
    ///   <item>Un cambio de porcentaje entra en vigor al inicio de un periodo financiero; nunca a mitad.</item>
    ///   <item>Un cambio no reescribe el acuerdo anterior: lo cierra y crea una versión nueva.</item>
    /// </list>
    /// </summary>
    public sealed class InvestorService : IInvestorService
    {
        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly IPlatformAuditService _auditService;
        private readonly ILogger<InvestorService> _logger;

        public InvestorService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider,
            IPlatformAuditService auditService,
            ILogger<InvestorService> logger)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
            _auditService = auditService;
            _logger = logger;
        }

        public async Task<InvestorsIndexViewModel> BuildIndexAsync(CancellationToken cancellationToken = default)
        {
            var hoy = Today();

            var inversionistas = await _context.TenantInvestors
                .AsNoTracking()
                .Include(investor => investor.Acuerdos)
                .OrderByDescending(investor => investor.Activo)
                .ThenBy(investor => investor.Nombre)
                .ToListAsync(cancellationToken);

            var saldos = await _context.InvestorStatements
                .AsNoTracking()
                .Where(statement => statement.Estado != InvestorStatementStatus.Voided &&
                                    statement.Estado != InvestorStatementStatus.Draft)
                .GroupBy(statement => statement.InvestorId)
                .Select(group => new
                {
                    InvestorId = group.Key,
                    Saldo = group.Sum(statement => statement.SaldoPendiente),
                    Pendientes = group.Count(statement => statement.SaldoPendiente > 0m)
                })
                .ToListAsync(cancellationToken);

            var saldoPorInversionista = saldos.ToDictionary(row => row.InvestorId);

            var filas = new List<InvestorListItemViewModel>(inversionistas.Count);
            foreach (var investor in inversionistas)
            {
                var vigente = ResolveVigente(investor.Acuerdos, hoy);
                saldoPorInversionista.TryGetValue(investor.Id, out var saldo);

                filas.Add(new InvestorListItemViewModel
                {
                    Id = investor.Id,
                    Nombre = investor.Nombre,
                    Email = investor.Email,
                    Telefono = investor.Telefono,
                    Activo = investor.Activo,
                    PorcentajeVigente = vigente?.ParticipacionPorcentaje,
                    Frecuencia = vigente?.Frecuencia,
                    ProximoReporte = vigente is null
                        ? null
                        : InvestorSettlementPeriodResolver.PreviousClosedPeriod(vigente, hoy).Etiqueta,
                    SaldoPendiente = saldo?.Saldo ?? 0m,
                    EstadosPendientes = saldo?.Pendientes ?? 0
                });
            }

            var policy = await GetPolicyAsync(cancellationToken);

            return new InvestorsIndexViewModel
            {
                Inversionistas = filas,
                ParticipacionTotalVigente = filas
                    .Where(fila => fila.Activo && fila.PorcentajeVigente.HasValue)
                    .Sum(fila => fila.PorcentajeVigente!.Value),
                SaldoPendienteTotal = filas.Sum(fila => fila.SaldoPendiente),
                Politica = MapPolicy(policy, Array.Empty<InvestorCategoriaOption>())
            };
        }

        public async Task<InvestorFormViewModel> BuildCreateFormAsync(CancellationToken cancellationToken = default)
        {
            var hoy = Today();
            var policy = await GetPolicyAsync(cancellationToken);
            var frecuencia = policy.FrecuenciaPorDefecto;
            var proximoInicio = InvestorSettlementPeriodResolver.Resolve(frecuencia, null, hoy).Inicio;

            return new InvestorFormViewModel
            {
                Frecuencia = frecuencia,
                TratamientoPerdidas = policy.TratamientoPerdidasPorDefecto,
                EffectiveFrom = proximoInicio.ToDateTime(TimeOnly.MinValue),
                ParticipacionOtros = await SumParticipacionVigenteAsync(null, hoy, cancellationToken),
                ProximoInicioPeriodo = proximoInicio
            };
        }

        public async Task<int> CreateAsync(
            InvestorFormViewModel form,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(form);

            var nombre = NormalizeText(form.Nombre, 150)
                ?? throw new InvestorValidationException("Indicá el nombre del inversionista.", nameof(form.Nombre));

            var email = NormalizeEmail(form.Email)
                ?? throw new InvestorValidationException("El correo del inversionista no es válido.", nameof(form.Email));

            await EnsureEmailDisponibleAsync(email, null, cancellationToken);

            var effectiveFrom = DateOnly.FromDateTime(form.EffectiveFrom.Date);
            var effectiveTo = form.EffectiveTo.HasValue ? DateOnly.FromDateTime(form.EffectiveTo.Value.Date) : (DateOnly?)null;

            var diaCorte = InvestorSettlementPeriodResolver.NormalizarDiaCorte(form.Frecuencia, form.DiaCorte);

            ValidateAgreementInput(form.ParticipacionPorcentaje, form.Frecuencia, diaCorte, effectiveFrom, effectiveTo);
            await EnsureParticipacionDisponibleAsync(
                null,
                form.ParticipacionPorcentaje,
                effectiveFrom,
                effectiveTo,
                cancellationToken);

            var ahora = DateTime.UtcNow;

            var investor = new TenantInvestor
            {
                Nombre = nombre,
                Email = email,
                Telefono = NormalizeText(form.Telefono, 30),
                Activo = form.Activo,
                NotasInternas = NormalizeText(form.NotasInternas, 1000),
                CreatedAtUtc = ahora,
                UpdatedAtUtc = ahora,
                CreatedByUserId = userId,
                UpdatedByUserId = userId
            };

            _context.TenantInvestors.Add(investor);
            await _context.SaveChangesAsync(cancellationToken);

            var agreement = new InvestorAgreement
            {
                InvestorId = investor.Id,
                ParticipacionPorcentaje = form.ParticipacionPorcentaje,
                EffectiveFrom = effectiveFrom,
                EffectiveTo = effectiveTo,
                Frecuencia = form.Frecuencia,
                DiaCorte = diaCorte,
                TratamientoPerdidas = form.TratamientoPerdidas,
                EnvioAutomatico = form.EnvioAutomatico,
                Activo = true,
                Notas = NormalizeText(form.Notas, 1000),
                CreatedAtUtc = ahora,
                UpdatedAtUtc = ahora,
                CreatedByUserId = userId,
                UpdatedByUserId = userId
            };

            _context.InvestorAgreements.Add(agreement);
            await _context.SaveChangesAsync(cancellationToken);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.InvestorCreated,
                    EntityType = PlatformAuditEntityTypes.Investor,
                    EntityId = investor.Id.ToString(),
                    TenantId = investor.TenantId,
                    AfterJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        investor.Nombre,
                        agreement.ParticipacionPorcentaje,
                        EffectiveFrom = agreement.EffectiveFrom.ToString("yyyy-MM-dd"),
                        Frecuencia = agreement.Frecuencia.ToString()
                    })
                },
                cancellationToken);

            _logger.LogInformation(
                "Inversionista {InvestorId} creado con participación {Porcentaje}% desde {Desde:yyyy-MM-dd}.",
                investor.Id,
                agreement.ParticipacionPorcentaje,
                agreement.EffectiveFrom);

            return investor.Id;
        }

        public async Task UpdateAsync(
            int investorId,
            InvestorFormViewModel form,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(form);

            var investor = await _context.TenantInvestors
                .Include(current => current.Acuerdos)
                .FirstOrDefaultAsync(current => current.Id == investorId, cancellationToken)
                ?? throw new InvestorValidationException("El inversionista indicado no existe o no pertenece a este negocio.");

            var nombre = NormalizeText(form.Nombre, 150)
                ?? throw new InvestorValidationException("Indicá el nombre del inversionista.", nameof(form.Nombre));

            var email = NormalizeEmail(form.Email)
                ?? throw new InvestorValidationException("El correo del inversionista no es válido.", nameof(form.Email));

            await EnsureEmailDisponibleAsync(email, investorId, cancellationToken);

            var hoy = Today();
            var vigente = ResolveVigente(investor.Acuerdos, hoy);
            var effectiveFrom = DateOnly.FromDateTime(form.EffectiveFrom.Date);
            var effectiveTo = form.EffectiveTo.HasValue ? DateOnly.FromDateTime(form.EffectiveTo.Value.Date) : (DateOnly?)null;

            var antes = System.Text.Json.JsonSerializer.Serialize(new
            {
                investor.Nombre,
                investor.Email,
                investor.Activo,
                Porcentaje = vigente?.ParticipacionPorcentaje,
                Frecuencia = vigente?.Frecuencia.ToString()
            });

            investor.Nombre = nombre;
            investor.Email = email;
            investor.Telefono = NormalizeText(form.Telefono, 30);
            investor.Activo = form.Activo;
            investor.NotasInternas = NormalizeText(form.NotasInternas, 1000);
            investor.UpdatedAtUtc = DateTime.UtcNow;
            investor.UpdatedByUserId = userId;

            var diaCorte = InvestorSettlementPeriodResolver.NormalizarDiaCorte(form.Frecuencia, form.DiaCorte);

            // Cambiar el día de corte cambia las FECHAS de los periodos siguientes, así que es un
            // cambio de acuerdo con todas las letras: cierra la versión vigente y abre una nueva.
            var cambioDeAcuerdo = vigente is null ||
                vigente.ParticipacionPorcentaje != form.ParticipacionPorcentaje ||
                vigente.Frecuencia != form.Frecuencia ||
                vigente.DiaCorte != diaCorte ||
                vigente.TratamientoPerdidas != form.TratamientoPerdidas ||
                vigente.EffectiveTo != effectiveTo;

            if (cambioDeAcuerdo)
            {
                ValidateAgreementInput(form.ParticipacionPorcentaje, form.Frecuencia, diaCorte, effectiveFrom, effectiveTo);

                // Regla anti-retroactividad: un acuerdo nuevo nunca puede empezar antes del inicio
                // del periodo en curso, porque reescribiría un periodo ya calculado. El periodo en
                // curso se mide con el corte NUEVO, que es el que va a regir de acá en adelante.
                var periodoActual = InvestorSettlementPeriodResolver.Resolve(form.Frecuencia, diaCorte, hoy);
                if (effectiveFrom < periodoActual.Inicio)
                {
                    throw new InvestorValidationException(
                        $"No se puede cambiar la participación con efecto retroactivo. La fecha efectiva más antigua permitida es {periodoActual.Inicio:dd/MM/yyyy}.",
                        nameof(form.EffectiveFrom));
                }

                await EnsureParticipacionDisponibleAsync(
                    investorId,
                    form.ParticipacionPorcentaje,
                    effectiveFrom,
                    effectiveTo,
                    cancellationToken);

                if (vigente is not null)
                {
                    if (vigente.EffectiveFrom >= effectiveFrom)
                    {
                        // El acuerdo vigente todavía no cubrió ningún periodo: se corrige en el sitio.
                        vigente.ParticipacionPorcentaje = form.ParticipacionPorcentaje;
                        vigente.Frecuencia = form.Frecuencia;
                        vigente.DiaCorte = diaCorte;
                        vigente.TratamientoPerdidas = form.TratamientoPerdidas;
                        vigente.EnvioAutomatico = form.EnvioAutomatico;
                        vigente.EffectiveFrom = effectiveFrom;
                        vigente.EffectiveTo = effectiveTo;
                        vigente.Notas = NormalizeText(form.Notas, 1000);
                        vigente.UpdatedAtUtc = DateTime.UtcNow;
                        vigente.UpdatedByUserId = userId;
                    }
                    else
                    {
                        // Versionado: se cierra el acuerdo anterior el día antes del cambio y se crea uno nuevo.
                        vigente.EffectiveTo = effectiveFrom.AddDays(-1);
                        vigente.UpdatedAtUtc = DateTime.UtcNow;
                        vigente.UpdatedByUserId = userId;

                        _context.InvestorAgreements.Add(new InvestorAgreement
                        {
                            InvestorId = investor.Id,
                            ParticipacionPorcentaje = form.ParticipacionPorcentaje,
                            EffectiveFrom = effectiveFrom,
                            EffectiveTo = effectiveTo,
                            Frecuencia = form.Frecuencia,
                            DiaCorte = diaCorte,
                            TratamientoPerdidas = form.TratamientoPerdidas,
                            EnvioAutomatico = form.EnvioAutomatico,
                            Activo = true,
                            Notas = NormalizeText(form.Notas, 1000),
                            CreatedAtUtc = DateTime.UtcNow,
                            UpdatedAtUtc = DateTime.UtcNow,
                            CreatedByUserId = userId,
                            UpdatedByUserId = userId
                        });
                    }
                }
                else
                {
                    _context.InvestorAgreements.Add(new InvestorAgreement
                    {
                        InvestorId = investor.Id,
                        ParticipacionPorcentaje = form.ParticipacionPorcentaje,
                        EffectiveFrom = effectiveFrom,
                        EffectiveTo = effectiveTo,
                        Frecuencia = form.Frecuencia,
                        DiaCorte = diaCorte,
                        TratamientoPerdidas = form.TratamientoPerdidas,
                        EnvioAutomatico = form.EnvioAutomatico,
                        Activo = true,
                        Notas = NormalizeText(form.Notas, 1000),
                        CreatedAtUtc = DateTime.UtcNow,
                        UpdatedAtUtc = DateTime.UtcNow,
                        CreatedByUserId = userId,
                        UpdatedByUserId = userId
                    });
                }
            }
            else if (vigente is not null)
            {
                vigente.EnvioAutomatico = form.EnvioAutomatico;
                vigente.Notas = NormalizeText(form.Notas, 1000);
                vigente.UpdatedAtUtc = DateTime.UtcNow;
                vigente.UpdatedByUserId = userId;
            }

            await _context.SaveChangesAsync(cancellationToken);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = cambioDeAcuerdo
                        ? PlatformAuditActions.InvestorAgreementChanged
                        : PlatformAuditActions.InvestorUpdated,
                    EntityType = PlatformAuditEntityTypes.Investor,
                    EntityId = investor.Id.ToString(),
                    TenantId = investor.TenantId,
                    BeforeJson = antes,
                    AfterJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        investor.Nombre,
                        investor.Email,
                        investor.Activo,
                        Porcentaje = form.ParticipacionPorcentaje,
                        EffectiveFrom = effectiveFrom.ToString("yyyy-MM-dd"),
                        Frecuencia = form.Frecuencia.ToString()
                    })
                },
                cancellationToken);
        }

        public async Task SetActivoAsync(
            int investorId,
            bool activo,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            var investor = await _context.TenantInvestors
                .FirstOrDefaultAsync(current => current.Id == investorId, cancellationToken)
                ?? throw new InvestorValidationException("El inversionista indicado no existe o no pertenece a este negocio.");

            investor.Activo = activo;
            investor.UpdatedAtUtc = DateTime.UtcNow;
            investor.UpdatedByUserId = userId;

            await _context.SaveChangesAsync(cancellationToken);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.InvestorUpdated,
                    EntityType = PlatformAuditEntityTypes.Investor,
                    EntityId = investor.Id.ToString(),
                    TenantId = investor.TenantId,
                    AfterJson = System.Text.Json.JsonSerializer.Serialize(new { investor.Activo })
                },
                cancellationToken);
        }

        public async Task<InvestorProfitPolicy> GetPolicyAsync(CancellationToken cancellationToken = default)
        {
            var policy = await _context.InvestorProfitPolicies
                .AsNoTracking()
                .Include(current => current.CategoriasSeleccionadas)
                .FirstOrDefaultAsync(cancellationToken);

            return policy ?? InvestorProfitPolicy.CreateDefault(Guid.Empty);
        }

        public async Task<InvestorPolicyViewModel> BuildPolicyFormAsync(CancellationToken cancellationToken = default)
        {
            var policy = await GetPolicyAsync(cancellationToken);
            var categorias = await LoadCategoriasAsync(cancellationToken);
            return MapPolicy(policy, categorias);
        }

        public async Task SavePolicyAsync(
            InvestorPolicyViewModel form,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(form);

            var policy = await _context.InvestorProfitPolicies
                .Include(current => current.CategoriasSeleccionadas)
                .FirstOrDefaultAsync(cancellationToken);

            if (policy is null)
            {
                policy = new InvestorProfitPolicy();
                _context.InvestorProfitPolicies.Add(policy);
            }

            policy.ExcluirIva = form.ExcluirIva;
            policy.IncluirLiquidaciones = form.IncluirLiquidaciones;
            policy.BaseLiquidaciones = form.BaseLiquidaciones;
            policy.ModoCategoriasGasto = form.ModoCategoriasGasto;
            policy.TratamientoPerdidasPorDefecto = form.TratamientoPerdidasPorDefecto;
            policy.FrecuenciaPorDefecto = form.FrecuenciaPorDefecto;
            policy.GeneracionAutomatica = form.GeneracionAutomatica;
            policy.EnvioAutomatico = form.EnvioAutomatico;
            policy.DiasEsperaGeneracion = Math.Clamp(form.DiasEsperaGeneracion, 0, 15);
            policy.HoraGeneracion = Math.Clamp(form.HoraGeneracion, 0, 23);
            policy.UpdatedAtUtc = DateTime.UtcNow;
            policy.UpdatedByUserId = userId;

            await _context.SaveChangesAsync(cancellationToken);

            // Categorías: solo tienen sentido cuando el modo no es "Todas".
            var deseadas = form.ModoCategoriasGasto == InvestorExpenseCategoryMode.Todas
                ? new HashSet<int>()
                : (form.CategoriasSeleccionadas ?? new List<int>()).ToHashSet();

            var validas = (await LoadCategoriasAsync(cancellationToken))
                .Select(categoria => categoria.Id)
                .ToHashSet();

            deseadas.IntersectWith(validas);

            var actuales = policy.CategoriasSeleccionadas.ToList();

            foreach (var link in actuales.Where(link => !deseadas.Contains(link.CategoriaId)))
            {
                _context.InvestorPolicyExpenseCategories.Remove(link);
            }

            foreach (var categoriaId in deseadas.Where(id => actuales.All(link => link.CategoriaId != id)))
            {
                _context.InvestorPolicyExpenseCategories.Add(new InvestorPolicyExpenseCategory
                {
                    PolicyId = policy.Id,
                    CategoriaId = categoriaId
                });
            }

            await _context.SaveChangesAsync(cancellationToken);

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.InvestorPolicyUpdated,
                    EntityType = PlatformAuditEntityTypes.InvestorPolicy,
                    EntityId = policy.Id.ToString(),
                    TenantId = policy.TenantId,
                    AfterJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        policy.ExcluirIva,
                        policy.IncluirLiquidaciones,
                        BaseLiquidaciones = policy.BaseLiquidaciones.ToString(),
                        ModoCategoriasGasto = policy.ModoCategoriasGasto.ToString(),
                        policy.GeneracionAutomatica,
                        policy.EnvioAutomatico
                    })
                },
                cancellationToken);
        }

        public async Task<InvestorAgreement?> GetAgreementForDateAsync(
            int investorId,
            DateOnly fecha,
            CancellationToken cancellationToken = default)
        {
            var acuerdos = await _context.InvestorAgreements
                .AsNoTracking()
                .Where(agreement => agreement.InvestorId == investorId)
                .ToListAsync(cancellationToken);

            return ResolveVigente(acuerdos, fecha);
        }

        public async Task<TenantInvestor?> GetByAssociateAsync(
            int associateId,
            CancellationToken cancellationToken = default) =>
            await _context.TenantInvestors
                .AsNoTracking()
                .Include(investor => investor.Acuerdos)
                .FirstOrDefaultAsync(investor => investor.AssociateId == associateId, cancellationToken);

        public async Task<int?> GetAssociateIdAsync(
            int investorId,
            CancellationToken cancellationToken = default) =>
            await _context.TenantInvestors
                .AsNoTracking()
                .Where(investor => investor.Id == investorId)
                .Select(investor => investor.AssociateId)
                .FirstOrDefaultAsync(cancellationToken);

        public async Task LinkToAssociateAsync(
            int investorId,
            int associateId,
            CancellationToken cancellationToken = default)
        {
            var investor = await _context.TenantInvestors
                .FirstOrDefaultAsync(current => current.Id == investorId, cancellationToken)
                ?? throw new InvestorValidationException(
                    "El perfil de inversionista indicado no existe o no pertenece a este negocio.");

            if (investor.AssociateId == associateId)
            {
                return;
            }

            if (investor.AssociateId.HasValue)
            {
                // Reasignar un perfil movería estados de cuenta y pagos de una persona a otra.
                throw new InvestorValidationException(
                    "Ese perfil de inversionista ya pertenece a otro asociado.");
            }

            investor.AssociateId = associateId;
            investor.UpdatedAtUtc = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
        }

        // ─────────────── Validaciones ───────────────

        private static void ValidateAgreementInput(
            decimal porcentaje,
            InvestorPayoutFrequency frecuencia,
            int? diaCorte,
            DateOnly effectiveFrom,
            DateOnly? effectiveTo)
        {
            if (porcentaje <= 0m || porcentaje > InvestorDefaults.MaxParticipacionAcumulada)
            {
                throw new InvestorValidationException(
                    "El porcentaje de participación debe estar entre 0,01 y 100.",
                    nameof(InvestorFormViewModel.ParticipacionPorcentaje));
            }

            if (effectiveTo.HasValue && effectiveTo.Value < effectiveFrom)
            {
                throw new InvestorValidationException(
                    "La fecha final del acuerdo no puede ser anterior a la fecha inicial.",
                    nameof(InvestorFormViewModel.EffectiveTo));
            }

            // Cambios a mitad de periodo: se rechazan con un mensaje que dice exactamente qué fecha
            // usar. El límite depende del día de corte: con corte 20 un periodo abre el día 21,
            // no el día 1.
            if (!InvestorSettlementPeriodResolver.EsInicioDePeriodo(frecuencia, diaCorte, effectiveFrom))
            {
                var periodo = InvestorSettlementPeriodResolver.Resolve(frecuencia, diaCorte, effectiveFrom);
                throw new InvestorValidationException(
                    $"El cambio debe entrar en vigor al inicio de un periodo {InvestorPeriodCalculator.FrecuenciaTexto(frecuencia).ToLowerInvariant()}. " +
                    $"La fecha {effectiveFrom:dd/MM/yyyy} cae a mitad del periodo {periodo.Etiqueta}. " +
                    $"Usá {periodo.Fin.AddDays(1):dd/MM/yyyy} (o {periodo.Inicio:dd/MM/yyyy} si querés cubrir el periodo completo).",
                    nameof(InvestorFormViewModel.EffectiveFrom));
            }
        }

        /// <summary>
        /// La suma de participaciones de acuerdos activos que se solapan con el rango no puede
        /// pasar de 100 %. Se evalúa contra el peor caso: cualquier acuerdo que comparta al menos
        /// un día con el nuevo rango.
        /// </summary>
        private async Task EnsureParticipacionDisponibleAsync(
            int? investorIdExcluido,
            decimal porcentaje,
            DateOnly effectiveFrom,
            DateOnly? effectiveTo,
            CancellationToken cancellationToken)
        {
            var hasta = effectiveTo ?? DateOnly.MaxValue;

            var acuerdos = await _context.InvestorAgreements
                .AsNoTracking()
                .Include(agreement => agreement.Investor)
                .Where(agreement => agreement.Activo)
                .Where(agreement => investorIdExcluido == null || agreement.InvestorId != investorIdExcluido.Value)
                .ToListAsync(cancellationToken);

            var solapados = acuerdos
                .Where(agreement => agreement.Investor is null || agreement.Investor.Activo)
                .Where(agreement => agreement.SolapaRango(effectiveFrom, hasta))
                .ToList();

            var ocupado = solapados.Sum(agreement => agreement.ParticipacionPorcentaje);
            var total = ocupado + porcentaje;

            if (total > InvestorDefaults.MaxParticipacionAcumulada)
            {
                var disponible = Math.Max(InvestorDefaults.MaxParticipacionAcumulada - ocupado, 0m);
                throw new InvestorValidationException(
                    $"La participación acumulada llegaría a {total:0.##} %. Otros acuerdos vigentes en ese periodo ya suman {ocupado:0.##} %, " +
                    $"así que el máximo disponible es {disponible:0.##} %.",
                    nameof(InvestorFormViewModel.ParticipacionPorcentaje));
            }
        }

        private async Task EnsureEmailDisponibleAsync(
            string email,
            int? investorIdExcluido,
            CancellationToken cancellationToken)
        {
            var existe = await _context.TenantInvestors
                .AsNoTracking()
                .AnyAsync(
                    investor => investor.Email == email &&
                                (investorIdExcluido == null || investor.Id != investorIdExcluido.Value),
                    cancellationToken);

            if (existe)
            {
                throw new InvestorValidationException(
                    "Ya existe un inversionista con ese correo en este negocio.",
                    nameof(InvestorFormViewModel.Email));
            }
        }

        private async Task<decimal> SumParticipacionVigenteAsync(
            int? investorIdExcluido,
            DateOnly fecha,
            CancellationToken cancellationToken)
        {
            var acuerdos = await _context.InvestorAgreements
                .AsNoTracking()
                .Include(agreement => agreement.Investor)
                .Where(agreement => agreement.Activo)
                .Where(agreement => investorIdExcluido == null || agreement.InvestorId != investorIdExcluido.Value)
                .ToListAsync(cancellationToken);

            return acuerdos
                .Where(agreement => agreement.Investor is null || agreement.Investor.Activo)
                .Where(agreement => agreement.CubreFecha(fecha))
                .Sum(agreement => agreement.ParticipacionPorcentaje);
        }

        // ─────────────── Helpers ───────────────

        private DateOnly Today() => DateOnly.FromDateTime(_businessDateTimeProvider.Today());

        /// <summary>
        /// Acuerdo vigente en la fecha. Si por datos históricos hubiera más de uno, gana el de
        /// fecha efectiva más reciente (el más nuevo manda), nunca el orden alfabético ni el Id.
        /// </summary>
        private static InvestorAgreement? ResolveVigente(IEnumerable<InvestorAgreement> acuerdos, DateOnly fecha) =>
            acuerdos
                .Where(agreement => agreement.CubreFecha(fecha))
                .OrderByDescending(agreement => agreement.EffectiveFrom)
                .ThenByDescending(agreement => agreement.Id)
                .FirstOrDefault();

        private async Task<IReadOnlyList<InvestorCategoriaOption>> LoadCategoriasAsync(CancellationToken cancellationToken) =>
            await _context.Categorias
                .AsNoTracking()
                .Where(categoria => categoria.Activo)
                .OrderBy(categoria => categoria.Nombre)
                .Select(categoria => new InvestorCategoriaOption(categoria.Id, categoria.Nombre ?? "Sin nombre"))
                .ToListAsync(cancellationToken);

        private static InvestorPolicyViewModel MapPolicy(
            InvestorProfitPolicy policy,
            IReadOnlyList<InvestorCategoriaOption> categorias) =>
            new()
            {
                ExcluirIva = policy.ExcluirIva,
                IncluirLiquidaciones = policy.IncluirLiquidaciones,
                BaseLiquidaciones = policy.BaseLiquidaciones,
                ModoCategoriasGasto = policy.ModoCategoriasGasto,
                CategoriasSeleccionadas = policy.CategoriasSeleccionadas
                    .Select(link => link.CategoriaId)
                    .ToList(),
                TratamientoPerdidasPorDefecto = policy.TratamientoPerdidasPorDefecto,
                FrecuenciaPorDefecto = policy.FrecuenciaPorDefecto,
                GeneracionAutomatica = policy.GeneracionAutomatica,
                EnvioAutomatico = policy.EnvioAutomatico,
                DiasEsperaGeneracion = policy.DiasEsperaGeneracion,
                HoraGeneracion = policy.HoraGeneracion,
                Categorias = categorias
            };

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

        private static string? NormalizeEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return null;
            }

            var trimmed = email.Trim();
            if (!MailAddress.TryCreate(trimmed, out var parsed) ||
                !string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return trimmed.ToLowerInvariant();
        }
    }
}
