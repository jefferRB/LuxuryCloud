using System.Data;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Productos;
using LuxuryApp.Models.Fiscal;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Fiscal;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Finanzas
{
    public sealed class CobroService : ICobroService
    {
        private static readonly HashSet<string> AllowedPaymentMethods = new(StringComparer.OrdinalIgnoreCase)
        {
            "EFECTIVO",
            "TARJETA",
            "SINPE"
        };

        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ITenantFiscalConfigService _fiscalConfig;
        private readonly ILogger<CobroService> _logger;

        public CobroService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ITenantFiscalConfigService fiscalConfig,
            ILogger<CobroService> logger)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
            _fiscalConfig = fiscalConfig;
            _logger = logger;
        }

        public async Task<int> RegistrarAsync(CobroCreateRequest request, CancellationToken cancellationToken = default)
        {
            var normalizedRequest = NormalizeRequest(request);
            ValidateRequest(normalizedRequest);

            // Configuración fiscal del negocio: se lee UNA vez y se usa para congelar la
            // fiscalidad efectiva del cobro dentro de la misma transacción que lo inserta.
            var tenantFiscal = await _fiscalConfig.ObtenerAsync(cancellationToken);

            var executionStrategy = _context.Database.CreateExecutionStrategy();
            var cobroId = 0;

            try
            {
                await executionStrategy.ExecuteAsync(async () =>
                {
                    var isolationLevel = normalizedRequest.ProductoId.HasValue
                        ? IsolationLevel.Serializable
                        : IsolationLevel.ReadCommitted;

                    await using var transaction = await _context.Database
                        .BeginTransactionAsync(isolationLevel, cancellationToken);

                    var remuneracion = await EnsureFuncionarioActivoAsync(
                        normalizedRequest.FuncionarioId,
                        currentFuncionarioId: null,
                        cancellationToken);

                    if (normalizedRequest.CitaId.HasValue)
                    {
                        await EnsureCitaCobrableAsync(
                            normalizedRequest.CitaId.Value,
                            normalizedRequest.FuncionarioId,
                            cancellationToken);
                    }

                    if (!normalizedRequest.ProductoId.HasValue)
                    {
                        // Cobro de servicio: de catálogo (ServicioId) o personalizado (cita sin
                        // catálogo, solo nombre). ValidateRequest ya garantizó que uno de los dos
                        // está presente.
                        int? servicioId = null;
                        ServicioSnapshot? servicio = null;
                        if (normalizedRequest.ServicioId.HasValue)
                        {
                            servicio = await LoadServicioAsync(
                                normalizedRequest.ServicioId.Value,
                                currentServicioId: null,
                                cancellationToken);
                            servicioId = servicio.Id;
                        }

                        var cobroServicio = BuildCobro(normalizedRequest, normalizedRequest.Monto, servicioId, productoId: null);

                        // Servicio PERSONALIZADO (cita fuera de catálogo): no tiene overrides
                        // propios, así que hereda la configuración efectiva del tenant. Igual
                        // recibe snapshot: no puede quedar legacy solo por no tener ServicioId.
                        AplicarSnapshotFiscal(
                            cobroServicio,
                            servicio?.AplicaIva ?? true,
                            servicio?.TarifaIva,
                            servicio?.PrecioIncluyeIva,
                            tenantFiscal);

                        AplicarSnapshotRemuneracion(cobroServicio, remuneracion);

                        // Nombre histórico: el del catálogo al vender, o el personalizado de la cita.
                        cobroServicio.DetalleSnapshot = servicio?.Nombre
                            ?? normalizedRequest.ServicioNombrePersonalizado;

                        _context.Cobros.Add(cobroServicio);
                        await _context.SaveChangesAsync(cancellationToken);
                        cobroId = cobroServicio.IdCobro;

                        if (normalizedRequest.ActualizarNotasServicio &&
                            normalizedRequest.ClienteId.HasValue &&
                            !string.IsNullOrWhiteSpace(normalizedRequest.NotasServicioTexto))
                        {
                            await TryActualizarNotasServicioAsync(
                                normalizedRequest.ClienteId.Value,
                                normalizedRequest.NotasServicioTexto,
                                cancellationToken);
                            await _context.SaveChangesAsync(cancellationToken);
                        }
                    }
                    else
                    {
                        var producto = await ReserveProductoAsync(normalizedRequest.ProductoId!.Value, cancellationToken);
                        var cobroProducto = BuildCobro(normalizedRequest, normalizedRequest.Monto, servicioId: null, producto.IdProducto);

                        AplicarSnapshotFiscal(
                            cobroProducto,
                            producto.AplicaIva,
                            producto.TarifaIva,
                            producto.PrecioIncluyeIva,
                            tenantFiscal);

                        AplicarSnapshotRemuneracion(cobroProducto, remuneracion);
                        cobroProducto.DetalleSnapshot = producto.NombreProducto;

                        _context.Cobros.Add(cobroProducto);
                        await _context.SaveChangesAsync(cancellationToken);
                        cobroId = cobroProducto.IdCobro;

                        _context.DetalleCobroProductos.Add(new DetalleCobroProducto
                        {
                            CobroId = cobroProducto.IdCobro,
                            ProductoId = producto.IdProducto,
                            Cantidad = 1,
                            PrecioUnitario = normalizedRequest.Monto,
                            Subtotal = normalizedRequest.Monto
                        });

                        _context.MovimientosInventario.Add(new MovimientoInventario
                        {
                            ProductoId = producto.IdProducto,
                            FechaMovimiento = _businessDateTimeProvider.Now(),
                            TipoMovimiento = "VENTA",
                            Cantidad = 1,
                            StockAnterior = producto.StockAnterior,
                            StockNuevo = producto.StockNuevo,
                            Observacion = $"Venta en cobro #{cobroProducto.IdCobro}"
                        });

                        await _context.SaveChangesAsync(cancellationToken);
                    }

                    await transaction.CommitAsync(cancellationToken);
                });
            }
            catch (CobroValidationException)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al registrar cobro para funcionario {FuncionarioId}.", normalizedRequest.FuncionarioId);
                throw new InvalidOperationException("No fue posible registrar el cobro.");
            }
            catch (InvalidOperationException ex) when (ex is not CobroValidationException)
            {
                _logger.LogError(ex, "Operacion invalida al registrar cobro para funcionario {FuncionarioId}.", normalizedRequest.FuncionarioId);
                throw;
            }

            return cobroId;
        }

        public async Task<bool> ActualizarAsync(
            CobroUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            var normalizedRequest = NormalizeRequest(request);
            ValidateRequest(normalizedRequest);

            try
            {
                var cobro = await _context.Cobros
                    .Include(c => c.ProductosVendidos)
                    .FirstOrDefaultAsync(c => c.IdCobro == normalizedRequest.IdCobro, cancellationToken);

                if (cobro is null)
                {
                    return false;
                }

                // El resultado se descarta a propósito: editar un cobro NO re-congela su
                // remuneración. El snapshot describe lo que se acordó cuando se hizo el trabajo,
                // y corregir el nombre del cliente no cambia ese acuerdo.
                _ = await EnsureFuncionarioActivoAsync(
                    normalizedRequest.FuncionarioId,
                    currentFuncionarioId: cobro.FuncionarioId,
                    cancellationToken);

                cobro.FechaCobro = normalizedRequest.FechaCobro;
                cobro.NombreCliente = normalizedRequest.NombreCliente;
                cobro.ClienteId = normalizedRequest.ClienteId;
                cobro.FuncionarioId = normalizedRequest.FuncionarioId;
                cobro.Monto = normalizedRequest.Monto;
                cobro.MetodoPago = normalizedRequest.MetodoPago;
                cobro.Observaciones = normalizedRequest.Observaciones;

                if (cobro.ProductoId.HasValue)
                {
                    cobro.ServicioId = null;

                    foreach (var detalle in cobro.ProductosVendidos)
                    {
                        detalle.PrecioUnitario = normalizedRequest.Monto;
                        detalle.Subtotal = normalizedRequest.Monto * detalle.Cantidad;
                    }
                }
                else
                {
                    if (!normalizedRequest.ServicioId.HasValue)
                    {
                        throw new CobroValidationException("Debe seleccionar un servicio.", "Cobro.ServicioId");
                    }

                    var servicioAnterior = cobro.ServicioId;

                    var servicio = await LoadServicioAsync(
                        normalizedRequest.ServicioId.Value,
                        cobro.ServicioId,
                        cancellationToken);

                    cobro.ServicioId = servicio.Id;
                    cobro.ProductoId = null;

                    // Si el cobro CAMBIÓ de servicio, su snapshot describía otro servicio y hay que
                    // rehacerlo. En cualquier otro caso NO se toca: re-resolverlo traería la
                    // configuración de HOY y destruiría justamente la historia que congelamos.
                    //
                    // Y solo se rehace cuando el cobro YA tenía snapshot. Un cobro legacy sigue
                    // legacy: rellenarlo con el catálogo actual sería inventarle una historia que
                    // no podemos demostrar.
                    if (cobro.AplicaIvaSnapshot.HasValue && servicioAnterior != servicio.Id)
                    {
                        var tenantFiscal = await _fiscalConfig.ObtenerAsync(cancellationToken);
                        AplicarSnapshotFiscal(
                            cobro,
                            servicio.AplicaIva,
                            servicio.TarifaIva,
                            servicio.PrecioIncluyeIva,
                            tenantFiscal);

                        cobro.DetalleSnapshot = servicio.Nombre;
                    }
                }

                await _context.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (CobroValidationException)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al actualizar cobro {CobroId}.", normalizedRequest.IdCobro);
                throw new InvalidOperationException("No fue posible actualizar el cobro.");
            }
            catch (InvalidOperationException ex) when (ex is not CobroValidationException)
            {
                _logger.LogError(ex, "Operacion invalida al actualizar cobro {CobroId}.", normalizedRequest.IdCobro);
                throw;
            }
        }

        public async Task<bool> EliminarAsync(int idCobro, CancellationToken cancellationToken = default)
        {
            var executionStrategy = _context.Database.CreateExecutionStrategy();

            try
            {
                return await executionStrategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);

                    var cobro = await _context.Cobros
                        .Include(c => c.ProductosVendidos)
                        .FirstOrDefaultAsync(c => c.IdCobro == idCobro, cancellationToken);

                    if (cobro is null)
                    {
                        return false;
                    }

                    await ReverseProductInventoryIfNeededAsync(cobro, cancellationToken);

                    if (cobro.ProductosVendidos.Count > 0)
                    {
                        _context.DetalleCobroProductos.RemoveRange(cobro.ProductosVendidos);
                    }

                    _context.Cobros.Remove(cobro);
                    await _context.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);

                    return true;
                });
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al eliminar cobro {CobroId}.", idCobro);
                throw new InvalidOperationException("No fue posible eliminar el cobro.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Operacion invalida al eliminar cobro {CobroId}.", idCobro);
                throw;
            }
        }

        private async Task EnsureCitaCobrableAsync(
            int citaId,
            int funcionarioId,
            CancellationToken cancellationToken)
        {
            // La cita debe existir, pertenecer al tenant actual (global filter) y al funcionario.
            var cita = await _context.Citas
                .AsNoTracking()
                .Where(c => c.Id == citaId)
                .Select(c => new { c.Id, c.FuncionarioId, c.Tipo })
                .SingleOrDefaultAsync(cancellationToken);

            if (cita is null)
            {
                throw new CobroValidationException(
                    "La cita indicada no existe o no pertenece al tenant actual.",
                    "Cobro.CitaId");
            }

            if (cita.FuncionarioId != funcionarioId)
            {
                throw new CobroValidationException(
                    "No puedes cobrar una cita que no es tuya.",
                    "Cobro.CitaId");
            }

            if (!string.Equals(cita.Tipo, "CITA", StringComparison.OrdinalIgnoreCase))
            {
                throw new CobroValidationException(
                    "Solo se pueden cobrar citas, no bloqueos de agenda.",
                    "Cobro.CitaId");
            }

            // Evita doble cobro: la unicidad la garantiza el índice, pero damos mensaje claro.
            var yaCobrada = await _context.Cobros
                .AsNoTracking()
                .AnyAsync(c => c.CitaId == citaId, cancellationToken);

            if (yaCobrada)
            {
                throw new CobroValidationException(
                    "Esta cita ya tiene un cobro registrado.",
                    "Cobro.CitaId");
            }
        }

        /// <summary>
        /// Valida el colaborador y devuelve su configuración de remuneración EN LA MISMA LECTURA.
        ///
        /// <para>
        /// Los seis valores salen de una única fila leída de una sola vez, dentro de la transacción
        /// del cobro. Eso es lo que garantiza que el snapshot sea coherente: es imposible congelar
        /// el porcentaje viejo junto con la modalidad de IVA nueva porque nunca se leen por separado.
        /// </para>
        /// </summary>
        private async Task<CobroRemuneracionEfectiva> EnsureFuncionarioActivoAsync(
            int funcionarioId,
            int? currentFuncionarioId,
            CancellationToken cancellationToken)
        {
            // Se proyecta a un tipo ANÓNIMO (de referencia) y no directamente a
            // CobroRemuneracionEfectiva: ese es un record struct, y "no encontrado" devolvería su
            // default, que es indistinguible de un colaborador REAL con toda su configuración en
            // cero (0 % servicio, 0 % producto, comisión sobre el total, empleado, no factura IVA,
            // tarifa 0). Esa combinación es válida —una recepcionista que cobra pero no gana
            // comisión— y quedaba rechazada como inexistente.
            //
            // "No cobra comisión" y "no existe" son cosas distintas: la presencia se decide por
            // presencia, nunca por el valor. Sigue siendo UNA sola lectura coherente de la fila.
            var fila = await _context.Funcionarios
                .AsNoTracking()
                .Where(f => f.IdFuncionario == funcionarioId &&
                            (f.Activo || (currentFuncionarioId.HasValue && f.IdFuncionario == currentFuncionarioId.Value)))
                .Select(f => new
                {
                    f.PorcentajeGanancia,
                    f.PorcentajeProducto,
                    f.ComisionCalculadaSobre,
                    f.TipoRelacionColaborador,
                    f.ModalidadIvaColaborador,
                    f.TarifaIvaFacturaColaborador
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (fila is null)
            {
                throw new CobroValidationException(
                    "El funcionario seleccionado no existe o no pertenece al tenant actual.",
                    "Cobro.FuncionarioId");
            }

            return new CobroRemuneracionEfectiva(
                fila.PorcentajeGanancia,
                fila.PorcentajeProducto,
                fila.ComisionCalculadaSobre,
                fila.TipoRelacionColaborador,
                fila.ModalidadIvaColaborador,
                fila.TarifaIvaFacturaColaborador,
                DesdeSnapshot: false);
        }

        private async Task<ServicioSnapshot> LoadServicioAsync(
            int servicioId,
            int? currentServicioId,
            CancellationToken cancellationToken)
        {
            var servicio = await _context.Servicios
                .AsNoTracking()
                .Where(s => s.Id == servicioId &&
                            (s.Activo || (currentServicioId.HasValue && s.Id == currentServicioId.Value)))
                .Select(s => new ServicioSnapshot
                {
                    Id = s.Id,
                    Nombre = s.Nombre,
                    Precio = s.Precio,
                    AplicaIva = s.AplicaIva,
                    TarifaIva = s.TarifaIva,
                    PrecioIncluyeIva = s.PrecioIncluyeIva
                })
                .SingleOrDefaultAsync(cancellationToken);

            if (servicio is null)
            {
                throw new CobroValidationException(
                    "El servicio seleccionado no existe o no pertenece al tenant actual.",
                    "Cobro.ServicioId");
            }

            return servicio;
        }

        private async Task<ProductoVentaSnapshot> ReserveProductoAsync(int productoId, CancellationToken cancellationToken)
        {
            var affectedRows = await _context.Productos
                .Where(p => p.IdProducto == productoId && p.Activo && p.CantidadProducto > 0)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(p => p.CantidadProducto, p => p.CantidadProducto - 1),
                    cancellationToken);

            if (affectedRows == 0)
            {
                var existingProduct = await _context.Productos
                    .AsNoTracking()
                    .Where(p => p.IdProducto == productoId)
                    .Select(p => new
                    {
                        p.Activo,
                        p.CantidadProducto,
                        p.NombreProducto
                    })
                    .SingleOrDefaultAsync(cancellationToken);

                if (existingProduct is null || !existingProduct.Activo)
                {
                    throw new CobroValidationException(
                        "El producto seleccionado no existe o no pertenece al tenant actual.",
                        "Cobro.ProductoId");
                }

                if (existingProduct.CantidadProducto <= 0)
                {
                    throw new CobroValidationException(
                        $"No hay stock disponible para {existingProduct.NombreProducto}.",
                        "Cobro.ProductoId");
                }

                throw new InvalidOperationException("No fue posible reservar inventario para el producto seleccionado.");
            }

            var producto = await _context.Productos
                .AsNoTracking()
                .Where(p => p.IdProducto == productoId && p.Activo)
                .Select(p => new ProductoVentaSnapshot
                {
                    IdProducto = p.IdProducto,
                    NombreProducto = p.NombreProducto,
                    PrecioProducto = p.PrecioProducto,
                    StockNuevo = p.CantidadProducto,
                    AplicaIva = p.AplicaIva,
                    TarifaIva = p.TarifaIva,
                    PrecioIncluyeIva = p.PrecioIncluyeIva
                })
                .SingleOrDefaultAsync(cancellationToken);

            if (producto is null)
            {
                throw new InvalidOperationException("No fue posible recuperar el producto reservado.");
            }

            producto.StockAnterior = producto.StockNuevo + 1;
            return producto;
        }

        private async Task ReverseProductInventoryIfNeededAsync(
            Cobro cobro,
            CancellationToken cancellationToken)
        {
            if (!cobro.ProductoId.HasValue)
            {
                return;
            }

            var cantidad = cobro.ProductosVendidos.Sum(d => d.Cantidad);
            if (cantidad <= 0)
            {
                cantidad = 1;
            }

            var affectedRows = await _context.Productos
                .Where(p => p.IdProducto == cobro.ProductoId.Value)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        p => p.CantidadProducto,
                        p => p.CantidadProducto + cantidad),
                    cancellationToken);

            if (affectedRows == 0)
            {
                throw new InvalidOperationException("No fue posible revertir inventario para el producto del cobro.");
            }

            var stockNuevo = await _context.Productos
                .AsNoTracking()
                .Where(p => p.IdProducto == cobro.ProductoId.Value)
                .Select(p => p.CantidadProducto)
                .SingleAsync(cancellationToken);

            _context.MovimientosInventario.Add(new MovimientoInventario
            {
                ProductoId = cobro.ProductoId.Value,
                FechaMovimiento = _businessDateTimeProvider.Now(),
                TipoMovimiento = "ANULACION_VENTA",
                Cantidad = cantidad,
                StockAnterior = stockNuevo - cantidad,
                StockNuevo = stockNuevo,
                Observacion = $"Eliminacion de cobro #{cobro.IdCobro}"
            });
        }

        private static Cobro BuildCobro(CobroCreateRequest request, decimal monto, int? servicioId, int? productoId) =>
            new()
            {
                FechaCobro = request.FechaCobro,
                NombreCliente = request.NombreCliente,
                ClienteId = request.ClienteId,
                FuncionarioId = request.FuncionarioId,
                ServicioId = servicioId,
                // Solo se conserva el nombre personalizado cuando el cobro NO es de catálogo
                // ni de producto (servicio personalizado de una cita).
                ServicioNombrePersonalizado = (servicioId == null && productoId == null)
                    ? request.ServicioNombrePersonalizado
                    : null,
                ProductoId = productoId,
                CitaId = request.CitaId,
                Monto = monto,
                MetodoPago = request.MetodoPago,
                Observaciones = request.Observaciones
            };

        /// <summary>
        /// Congela en el cobro la fiscalidad EFECTIVA de la línea: los overrides del
        /// servicio/producto ya combinados con la configuración del tenant, resueltos por el mismo
        /// <see cref="ITenantFiscalConfigService"/> que usa el motor de impuestos. Siempre escribe
        /// los tres campos — no existe el snapshot parcial (CK_Cobros_SnapshotFiscal).
        /// </summary>
        private void AplicarSnapshotFiscal(
            Cobro cobro,
            bool aplicaIva,
            decimal? tarifaOverride,
            bool? precioIncluyeIvaOverride,
            TenantFiscalConfig tenantFiscal)
        {
            var linea = _fiscalConfig.ResolverLinea(
                cobro.Monto, aplicaIva, tarifaOverride, precioIncluyeIvaOverride, tenantFiscal);

            cobro.AplicaIvaSnapshot = linea.Taxable;
            cobro.TarifaIvaSnapshot = linea.TaxRatePercent;
            cobro.PrecioIncluyeIvaSnapshot = linea.PriceIncludesTax;
        }

        /// <summary>
        /// Congela en el cobro la configuración de remuneración del colaborador. Siempre escribe
        /// los seis campos: no existe el snapshot parcial (CK_Cobros_SnapshotRemuneracion).
        /// </summary>
        private static void AplicarSnapshotRemuneracion(Cobro cobro, CobroRemuneracionEfectiva remuneracion)
        {
            cobro.PorcentajeServicioSnapshot = remuneracion.PorcentajeServicios;
            cobro.PorcentajeProductoSnapshot = remuneracion.PorcentajeProductos;
            cobro.ComisionCalculadaSobreSnapshot = remuneracion.ComisionCalculadaSobre;
            cobro.TipoRelacionColaboradorSnapshot = remuneracion.TipoRelacion;
            cobro.ModalidadIvaColaboradorSnapshot = remuneracion.ModalidadIva;
            cobro.TarifaIvaColaboradorSnapshot = remuneracion.TarifaIvaColaborador;
        }

        private async Task TryActualizarNotasServicioAsync(
            int clienteId,
            string notasTexto,
            CancellationToken cancellationToken)
        {
            var cliente = await _context.Clientes
                .FirstOrDefaultAsync(c => c.Id == clienteId, cancellationToken);

            if (cliente is null)
            {
                return;
            }

            cliente.DescripcionServiciosRealizados = notasTexto;
        }

        private CobroCreateRequest NormalizeRequest(CobroCreateRequest request) =>
            new()
            {
                FechaCobro = NormalizeCobroDateTime(request.FechaCobro),
                NombreCliente = CollapseWhitespace(request.NombreCliente),
                ClienteId = request.ClienteId.HasValue && request.ClienteId.Value > 0
                    ? request.ClienteId
                    : null,
                FuncionarioId = request.FuncionarioId,
                ServicioId = request.ServicioId,
                ServicioNombrePersonalizado = string.IsNullOrWhiteSpace(request.ServicioNombrePersonalizado)
                    ? null
                    : request.ServicioNombrePersonalizado.Trim(),
                ProductoId = request.ProductoId,
                CitaId = request.CitaId,
                Monto = Math.Round(request.Monto, 2, MidpointRounding.AwayFromZero),
                MetodoPago = string.IsNullOrWhiteSpace(request.MetodoPago)
                    ? string.Empty
                    : request.MetodoPago.Trim().ToUpperInvariant(),
                Observaciones = string.IsNullOrWhiteSpace(request.Observaciones)
                    ? null
                    : request.Observaciones.Trim(),
                ActualizarNotasServicio = request.ActualizarNotasServicio,
                NotasServicioTexto = string.IsNullOrWhiteSpace(request.NotasServicioTexto)
                    ? null
                    : request.NotasServicioTexto.Trim()
            };

        private CobroUpdateRequest NormalizeRequest(CobroUpdateRequest request) =>
            new()
            {
                IdCobro = request.IdCobro,
                FechaCobro = NormalizeCobroDateTime(request.FechaCobro),
                NombreCliente = CollapseWhitespace(request.NombreCliente),
                ClienteId = request.ClienteId.HasValue && request.ClienteId.Value > 0
                    ? request.ClienteId
                    : null,
                FuncionarioId = request.FuncionarioId,
                ServicioId = request.ServicioId,
                Monto = Math.Round(request.Monto, 2, MidpointRounding.AwayFromZero),
                MetodoPago = string.IsNullOrWhiteSpace(request.MetodoPago)
                    ? string.Empty
                    : request.MetodoPago.Trim().ToUpperInvariant(),
                Observaciones = string.IsNullOrWhiteSpace(request.Observaciones)
                    ? null
                    : request.Observaciones.Trim()
            };

        private static void ValidateRequest(CobroCreateRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.NombreCliente))
            {
                throw new CobroValidationException("Debe indicar el nombre del cliente.", "Cobro.NombreCliente");
            }

            var hasServicio = request.ServicioId.HasValue;
            var hasProducto = request.ProductoId.HasValue;

            // Servicio personalizado: cita fuera de catálogo. No hay ServicioId ni ProductoId,
            // pero sí un nombre de servicio y una cita de origen. A efectos de finanzas cuenta
            // como servicio (ver CobroQueryService / LiquidacionSemanalService).
            var esServicioPersonalizado = !hasServicio
                && !hasProducto
                && request.CitaId.HasValue
                && !string.IsNullOrWhiteSpace(request.ServicioNombrePersonalizado);

            if (hasServicio && hasProducto)
            {
                throw new CobroValidationException("Debe seleccionar un servicio o un producto, pero no ambos.");
            }

            if (!hasServicio && !hasProducto && !esServicioPersonalizado)
            {
                throw new CobroValidationException("Debe seleccionar un servicio o un producto.");
            }

            if (request.FuncionarioId <= 0)
            {
                throw new CobroValidationException("Debe seleccionar un funcionario valido.", "Cobro.FuncionarioId");
            }

            if (!AllowedPaymentMethods.Contains(request.MetodoPago))
            {
                throw new CobroValidationException("El metodo de pago seleccionado no es valido.", "Cobro.MetodoPago");
            }

            if (request.Monto <= 0 || request.Monto > 999999)
            {
                throw new CobroValidationException("Debe indicar un monto mayor a cero y dentro del rango permitido.", "Cobro.Monto");
            }
        }

        private static void ValidateRequest(CobroUpdateRequest request)
        {
            if (request.IdCobro <= 0)
            {
                throw new CobroValidationException("El cobro indicado no es valido.");
            }

            if (string.IsNullOrWhiteSpace(request.NombreCliente))
            {
                throw new CobroValidationException("Debe indicar el nombre del cliente.", "Cobro.NombreCliente");
            }

            if (request.FuncionarioId <= 0)
            {
                throw new CobroValidationException("Debe seleccionar un funcionario valido.", "Cobro.FuncionarioId");
            }

            if (!AllowedPaymentMethods.Contains(request.MetodoPago))
            {
                throw new CobroValidationException("El metodo de pago seleccionado no es valido.", "Cobro.MetodoPago");
            }

            if (request.Monto <= 0 || request.Monto > 999999)
            {
                throw new CobroValidationException("Debe indicar un monto mayor a cero y dentro del rango permitido.", "Cobro.Monto");
            }
        }

        private DateTime NormalizeCobroDateTime(DateTime value)
        {
            var source = value == default ? _businessDateTimeProvider.Now() : value;
            return new DateTime(
                source.Year,
                source.Month,
                source.Day,
                source.Hour,
                source.Minute,
                0);
        }

        private static string CollapseWhitespace(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return string.Join(
                ' ',
                value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        private sealed class ServicioSnapshot
        {
            public int Id { get; init; }
            public string Nombre { get; init; } = string.Empty;
            public decimal Precio { get; init; }
            public bool AplicaIva { get; init; }
            public decimal? TarifaIva { get; init; }
            public bool? PrecioIncluyeIva { get; init; }
        }

        private sealed class ProductoVentaSnapshot
        {
            public int IdProducto { get; init; }
            public string NombreProducto { get; init; } = string.Empty;
            public decimal PrecioProducto { get; init; }
            public int StockAnterior { get; set; }
            public int StockNuevo { get; init; }
            public bool AplicaIva { get; init; }
            public decimal? TarifaIva { get; init; }
            public bool? PrecioIncluyeIva { get; init; }
        }
    }
}
