using LuxuryApp.Models.Finanzas;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Finanzas
{
    /// <inheritdoc />
    public sealed class SystemCategoryService : ISystemCategoryService
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<SystemCategoryService> _logger;

        public SystemCategoryService(ApplicationDbContext context, ILogger<SystemCategoryService> logger)
        {
            _context = context;
            _logger = logger;
        }

        public Task<Categoria?> FindAsync(string systemCode, CancellationToken cancellationToken = default)
        {
            EnsureCodigoValido(systemCode);

            // El filtro global de tenant aplica: nunca se ve la categoría de otro negocio.
            return _context.Categorias
                .FirstOrDefaultAsync(c => c.SystemCode == systemCode, cancellationToken);
        }

        public async Task<Categoria> EnsureAsync(string systemCode, CancellationToken cancellationToken = default)
        {
            EnsureCodigoValido(systemCode);

            // 1) Ya tiene identidad estructural.
            var existente = await FindAsync(systemCode, cancellationToken);
            if (existente is not null)
            {
                return await ReactivarSiHaceFaltaAsync(existente, cancellationToken);
            }

            // 2) Existe la categoría histórica por nombre pero sin código todavía: se ADOPTA.
            //    Crear otra dejaría el historial partido en dos categorías.
            var nombrePorDefecto = SystemCategoryCodes.NombrePorDefecto(systemCode);
            var legacy = await _context.Categorias
                .FirstOrDefaultAsync(c => c.SystemCode == null && c.Nombre == nombrePorDefecto, cancellationToken);

            if (legacy is not null)
            {
                legacy.SystemCode = systemCode;
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "Categoria {CategoriaId} adoptada como categoria del sistema {SystemCode}.",
                    legacy.Id,
                    systemCode);

                return await ReactivarSiHaceFaltaAsync(legacy, cancellationToken);
            }

            // 3) No existe: se crea.
            var nueva = new Categoria
            {
                Nombre = nombrePorDefecto,
                Detalle = SystemCategoryCodes.DetallePorDefecto(systemCode),
                Activo = true,
                SystemCode = systemCode
            };

            _context.Categorias.Add(nueva);

            try
            {
                await _context.SaveChangesAsync(cancellationToken);
                return nueva;
            }
            catch (DbUpdateException ex)
            {
                // Otra petición la creó primero. El índice único es la garantía real; esto solo
                // convierte la carrera en un resultado correcto.
                _logger.LogWarning(
                    ex,
                    "La categoria del sistema {SystemCode} fue creada concurrentemente por otra solicitud.",
                    systemCode);

                _context.Entry(nueva).State = EntityState.Detached;

                var ganadora = await FindAsync(systemCode, cancellationToken)
                    ?? await _context.Categorias
                        .FirstOrDefaultAsync(c => c.Nombre == nombrePorDefecto, cancellationToken);

                if (ganadora is null)
                {
                    throw;
                }

                return await ReactivarSiHaceFaltaAsync(ganadora, cancellationToken);
            }
        }

        private async Task<Categoria> ReactivarSiHaceFaltaAsync(Categoria categoria, CancellationToken cancellationToken)
        {
            if (categoria.Activo)
            {
                return categoria;
            }

            categoria.Activo = true;
            await _context.SaveChangesAsync(cancellationToken);
            return categoria;
        }

        private static void EnsureCodigoValido(string systemCode)
        {
            if (!SystemCategoryCodes.EsCodigoValido(systemCode))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(systemCode),
                    systemCode,
                    "Código de categoría del sistema desconocido.");
            }
        }
    }
}
