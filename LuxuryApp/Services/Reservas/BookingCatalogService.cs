using LuxuryApp.Models.Reservas;
using LuxuryApp.Services.Calendar;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Reservas
{
    public sealed class BookingCatalogService : IBookingCatalogService
    {
        private readonly ApplicationDbContext _context;

        public BookingCatalogService(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IReadOnlyList<PublicBookingServiceOption>> GetPublicServicesAsync(
            CancellationToken cancellationToken = default)
        {
            var servicios = await _context.Servicios
                .AsNoTracking()
                .Where(s => s.Activo)
                .Select(s => new
                {
                    s.Id,
                    s.Nombre,
                    s.DuracionMinutos,
                    s.Precio
                })
                .ToListAsync(cancellationToken);

            if (servicios.Count == 0)
            {
                return Array.Empty<PublicBookingServiceOption>();
            }

            var settings = await _context.TenantBookingServiceSettings
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            var compatibles = await BuildCompatibilityMapAsync(cancellationToken);

            // Compatibilidad: sin configuración → mostrar todos los servicios activos (como antes).
            var hayConfiguracion = settings.Count > 0;
            var settingsById = settings.ToDictionary(s => s.ServicioId);

            var options = new List<PublicBookingServiceOption>();

            foreach (var servicio in servicios)
            {
                settingsById.TryGetValue(servicio.Id, out var setting);

                if (hayConfiguracion && (setting is null || !setting.IsVisibleOnline))
                {
                    // Con configuración activa, solo se publican los marcados como visibles.
                    continue;
                }

                // El nombre que ve el cliente es el del servicio real: una sola fuente de verdad,
                // sin una copia que se desincronice al renombrar el servicio.
                var nombrePublico = servicio.Nombre;

                var descripcion = string.IsNullOrWhiteSpace(setting?.PublicDescription)
                    ? null
                    : setting!.PublicDescription!.Trim();

                var categoria = string.IsNullOrWhiteSpace(setting?.Category)
                    ? null
                    : setting!.Category!.Trim();

                var mostrarPrecio = setting?.ShowPrice ?? false;

                var duracion = servicio.DuracionMinutos ?? CalendarCommandService.DefaultDurationMinutes;

                options.Add(new PublicBookingServiceOption
                {
                    Id = servicio.Id,
                    Nombre = nombrePublico,
                    DuracionMinutos = duracion,
                    Precio = mostrarPrecio ? servicio.Precio : null,
                    Descripcion = descripcion,
                    Categoria = categoria,
                    FuncionarioIds = compatibles.Resolve(servicio.Id)
                });
            }

            // Orden ALFABÉTICO por nombre del servicio, igual que en el panel: el negocio ya no
            // administra un campo "orden" manual que se desincronizaba de lo que ve el cliente.
            return options
                .OrderBy(option => option.Nombre, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public async Task<IReadOnlyList<int>> GetCompatibleFuncionarioIdsAsync(
            int servicioId,
            CancellationToken cancellationToken = default)
        {
            var compatibles = await BuildCompatibilityMapAsync(cancellationToken);
            return compatibles.Resolve(servicioId);
        }

        public async Task<bool> IsServiceVisibleOnlineAsync(
            int servicioId,
            CancellationToken cancellationToken = default)
        {
            if (servicioId <= 0)
            {
                return false;
            }

            var activo = await _context.Servicios
                .AsNoTracking()
                .AnyAsync(s => s.Id == servicioId && s.Activo, cancellationToken);

            if (!activo)
            {
                return false;
            }

            var hayConfiguracion = await _context.TenantBookingServiceSettings
                .AsNoTracking()
                .AnyAsync(cancellationToken);

            if (!hayConfiguracion)
            {
                // Compatibilidad: sin configuración, cualquier servicio activo es reservable.
                return true;
            }

            return await _context.TenantBookingServiceSettings
                .AsNoTracking()
                .AnyAsync(s => s.ServicioId == servicioId && s.IsVisibleOnline, cancellationToken);
        }

        public async Task<BookingCatalogViewModel> BuildManagementAsync(CancellationToken cancellationToken = default)
        {
            var servicios = await _context.Servicios
                .AsNoTracking()
                .Where(s => s.Activo)
                .OrderBy(s => s.Nombre)
                .Select(s => new
                {
                    s.Id,
                    s.Nombre,
                    s.DuracionMinutos,
                    s.Precio
                })
                .ToListAsync(cancellationToken);

            var funcionarios = await _context.Funcionarios
                .AsNoTracking()
                .Where(f => f.Activo)
                .OrderBy(f => f.Nombre)
                .Select(f => new BookingCatalogFuncionarioOption
                {
                    Id = f.IdFuncionario,
                    Nombre = f.Nombre,
                    Puesto = f.Puesto != null ? f.Puesto.NombrePuesto : null,
                    FotoUrl = f.FotoUrl,
                    ColorCalendario = f.ColorCalendario
                })
                .ToListAsync(cancellationToken);

            var settings = await _context.TenantBookingServiceSettings
                .AsNoTracking()
                .ToDictionaryAsync(s => s.ServicioId, cancellationToken);

            var asignaciones = await _context.TenantBookingFuncionarioServices
                .AsNoTracking()
                .Where(fs => fs.IsEnabled)
                .Select(fs => new { fs.ServicioId, fs.FuncionarioId })
                .ToListAsync(cancellationToken);

            var asignacionesPorServicio = asignaciones
                .GroupBy(a => a.ServicioId)
                .ToDictionary(g => g.Key, g => g.Select(a => a.FuncionarioId).ToList());

            var items = servicios.Select(servicio =>
            {
                settings.TryGetValue(servicio.Id, out var setting);
                asignacionesPorServicio.TryGetValue(servicio.Id, out var funcIds);

                return new BookingCatalogServiceItem
                {
                    ServicioId = servicio.Id,
                    NombreServicio = servicio.Nombre,
                    DuracionMinutos = servicio.DuracionMinutos ?? CalendarCommandService.DefaultDurationMinutes,
                    Precio = servicio.Precio,
                    IsVisibleOnline = setting?.IsVisibleOnline ?? true,
                    PublicDescription = setting?.PublicDescription,
                    ShowPrice = setting?.ShowPrice ?? false,
                    FuncionarioIds = funcIds ?? new List<int>(),
                    AtiendenTodos = funcIds is null || funcIds.Count == 0
                };
            })
            // Mismo criterio que el público: alfabético por el nombre del servicio.
            .OrderBy(item => item.NombreServicio, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

            return new BookingCatalogViewModel
            {
                Servicios = items,
                Funcionarios = funcionarios,
                UsandoCompatibilidad = settings.Count == 0
            };
        }

        public async Task SaveAsync(
            BookingCatalogSaveInput input,
            string? userId,
            CancellationToken cancellationToken = default)
        {
            if (input?.Servicios is null || input.Servicios.Count == 0)
            {
                return;
            }

            // Solo se aceptan servicios activos del tenant (evita inyectar ids de otros negocios;
            // el guard de tenant del DbContext valida además que el ServicioId pertenezca al tenant).
            var serviciosValidos = await _context.Servicios
                .Where(s => s.Activo)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);
            var serviciosValidosSet = serviciosValidos.ToHashSet();

            var funcionariosActivos = await _context.Funcionarios
                .Where(f => f.Activo)
                .Select(f => f.IdFuncionario)
                .ToListAsync(cancellationToken);
            var funcionariosActivosSet = funcionariosActivos.ToHashSet();

            var settingsExistentes = await _context.TenantBookingServiceSettings
                .ToDictionaryAsync(s => s.ServicioId, cancellationToken);

            var asignacionesExistentes = await _context.TenantBookingFuncionarioServices
                .ToListAsync(cancellationToken);
            var asignacionesPorServicio = asignacionesExistentes
                .GroupBy(a => a.ServicioId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var now = DateTime.UtcNow;

            foreach (var item in input.Servicios)
            {
                if (!serviciosValidosSet.Contains(item.ServicioId))
                {
                    continue;
                }

                // ── Configuración del servicio publicado (upsert idempotente por ServicioId) ──
                // PublicName, Category y DisplayOrder NO se tocan: salieron de la interfaz pero sus
                // valores históricos se conservan intactos en la base.
                if (settingsExistentes.TryGetValue(item.ServicioId, out var setting))
                {
                    setting.IsVisibleOnline = item.IsVisibleOnline;
                    setting.PublicDescription = NormalizeText(item.PublicDescription, 300);
                    setting.ShowPrice = item.ShowPrice;
                    setting.UpdatedAtUtc = now;
                }
                else
                {
                    _context.TenantBookingServiceSettings.Add(new TenantBookingServiceSetting
                    {
                        ServicioId = item.ServicioId,
                        IsVisibleOnline = item.IsVisibleOnline,
                        PublicDescription = NormalizeText(item.PublicDescription, 300),
                        ShowPrice = item.ShowPrice,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    });
                }

                // ── Relación servicio-funcionario (sincroniza el set deseado) ──
                var deseados = (item.FuncionarioIds ?? new List<int>())
                    .Where(id => funcionariosActivosSet.Contains(id))
                    .Distinct()
                    .ToHashSet();

                asignacionesPorServicio.TryGetValue(item.ServicioId, out var actuales);
                actuales ??= new List<TenantBookingFuncionarioService>();

                foreach (var existente in actuales)
                {
                    var debeEstar = deseados.Contains(existente.FuncionarioId);
                    if (existente.IsEnabled != debeEstar)
                    {
                        existente.IsEnabled = debeEstar;
                        existente.UpdatedAtUtc = now;
                    }

                    deseados.Remove(existente.FuncionarioId);
                }

                // Nuevos funcionarios habilitados que aún no tenían fila.
                foreach (var nuevoFuncId in deseados)
                {
                    _context.TenantBookingFuncionarioServices.Add(new TenantBookingFuncionarioService
                    {
                        ServicioId = item.ServicioId,
                        FuncionarioId = nuevoFuncId,
                        IsEnabled = true,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    });
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
        }

        /// <summary>
        /// Mapa servicio → funcionarios compatibles. Precarga funcionarios activos y asignaciones
        /// habilitadas una sola vez (evita N+1). Fallback a "todos los activos" cuando un servicio
        /// no tiene ninguna asignación habilitada activa.
        /// </summary>
        private async Task<CompatibilityMap> BuildCompatibilityMapAsync(CancellationToken cancellationToken)
        {
            var activos = await _context.Funcionarios
                .AsNoTracking()
                .Where(f => f.Activo)
                .Select(f => f.IdFuncionario)
                .ToListAsync(cancellationToken);
            var activosSet = activos.ToHashSet();

            var asignaciones = await _context.TenantBookingFuncionarioServices
                .AsNoTracking()
                .Where(fs => fs.IsEnabled)
                .Select(fs => new { fs.ServicioId, fs.FuncionarioId })
                .ToListAsync(cancellationToken);

            var porServicio = new Dictionary<int, List<int>>();
            foreach (var asignacion in asignaciones)
            {
                if (!activosSet.Contains(asignacion.FuncionarioId))
                {
                    continue; // ignora asignaciones a funcionarios inactivos
                }

                if (!porServicio.TryGetValue(asignacion.ServicioId, out var lista))
                {
                    lista = new List<int>();
                    porServicio[asignacion.ServicioId] = lista;
                }

                lista.Add(asignacion.FuncionarioId);
            }

            return new CompatibilityMap(activos, porServicio);
        }

        private sealed class CompatibilityMap
        {
            private readonly IReadOnlyList<int> _todosActivos;
            private readonly Dictionary<int, List<int>> _porServicio;

            public CompatibilityMap(IReadOnlyList<int> todosActivos, Dictionary<int, List<int>> porServicio)
            {
                _todosActivos = todosActivos;
                _porServicio = porServicio;
            }

            public IReadOnlyList<int> Resolve(int servicioId)
            {
                if (_porServicio.TryGetValue(servicioId, out var especificos) && especificos.Count > 0)
                {
                    return especificos;
                }

                return _todosActivos;
            }
        }

        private static string? NormalizeText(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
        }
    }
}
