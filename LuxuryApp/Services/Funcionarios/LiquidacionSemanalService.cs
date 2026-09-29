using System.Data;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Fiscal;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Productos;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Fiscal;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Funcionarios
{
    public class LiquidacionSemanalService : ILiquidacionSemanalService
    {
        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ITaxCalculationService _taxService;
        private readonly ILiquidacionFuncionarioService _liquidacionFuncionario;
        private readonly ITenantFiscalConfigService _fiscalConfig;
        private readonly LuxuryApp.Services.Finanzas.ISystemCategoryService _systemCategoryService;
        private readonly LuxuryApp.Services.Platform.IPlatformAuditService _auditService;
        private readonly LuxuryApp.Services.Tenant.ITenantProvider _tenantProvider;
        private readonly ILogger<LiquidacionSemanalService> _logger;

        public LiquidacionSemanalService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ITaxCalculationService taxService,
            ILiquidacionFuncionarioService liquidacionFuncionario,
            ITenantFiscalConfigService fiscalConfig,
            LuxuryApp.Services.Finanzas.ISystemCategoryService systemCategoryService,
            LuxuryApp.Services.Platform.IPlatformAuditService auditService,
            LuxuryApp.Services.Tenant.ITenantProvider tenantProvider,
            ILogger<LiquidacionSemanalService> logger)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
            _taxService = taxService;
            _liquidacionFuncionario = liquidacionFuncionario;
            _fiscalConfig = fiscalConfig;
            _systemCategoryService = systemCategoryService;
            _auditService = auditService;
            _tenantProvider = tenantProvider;
            _logger = logger;
        }

        public Task<PagosSemanaResumen> ObtenerResumenSemanaAsync(
            DateTime fechaReferencia,
            CancellationToken cancellationToken = default)
        {
            var hoy = fechaReferencia.Date;
            var diff = (7 + (hoy.DayOfWeek - DayOfWeek.Monday)) % 7;
            var inicioSemana = hoy.AddDays(-diff).Date;
            var finSemana = inicioSemana.AddDays(6).Date;

            return ObtenerResumenSemanaAsync(inicioSemana, finSemana, cancellationToken);
        }

        public async Task<PagosSemanaResumen> ObtenerResumenSemanaAsync(
            DateTime inicioSemana,
            DateTime finSemana,
            CancellationToken cancellationToken = default)
        {
            inicioSemana = inicioSemana.Date;
            finSemana = finSemana.Date;

            if (finSemana < inicioSemana)
            {
                throw new InvalidOperationException("La semana indicada no es valida.");
            }

            var finSemanaExclusive = finSemana.AddDays(1);

            // Configuración fiscal del negocio (una vez): tarifa e "incluye IVA" por defecto.
            var tenantFiscal = await _fiscalConfig.ObtenerAsync(cancellationToken);

            // Cobros por línea (servicios y productos) con su configuración fiscal efectiva.
            // El desglose Base/IVA se calcula POR cobro y luego se suma (evita diferencias por
            // redondeo). Los precios en CR incluyen IVA por defecto → base = total / 1.13.
            var serviciosCobros = await LoadServiceCobrosAsync(inicioSemana, finSemanaExclusive, cancellationToken);
            var productosCobros = await LoadProductCobrosAsync(inicioSemana, finSemanaExclusive, cancellationToken);

            // ── Atribución de pagos por PRODUCCIÓN REAL del periodo ──
            // Un pago NO pertenece al rango por su encabezado (PeriodoInicio/Fin). Se prorratea sobre
            // los cobros reales de SU periodo (por devengado) y solo cuenta la fracción cuya fecha de
            // producción cae en [inicio, fin). Es el mismo modelo de devengado que ya usa la
            // distribución mensual del Dashboard (PagoFuncionarioDevengadoCalculator), generalizado a
            // un rango arbitrario. Así una quincena/rango toma únicamente lo pagado por producción
            // dentro de sus fechas, no el pago completo por el inicio de su encabezado.
            var atribucion = await AtribuirPagosPorProduccionAsync(inicioSemana, finSemana, cancellationToken);

            var funcionarioIdsInvolucrados = serviciosCobros
                .Select(s => s.FuncionarioId)
                .Concat(productosCobros.Select(p => p.FuncionarioId))
                .Concat(atribucion.FuncionarioIds)
                .Distinct()
                .ToList();

            var funcionarios = await LoadFuncionariosSemanaAsync(
                funcionarioIdsInvolucrados,
                cancellationToken);

            var serviciosPorFuncionario = serviciosCobros
                .GroupBy(s => s.FuncionarioId)
                .ToDictionary(group => group.Key, group => group.ToList());

            var productosPorFuncionario = productosCobros
                .GroupBy(p => p.FuncionarioId)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderByDescending(item => item.Fecha).ToList());

            var pagos = new List<PagoFuncionarioVM>(funcionarios.Count);
            foreach (var funcionario in funcionarios)
            {
                serviciosPorFuncionario.TryGetValue(funcionario.IdFuncionario, out var serviciosFunc);
                productosPorFuncionario.TryGetValue(funcionario.IdFuncionario, out var productosFunc);
                serviciosFunc ??= new List<ServicioCobroRow>();
                productosFunc ??= new List<ProductoCobroRow>();

                // Desglose fiscal de la venta (por líneas → suma).
                var ventaServicios = CalcularVenta(serviciosFunc, tenantFiscal);
                var ventaProductos = CalcularVenta(productosFunc, tenantFiscal);

                // Liquidación del colaborador AGRUPANDO POR CONFIGURACIÓN EFECTIVA: dentro de un
                // mismo periodo pueden convivir cobros al 50 % y al 55 % si el porcentaje cambió a
                // mitad de quincena. Con una sola configuración el resultado es idéntico al del
                // algoritmo anterior (ver LiquidarPorConfiguracion).
                var liquidacion = LiquidarPorConfiguracion(
                    serviciosFunc, productosFunc, funcionario.RemuneracionActual, tenantFiscal);

                var totalServicios = ventaServicios.GrossTotal;
                var totalProductos = ventaProductos.GrossTotal;
                var totalGenerado = totalServicios + totalProductos;

                var pagoFinal = liquidacion.TotalAPagarColaborador;
                // Pagado APLICADO a la producción del rango (prorrateo por producción real, no por encabezado).
                var montoPagado = GetAmount(atribucion.AplicadoPorFuncionario, funcionario.IdFuncionario);
                // Pendiente y excedente sin negativos: un sobrepago se muestra como excedente, no como pendiente < 0.
                var montoPagadoAplicado = Math.Min(montoPagado, pagoFinal);
                var montoPendiente = Math.Max(pagoFinal - montoPagado, 0m);
                var excedente = Math.Max(montoPagado - pagoFinal, 0m);

                atribucion.HistorialPorFuncionario.TryGetValue(funcionario.IdFuncionario, out var historialPagos);

                var productosFuncionario = productosFunc
                    .Select(producto => new ProductoVendidoVM
                    {
                        Fecha = producto.Fecha,
                        NombreProducto = producto.NombreProducto,
                        Precio = producto.Monto,
                        GananciaFuncionario = CalcularComisionProducto(producto, funcionario, tenantFiscal)
                    })
                    .ToList();

                pagos.Add(new PagoFuncionarioVM
                {
                    FuncionarioId = funcionario.IdFuncionario,
                    Nombre = funcionario.Nombre,
                    ColorCalendario = funcionario.ColorCalendario,
                    TotalGenerado = totalGenerado,
                    TotalCobrado = totalGenerado,
                    BaseVentaSinIva = liquidacion.BaseVentaSinIva,
                    IvaVentaIncluido = liquidacion.IvaVentaIncluido,
                    // LEGACY mapeado a los conceptos correctos (IVA incluido).
                    Impuestos = liquidacion.IvaVentaIncluido,
                    TotalNeto = liquidacion.BaseVentaSinIva,
                    Porcentaje = funcionario.PorcentajeGanancia,
                    PorcentajeProducto = funcionario.PorcentajeProducto,
                    BaseComisionServicios = liquidacion.BaseComisionServicios,
                    BaseComisionProductos = liquidacion.BaseComisionProductos,
                    MontoColaborador = liquidacion.MontoColaborador,
                    BaseColaborador = liquidacion.BaseColaborador,
                    IvaColaborador = liquidacion.IvaColaborador,
                    IvaNetoNegocio = liquidacion.IvaNetoNegocio,
                    TotalAPagarColaborador = liquidacion.TotalAPagarColaborador,
                    PagoFinal = pagoFinal,
                    MontoPagado = montoPagado,
                    MontoPendiente = montoPendiente,
                    MontoPagadoAplicado = montoPagadoAplicado,
                    Excedente = excedente,
                    TotalPagado = montoPagado,
                    Pendiente = montoPendiente,
                    TotalServicios = totalServicios,
                    TotalProductos = totalProductos,
                    TipoRelacionColaborador = funcionario.TipoRelacionColaborador,
                    ComisionCalculadaSobre = funcionario.ComisionCalculadaSobre,
                    ColaboradorFacturaIva = funcionario.ColaboradorFacturaIva,
                    ModalidadIvaColaborador = funcionario.ModalidadIvaColaborador,
                    TarifaIvaColaborador = funcionario.TarifaIvaFacturaColaborador,
                    RequiereFacturaAntesDePagar = funcionario.RequiereFacturaAntesDePagar,
                    DetalleDias = BuildDetalleDias(inicioSemana, finSemana, serviciosFunc),
                    ProductosVendidos = productosFuncionario,
                    HistorialPagos = historialPagos ?? new List<HistorialPagoFuncionarioViewModel>()
                });
            }

            var totalGeneradoServicios = pagos.Sum(p => p.TotalServicios);
            var totalGeneradoProductos = pagos.Sum(p => p.TotalProductos);
            var totalGeneradoGeneral = totalGeneradoServicios + totalGeneradoProductos;
            var totalBaseVentaGeneral = pagos.Sum(p => p.BaseVentaSinIva);
            var totalIvaVentaGeneral = pagos.Sum(p => p.IvaVentaIncluido);
            var totalIvaColabGeneral = pagos.Sum(p => p.IvaColaborador);
            var totalIvaNetoNegocioGeneral = pagos.Sum(p => p.IvaNetoNegocio);
            var totalAPagarColabGeneral = pagos.Sum(p => p.TotalAPagarColaborador);
            var totalBaseComisionGeneral = pagos.Sum(p => p.BaseComisionServicios + p.BaseComisionProductos);
            var totalPagadoGeneral = pagos.Sum(p => p.MontoPagado);
            var totalPagadoAplicadoGeneral = pagos.Sum(p => p.MontoPagadoAplicado);
            var totalPendienteGeneral = pagos.Sum(p => p.MontoPendiente);
            var totalExcedenteGeneral = pagos.Sum(p => p.Excedente);
            var gananciaNegocio = totalBaseVentaGeneral - pagos.Sum(p => p.PagoFinal);

            return new PagosSemanaResumen
            {
                InicioSemana = inicioSemana,
                FinSemana = finSemana,
                Funcionarios = pagos,
                TotalGeneradoServicios = totalGeneradoServicios,
                TotalGeneradoProductos = totalGeneradoProductos,
                TotalGeneradoGeneral = totalGeneradoGeneral,
                TotalImpuestosGeneral = totalIvaVentaGeneral,
                TotalSinImpuestosGeneral = totalBaseVentaGeneral,
                TotalPagadoGeneral = totalPagadoGeneral,
                TotalPagadoAplicadoGeneral = totalPagadoAplicadoGeneral,
                TotalPendienteGeneral = totalPendienteGeneral,
                TotalExcedenteGeneral = totalExcedenteGeneral,
                GananciaNegocio = gananciaNegocio,
                TotalBaseVentaSinIvaGeneral = totalBaseVentaGeneral,
                TotalIvaVentaIncluidoGeneral = totalIvaVentaGeneral,
                TotalIvaColaboradorGeneral = totalIvaColabGeneral,
                TotalIvaNetoNegocioGeneral = totalIvaNetoNegocioGeneral,
                TotalAPagarColaboradoresGeneral = totalAPagarColabGeneral,
                TotalBaseComisionGeneral = totalBaseComisionGeneral
            };
        }

        public async Task<int> RegistrarPagoAsync(
            RegistrarLiquidacionSemanalCommand command,
            CancellationToken cancellationToken = default)
        {
            if (command == null)
            {
                throw new InvalidOperationException("La liquidacion solicitada no es valida.");
            }

            command.SemanaInicio = command.SemanaInicio.Date;
            command.SemanaFin = command.SemanaFin.Date;

            if (command.SemanaFin < command.SemanaInicio)
            {
                throw new InvalidOperationException("La semana indicada no es valida.");
            }

            command.MetodoPago = NormalizeMetodoPago(command.MetodoPago);
            command.Observacion = NormalizeOptionalText(command.Observacion, 500);
            command.CreadoPor = NormalizeOptionalText(command.CreadoPor, 450);

            var detallesSolicitados = command.Detalles
                .Where(d => d.MontoPagado > 0)
                .GroupBy(d => d.FuncionarioId)
                .Select(group => new RegistrarLiquidacionSemanalDetalleCommand
                {
                    FuncionarioId = group.Key,
                    MontoPagado = group.Sum(item => item.MontoPagado)
                })
                .ToList();

            if (detallesSolicitados.Count == 0)
            {
                throw new InvalidOperationException("No hay montos validos para registrar en la liquidacion.");
            }

            var executionStrategy = _context.Database.CreateExecutionStrategy();

            return await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);

                // IDEMPOTENCIA. Cubre los tres casos que sí duplican dinero:
                //   · doble click / reenvío del formulario,
                //   · reintento de la ExecutionStrategy cuando el COMMIT ocurrió pero se perdió el ACK,
                //   · reintento del navegador tras un timeout.
                // Validar el pendiente NO alcanza: un pago PARCIAL repetido cabe dos veces dentro
                // del pendiente y se cobraría dos veces.
                var yaRegistrada = await BuscarPorIdempotencyKeyAsync(command.IdempotencyKey, cancellationToken);
                if (yaRegistrada is not null)
                {
                    _logger.LogInformation(
                        "Pago idempotente: la clave {IdempotencyKey} ya corresponde a la liquidacion {LiquidacionId}. No se registra de nuevo.",
                        command.IdempotencyKey,
                        yaRegistrada.Id);

                    await transaction.CommitAsync(cancellationToken);
                    return yaRegistrada.Id;
                }

                var resumen = await ObtenerResumenSemanaAsync(
                    command.SemanaInicio,
                    command.SemanaFin,
                    cancellationToken);

                var detallesValidados = new List<(PagoFuncionarioVM Resumen, decimal MontoPagado)>();
                foreach (var detalle in detallesSolicitados)
                {
                    var resumenFuncionario = resumen.Funcionarios
                        .FirstOrDefault(f => f.FuncionarioId == detalle.FuncionarioId);

                    if (resumenFuncionario == null)
                    {
                        throw new InvalidOperationException("Uno de los funcionarios indicados no existe o no pertenece al tenant actual.");
                    }

                    if (detalle.MontoPagado > resumenFuncionario.MontoPendiente)
                    {
                        throw new InvalidOperationException(
                            $"El monto a pagar de {resumenFuncionario.Nombre} excede el pendiente disponible para la semana seleccionada.");
                    }

                    detallesValidados.Add((resumenFuncionario, detalle.MontoPagado));
                }

                // Identidad estructural de la categoría (ver ISystemCategoryService): la asigna el
                // sistema, nunca el usuario, y no depende del nombre visible.
                var categoria = await _systemCategoryService.EnsureAsync(
                    SystemCategoryCodes.EmployeeSettlement,
                    cancellationToken);
                var now = _businessDateTimeProvider.Now();
                var fechaPago = NormalizeFechaPago(command.FechaPago, now);
                var montoTotal = detallesValidados.Sum(item => item.MontoPagado);

                var egreso = new Egreso
                {
                    FechaEgreso = fechaPago,
                    CategoriaId = categoria.Id,
                    Monto = montoTotal,
                    MetodoPago = command.MetodoPago,
                    Detalle = BuildDetalleEgreso(
                        detallesValidados.Select(item => item.Resumen.Nombre).ToList(),
                        command.SemanaInicio,
                        command.SemanaFin)
                };

                _context.Egresos.Add(egreso);
                await _context.SaveChangesAsync(cancellationToken);

                var liquidacion = new LiquidacionSemanal
                {
                    SemanaInicio = command.SemanaInicio,
                    SemanaFin = command.SemanaFin,
                    FechaPago = fechaPago,
                    MontoTotal = montoTotal,
                    Estado = LiquidacionSemanalDefaults.EstadoPagada,
                    Observacion = command.Observacion,
                    CreadoPor = command.CreadoPor,
                    FechaCreacion = now,
                    EgresoId = egreso.IdEgreso,
                    IdempotencyKey = command.IdempotencyKey
                };

                _context.LiquidacionesSemanales.Add(liquidacion);

                try
                {
                    await _context.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException ex) when (command.IdempotencyKey.HasValue && IsUniqueConstraintViolation(ex))
                {
                    // Otra petición con la misma intención ganó la carrera entre la lectura de arriba
                    // y este INSERT. El índice único es la garantía real; acá solo se convierte la
                    // carrera en el resultado correcto: una sola operación.
                    _context.Entry(liquidacion).State = EntityState.Detached;
                    _context.Entry(egreso).State = EntityState.Detached;

                    // Se deshace lo propio ANTES de leer al ganador: su fila vive en otra
                    // transacción ya confirmada.
                    await transaction.RollbackAsync(cancellationToken);

                    var ganadora = await BuscarPorIdempotencyKeyAsync(command.IdempotencyKey, cancellationToken);
                    if (ganadora is null)
                    {
                        throw;
                    }

                    _logger.LogWarning(
                        ex,
                        "Carrera de idempotencia resuelta: la clave {IdempotencyKey} ya corresponde a la liquidacion {LiquidacionId}.",
                        command.IdempotencyKey,
                        ganadora.Id);

                    return ganadora.Id;
                }

                foreach (var detalle in detallesValidados)
                {
                    _context.LiquidacionesSemanalesDetalle.Add(new LiquidacionSemanalDetalle
                    {
                        LiquidacionSemanalId = liquidacion.Id,
                        FuncionarioId = detalle.Resumen.FuncionarioId,
                        MontoServicios = detalle.Resumen.TotalServicios,
                        MontoProductos = detalle.Resumen.TotalProductos,
                        Impuestos = detalle.Resumen.IvaVentaIncluido,
                        MontoNeto = detalle.Resumen.BaseVentaSinIva,
                        MontoPagado = detalle.MontoPagado,
                        Pendiente = detalle.Resumen.MontoPendiente - detalle.MontoPagado
                    });
                }

                foreach (var distribucion in await BuildDistribucionMensualDesdeCobrosAsync(
                    liquidacion.Id,
                    command.SemanaInicio,
                    command.SemanaFin,
                    detallesValidados,
                    cancellationToken))
                {
                    _context.LiquidacionesSemanalesDistribucionMensual.Add(distribucion);
                }

                await _context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation(
                    "Liquidacion semanal {LiquidacionId} registrada. Semana {SemanaInicio:yyyy-MM-dd} a {SemanaFin:yyyy-MM-dd}. Monto {MontoTotal}.",
                    liquidacion.Id,
                    command.SemanaInicio,
                    command.SemanaFin,
                    montoTotal);

                return liquidacion.Id;
            });
        }

        private Task<LiquidacionSemanal?> BuscarPorIdempotencyKeyAsync(
            Guid? idempotencyKey,
            CancellationToken cancellationToken)
        {
            if (!idempotencyKey.HasValue)
            {
                return Task.FromResult<LiquidacionSemanal?>(null);
            }

            // El filtro global de tenant aplica: la clave de otro negocio nunca se ve, y por eso
            // el mismo GUID en dos tenants NO colisiona.
            return _context.LiquidacionesSemanales
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.IdempotencyKey == idempotencyKey.Value, cancellationToken);
        }

        /// <summary>
        /// Deshace por completo una operación de pago. Ver <see cref="ILiquidacionSemanalService"/>
        /// para la semántica; acá vive la garantía de atomicidad.
        /// </summary>
        public async Task<ReversionPagoResultado> RevertirPagoAsync(
            int liquidacionId,
            string motivo,
            string? revertidoPorUserId,
            CancellationToken cancellationToken = default)
        {
            var motivoNormalizado = NormalizeOptionalText(motivo, 500);
            if (string.IsNullOrWhiteSpace(motivoNormalizado))
            {
                throw new InvalidOperationException("Debe indicar el motivo de la reversión.");
            }

            var executionStrategy = _context.Database.CreateExecutionStrategy();

            return await executionStrategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable,
                    cancellationToken);

                // El filtro global de tenant hace el aislamiento: el id de otro negocio no existe
                // acá aunque quien llama conozca el número.
                var liquidacion = await _context.LiquidacionesSemanales
                    .Include(l => l.Detalles)
                    .Include(l => l.DistribucionesMensuales)
                    .Include(l => l.Egreso)
                    .FirstOrDefaultAsync(l => l.Id == liquidacionId, cancellationToken);

                if (liquidacion is null)
                {
                    // Idempotencia: un segundo click no puede fallar ni inventar estados. Si la
                    // bitácora ya tiene la reversión, la respuesta es "ya estaba revertida".
                    await transaction.RollbackAsync(cancellationToken);
                    return await ResolverReversionPreviaAsync(liquidacionId, cancellationToken);
                }

                var funcionarioIds = liquidacion.Detalles.Select(d => d.FuncionarioId).Distinct().ToList();
                var nombres = await _context.Funcionarios
                    .AsNoTracking()
                    .Where(f => funcionarioIds.Contains(f.IdFuncionario))
                    .Select(f => f.Nombre)
                    .ToListAsync(cancellationToken);

                var snapshot = new ReversionPagoSnapshot(
                    liquidacion.Id,
                    liquidacion.EgresoId,
                    liquidacion.MontoTotal,
                    liquidacion.FechaPago,
                    liquidacion.SemanaInicio,
                    liquidacion.SemanaFin,
                    liquidacion.Egreso?.MetodoPago,
                    liquidacion.Egreso?.Monto,
                    liquidacion.CreadoPor,
                    liquidacion.IdempotencyKey,
                    nombres,
                    liquidacion.Detalles
                        .Select(d => new ReversionPagoDetalleSnapshot(d.FuncionarioId, d.MontoPagado))
                        .ToList());

                // La bitácora se escribe ANTES de borrar y DENTRO de la transacción: si la auditoría
                // falla, no hay reversión. Nunca un borrado silencioso de dinero.
                await _auditService.LogAsync(
                    new LuxuryApp.Services.Platform.PlatformAuditEntry
                    {
                        Action = LuxuryApp.Models.Platform.PlatformAuditActions.EmployeeSettlementPaymentReverted,
                        EntityType = LuxuryApp.Models.Platform.PlatformAuditEntityTypes.EmployeeSettlementPayment,
                        EntityId = liquidacion.Id.ToString(),
                        TenantId = liquidacion.TenantId,
                        TargetUserId = revertidoPorUserId,
                        Reason = motivoNormalizado,
                        BeforeJson = System.Text.Json.JsonSerializer.Serialize(snapshot)
                    },
                    cancellationToken);

                // Se revierte la OPERACIÓN COMPLETA (todos los colaboradores del lote): el Egreso es
                // uno solo por el total, así que revertir una parte dejaría Egreso != liquidación.
                _context.LiquidacionesSemanalesDistribucionMensual.RemoveRange(liquidacion.DistribucionesMensuales);
                _context.LiquidacionesSemanalesDetalle.RemoveRange(liquidacion.Detalles);
                _context.LiquidacionesSemanales.Remove(liquidacion);
                await _context.SaveChangesAsync(cancellationToken);

                // El Egreso va después: la liquidación lo referencia (FK Restrict).
                if (liquidacion.Egreso is not null)
                {
                    _context.Egresos.Remove(liquidacion.Egreso);
                    await _context.SaveChangesAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation(
                    "Liquidacion {LiquidacionId} revertida. Monto {MontoTotal}. Egreso {EgresoId}. Motivo: {Motivo}.",
                    snapshot.LiquidacionId,
                    snapshot.MontoTotal,
                    snapshot.EgresoId,
                    motivoNormalizado);

                return new ReversionPagoResultado(
                    ReversionPagoEstado.Revertida,
                    snapshot.MontoTotal,
                    snapshot.Funcionarios);
            });
        }

        /// <summary>
        /// Distingue "ya la revertí" de "ese id nunca existió acá". Sin esto, el segundo click de
        /// un usuario impaciente parecería un error.
        /// </summary>
        private async Task<ReversionPagoResultado> ResolverReversionPreviaAsync(
            int liquidacionId,
            CancellationToken cancellationToken)
        {
            var entityId = liquidacionId.ToString();
            var tenantId = _tenantProvider.HasTenant() ? _tenantProvider.GetTenantId() : Guid.Empty;

            var reversionPrevia = await _context.PlatformAuditLogs
                .AsNoTracking()
                .AnyAsync(
                    log => log.Action == LuxuryApp.Models.Platform.PlatformAuditActions.EmployeeSettlementPaymentReverted &&
                           log.EntityType == LuxuryApp.Models.Platform.PlatformAuditEntityTypes.EmployeeSettlementPayment &&
                           log.EntityId == entityId &&
                           log.TenantId == tenantId,
                    cancellationToken);

            return reversionPrevia
                ? new ReversionPagoResultado(ReversionPagoEstado.YaRevertida, 0m, Array.Empty<string>())
                : new ReversionPagoResultado(ReversionPagoEstado.NoEncontrada, 0m, Array.Empty<string>());
        }

        private sealed record ReversionPagoSnapshot(
            int LiquidacionId,
            int? EgresoId,
            decimal MontoTotal,
            DateTime FechaPago,
            DateTime PeriodoInicio,
            DateTime PeriodoFin,
            string? MetodoPago,
            decimal? EgresoMonto,
            string? RegistradoPor,
            Guid? IdempotencyKey,
            IReadOnlyList<string> Funcionarios,
            IReadOnlyList<ReversionPagoDetalleSnapshot> Detalles);

        private sealed record ReversionPagoDetalleSnapshot(int FuncionarioId, decimal MontoPagado);

        private async Task<List<LiquidacionSemanalDistribucionMensual>> BuildDistribucionMensualDesdeCobrosAsync(
            int liquidacionId,
            DateTime semanaInicio,
            DateTime semanaFin,
            IReadOnlyCollection<(PagoFuncionarioVM Resumen, decimal MontoPagado)> detallesValidados,
            CancellationToken cancellationToken)
        {
            var funcionarioIds = detallesValidados
                .Select(d => d.Resumen.FuncionarioId)
                .Distinct()
                .ToList();

            if (funcionarioIds.Count == 0)
            {
                return new List<LiquidacionSemanalDistribucionMensual>();
            }

            var tenantFiscal = await _fiscalConfig.ObtenerAsync(cancellationToken);

            var funcionarios = (await LoadFuncionariosSemanaAsync(funcionarioIds, cancellationToken))
                .ToDictionary(f => f.IdFuncionario);

            var cobrosSemana = await LoadDevengadoCobrosAsync(
                funcionarioIds,
                semanaInicio.Date,
                semanaFin.Date.AddDays(1),
                cancellationToken);

            var cobrosPorFuncionario = cobrosSemana
                .GroupBy(c => c.FuncionarioId)
                .ToDictionary(group => group.Key, group => group.ToList());

            var diasPorMes = cobrosSemana
                .GroupBy(c => (c.Fecha.Year, c.Fecha.Month))
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .Select(c => c.Fecha.Date)
                        .Distinct()
                        .Count());

            var distribucionesPorMes = new Dictionary<(int Anio, int Mes), LiquidacionSemanalDistribucionMensual>();
            foreach (var detalle in detallesValidados)
            {
                if (!funcionarios.TryGetValue(detalle.Resumen.FuncionarioId, out var funcionario))
                {
                    throw new InvalidOperationException(
                        $"No se pudo resolver el funcionario {detalle.Resumen.FuncionarioId} para construir la distribucion analitica.");
                }

                var cobrosFuncionario = cobrosPorFuncionario.TryGetValue(detalle.Resumen.FuncionarioId, out var listaCobros)
                    ? listaCobros
                    : new List<DevengadoCobroRow>();

                // El devengado sale de la MISMA primitiva que usa la atribución de pagos: snapshot
                // del cobro cuando lo tiene y motor fiscal canónico siempre.
                var distribucionFuncionario = PagoFuncionarioDevengadoCalculator.DistribuirMontoPagadoPorMes(
                    cobrosFuncionario.Select(c => (c.Fecha, Devengado(c, funcionario, tenantFiscal))),
                    detalle.MontoPagado);

                if (distribucionFuncionario.Count == 0 && detalle.MontoPagado > 0)
                {
                    throw new InvalidOperationException(
                        $"No se encontro produccion devengada para distribuir analiticamente el pago del funcionario {funcionario.Nombre} en la semana seleccionada.");
                }

                foreach (var distribucionMes in distribucionFuncionario)
                {
                    var claveMes = (distribucionMes.Anio, distribucionMes.Mes);
                    if (!distribucionesPorMes.TryGetValue(claveMes, out var acumulado))
                    {
                        distribucionesPorMes[claveMes] = new LiquidacionSemanalDistribucionMensual
                        {
                            LiquidacionSemanalId = liquidacionId,
                            Anio = distribucionMes.Anio,
                            Mes = distribucionMes.Mes,
                            MontoAsignado = distribucionMes.MontoAsignado,
                            DiasAplicados = diasPorMes.GetValueOrDefault(claveMes)
                        };
                        continue;
                    }

                    acumulado.MontoAsignado += distribucionMes.MontoAsignado;
                }
            }

            var distribuciones = distribucionesPorMes.Values
                .OrderBy(d => d.Anio)
                .ThenBy(d => d.Mes)
                .ToList();

            AjustarRedondeoDistribucion(distribuciones, detallesValidados.Sum(d => d.MontoPagado));
            return distribuciones;
        }

        /// <summary>Desglose fiscal (Base/IVA) de un conjunto de cobros con su config efectiva.</summary>

        // ─────────────── Liquidación por CONFIGURACIÓN EFECTIVA ───────────────

        /// <summary>
        /// Liquida al colaborador respetando la configuración con la que nació CADA cobro.
        ///
        /// <para>Algoritmo, explícito a propósito:</para>
        /// <list type="number">
        ///   <item>cada cobro resuelve su remuneración efectiva (snapshot si lo tiene, o la
        ///   configuración actual del colaborador si es legacy);</item>
        ///   <item>los cobros se agrupan por esa configuración;</item>
        ///   <item>dentro de cada grupo se suman las ventas con el motor fiscal por línea;</item>
        ///   <item>se ejecuta UNA vez la fórmula canónica del motor para ese grupo;</item>
        ///   <item>se suman los resultados de los grupos.</item>
        /// </list>
        ///
        /// <para>
        /// <b>Identidad con el algoritmo anterior.</b> Si todos los cobros comparten configuración
        /// hay exactamente UN grupo, el acumulador arranca en ese resultado y no se le suma nada
        /// más: la salida es la misma que la de la llamada única que había antes. No es una
        /// coincidencia numérica, es estructural — por eso no hace falta tolerancia de redondeo.
        /// </para>
        ///
        /// <para>
        /// La fiscalidad NO entra en la clave de agrupación: CalcularVenta ya resuelve el IVA línea
        /// por línea, así que un grupo puede mezclar gravados y exentos sin problema.
        /// </para>
        ///
        /// <para>
        /// El orden de los cobros no altera el resultado: todo lo que se acumula son sumas de
        /// decimales, y la suma es asociativa y conmutativa.
        /// </para>
        /// </summary>
        private LiquidacionColaboradorResult LiquidarPorConfiguracion(
            IReadOnlyCollection<ServicioCobroRow> servicios,
            IReadOnlyCollection<ProductoCobroRow> productos,
            CobroRemuneracionEfectiva remuneracionActual,
            TenantFiscalConfig tenantFiscal)
        {
            // Sin producción: una pasada en cero con la configuración vigente, para que el
            // colaborador igual aparezca en la planilla. Es lo que hacía el algoritmo anterior.
            if (servicios.Count == 0 && productos.Count == 0)
            {
                return LiquidarGrupo(
                    Array.Empty<ICobroFiscalRow>(),
                    Array.Empty<ICobroFiscalRow>(),
                    remuneracionActual,
                    tenantFiscal);
            }

            var lineas = servicios
                .Select(row => (Row: (ICobroFiscalRow)row, EsProducto: false,
                    Remuneracion: CobroRemuneracionEfectiva.Resolver(row, remuneracionActual)))
                .Concat(productos.Select(row => (Row: (ICobroFiscalRow)row, EsProducto: true,
                    Remuneracion: CobroRemuneracionEfectiva.Resolver(row, remuneracionActual))));

            LiquidacionColaboradorResult? acumulado = null;

            foreach (var grupo in lineas.GroupBy(linea => linea.Remuneracion.ClaveDeAgrupacion()))
            {
                var parcial = LiquidarGrupo(
                    grupo.Where(linea => !linea.EsProducto).Select(linea => linea.Row).ToList(),
                    grupo.Where(linea => linea.EsProducto).Select(linea => linea.Row).ToList(),
                    grupo.Key,
                    tenantFiscal);

                acumulado = acumulado is null ? parcial : Sumar(acumulado, parcial);
            }

            return acumulado!;
        }

        /// <summary>Una llamada al motor canónico para cobros con la MISMA configuración.</summary>
        private LiquidacionColaboradorResult LiquidarGrupo(
            IReadOnlyCollection<ICobroFiscalRow> servicios,
            IReadOnlyCollection<ICobroFiscalRow> productos,
            CobroRemuneracionEfectiva remuneracion,
            TenantFiscalConfig tenantFiscal)
        {
            var ventaServicios = CalcularVenta(servicios, tenantFiscal);
            var ventaProductos = CalcularVenta(productos, tenantFiscal);

            return _liquidacionFuncionario.Liquidar(new LiquidacionColaboradorInput
            {
                TotalVentaServicios = ventaServicios.GrossTotal,
                BaseVentaServicios = ventaServicios.NetBase,
                IvaVentaServicios = ventaServicios.TaxAmount,
                TotalVentaProductos = ventaProductos.GrossTotal,
                BaseVentaProductos = ventaProductos.NetBase,
                IvaVentaProductos = ventaProductos.TaxAmount,
                PorcentajeServicios = remuneracion.PorcentajeServicios,
                PorcentajeProductos = remuneracion.PorcentajeProductos,
                ComisionCalculadaSobre = remuneracion.ComisionCalculadaSobre,
                TipoRelacion = remuneracion.TipoRelacion,
                ModalidadIva = remuneracion.ModalidadIva,
                TarifaIvaFacturaColaborador = remuneracion.TarifaIvaColaborador
            });
        }

        /// <summary>
        /// Suma dos resultados de liquidación. Todos los importes son aditivos por definición: cada
        /// grupo ya resolvió su propio IVA de colaborador con su propia tarifa, así que sumarlos es
        /// exactamente lo que hay que pagarle.
        ///
        /// <para>
        /// ModalidadAplicada conserva la del primer grupo por compatibilidad: con configuraciones
        /// mixtas deja de tener un valor único, y ningún consumidor la lee.
        /// </para>
        /// </summary>
        private static LiquidacionColaboradorResult Sumar(
            LiquidacionColaboradorResult a,
            LiquidacionColaboradorResult b) =>
            new()
            {
                TotalCobrado = a.TotalCobrado + b.TotalCobrado,
                BaseVentaSinIva = a.BaseVentaSinIva + b.BaseVentaSinIva,
                IvaVentaIncluido = a.IvaVentaIncluido + b.IvaVentaIncluido,
                BaseComisionServicios = a.BaseComisionServicios + b.BaseComisionServicios,
                BaseComisionProductos = a.BaseComisionProductos + b.BaseComisionProductos,
                MontoColaborador = a.MontoColaborador + b.MontoColaborador,
                BaseColaborador = a.BaseColaborador + b.BaseColaborador,
                IvaColaborador = a.IvaColaborador + b.IvaColaborador,
                TotalAPagarColaborador = a.TotalAPagarColaborador + b.TotalAPagarColaborador,
                IvaNetoNegocio = a.IvaNetoNegocio + b.IvaNetoNegocio,
                ModalidadAplicada = a.ModalidadAplicada
            };

        private TaxBreakdown CalcularVenta(IEnumerable<ICobroFiscalRow> cobros, TenantFiscalConfig tenantFiscal)
        {
            var lineas = cobros.Select(c =>
            {
                var fiscal = c.Fiscalidad();
                return _fiscalConfig.ResolverLinea(
                    c.Monto, fiscal.AplicaIva, fiscal.TarifaIva, fiscal.PrecioIncluyeIva, tenantFiscal);
            });
            return _taxService.Sumar(lineas);
        }

        private decimal CalcularComisionProducto(
            ProductoCobroRow producto,
            FuncionarioResumenData funcionario,
            TenantFiscalConfig tenantFiscal)
        {
            var fiscal = producto.Fiscalidad();
            var linea = _fiscalConfig.ResolverLinea(
                producto.Monto, fiscal.AplicaIva, fiscal.TarifaIva, fiscal.PrecioIncluyeIva, tenantFiscal);
            var breakdown = _taxService.Calcular(
                linea.TotalOrBase, linea.TaxRatePercent, linea.PriceIncludesTax, linea.Taxable);

            // También por snapshot: el detalle de productos vendidos muestra lo que se ganó con
            // ESA venta, no lo que ganaría hoy con la configuración vigente.
            var remuneracion = CobroRemuneracionEfectiva.Resolver(producto, funcionario.RemuneracionActual);

            var baseComision = remuneracion.ComisionCalculadaSobre == ComisionCalculadaSobre.BaseSinIva
                ? breakdown.NetBase
                : breakdown.GrossTotal;

            return FiscalMath.Redondear(baseComision * (remuneracion.PorcentajeProductos / 100m));
        }

        private static List<DetalleDiaVM> BuildDetalleDias(
            DateTime inicioSemana,
            DateTime finSemana,
            IReadOnlyList<ServicioCobroRow> serviciosFuncionario)
        {
            var porDia = serviciosFuncionario
                .GroupBy(s => s.Fecha.Date)
                .ToDictionary(
                    group => group.Key,
                    group => (Cantidad: group.Count(), Monto: group.Sum(x => x.Monto)));

            var totalDias = (finSemana - inicioSemana).Days + 1;
            var detalle = new List<DetalleDiaVM>(totalDias);

            for (var index = 0; index < totalDias; index++)
            {
                var fechaDia = inicioSemana.AddDays(index).Date;
                porDia.TryGetValue(fechaDia, out var resumenDia);

                detalle.Add(new DetalleDiaVM
                {
                    Dia = fechaDia.ToString("dddd"),
                    CantidadServicios = resumenDia.Cantidad,
                    Monto = resumenDia.Monto
                });
            }

            return detalle;
        }

        private async Task<List<ServicioCobroRow>> LoadServiceCobrosAsync(
            DateTime inicioSemana,
            DateTime finSemanaExclusive,
            CancellationToken cancellationToken)
        {
            return await _context.Cobros
                .AsNoTracking()
                .Where(c => (c.ServicioId != null || c.ServicioNombrePersonalizado != null) &&
                            c.FechaCobro >= inicioSemana &&
                            c.FechaCobro < finSemanaExclusive)
                .Select(c => new ServicioCobroRow
                {
                    FuncionarioId = c.FuncionarioId,
                    Fecha = c.FechaCobro,
                    Monto = c.Monto,
                    // Las dos fuentes viajan juntas: manda el snapshot congelado del cobro y el
                    // catálogo solo entra para cobros legacy (ver CobroFiscalidadEfectiva).
                    AplicaIvaSnapshot = c.AplicaIvaSnapshot,
                    TarifaIvaSnapshot = c.TarifaIvaSnapshot,
                    PrecioIncluyeIvaSnapshot = c.PrecioIncluyeIvaSnapshot,
                    PorcentajeServicioSnapshot = c.PorcentajeServicioSnapshot,
                    PorcentajeProductoSnapshot = c.PorcentajeProductoSnapshot,
                    ComisionCalculadaSobreSnapshot = c.ComisionCalculadaSobreSnapshot,
                    TipoRelacionColaboradorSnapshot = c.TipoRelacionColaboradorSnapshot,
                    ModalidadIvaColaboradorSnapshot = c.ModalidadIvaColaboradorSnapshot,
                    TarifaIvaColaboradorSnapshot = c.TarifaIvaColaboradorSnapshot,
                    // Servicio personalizado (sin catálogo) → sujeto a IVA con config del tenant.
                    AplicaIvaCatalogo = c.Servicio == null || c.Servicio.AplicaIva,
                    TarifaIvaCatalogo = c.Servicio != null ? c.Servicio.TarifaIva : null,
                    PrecioIncluyeIvaCatalogo = c.Servicio != null ? c.Servicio.PrecioIncluyeIva : null
                })
                .ToListAsync(cancellationToken);
        }

        private async Task<List<ProductoCobroRow>> LoadProductCobrosAsync(
            DateTime inicioSemana,
            DateTime finSemanaExclusive,
            CancellationToken cancellationToken)
        {
            return await _context.Cobros
                .AsNoTracking()
                .Where(c => c.ProductoId != null &&
                            c.FechaCobro >= inicioSemana &&
                            c.FechaCobro < finSemanaExclusive)
                .Select(c => new ProductoCobroRow
                {
                    FuncionarioId = c.FuncionarioId,
                    Fecha = c.FechaCobro,
                    NombreProducto = c.DetalleSnapshot != null
                        ? c.DetalleSnapshot
                        : (c.Producto != null ? c.Producto.NombreProducto : "Producto"),
                    Monto = c.Monto,
                    AplicaIvaSnapshot = c.AplicaIvaSnapshot,
                    TarifaIvaSnapshot = c.TarifaIvaSnapshot,
                    PrecioIncluyeIvaSnapshot = c.PrecioIncluyeIvaSnapshot,
                    PorcentajeServicioSnapshot = c.PorcentajeServicioSnapshot,
                    PorcentajeProductoSnapshot = c.PorcentajeProductoSnapshot,
                    ComisionCalculadaSobreSnapshot = c.ComisionCalculadaSobreSnapshot,
                    TipoRelacionColaboradorSnapshot = c.TipoRelacionColaboradorSnapshot,
                    ModalidadIvaColaboradorSnapshot = c.ModalidadIvaColaboradorSnapshot,
                    TarifaIvaColaboradorSnapshot = c.TarifaIvaColaboradorSnapshot,
                    AplicaIvaCatalogo = c.Producto == null || c.Producto.AplicaIva,
                    TarifaIvaCatalogo = c.Producto != null ? c.Producto.TarifaIva : null,
                    PrecioIncluyeIvaCatalogo = c.Producto != null ? c.Producto.PrecioIncluyeIva : null
                })
                .ToListAsync(cancellationToken);
        }

        // ─────────────── Atribución de pagos por PRODUCCIÓN REAL ───────────────
        // Problema: el encabezado del pago (PeriodoInicio/Fin) no dice qué producción pagó. Filtrar por
        // ese encabezado rompe quincenas/rangos que cruzan semanas. Solución: prorratear cada pago sobre
        // los cobros reales de SU periodo (por devengado) y contar sólo la fracción cuya FECHA de
        // producción cae en [inicio, fin). Es el mismo modelo de devengado que la distribución mensual
        // del Dashboard (PagoFuncionarioDevengadoCalculator), generalizado a un rango arbitrario.

        /// <summary>Salida de diagnóstico: cómo se atribuyó cada pago al rango (incluido/excluido y por qué).</summary>
        public async Task<IReadOnlyList<PagoAtribucionDiagnostico>> ObtenerDiagnosticoPagosAsync(
            DateTime inicioSemana,
            DateTime finSemana,
            CancellationToken cancellationToken = default)
        {
            var atribucion = await AtribuirPagosPorProduccionAsync(
                inicioSemana.Date, finSemana.Date, cancellationToken);
            return atribucion.Diagnostico;
        }

        /// <summary>
        /// Cobros del periodo listos para calcular devengado: traen su fiscalidad y su remuneración
        /// efectivas. Lo usan tanto la atribución de pagos como la distribución mensual, para que
        /// las dos pesen la producción exactamente igual.
        /// </summary>
        private async Task<List<DevengadoCobroRow>> LoadDevengadoCobrosAsync(
            IReadOnlyCollection<int> funcionarioIds,
            DateTime desde,
            DateTime hastaExclusive,
            CancellationToken cancellationToken)
        {
            return await _context.Cobros
                .AsNoTracking()
                .Where(c => funcionarioIds.Contains(c.FuncionarioId) &&
                            c.FechaCobro >= desde &&
                            c.FechaCobro < hastaExclusive)
                .Select(c => new DevengadoCobroRow
                {
                    FuncionarioId = c.FuncionarioId,
                    Fecha = c.FechaCobro,
                    Monto = c.Monto,
                    EsProducto = c.ProductoId != null,
                    AplicaIvaSnapshot = c.AplicaIvaSnapshot,
                    TarifaIvaSnapshot = c.TarifaIvaSnapshot,
                    PrecioIncluyeIvaSnapshot = c.PrecioIncluyeIvaSnapshot,
                    AplicaIvaCatalogo = c.ProductoId != null
                        ? (c.Producto == null || c.Producto.AplicaIva)
                        : (c.Servicio == null || c.Servicio.AplicaIva),
                    TarifaIvaCatalogo = c.ProductoId != null
                        ? (c.Producto != null ? c.Producto.TarifaIva : null)
                        : (c.Servicio != null ? c.Servicio.TarifaIva : null),
                    PrecioIncluyeIvaCatalogo = c.ProductoId != null
                        ? (c.Producto != null ? c.Producto.PrecioIncluyeIva : null)
                        : (c.Servicio != null ? c.Servicio.PrecioIncluyeIva : null),
                    PorcentajeServicioSnapshot = c.PorcentajeServicioSnapshot,
                    PorcentajeProductoSnapshot = c.PorcentajeProductoSnapshot,
                    ComisionCalculadaSobreSnapshot = c.ComisionCalculadaSobreSnapshot,
                    TipoRelacionColaboradorSnapshot = c.TipoRelacionColaboradorSnapshot,
                    ModalidadIvaColaboradorSnapshot = c.ModalidadIvaColaboradorSnapshot,
                    TarifaIvaColaboradorSnapshot = c.TarifaIvaColaboradorSnapshot
                })
                .ToListAsync(cancellationToken);
        }

        private async Task<PaymentAttributionResult> AtribuirPagosPorProduccionAsync(
            DateTime inicio,
            DateTime finInclusive,
            CancellationToken cancellationToken)
        {
            inicio = inicio.Date;
            finInclusive = finInclusive.Date;
            var finExclusive = finInclusive.AddDays(1);

            // Misma configuración fiscal del negocio que usa la liquidación: el devengado que pesa
            // la atribución tiene que salir del mismo motor que el total a pagar.
            var tenantFiscal = await _fiscalConfig.ObtenerAsync(cancellationToken);

            var candidatos = await LoadCandidatePaymentsAsync(inicio, finInclusive, cancellationToken);

            var funcionarioIds = candidatos.Select(c => c.FuncionarioId).Distinct().ToList();
            var result = new PaymentAttributionResult { FuncionarioIds = funcionarioIds };
            if (candidatos.Count == 0)
            {
                return result;
            }

            // Ventana de cobros que cubre el periodo COMPLETO de cada pago (puede exceder el rango).
            var ventanaInicio = candidatos.Min(c => c.PeriodoInicio).Date;
            if (inicio < ventanaInicio) ventanaInicio = inicio;
            var ventanaFinExclusive = candidatos.Max(c => c.PeriodoFin).Date.AddDays(1);
            if (finExclusive > ventanaFinExclusive) ventanaFinExclusive = finExclusive;

            var cobros = await LoadDevengadoCobrosAsync(
                funcionarioIds, ventanaInicio, ventanaFinExclusive, cancellationToken);

            var funcionarios = (await LoadFuncionariosSemanaAsync(funcionarioIds, cancellationToken))
                .ToDictionary(f => f.IdFuncionario);

            var cobrosPorFuncionario = cobros
                .GroupBy(c => c.FuncionarioId)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Asignación por producción (tipo "fill"): cada cobro se paga una sola vez. Los pagos se
            // procesan en orden CRONOLÓGICO; cada pago llena el devengado pendiente de los cobros de su
            // periodo (más antiguos primero). Así, si se paga por semanas y luego se hace "Pagar quincena"
            // por el resto, el pago de quincena NO se re-prorratea sobre las semanas ya pagadas (sin doble
            // conteo). El monto aplicado al rango = lo que cayó sobre cobros dentro de [inicio, fin).
            foreach (var grupo in candidatos.GroupBy(c => c.FuncionarioId))
            {
                var funcId = grupo.Key;
                funcionarios.TryGetValue(funcId, out var funcionario);
                cobrosPorFuncionario.TryGetValue(funcId, out var cobrosFunc);
                cobrosFunc ??= new List<DevengadoCobroRow>();

                var alloc = funcionario == null
                    ? new List<CobroAllocacion>()
                    : cobrosFunc
                        .Select(c => new CobroAllocacion { Fecha = c.Fecha.Date, Devengado = Devengado(c, funcionario, tenantFiscal) })
                        .Where(a => a.Devengado > 0m)
                        .OrderBy(a => a.Fecha)
                        .ToList();

                foreach (var pago in grupo.OrderBy(p => p.FechaPago).ThenBy(p => p.ReferenciaId))
                {
                    var periodoFinExclusive = pago.PeriodoFin.Date.AddDays(1);
                    var cobrosPeriodo = alloc
                        .Where(a => a.Fecha >= pago.PeriodoInicio.Date && a.Fecha < periodoFinExclusive)
                        .ToList();

                    var devPeriodo = cobrosPeriodo.Sum(a => a.Devengado);
                    var devRango = cobrosPeriodo
                        .Where(a => a.Fecha >= inicio && a.Fecha < finExclusive)
                        .Sum(a => a.Devengado);

                    // 1) Llenar el devengado pendiente de los cobros del periodo (antiguos primero).
                    var restante = pago.Monto;
                    var aplicadoRango = 0m;
                    foreach (var cobro in cobrosPeriodo)
                    {
                        if (restante <= 0m) break;
                        var capacidad = cobro.Devengado - cobro.Pagado;
                        if (capacidad <= 0m) continue;
                        var toma = Math.Min(capacidad, restante);
                        cobro.Pagado += toma;
                        restante -= toma;
                        if (cobro.Fecha >= inicio && cobro.Fecha < finExclusive)
                        {
                            aplicadoRango += toma;
                        }
                    }

                    // 2) Excedente (sobrepago sobre el devengado del periodo) prorrateado al rango.
                    if (restante > 0m)
                    {
                        var fracExc = devPeriodo > 0m
                            ? devRango / devPeriodo
                            : DayOverlapFraction(pago.PeriodoInicio, pago.PeriodoFin, inicio, finInclusive);
                        aplicadoRango += restante * fracExc;
                    }

                    var aplicado = Math.Round(aplicadoRango, 2, MidpointRounding.AwayFromZero);

                    string motivo;
                    if (funcionario == null)
                    {
                        motivo = "Excluido: no se pudo resolver el colaborador para atribuir.";
                    }
                    else if (aplicado > 0m)
                    {
                        motivo = $"Aplicado por producción del rango: {aplicado:0.##} de {pago.Monto:0.##}.";
                    }
                    else if (devRango > 0m)
                    {
                        motivo = "Excluido: la producción del rango ya fue cubierta por pagos anteriores (sin doble conteo).";
                    }
                    else if (devPeriodo > 0m)
                    {
                        motivo = "Excluido: la producción del periodo del pago no cae en el rango.";
                    }
                    else
                    {
                        motivo = "Excluido: el periodo del pago no tiene producción ni solape con el rango.";
                    }

                    if (aplicado > 0m)
                    {
                        result.AplicadoPorFuncionario.TryGetValue(funcId, out var acumulado);
                        result.AplicadoPorFuncionario[funcId] = acumulado + aplicado;

                        if (!result.HistorialPorFuncionario.TryGetValue(funcId, out var historial))
                        {
                            historial = new List<HistorialPagoFuncionarioViewModel>();
                            result.HistorialPorFuncionario[funcId] = historial;
                        }

                        historial.Add(new HistorialPagoFuncionarioViewModel
                        {
                            ReferenciaId = pago.ReferenciaId,
                            FuncionarioId = funcId,
                            // En una vista por rango, el historial muestra el monto APLICADO a ese rango.
                            MontoPagado = aplicado,
                            FechaPago = pago.FechaPago,
                            InicioSemana = pago.PeriodoInicio,
                            FinSemana = pago.PeriodoFin,
                            Observacion = pago.Observacion,
                            OrigenRegistro = pago.Origen,
                            MetodoPago = pago.MetodoPago,
                            RegistradoPor = pago.RegistradoPor
                        });
                    }

                    result.Diagnostico.Add(new PagoAtribucionDiagnostico
                    {
                        Origen = pago.Origen,
                        PagoId = pago.ReferenciaId,
                        FuncionarioId = funcId,
                        FuncionarioNombre = funcionario?.Nombre ?? $"#{funcId}",
                        Monto = pago.Monto,
                        PeriodoInicio = pago.PeriodoInicio,
                        PeriodoFin = pago.PeriodoFin,
                        FechaPago = pago.FechaPago,
                        Incluido = aplicado > 0m,
                        Fraccion = pago.Monto > 0m ? Math.Round(aplicado / pago.Monto, 4) : 0m,
                        MontoAplicado = aplicado,
                        Motivo = motivo
                    });

                    _logger.LogDebug(
                        "[LIQ-DIAG] Rango {Inicio:yyyy-MM-dd}..{Fin:yyyy-MM-dd} Pago {Origen}#{PagoId} Func {FuncId} Monto {Monto} Periodo {PIni:yyyy-MM-dd}..{PFin:yyyy-MM-dd} FechaPago {FPago:yyyy-MM-dd} Aplicado {Aplicado} — {Motivo}",
                        inicio, finInclusive, pago.Origen, pago.ReferenciaId, funcId, pago.Monto,
                        pago.PeriodoInicio, pago.PeriodoFin, pago.FechaPago, aplicado, motivo);
                }
            }

            return result;
        }

        /// <summary>Cobro con su devengado y lo ya "pagado" durante la asignación (mutable).</summary>
        private sealed class CobroAllocacion
        {
            public DateTime Fecha { get; init; }
            public decimal Devengado { get; init; }
            public decimal Pagado { get; set; }
        }

        /// <summary>
        /// Devengado de UN cobro: lo que el colaborador se ganó con ese trabajo.
        ///
        /// <para>
        /// Su responsabilidad no cambió —sigue siendo el peso con el que la atribución greedy
        /// reparte los pagos— pero ahora lee las mismas dos fuentes que todo el resto del módulo:
        /// </para>
        /// <list type="bullet">
        ///   <item><b>Remuneración efectiva:</b> el snapshot del cobro si lo tiene, la configuración
        ///   actual del colaborador si es legacy. Un cobro vendido al 50 % se devenga al 50 % aunque
        ///   hoy el colaborador esté al 55 %.</item>
        ///   <item><b>Fiscalidad canónica:</b> el motor fiscal por línea, NO la división plana entre
        ///   1,13 que usaba antes. Esa división daba una base equivocada en servicios exentos y con
        ///   tarifas distintas del 13 %, y hacía que el pendiente mostrado no cuadrara con el total a
        ///   pagar que calcula la liquidación.</item>
        /// </list>
        /// </summary>
        private decimal Devengado(
            DevengadoCobroRow cobro,
            FuncionarioResumenData funcionario,
            TenantFiscalConfig tenantFiscal)
        {
            var remuneracion = CobroRemuneracionEfectiva.Resolver(cobro, funcionario.RemuneracionActual);
            var porcentaje = cobro.EsProducto
                ? remuneracion.PorcentajeProductos
                : remuneracion.PorcentajeServicios;

            var fiscal = cobro.Fiscalidad();
            var linea = _fiscalConfig.ResolverLinea(
                cobro.Monto, fiscal.AplicaIva, fiscal.TarifaIva, fiscal.PrecioIncluyeIva, tenantFiscal);
            var desglose = _taxService.Calcular(
                linea.TotalOrBase, linea.TaxRatePercent, linea.PriceIncludesTax, linea.Taxable);

            var baseComision = remuneracion.ComisionCalculadaSobre == ComisionCalculadaSobre.BaseSinIva
                ? desglose.NetBase
                : desglose.GrossTotal;

            return baseComision * (porcentaje / 100m);
        }

        /// <summary>Fracción de días del periodo del pago que caen en el rango (fallback sin producción).</summary>
        private static decimal DayOverlapFraction(
            DateTime periodoInicio, DateTime periodoFin, DateTime inicio, DateTime finInclusive)
        {
            var ps = periodoInicio.Date;
            var pf = periodoFin.Date;
            var totalDias = (pf - ps).Days + 1;
            if (totalDias <= 0) return 0m;

            var overlapInicio = ps > inicio.Date ? ps : inicio.Date;
            var overlapFin = pf < finInclusive.Date ? pf : finInclusive.Date;
            var overlapDias = (overlapFin - overlapInicio).Days + 1;
            if (overlapDias <= 0) return 0m;

            return (decimal)overlapDias / totalDias;
        }

        private async Task<List<PagoCandidato>> LoadCandidatePaymentsAsync(
            DateTime inicio,
            DateTime finInclusive,
            CancellationToken cancellationToken)
        {
            // Candidato = pago cuyo periodo INTERSECTA [inicio, finInclusive] (condición necesaria para
            // aportar; el monto real aplicado se resuelve luego por prorrateo de producción).
            var legacy = await _context.PagosFuncionarios
                .AsNoTracking()
                .Where(p => p.InicioSemana <= finInclusive && p.FinSemana >= inicio)
                .Select(p => new PagoCandidato(
                    "LEGACY",
                    p.IdPago,
                    p.FuncionarioId,
                    p.InicioSemana,
                    p.FinSemana,
                    p.FechaPago,
                    p.MontoPagado,
                    p.Observacion,
                    null,
                    null))
                .ToListAsync(cancellationToken);

            var liquidacion = await _context.LiquidacionesSemanalesDetalle
                .AsNoTracking()
                .Where(d => d.LiquidacionSemanal != null &&
                            d.LiquidacionSemanal.SemanaInicio <= finInclusive &&
                            d.LiquidacionSemanal.SemanaFin >= inicio)
                .Select(d => new PagoCandidato(
                    "LIQUIDACION",
                    d.LiquidacionSemanalId,
                    d.FuncionarioId,
                    d.LiquidacionSemanal!.SemanaInicio,
                    d.LiquidacionSemanal.SemanaFin,
                    d.LiquidacionSemanal.FechaPago,
                    d.MontoPagado,
                    d.LiquidacionSemanal.Observacion,
                    d.LiquidacionSemanal.Egreso != null ? d.LiquidacionSemanal.Egreso.MetodoPago : null,
                    d.LiquidacionSemanal.CreadoPor))
                .ToListAsync(cancellationToken);

            legacy.AddRange(liquidacion);
            return legacy;
        }

        private sealed record PagoCandidato(
            string Origen,
            int ReferenciaId,
            int FuncionarioId,
            DateTime PeriodoInicio,
            DateTime PeriodoFin,
            DateTime FechaPago,
            decimal Monto,
            string? Observacion,
            string? MetodoPago,
            string? RegistradoPor);

        /// <summary>
        /// Cobro visto por la atribución de pagos. Trae las dos fuentes de verdad —fiscalidad y
        /// remuneración— para que el devengado se calcule con el MISMO criterio que la liquidación.
        /// </summary>
        private sealed class DevengadoCobroRow : ICobroFiscalRow
        {
            public int FuncionarioId { get; init; }
            public DateTime Fecha { get; init; }
            public decimal Monto { get; init; }
            public bool EsProducto { get; init; }

            public bool? AplicaIvaSnapshot { get; init; }
            public decimal? TarifaIvaSnapshot { get; init; }
            public bool? PrecioIncluyeIvaSnapshot { get; init; }
            public bool AplicaIvaCatalogo { get; init; }
            public decimal? TarifaIvaCatalogo { get; init; }
            public bool? PrecioIncluyeIvaCatalogo { get; init; }

            public decimal? PorcentajeServicioSnapshot { get; init; }
            public decimal? PorcentajeProductoSnapshot { get; init; }
            public ComisionCalculadaSobre? ComisionCalculadaSobreSnapshot { get; init; }
            public TipoRelacionColaborador? TipoRelacionColaboradorSnapshot { get; init; }
            public ModalidadIvaColaborador? ModalidadIvaColaboradorSnapshot { get; init; }
            public decimal? TarifaIvaColaboradorSnapshot { get; init; }
        }

        private sealed class PaymentAttributionResult
        {
            public Dictionary<int, decimal> AplicadoPorFuncionario { get; } = new();
            public Dictionary<int, List<HistorialPagoFuncionarioViewModel>> HistorialPorFuncionario { get; } = new();
            public List<PagoAtribucionDiagnostico> Diagnostico { get; } = new();
            public IReadOnlyCollection<int> FuncionarioIds { get; init; } = Array.Empty<int>();
        }

        private async Task<List<FuncionarioResumenData>> LoadFuncionariosSemanaAsync(
            IReadOnlyCollection<int> funcionarioIdsInvolucrados,
            CancellationToken cancellationToken)
        {
            return await _context.Funcionarios
                .AsNoTracking()
                .Where(f => f.Activo || funcionarioIdsInvolucrados.Contains(f.IdFuncionario))
                .OrderBy(f => f.Nombre)
                .Select(f => new FuncionarioResumenData
                {
                    IdFuncionario = f.IdFuncionario,
                    Nombre = f.Nombre,
                    ColorCalendario = f.ColorCalendario,
                    PorcentajeGanancia = f.PorcentajeGanancia,
                    PorcentajeProducto = f.PorcentajeProducto,
                    ComisionCalculadaSobre = f.ComisionCalculadaSobre,
                    TipoRelacionColaborador = f.TipoRelacionColaborador,
                    ColaboradorFacturaIva = f.ColaboradorFacturaIva,
                    ModalidadIvaColaborador = f.ModalidadIvaColaborador,
                    TarifaIvaFacturaColaborador = f.TarifaIvaFacturaColaborador,
                    RequiereFacturaAntesDePagar = f.RequiereFacturaAntesDePagar
                })
                .ToListAsync(cancellationToken);
        }

        private static void AjustarRedondeoDistribucion(
            List<LiquidacionSemanalDistribucionMensual> distribuciones,
            decimal montoTotalEsperado)
        {
            if (distribuciones.Count == 0)
            {
                return;
            }

            var montoActual = distribuciones.Sum(d => d.MontoAsignado);
            var diferencia = montoTotalEsperado - montoActual;
            distribuciones[^1].MontoAsignado += diferencia;
        }

        private static DateTime NormalizeFechaPago(DateTime? fechaPago, DateTime defaultNow)
        {
            var baseDate = fechaPago ?? defaultNow;
            return new DateTime(
                baseDate.Year,
                baseDate.Month,
                baseDate.Day,
                baseDate.Hour,
                baseDate.Minute,
                0);
        }

        private static string NormalizeMetodoPago(string? metodoPago)
        {
            var normalized = string.IsNullOrWhiteSpace(metodoPago)
                ? "EFECTIVO"
                : metodoPago.Trim().ToUpperInvariant();

            if (!LiquidacionSemanalDefaults.MetodosPagoPermitidos.Contains(normalized))
            {
                throw new InvalidOperationException("El metodo de pago indicado no es valido.");
            }

            return normalized;
        }

        private static string? NormalizeOptionalText(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.Length <= maxLength
                ? trimmed
                : trimmed[..maxLength];
        }

        private static string BuildDetalleEgreso(
            IReadOnlyCollection<string> nombresFuncionarios,
            DateTime semanaInicio,
            DateTime semanaFin)
        {
            var detalle = nombresFuncionarios.Count == 1
                ? $"Pago a {nombresFuncionarios.Single()} - Semana {semanaInicio:dd/MM} al {semanaFin:dd/MM}"
                : $"Liquidacion semanal de funcionarios - Semana {semanaInicio:dd/MM} al {semanaFin:dd/MM}";

            return detalle.Length <= 200
                ? detalle
                : detalle[..200];
        }

        private static decimal GetAmount(IReadOnlyDictionary<int, decimal> amounts, int funcionarioId)
        {
            return amounts.TryGetValue(funcionarioId, out var amount)
                ? amount
                : 0m;
        }

        private static bool IsUniqueConstraintViolation(DbUpdateException ex)
        {
            var message = ex.InnerException?.Message ?? ex.Message;
            return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("PagoFuncionarios", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class FuncionarioResumenData
        {
            public int IdFuncionario { get; init; }
            public string Nombre { get; init; } = string.Empty;
            public string ColorCalendario { get; init; } = string.Empty;
            public decimal PorcentajeGanancia { get; init; }
            public decimal PorcentajeProducto { get; init; }
            public ComisionCalculadaSobre ComisionCalculadaSobre { get; init; }
            public TipoRelacionColaborador TipoRelacionColaborador { get; init; }
            public bool ColaboradorFacturaIva { get; init; }
            public ModalidadIvaColaborador ModalidadIvaColaborador { get; init; }
            public decimal TarifaIvaFacturaColaborador { get; init; }

            /// <summary>Configuración VIGENTE del colaborador: el fallback de los cobros legacy.</summary>
            public CobroRemuneracionEfectiva RemuneracionActual => new(
                PorcentajeGanancia,
                PorcentajeProducto,
                ComisionCalculadaSobre,
                TipoRelacionColaborador,
                ModalidadIvaColaborador,
                TarifaIvaFacturaColaborador,
                DesdeSnapshot: false);
            public bool RequiereFacturaAntesDePagar { get; init; }
        }

        /// <summary>Datos fiscales mínimos de un cobro para el motor de IVA.</summary>
        private interface ICobroFiscalRow : ICobroFiscalSnapshotOrigen, ICobroRemuneracionSnapshot
        {
            decimal Monto { get; }
        }

        private sealed class ServicioCobroRow : ICobroFiscalRow
        {
            public int FuncionarioId { get; init; }
            public DateTime Fecha { get; init; }
            public decimal Monto { get; init; }
            public bool? AplicaIvaSnapshot { get; init; }
            public decimal? TarifaIvaSnapshot { get; init; }
            public bool? PrecioIncluyeIvaSnapshot { get; init; }
            public bool AplicaIvaCatalogo { get; init; }
            public decimal? TarifaIvaCatalogo { get; init; }
            public bool? PrecioIncluyeIvaCatalogo { get; init; }

            public decimal? PorcentajeServicioSnapshot { get; init; }
            public decimal? PorcentajeProductoSnapshot { get; init; }
            public ComisionCalculadaSobre? ComisionCalculadaSobreSnapshot { get; init; }
            public TipoRelacionColaborador? TipoRelacionColaboradorSnapshot { get; init; }
            public ModalidadIvaColaborador? ModalidadIvaColaboradorSnapshot { get; init; }
            public decimal? TarifaIvaColaboradorSnapshot { get; init; }
        }

        private sealed class ProductoCobroRow : ICobroFiscalRow
        {
            public int FuncionarioId { get; init; }
            public DateTime Fecha { get; init; }
            public string NombreProducto { get; init; } = string.Empty;
            public decimal Monto { get; init; }
            public bool? AplicaIvaSnapshot { get; init; }
            public decimal? TarifaIvaSnapshot { get; init; }
            public bool? PrecioIncluyeIvaSnapshot { get; init; }
            public bool AplicaIvaCatalogo { get; init; }
            public decimal? TarifaIvaCatalogo { get; init; }
            public bool? PrecioIncluyeIvaCatalogo { get; init; }

            public decimal? PorcentajeServicioSnapshot { get; init; }
            public decimal? PorcentajeProductoSnapshot { get; init; }
            public ComisionCalculadaSobre? ComisionCalculadaSobreSnapshot { get; init; }
            public TipoRelacionColaborador? TipoRelacionColaboradorSnapshot { get; init; }
            public ModalidadIvaColaborador? ModalidadIvaColaboradorSnapshot { get; init; }
            public decimal? TarifaIvaColaboradorSnapshot { get; init; }
        }
    }
}
