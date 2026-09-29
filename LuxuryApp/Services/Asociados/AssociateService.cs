using System.Net.Mail;
using LuxuryApp.Models.Asociados;
using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Models.Platform;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Inversionistas;
using LuxuryApp.Services.Platform;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Asociados
{
    /// <summary>
    /// Implementación de la gestión de asociados.
    /// Ver <see cref="IAssociateService"/> para el reparto de responsabilidades.
    /// </summary>
    public sealed class AssociateService : IAssociateService
    {
        private readonly ApplicationDbContext _context;
        private readonly IInvestorService _investorService;
        private readonly IInvestorCycleService _investorCycleService;
        private readonly IAssociatePermissionService _permissionService;
        private readonly IAssociateAccessService _accessService;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly IPlatformAuditService _auditService;
        private readonly ILogger<AssociateService> _logger;

        public AssociateService(
            ApplicationDbContext context,
            IInvestorService investorService,
            IInvestorCycleService investorCycleService,
            IAssociatePermissionService permissionService,
            IAssociateAccessService accessService,
            IBusinessDateTimeProvider businessDateTimeProvider,
            IPlatformAuditService auditService,
            ILogger<AssociateService> logger)
        {
            _context = context;
            _investorService = investorService;
            _investorCycleService = investorCycleService;
            _permissionService = permissionService;
            _accessService = accessService;
            _businessDateTimeProvider = businessDateTimeProvider;
            _auditService = auditService;
            _logger = logger;
        }

        // ─────────────── Lectura ───────────────

        public async Task<AssociatesIndexViewModel> BuildIndexAsync(
            bool puedeAdministrar,
            CancellationToken cancellationToken = default)
        {
            var hoy = Today();

            // Una sola consulta proyectada: sin N+1 ni entidades completas en memoria.
            var filas = await _context.Associates
                .AsNoTracking()
                .OrderByDescending(associate => associate.Activo)
                .ThenBy(associate => associate.Nombre)
                .Select(associate => new
                {
                    associate.Id,
                    associate.Nombre,
                    associate.Email,
                    associate.Telefono,
                    associate.Puesto,
                    associate.Activo,
                    associate.AppUsuarioId,
                    CuentaActiva = _context.Users
                        .Where(user => user.Id == associate.AppUsuarioId)
                        .Select(user => (bool?)user.State)
                        .FirstOrDefault(),
                    Tipos = associate.Tipos.Select(tipo => tipo.Tipo).ToList(),
                    Acuerdos = associate.PerfilInversionista == null
                        ? null
                        : associate.PerfilInversionista.Acuerdos
                            .Where(agreement => agreement.Activo)
                            .Select(agreement => new
                            {
                                agreement.ParticipacionPorcentaje,
                                agreement.EffectiveFrom,
                                agreement.EffectiveTo,
                                agreement.Frecuencia,
                                agreement.DiaCorte,
                                agreement.Id
                            })
                            .ToList()
                })
                .ToListAsync(cancellationToken);

            var items = new List<AssociateListItemViewModel>(filas.Count);

            foreach (var fila in filas)
            {
                var vigente = fila.Acuerdos?
                    .Where(agreement => agreement.EffectiveFrom <= hoy &&
                                        (agreement.EffectiveTo == null || agreement.EffectiveTo >= hoy))
                    .OrderByDescending(agreement => agreement.EffectiveFrom)
                    .ThenByDescending(agreement => agreement.Id)
                    .FirstOrDefault();

                var estadoAcceso = string.IsNullOrWhiteSpace(fila.AppUsuarioId)
                    ? AssociateAccessState.SinAcceso
                    : fila.CuentaActiva == true
                        ? AssociateAccessState.AccesoActivo
                        : AssociateAccessState.AccesoBloqueado;

                items.Add(new AssociateListItemViewModel
                {
                    Id = fila.Id,
                    Nombre = fila.Nombre,
                    Email = fila.Email,
                    Telefono = fila.Telefono,
                    Puesto = fila.Puesto,
                    Activo = fila.Activo,
                    Tipos = fila.Tipos.OrderBy(tipo => (int)tipo).ToArray(),
                    EstadoAcceso = estadoAcceso,
                    ParticipacionVigente = vigente?.ParticipacionPorcentaje,
                    CorteTexto = vigente is null
                        ? null
                        : InvestorSettlementPeriodResolver.EtiquetaCorte(vigente.Frecuencia, vigente.DiaCorte)
                });
            }

            return new AssociatesIndexViewModel
            {
                Asociados = items,
                TotalActivos = items.Count(item => item.Activo),
                TotalConAcceso = items.Count(item => item.EstadoAcceso == AssociateAccessState.AccesoActivo),
                ParticipacionAsignada = items
                    .Where(item => item.Activo && item.ParticipacionVigente.HasValue)
                    .Sum(item => item.ParticipacionVigente!.Value),
                PuedeAdministrar = puedeAdministrar
            };
        }

        public async Task<AssociateFormViewModel> BuildCreateFormAsync(CancellationToken cancellationToken = default)
        {
            var hoy = Today();
            var policy = await _investorService.GetPolicyAsync(cancellationToken);
            var frecuencia = policy.FrecuenciaPorDefecto;

            var proximoInicio = InvestorSettlementPeriodResolver.Resolve(frecuencia, null, hoy).Inicio;

            return new AssociateFormViewModel
            {
                Activo = true,
                DarAcceso = false,
                Frecuencia = frecuencia,
                DiaCorte = null,
                TratamientoPerdidas = policy.TratamientoPerdidasPorDefecto,
                EffectiveFrom = proximoInicio.ToDateTime(TimeOnly.MinValue),
                ProximoInicioPeriodo = proximoInicio,
                ParticipacionOtros = await SumParticipacionVigenteAsync(null, hoy, cancellationToken)
            };
        }

        public async Task<AssociateFormViewModel> RehydrateFormAsync(
            AssociateFormViewModel form,
            CancellationToken cancellationToken = default)
        {
            var hoy = Today();
            var investorId = form.Id.HasValue
                ? (await _investorService.GetByAssociateAsync(form.Id.Value, cancellationToken))?.Id
                : null;

            form.ParticipacionOtros = await SumParticipacionVigenteAsync(investorId, hoy, cancellationToken);
            form.ProximoInicioPeriodo = InvestorSettlementPeriodResolver.Resolve(
                form.Frecuencia,
                InvestorSettlementPeriodResolver.NormalizarDiaCorte(form.Frecuencia, form.DiaCorte),
                hoy).Inicio;
            return form;
        }

        public async Task<AssociateDetailViewModel?> BuildDetailAsync(
            int associateId,
            bool puedeAdministrar,
            CancellationToken cancellationToken = default)
        {
            var associate = await _context.Associates
                .AsNoTracking()
                .Include(current => current.Tipos)
                .FirstOrDefaultAsync(current => current.Id == associateId, cancellationToken);

            if (associate is null)
            {
                return null;
            }

            var tipos = associate.Tipos.Select(tipo => tipo.Tipo).OrderBy(tipo => (int)tipo).ToList();

            var datos = new AssociateFormViewModel
            {
                Id = associate.Id,
                Nombre = associate.Nombre,
                Email = associate.Email,
                Telefono = associate.Telefono,
                Puesto = associate.Puesto,
                Activo = associate.Activo,
                NotasInternas = associate.NotasInternas,
                Tipos = tipos,
                DarAcceso = associate.TieneAcceso
            };

            var acceso = await _accessService.ObtenerEstadoAsync(associateId, cancellationToken);
            var permisos = await _permissionService.ObtenerDeAsociadoAsync(associateId, cancellationToken);

            AssociateParticipationViewModel? participacion = null;
            InvestorFinancialSummaryViewModel? resumen = null;

            if (tipos.Contains(AssociateType.Inversionista))
            {
                participacion = await BuildParticipationAsync(associateId, cancellationToken);

                // El resumen del dinero (último corte emitido, ciclo en curso, saldo) lo arma el
                // servicio de ciclos: acá no se deducen fechas de corte ni se suman montos.
                if (participacion.InvestorId.HasValue)
                {
                    resumen = await _investorCycleService.BuildSummaryAsync(
                        participacion.InvestorId.Value,
                        cancellationToken);
                }
            }

            return new AssociateDetailViewModel
            {
                Datos = datos,
                Acceso = acceso,
                Permisos = permisos,
                Participacion = participacion,
                ResumenFinanciero = resumen,
                PuedeAdministrar = puedeAdministrar
            };
        }

        private async Task<AssociateParticipationViewModel> BuildParticipationAsync(
            int associateId,
            CancellationToken cancellationToken)
        {
            var hoy = Today();
            var perfil = await _investorService.GetByAssociateAsync(associateId, cancellationToken);

            if (perfil is null)
            {
                var frecuenciaDefault = (await _investorService.GetPolicyAsync(cancellationToken)).FrecuenciaPorDefecto;

                return new AssociateParticipationViewModel
                {
                    AssociateId = associateId,
                    Frecuencia = frecuenciaDefault,
                    CorteTexto = InvestorSettlementPeriodResolver.EtiquetaCorte(frecuenciaDefault, null),
                    CorteDescripcion = InvestorSettlementPeriodResolver.DescribirCorte(frecuenciaDefault, null),
                    ParticipacionOtros = await SumParticipacionVigenteAsync(null, hoy, cancellationToken),
                    ProximoInicioPeriodo = InvestorSettlementPeriodResolver.Resolve(frecuenciaDefault, null, hoy).Inicio
                };
            }

            var vigente = perfil.Acuerdos
                .Where(agreement => agreement.CubreFecha(hoy))
                .OrderByDescending(agreement => agreement.EffectiveFrom)
                .ThenByDescending(agreement => agreement.Id)
                .FirstOrDefault();

            var historial = perfil.Acuerdos
                .OrderByDescending(agreement => agreement.EffectiveFrom)
                .ThenByDescending(agreement => agreement.Id)
                .Select(agreement => new AssociateParticipationHistoryRow(
                    agreement.ParticipacionPorcentaje,
                    agreement.EffectiveFrom,
                    agreement.EffectiveTo,
                    agreement.Frecuencia,
                    agreement.DiaCorte,
                    vigente is not null && agreement.Id == vigente.Id))
                .ToArray();

            // El saldo pendiente NO se calcula acá: lo arma InvestorCycleService junto con el
            // último corte emitido, para que la tarjeta del saldo y la del corte no puedan
            // contradecirse (era el mismo dato sumado en dos lugares).
            var frecuencia = vigente?.Frecuencia
                ?? (await _investorService.GetPolicyAsync(cancellationToken)).FrecuenciaPorDefecto;
            var diaCorte = vigente?.DiaCorte;

            // Todas las fechas de corte salen del resolver; ni la vista ni el controlador las calculan.
            // OJO: acá NO se calcula ningún "último corte". PreviousClosedPeriod es aritmética de
            // calendario, no un estado de cuenta emitido: usarlo como "último corte" mostraba una
            // fecha que nunca existió. Ese dato sale de InvestorCycleService, leyendo la base.
            var referencia = vigente ?? new InvestorAgreement { Frecuencia = frecuencia, DiaCorte = diaCorte };
            var periodoActual = InvestorSettlementPeriodResolver.CurrentPeriod(referencia, hoy);

            return new AssociateParticipationViewModel
            {
                AssociateId = associateId,
                InvestorId = perfil.Id,
                PorcentajeVigente = vigente?.ParticipacionPorcentaje,
                Frecuencia = vigente?.Frecuencia,
                DiaCorte = diaCorte,
                CorteTexto = InvestorSettlementPeriodResolver.EtiquetaCorte(frecuencia, diaCorte),
                CorteDescripcion = InvestorSettlementPeriodResolver.DescribirCorte(frecuencia, diaCorte),
                PeriodoActualInicio = periodoActual.Inicio,
                PeriodoActualFin = periodoActual.Fin,
                ProximoCorte = periodoActual.Fin,
                TratamientoPerdidas = vigente?.TratamientoPerdidas ?? InvestorLossTreatment.NoDistribution,
                EnvioAutomatico = vigente?.EnvioAutomatico ?? false,
                VigenteDesde = vigente?.EffectiveFrom,
                Historial = historial,
                ParticipacionOtros = await SumParticipacionVigenteAsync(perfil.Id, hoy, cancellationToken),
                ProximoInicioPeriodo = InvestorSettlementPeriodResolver.ProximoInicioDePeriodo(frecuencia, diaCorte, hoy)
            };
        }

        // ─────────────── Escritura ───────────────

        public async Task<int> CreateAsync(
            AssociateFormViewModel form,
            string? actorUserId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(form);

            var nombre = NormalizeText(form.Nombre, 150)
                ?? throw new AssociateValidationException("Indicá el nombre del asociado.", nameof(form.Nombre));

            var email = NormalizeEmail(form.Email);
            var tipos = NormalizeTipos(form.Tipos);
            var esInversionista = tipos.Contains(AssociateType.Inversionista);

            ValidarCorreoSegunNecesidad(form, esInversionista, email);
            await EnsureEmailDisponibleAsync(email, null, cancellationToken);

            var ahora = DateTime.UtcNow;
            var associate = new Associate
            {
                Nombre = nombre,
                Email = email,
                Telefono = NormalizeText(form.Telefono, 30),
                Puesto = NormalizeText(form.Puesto, 120),
                Activo = form.Activo,
                NotasInternas = NormalizeText(form.NotasInternas, 1000),
                CreatedAtUtc = ahora,
                UpdatedAtUtc = ahora,
                CreatedByUserId = actorUserId,
                UpdatedByUserId = actorUserId
            };

            // Asociado + tipos + participación en una sola transacción: nunca queda un asociado
            // "a medias" con tipo Inversionista pero sin acuerdo.
            var executionStrategy = _context.Database.CreateExecutionStrategy();
            await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    _context.Associates.Add(associate);
                    await _context.SaveChangesAsync(cancellationToken);

                    foreach (var tipo in tipos)
                    {
                        _context.AssociateTypes.Add(new AssociateTypeAssignment
                        {
                            AssociateId = associate.Id,
                            Tipo = tipo,
                            CreatedAtUtc = ahora
                        });
                    }

                    await _context.SaveChangesAsync(cancellationToken);

                    if (esInversionista)
                    {
                        await CrearParticipacionAsync(associate, form, actorUserId, cancellationToken);
                    }

                    await transaction.CommitAsync(cancellationToken);
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            });

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.AssociateCreated,
                    EntityType = PlatformAuditEntityTypes.Associate,
                    EntityId = associate.Id.ToString(),
                    TenantId = associate.TenantId,
                    AfterJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        associate.Nombre,
                        associate.Email,
                        associate.Puesto,
                        Tipos = tipos.Select(AssociateTypeTexts.Describe).ToArray(),
                        Participacion = esInversionista ? form.ParticipacionPorcentaje : (decimal?)null
                    })
                },
                cancellationToken);

            _logger.LogInformation(
                "Asociado {AssociateId} creado. Tipos {Tipos}. Inversionista {EsInversionista}.",
                associate.Id,
                string.Join(",", tipos),
                esInversionista);

            return associate.Id;
        }

        public async Task UpdateAsync(
            int associateId,
            AssociateFormViewModel form,
            string? actorUserId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(form);

            var associate = await _context.Associates
                .Include(current => current.Tipos)
                .FirstOrDefaultAsync(current => current.Id == associateId, cancellationToken)
                ?? throw new AssociateValidationException(
                    "El asociado indicado no existe o no pertenece a este negocio.");

            var nombre = NormalizeText(form.Nombre, 150)
                ?? throw new AssociateValidationException("Indicá el nombre del asociado.", nameof(form.Nombre));

            var email = NormalizeEmail(form.Email);
            var tipos = NormalizeTipos(form.Tipos);
            var esInversionista = tipos.Contains(AssociateType.Inversionista);

            ValidarCorreoSegunNecesidad(form, esInversionista, email);
            await EnsureEmailDisponibleAsync(email, associateId, cancellationToken);

            // Si el asociado tiene cuenta, el correo de acceso manda: cambiarlo se hace desde el
            // bloque de acceso (para que Identity y el asociado no queden desalineados).
            if (associate.TieneAcceso &&
                !string.Equals(associate.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                throw new AssociateValidationException(
                    "Este asociado tiene acceso al sistema: cambiá su correo desde el bloque \"Acceso al sistema\".",
                    nameof(form.Email));
            }

            // Quitar el tipo Inversionista no borra su participación ni su histórico: solo deja de
            // mostrarse el bloque. Volver a marcarlo lo recupera tal cual estaba.
            var perfil = await _investorService.GetByAssociateAsync(associateId, cancellationToken);
            if (!esInversionista && perfil is not null && perfil.Acuerdos.Any(agreement => agreement.Activo))
            {
                throw new AssociateValidationException(
                    "No podés quitar el tipo Inversionista mientras tenga una participación activa. " +
                    "Cerrá primero su participación.",
                    nameof(form.Tipos));
            }

            var antes = System.Text.Json.JsonSerializer.Serialize(new
            {
                associate.Nombre,
                associate.Email,
                associate.Puesto,
                associate.Activo,
                Tipos = associate.Tipos.Select(tipo => tipo.Tipo.ToString()).OrderBy(tipo => tipo).ToArray()
            });

            associate.Nombre = nombre;
            associate.Email = email;
            associate.Telefono = NormalizeText(form.Telefono, 30);
            associate.Puesto = NormalizeText(form.Puesto, 120);
            associate.NotasInternas = NormalizeText(form.NotasInternas, 1000);
            associate.UpdatedAtUtc = DateTime.UtcNow;
            associate.UpdatedByUserId = actorUserId;

            SincronizarTipos(associate, tipos);

            await _context.SaveChangesAsync(cancellationToken);

            // El perfil de inversionista refleja la identidad del asociado: una sola verdad para
            // el nombre y el correo que salen en los estados de cuenta.
            if (perfil is not null && !string.IsNullOrWhiteSpace(email))
            {
                await SincronizarPerfilInversionistaAsync(perfil.Id, nombre, email!, form.Telefono, cancellationToken);
            }

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.AssociateUpdated,
                    EntityType = PlatformAuditEntityTypes.Associate,
                    EntityId = associate.Id.ToString(),
                    TenantId = associate.TenantId,
                    BeforeJson = antes,
                    AfterJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        associate.Nombre,
                        associate.Email,
                        associate.Puesto,
                        associate.Activo,
                        Tipos = tipos.Select(tipo => tipo.ToString()).OrderBy(tipo => tipo).ToArray()
                    })
                },
                cancellationToken);
        }

        public async Task SetActivoAsync(
            int associateId,
            bool activo,
            string? actorUserId,
            CancellationToken cancellationToken = default)
        {
            var associate = await _context.Associates
                .FirstOrDefaultAsync(current => current.Id == associateId, cancellationToken)
                ?? throw new AssociateValidationException(
                    "El asociado indicado no existe o no pertenece a este negocio.");

            if (associate.Activo == activo)
            {
                return;
            }

            associate.Activo = activo;
            associate.UpdatedAtUtc = DateTime.UtcNow;
            associate.UpdatedByUserId = actorUserId;
            await _context.SaveChangesAsync(cancellationToken);

            // Desactivar el asociado también corta su acceso: dejar la sesión viva sería un hueco.
            // No se borra ni un permiso: reactivarlo lo devuelve tal cual estaba.
            if (!activo && associate.TieneAcceso)
            {
                await _accessService.BloquearAccesoAsync(associateId, actorUserId, cancellationToken);
            }

            // El perfil de inversionista sigue el estado del asociado: un inversionista inactivo
            // no participa del reparto (ver EnsureParticipacionDisponible en InvestorService).
            var perfil = await _context.TenantInvestors
                .FirstOrDefaultAsync(investor => investor.AssociateId == associateId, cancellationToken);

            if (perfil is not null && perfil.Activo != activo)
            {
                perfil.Activo = activo;
                perfil.UpdatedAtUtc = DateTime.UtcNow;
                perfil.UpdatedByUserId = actorUserId;
                await _context.SaveChangesAsync(cancellationToken);
            }

            await _auditService.TryLogAsync(
                new PlatformAuditEntry
                {
                    Action = PlatformAuditActions.AssociateStateChanged,
                    EntityType = PlatformAuditEntityTypes.Associate,
                    EntityId = associate.Id.ToString(),
                    TenantId = associate.TenantId,
                    AfterJson = System.Text.Json.JsonSerializer.Serialize(new { Activo = activo })
                },
                cancellationToken);
        }

        public async Task SaveParticipationAsync(
            AssociateParticipationFormViewModel form,
            string? actorUserId,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(form);

            var associate = await _context.Associates
                .Include(current => current.Tipos)
                .FirstOrDefaultAsync(current => current.Id == form.AssociateId, cancellationToken)
                ?? throw new AssociateValidationException(
                    "El asociado indicado no existe o no pertenece a este negocio.");

            if (!associate.Tipos.Any(tipo => tipo.Tipo == AssociateType.Inversionista))
            {
                throw new AssociateValidationException(
                    "Marcá primero al asociado como Inversionista para asignarle participación.");
            }

            if (string.IsNullOrWhiteSpace(associate.Email))
            {
                throw new AssociateValidationException(
                    "Necesitás el correo del asociado para poder enviarle su estado de cuenta.");
            }

            var perfil = await _investorService.GetByAssociateAsync(form.AssociateId, cancellationToken);

            var investorForm = new InvestorFormViewModel
            {
                Id = perfil?.Id,
                Nombre = associate.Nombre,
                Email = associate.Email!,
                Telefono = associate.Telefono,
                Activo = associate.Activo,
                NotasInternas = associate.NotasInternas,
                ParticipacionPorcentaje = form.ParticipacionPorcentaje,
                EffectiveFrom = form.EffectiveFrom,
                Frecuencia = form.Frecuencia,
                DiaCorte = form.DiaCorte,
                TratamientoPerdidas = form.TratamientoPerdidas,
                EnvioAutomatico = form.EnvioAutomatico
            };

            // Las reglas duras (100 %, solapes, inicio de periodo con día de corte, versionado del
            // acuerdo) son las del módulo de inversionistas. No se reimplementan acá.
            if (perfil is null)
            {
                var investorId = await _investorService.CreateAsync(investorForm, actorUserId, cancellationToken);
                await _investorService.LinkToAssociateAsync(investorId, form.AssociateId, cancellationToken);
            }
            else
            {
                await _investorService.UpdateAsync(perfil.Id, investorForm, actorUserId, cancellationToken);
            }
        }

        // ─────────────── Helpers ───────────────

        private async Task CrearParticipacionAsync(
            Associate associate,
            AssociateFormViewModel form,
            string? actorUserId,
            CancellationToken cancellationToken)
        {
            var investorForm = new InvestorFormViewModel
            {
                Nombre = associate.Nombre,
                Email = associate.Email!,
                Telefono = associate.Telefono,
                Activo = associate.Activo,
                NotasInternas = associate.NotasInternas,
                ParticipacionPorcentaje = form.ParticipacionPorcentaje,
                EffectiveFrom = form.EffectiveFrom,
                Frecuencia = form.Frecuencia,
                DiaCorte = form.DiaCorte,
                TratamientoPerdidas = form.TratamientoPerdidas,
                EnvioAutomatico = form.EnvioAutomatico
            };

            var investorId = await _investorService.CreateAsync(investorForm, actorUserId, cancellationToken);
            await _investorService.LinkToAssociateAsync(investorId, associate.Id, cancellationToken);
        }

        private async Task SincronizarPerfilInversionistaAsync(
            int investorId,
            string nombre,
            string email,
            string? telefono,
            CancellationToken cancellationToken)
        {
            var perfil = await _context.TenantInvestors
                .FirstOrDefaultAsync(investor => investor.Id == investorId, cancellationToken);

            if (perfil is null)
            {
                return;
            }

            var cambios = false;

            if (!string.Equals(perfil.Nombre, nombre, StringComparison.Ordinal))
            {
                perfil.Nombre = nombre;
                cambios = true;
            }

            if (!string.Equals(perfil.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                perfil.Email = email;
                cambios = true;
            }

            var telefonoNormalizado = NormalizeText(telefono, 30);
            if (!string.Equals(perfil.Telefono, telefonoNormalizado, StringComparison.Ordinal))
            {
                perfil.Telefono = telefonoNormalizado;
                cambios = true;
            }

            if (cambios)
            {
                perfil.UpdatedAtUtc = DateTime.UtcNow;
                await _context.SaveChangesAsync(cancellationToken);
            }
        }

        private static void SincronizarTipos(Associate associate, IReadOnlyCollection<AssociateType> deseados)
        {
            var actuales = associate.Tipos.ToList();

            foreach (var sobrante in actuales.Where(tipo => !deseados.Contains(tipo.Tipo)))
            {
                associate.Tipos.Remove(sobrante);
            }

            foreach (var nuevo in deseados.Where(tipo => actuales.All(actual => actual.Tipo != tipo)))
            {
                associate.Tipos.Add(new AssociateTypeAssignment
                {
                    AssociateId = associate.Id,
                    Tipo = nuevo,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }
        }

        private static void ValidarCorreoSegunNecesidad(
            AssociateFormViewModel form,
            bool esInversionista,
            string? email)
        {
            if (!string.IsNullOrWhiteSpace(email))
            {
                return;
            }

            if (form.DarAcceso)
            {
                throw new AssociateValidationException(
                    "Para dar acceso al sistema necesitás el correo del asociado.",
                    nameof(form.Email));
            }

            if (esInversionista)
            {
                throw new AssociateValidationException(
                    "Un inversionista necesita correo: es a donde se envía su estado de cuenta.",
                    nameof(form.Email));
            }
        }

        private async Task EnsureEmailDisponibleAsync(
            string? email,
            int? associateIdExcluido,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return;
            }

            var existe = await _context.Associates
                .AsNoTracking()
                .AnyAsync(
                    associate => associate.Email == email &&
                                 (associateIdExcluido == null || associate.Id != associateIdExcluido.Value),
                    cancellationToken);

            if (existe)
            {
                throw new AssociateValidationException(
                    "Ya existe un asociado con ese correo en este negocio.",
                    nameof(AssociateFormViewModel.Email));
            }
        }

        /// <summary>
        /// Participación vigente de los OTROS asociados en la fecha indicada. Sirve como ayuda
        /// contextual en la UI; la validación dura de 100 % la hace <c>IInvestorService</c>.
        /// </summary>
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

        private static IReadOnlyList<AssociateType> NormalizeTipos(IEnumerable<AssociateType>? tipos)
        {
            var normalizados = (tipos ?? Enumerable.Empty<AssociateType>())
                .Where(tipo => Enum.IsDefined(tipo))
                .Distinct()
                .OrderBy(tipo => (int)tipo)
                .ToArray();

            if (normalizados.Length == 0)
            {
                throw new AssociateValidationException(
                    "Elegí al menos un tipo para el asociado.",
                    nameof(AssociateFormViewModel.Tipos));
            }

            return normalizados;
        }

        private DateOnly Today() => DateOnly.FromDateTime(_businessDateTimeProvider.Today());

        private static string? NormalizeText(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
        }

        private static string? NormalizeEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return null;
            }

            var trimmed = email.Trim();

            try
            {
                var direccion = new MailAddress(trimmed);
                return direccion.Address.ToLowerInvariant();
            }
            catch (FormatException)
            {
                throw new AssociateValidationException(
                    "El correo del asociado no tiene un formato válido.",
                    nameof(AssociateFormViewModel.Email));
            }
        }
    }
}
