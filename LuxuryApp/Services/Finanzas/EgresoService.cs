using LuxuryApp.Models.Finanzas;
using LuxuryApp.Services.BusinessTime;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Finanzas
{
    public sealed class EgresoService : IEgresoService
    {
        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ILogger<EgresoService> _logger;

        public EgresoService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ILogger<EgresoService> logger)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
            _logger = logger;
        }

        public async Task RegistrarAsync(EgresoCreateRequest request, CancellationToken cancellationToken = default)
        {
            var normalizedRequest = NormalizeRequest(request);
            ValidateRequest(normalizedRequest);

            try
            {
                await EnsureCategoriaActivaAsync(
                    normalizedRequest.CategoriaId,
                    currentCategoriaId: null,
                    cancellationToken);

                _context.Egresos.Add(new Egreso
                {
                    FechaEgreso = normalizedRequest.FechaEgreso,
                    Detalle = normalizedRequest.Detalle,
                    Monto = normalizedRequest.Monto,
                    MetodoPago = normalizedRequest.MetodoPago,
                    CategoriaId = normalizedRequest.CategoriaId
                });

                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (EgresoValidationException)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al registrar egreso para categoria {CategoriaId}.", normalizedRequest.CategoriaId);
                throw new InvalidOperationException("No fue posible registrar el egreso.");
            }
            catch (InvalidOperationException ex) when (ex is not EgresoValidationException)
            {
                _logger.LogError(ex, "Operacion invalida al registrar egreso para categoria {CategoriaId}.", normalizedRequest.CategoriaId);
                throw;
            }
        }

        public async Task<bool> ActualizarAsync(
            EgresoUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            var normalizedRequest = NormalizeRequest(request);
            ValidateRequest(normalizedRequest);

            try
            {
                var egreso = await _context.Egresos
                    .FirstOrDefaultAsync(e => e.IdEgreso == normalizedRequest.IdEgreso, cancellationToken);

                if (egreso is null)
                {
                    return false;
                }

                // Un egreso generado por una liquidación es parte de esa operación financiera:
                // editarlo acá dejaría Liquidacion.MontoTotal != Egreso.Monto. Se corrige
                // revirtiendo el pago, no editando el egreso.
                await EnsureEgresoNoEsDeLiquidacionAsync(
                    egreso.IdEgreso,
                    "Este egreso fue generado por una liquidación de colaboradores. Para corregirlo, revertí el pago desde Liquidaciones.",
                    cancellationToken);

                await EnsureCategoriaActivaAsync(
                    normalizedRequest.CategoriaId,
                    egreso.CategoriaId,
                    cancellationToken);

                egreso.FechaEgreso = normalizedRequest.FechaEgreso;
                egreso.Detalle = normalizedRequest.Detalle;
                egreso.Monto = normalizedRequest.Monto;
                egreso.MetodoPago = normalizedRequest.MetodoPago;
                egreso.CategoriaId = normalizedRequest.CategoriaId;

                await _context.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (EgresoValidationException)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al actualizar egreso {EgresoId}.", normalizedRequest.IdEgreso);
                throw new InvalidOperationException("No fue posible actualizar el egreso.");
            }
            catch (InvalidOperationException ex) when (ex is not EgresoValidationException)
            {
                _logger.LogError(ex, "Operacion invalida al actualizar egreso {EgresoId}.", normalizedRequest.IdEgreso);
                throw;
            }
        }

        public async Task<bool> EliminarAsync(int idEgreso, CancellationToken cancellationToken = default)
        {
            try
            {
                var egreso = await _context.Egresos
                    .FirstOrDefaultAsync(e => e.IdEgreso == idEgreso, cancellationToken);

                if (egreso is null)
                {
                    return false;
                }

                await EnsureEgresoNoEsDeLiquidacionAsync(
                    idEgreso,
                    "Este egreso fue generado por una liquidación de colaboradores. Para eliminarlo, revertí el pago desde Liquidaciones.",
                    cancellationToken);

                _context.Egresos.Remove(egreso);
                await _context.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (EgresoValidationException)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al eliminar egreso {EgresoId}.", idEgreso);
                throw new InvalidOperationException("No fue posible eliminar el egreso.");
            }
            catch (InvalidOperationException ex) when (ex is not EgresoValidationException)
            {
                _logger.LogError(ex, "Operacion invalida al eliminar egreso {EgresoId}.", idEgreso);
                throw;
            }
        }

        /// <summary>
        /// Un egreso creado por una liquidación pertenece a esa operación financiera y no se toca
        /// desde Egresos: la única corrección válida es revertir el pago, que deshace el conjunto
        /// completo de forma atómica.
        /// </summary>
        private async Task EnsureEgresoNoEsDeLiquidacionAsync(
            int idEgreso,
            string mensaje,
            CancellationToken cancellationToken)
        {
            var esDeLiquidacion = await _context.LiquidacionesSemanales
                .AsNoTracking()
                .AnyAsync(l => l.EgresoId == idEgreso, cancellationToken);

            if (esDeLiquidacion)
            {
                throw new EgresoValidationException(mensaje, string.Empty);
            }
        }

        private async Task EnsureCategoriaActivaAsync(
            int categoriaId,
            int? currentCategoriaId,
            CancellationToken cancellationToken)
        {
            var categoria = await _context.Categorias
                .AsNoTracking()
                .Where(c => c.Id == categoriaId)
                .Select(c => new { c.Id, c.Activo, c.Nombre, c.SystemCode })
                .SingleOrDefaultAsync(cancellationToken);

            var esLaActual = currentCategoriaId.HasValue && currentCategoriaId.Value == categoriaId;

            if (categoria is null || !(categoria.Activo || esLaActual))
            {
                throw new EgresoValidationException(
                    "La categoria seleccionada no existe, no esta activa o no pertenece al tenant actual.",
                    "Egreso.CategoriaId");
            }

            // Las categorías técnicas las asigna el sistema (liquidaciones, distribuciones). Se
            // permite CONSERVAR la que un egreso ya tenía —para poder reclasificarlo— pero nunca
            // asignarla a mano: así es como vacaciones y bonos terminaban fuera de la ganancia.
            var esTecnica =
                SystemCategoryCodes.EsLiquidacionDeColaboradores(categoria.SystemCode, categoria.Nombre) ||
                SystemCategoryCodes.EsDistribucionAInversionistas(categoria.SystemCode, categoria.Nombre);

            if (esTecnica && !esLaActual)
            {
                throw new EgresoValidationException(
                    "Esa categoria la administra el sistema. Para vacaciones, bonos o pagos adicionales usa \"" +
                    SystemCategoryCodes.NombreSugeridoExtraordinaryLaborCost + "\".",
                    "Egreso.CategoriaId");
            }
        }

        private EgresoCreateRequest NormalizeRequest(EgresoCreateRequest request) =>
            new()
            {
                FechaEgreso = NormalizeEgresoDateTime(request.FechaEgreso),
                Detalle = CollapseWhitespace(request.Detalle),
                Monto = Math.Round(request.Monto, 2, MidpointRounding.AwayFromZero),
                MetodoPago = string.IsNullOrWhiteSpace(request.MetodoPago)
                    ? string.Empty
                    : request.MetodoPago.Trim().ToUpperInvariant(),
                CategoriaId = request.CategoriaId
            };

        private EgresoUpdateRequest NormalizeRequest(EgresoUpdateRequest request) =>
            new()
            {
                IdEgreso = request.IdEgreso,
                FechaEgreso = NormalizeEgresoDateTime(request.FechaEgreso),
                Detalle = CollapseWhitespace(request.Detalle),
                Monto = Math.Round(request.Monto, 2, MidpointRounding.AwayFromZero),
                MetodoPago = string.IsNullOrWhiteSpace(request.MetodoPago)
                    ? string.Empty
                    : request.MetodoPago.Trim().ToUpperInvariant(),
                CategoriaId = request.CategoriaId
            };

        private static void ValidateRequest(EgresoCreateRequest request)
        {
            if (request.CategoriaId <= 0)
            {
                throw new EgresoValidationException("Debe seleccionar una categoria valida.", "Egreso.CategoriaId");
            }

            if (string.IsNullOrWhiteSpace(request.Detalle))
            {
                throw new EgresoValidationException("Debe indicar el detalle del egreso.", "Egreso.Detalle");
            }

            if (request.Detalle.Length > 200)
            {
                throw new EgresoValidationException("El detalle del egreso no puede exceder 200 caracteres.", "Egreso.Detalle");
            }

            if (request.Monto <= 0 || request.Monto > 999999)
            {
                throw new EgresoValidationException("Debe indicar un monto mayor a cero y dentro del rango permitido.", "Egreso.Monto");
            }

            if (!EgresoPaymentCatalog.IsAllowed(request.MetodoPago))
            {
                throw new EgresoValidationException("El metodo de pago seleccionado no es valido.", "Egreso.MetodoPago");
            }
        }

        private static void ValidateRequest(EgresoUpdateRequest request)
        {
            if (request.IdEgreso <= 0)
            {
                throw new EgresoValidationException("El egreso indicado no es valido.");
            }

            if (request.CategoriaId <= 0)
            {
                throw new EgresoValidationException("Debe seleccionar una categoria valida.", "Egreso.CategoriaId");
            }

            if (string.IsNullOrWhiteSpace(request.Detalle))
            {
                throw new EgresoValidationException("Debe indicar el detalle del egreso.", "Egreso.Detalle");
            }

            if (request.Detalle.Length > 200)
            {
                throw new EgresoValidationException("El detalle del egreso no puede exceder 200 caracteres.", "Egreso.Detalle");
            }

            if (request.Monto <= 0 || request.Monto > 999999)
            {
                throw new EgresoValidationException("Debe indicar un monto mayor a cero y dentro del rango permitido.", "Egreso.Monto");
            }

            if (!EgresoPaymentCatalog.IsAllowed(request.MetodoPago))
            {
                throw new EgresoValidationException("El metodo de pago seleccionado no es valido.", "Egreso.MetodoPago");
            }
        }

        private DateTime NormalizeEgresoDateTime(DateTime value)
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
    }
}
