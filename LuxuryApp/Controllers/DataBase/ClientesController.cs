using LuxuryApp.Services.Identity;
using LuxuryApp.Models.Asociados;
using System.Linq.Expressions;
using System.Security.Claims;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.WhatsApp;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Clientes;
using LuxuryApp.Services.Security;
using LuxuryApp.Services.WhatsApp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Controllers.DataBase
{
    [Authorize]
    [RequirePermission(AppPermissions.ClientsView)]
    public class ClientesController : Controller
    {
        private const int DefaultPageSize = 20;
        private const int MaxPageSize = 100;
        private const int BuscarNombreMaxResults = 50;
        private const int AutocompleteMinLength = 3;
        private const int AutocompleteMaxResults = 10;
        private const string TenantWhatsAppEnabledViewDataKey = "TenantWhatsAppEnabled";
        private const string FrecuenciaCalculadaViewDataKey = "FrecuenciaCalculadaDias";

        /// <summary>
        /// Datos propios del cliente. La frecuencia y la última visita NO salen de aquí: son
        /// derivadas y las completa <see cref="AplicarMetricasAsync"/>.
        /// </summary>
        private static readonly Expression<Func<ClientesModel, ClienteSummaryViewModel>> ClienteSummaryProjection = cliente =>
            new ClienteSummaryViewModel
            {
                Id = cliente.Id,
                Nombre = cliente.Nombre,
                CorreoElectronico = cliente.CorreoElectronico,
                NumeroTelefono = cliente.NumeroTelefono,
                FechaCumpleanos = cliente.FechaCumpleaños
            };

        private const int HistorialCitasMaxRows = 100;

        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;
        private readonly ITenantWhatsAppFeatureService _tenantWhatsAppFeatureService;
        private readonly IClienteIdentityService _clienteIdentityService;
        private readonly IClienteVisitMetricsService _clienteVisitMetricsService;
        private readonly IClienteNotasService _clienteNotasService;
        private readonly ILogger<ClientesController> _logger;

        public ClientesController(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider,
            ITenantWhatsAppFeatureService tenantWhatsAppFeatureService,
            IClienteIdentityService clienteIdentityService,
            IClienteVisitMetricsService clienteVisitMetricsService,
            IClienteNotasService clienteNotasService,
            ILogger<ClientesController> logger)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
            _tenantWhatsAppFeatureService = tenantWhatsAppFeatureService;
            _clienteIdentityService = clienteIdentityService;
            _clienteVisitMetricsService = clienteVisitMetricsService;
            _clienteNotasService = clienteNotasService;
            _logger = logger;
        }

        public async Task<IActionResult> Index(int pageNumber = 1, int pageSize = DefaultPageSize)
        {
            var normalizedPageSize = NormalizePageSize(pageSize);
            var clientesQuery = _context.Clientes.AsNoTracking();
            var totalCount = await clientesQuery.CountAsync();
            var totalPages = CalculateTotalPages(totalCount, normalizedPageSize);
            var normalizedPageNumber = NormalizePageNumber(pageNumber, totalPages);

            // Solo la página visible: las métricas se calculan para estos clientes, no para
            // todos los del tenant.
            IReadOnlyList<ClienteSummaryViewModel> clientes = totalCount == 0
                ? Array.Empty<ClienteSummaryViewModel>()
                : await AplicarMetricasAsync(await clientesQuery
                    .OrderBy(c => c.Nombre)
                    .ThenBy(c => c.Id)
                    .Skip((normalizedPageNumber - 1) * normalizedPageSize)
                    .Take(normalizedPageSize)
                    .Select(ClienteSummaryProjection)
                    .ToListAsync());

            return View(new ClientesIndexViewModel
            {
                Clientes = clientes,
                PageNumber = normalizedPageNumber,
                PageSize = normalizedPageSize,
                TotalCount = totalCount
            });
        }

        /// <summary>
        /// Completa la frecuencia efectiva y la última visita de una lista de clientes con la
        /// MISMA fuente que usa el perfil. Resuelve toda la página en dos consultas: nunca una
        /// por cliente.
        /// </summary>
        private async Task<IReadOnlyList<ClienteSummaryViewModel>> AplicarMetricasAsync(
            IReadOnlyList<ClienteSummaryViewModel> clientes,
            CancellationToken cancellationToken = default)
        {
            if (clientes.Count == 0)
            {
                return clientes;
            }

            var metricas = await _clienteVisitMetricsService.GetForClientesAsync(
                clientes.Select(c => c.Id).ToArray(),
                cancellationToken);

            return clientes
                .Select(cliente => metricas.TryGetValue(cliente.Id, out var m)
                    ? cliente with
                    {
                        FrecuenciaEfectivaDias = m.EffectiveFrequencyDays,
                        UltimaVisita = m.LastVisitDate
                    }
                    : cliente)
                .ToList();
        }

        [HttpGet]
        public async Task<IActionResult> Create(CancellationToken cancellationToken)
        {
            // El default vive en el servidor, no en el value del input: el alta automática desde
            // calendario y reservas no pasa por esta vista y necesita el mismo número.
            var cliente = new ClientesModel
            {
                FrecuenciaVisita = ClienteDefaults.InitialVisitFrequencyDays
            };

            await SetTenantWhatsAppEnabledViewDataAsync(cancellationToken);
            return View(cliente);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ClientsManage)]
        [ValidateAntiForgeryToken]
        // FrecuenciaVisita SÍ se bindea: es la frecuencia INICIAL del cliente, un dato que el
        // usuario elige. FechaUltimaVisita y las demás derivadas del historial no.
        public async Task<IActionResult> Create(
            [Bind(
                nameof(ClientesModel.NumeroTelefono) + "," +
                nameof(ClientesModel.CorreoElectronico) + "," +
                nameof(ClientesModel.Nombre) + "," +
                nameof(ClientesModel.FrecuenciaVisita) + "," +
                nameof(ClientesModel.FechaCumpleaños) + "," +
                nameof(ClientesModel.AceptaMensajesWhatsApp))]
            ClientesModel cliente)
        {
            // Columna legacy que ya nadie lee pero sigue siendo NOT NULL.
            cliente.FechaUltimaVisita = _businessDateTimeProvider.Today();

            NormalizeCliente(cliente);
            var tenantWhatsAppEnabled = await SetTenantWhatsAppEnabledViewDataAsync();

            if (tenantWhatsAppEnabled)
            {
                ApplyConsentAudit(cliente);
            }
            else
            {
                cliente.AceptaMensajesWhatsApp = false;
            }

            await ValidateTelefonoDisponibleAsync(cliente.NumeroTelefono);

            if (!ModelState.IsValid)
            {
                return View(cliente);
            }

            try
            {
                // Dar de alta un cliente NO es atenderlo: no se registra visita aquí. Es la
                // misma regla que ya aplicaba ClienteIdentityService al crear clientes desde
                // el calendario y las reservas; la visita la produce la atención real (un cobro
                // o el cierre de la cita), no el alta.
                _context.Clientes.Add(cliente);
                await _context.SaveChangesAsync();

                TempData["Mensaje"] = "Cliente creado correctamente.";
                return RedirectToAction(nameof(Index));
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(
                    ex,
                    "Error al crear cliente para telefono {MaskedNumeroTelefono}.",
                    SensitiveDataMasker.MaskPhone(cliente.NumeroTelefono));
                ModelState.AddModelError(string.Empty, "No fue posible guardar el cliente. Revisa los datos e intentalo de nuevo.");
                return View(cliente);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(
                    ex,
                    "Guard bloqueo la creacion del cliente {MaskedNumeroTelefono}.",
                    SensitiveDataMasker.MaskPhone(cliente.NumeroTelefono));
                ModelState.AddModelError(string.Empty, "No fue posible guardar el cliente por una validacion de seguridad o consistencia.");
                return View(cliente);
            }
        }

        [HttpGet, HttpPost]
        [AutoValidateAntiforgeryToken]
        public async Task<IActionResult> Buscar(string criterio)
        {
            if (string.IsNullOrWhiteSpace(criterio))
            {
                return View(new BuscarClienteViewModel());
            }

            criterio = criterio.Trim();
            var esBusquedaTelefonica = LooksLikePhoneForSearch(criterio);
            var clientesQuery = _context.Clientes.AsNoTracking();

            List<ClienteSummaryViewModel> clientes;
            string? mensaje = null;
            var resultadosLimitados = false;

            if (esBusquedaTelefonica)
            {
                clientes = await ApplyExactPhoneSearch(clientesQuery, criterio)
                    .OrderBy(c => c.Nombre)
                    .ThenBy(c => c.Id)
                    .Select(ClienteSummaryProjection)
                    .ToListAsync();
            }
            else
            {
                clientes = await clientesQuery
                    .Where(c => EF.Functions.Like(c.Nombre, $"%{criterio}%"))
                    .OrderBy(c => c.Nombre)
                    .ThenBy(c => c.Id)
                    .Take(BuscarNombreMaxResults + 1)
                    .Select(ClienteSummaryProjection)
                    .ToListAsync();

                if (clientes.Count > BuscarNombreMaxResults)
                {
                    clientes = clientes.Take(BuscarNombreMaxResults).ToList();
                    resultadosLimitados = true;
                    mensaje = $"Se encontraron muchos clientes. Mostrando los primeros {BuscarNombreMaxResults}; refina la búsqueda.";
                }
            }

            if (clientes.Count == 0)
            {
                return View(new BuscarClienteViewModel
                {
                    Criterio = criterio,
                    EsBusquedaTelefonica = esBusquedaTelefonica,
                    Mensaje = $"No se encontraron clientes con el criterio: {criterio}."
                });
            }

            // Con un solo resultado la vista pinta el perfil completo desde Metricas, así que
            // solo la lista de varios resultados necesita que se le completen las métricas.
            var encontrados = clientes.Count > 1
                ? await AplicarMetricasAsync(clientes)
                : clientes;

            var model = new BuscarClienteViewModel
            {
                Criterio = criterio,
                EsBusquedaTelefonica = esBusquedaTelefonica,
                ResultadosLimitados = resultadosLimitados,
                Mensaje = mensaje,
                ClientesEncontrados = encontrados
            };

            if (clientes.Count == 1)
            {
                var cliente = clientes[0];

                var historialCitas = await _context.Citas
                    .AsNoTracking()
                    .Where(c => c.ClienteId == cliente.Id)
                    .OrderByDescending(c => c.FechaHoraCita)
                    .Take(HistorialCitasMaxRows)
                    .Select(c => new CitaVisitaItemViewModel
                    {
                        Id = c.Id,
                        FechaHoraCita = c.FechaHoraCita,
                        NombreServicio = c.Servicio != null ? c.Servicio.Nombre : null,
                        NombreFuncionario = c.Funcionario != null ? c.Funcionario.Nombre : null
                    })
                    .ToListAsync();

                var notasServicio = await _context.Clientes
                    .AsNoTracking()
                    .Where(c => c.Id == cliente.Id)
                    .Select(c => c.DescripcionServiciosRealizados)
                    .FirstOrDefaultAsync();

                var historialPagos = await _context.Cobros
                    .AsNoTracking()
                    .Where(c => c.ClienteId == cliente.Id)
                    .OrderByDescending(c => c.FechaCobro)
                    .Select(c => new CobroClienteHistorialItemViewModel
                    {
                        IdCobro = c.IdCobro,
                        FechaCobro = c.FechaCobro,
                        Detalle = c.ServicioId != null
                            ? (c.Servicio != null ? c.Servicio.Nombre : "Servicio")
                            : (c.Producto != null ? c.Producto.NombreProducto : "Producto"),
                        NombreFuncionario = c.Funcionario != null ? c.Funcionario.Nombre : null,
                        MetodoPago = c.MetodoPago,
                        Monto = c.Monto,
                        EsServicio = c.ServicioId != null
                    })
                    .ToListAsync();

                // Las tarjetas del perfil salen SIEMPRE del servicio de métricas, nunca de
                // Cliente.FrecuenciaVisita / FechaUltimaVisita: esos campos se capturan a mano
                // y se desincronizan del historial real.
                var metricas = await _clienteVisitMetricsService.GetForClienteAsync(cliente.Id);
                var notas = await _clienteNotasService.GetNotasAsync(cliente.Id);

                model.ClienteSeleccionado = cliente;
                model.Metricas = metricas;
                // El contador del botón sale de la MISMA fuente que las tarjetas. La tabla
                // muestra las HistorialCitasMaxRows más recientes; el contador es el total.
                model.TotalCitasHistorial = metricas.AttendedVisits;
                model.HistorialVisitas = historialCitas;
                model.NotasServicio = notasServicio;
                model.Notas = notas;
                model.HistorialPagos = historialPagos;
            }

            return View(model);
        }

        [HttpGet]
        public async Task<IActionResult> Editar(int id)
        {
            if (id <= 0)
            {
                return NotFound();
            }

            var cliente = await _context.Clientes
                .AsNoTracking()
                .Where(c => c.Id == id)
                .Select(c => new ClientesModel
                {
                    Id = c.Id,
                    Nombre = c.Nombre,
                    NumeroTelefono = c.NumeroTelefono,
                    CorreoElectronico = c.CorreoElectronico,
                    AceptaMensajesWhatsApp = c.AceptaMensajesWhatsApp,
                    WhatsAppConsentUpdatedAtUtc = c.WhatsAppConsentUpdatedAtUtc,
                    WhatsAppConsentSource = c.WhatsAppConsentSource,
                    WhatsAppConsentCapturedByUserId = c.WhatsAppConsentCapturedByUserId,
                    WhatsAppConsentTextVersion = c.WhatsAppConsentTextVersion,
                    FechaCumpleaños = c.FechaCumpleaños
                })
                .FirstOrDefaultAsync();

            if (cliente == null)
            {
                return NotFound();
            }

            // Solo informativa y de la MISMA fuente que el perfil. No se bindea de vuelta.
            var metricas = await _clienteVisitMetricsService.GetForClienteAsync(id);
            ViewData[FrecuenciaCalculadaViewDataKey] = metricas.EffectiveFrequencyDays;

            await SetTenantWhatsAppEnabledViewDataAsync();
            return View(cliente);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ClientsManage)]
        [ValidateAntiForgeryToken]
        // FrecuenciaVisita, FechaUltimaVisita y ProximaVisita son DERIVADAS del historial de
        // citas y no se bindean: nadie debe poder enviarlas por POST para alterar las métricas.
        public async Task<IActionResult> Editar(
            [Bind(
                nameof(ClientesModel.Id) + "," +
                nameof(ClientesModel.NumeroTelefono) + "," +
                nameof(ClientesModel.CorreoElectronico) + "," +
                nameof(ClientesModel.Nombre) + "," +
                nameof(ClientesModel.FechaCumpleaños) + "," +
                nameof(ClientesModel.AceptaMensajesWhatsApp))]
            ClientesModel cliente)
        {
            NormalizeCliente(cliente);
            var tenantWhatsAppEnabled = await SetTenantWhatsAppEnabledViewDataAsync();

            await ValidateTelefonoDisponibleAsync(cliente.NumeroTelefono, cliente.Id);

            if (!ModelState.IsValid)
            {
                return View(cliente);
            }

            ClientesModel? clienteExistente = null;

            try
            {
                var executionStrategy = _context.Database.CreateExecutionStrategy();
                await executionStrategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync();

                    clienteExistente = await _context.Clientes
                        .FirstOrDefaultAsync(c => c.Id == cliente.Id);

                    if (clienteExistente == null)
                    {
                        return;
                    }

                    var telefonoAnterior = clienteExistente.NumeroTelefono;
                    var cambioTelefono = !string.Equals(
                        telefonoAnterior,
                        cliente.NumeroTelefono,
                        StringComparison.Ordinal);
                    var cambioConsentimiento = tenantWhatsAppEnabled &&
                        clienteExistente.AceptaMensajesWhatsApp != cliente.AceptaMensajesWhatsApp;

                    clienteExistente.Nombre = cliente.Nombre;
                    clienteExistente.NumeroTelefono = cliente.NumeroTelefono;
                    clienteExistente.CorreoElectronico = cliente.CorreoElectronico;
                    if (tenantWhatsAppEnabled)
                    {
                        clienteExistente.AceptaMensajesWhatsApp = cliente.AceptaMensajesWhatsApp;
                    }
                    clienteExistente.FechaCumpleaños = cliente.FechaCumpleaños;

                    if (cambioConsentimiento)
                    {
                        ApplyConsentAudit(clienteExistente);
                    }

                    if (cambioTelefono)
                    {
                        await _context.ClienteVisitas
                            .Where(v => v.ClienteId == clienteExistente.Id)
                            .ExecuteUpdateAsync(setters => setters
                                .SetProperty(v => v.NumeroTelefono, cliente.NumeroTelefono));
                    }

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                });

                if (clienteExistente == null)
                {
                    return NotFound();
                }

                TempData["Mensaje"] = "Cliente editado con exito.";
                return RedirectToAction(nameof(Buscar), new { criterio = clienteExistente.NumeroTelefono });
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al editar cliente {ClienteId}.", cliente.Id);
                ModelState.AddModelError(string.Empty, "No fue posible guardar los cambios del cliente.");
                return View(cliente);
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Guard bloqueo la edicion del cliente {ClienteId}.", cliente.Id);
                ModelState.AddModelError(string.Empty, "No fue posible guardar los cambios por una validacion de seguridad o consistencia.");
                return View(cliente);
            }
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ClientsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Eliminar(int id)
        {
            if (id <= 0)
            {
                return BadRequest("Cliente no valido.");
            }

            var cliente = await _context.Clientes
                .FirstOrDefaultAsync(c => c.Id == id);

            if (cliente == null)
            {
                return NotFound();
            }

            try
            {
                _context.Clientes.Remove(cliente);
                await _context.SaveChangesAsync();

                TempData["Mensaje"] = "Cliente eliminado correctamente.";
                return RedirectToAction(nameof(Buscar));
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al eliminar cliente {ClienteId}.", id);
                TempData["Error"] = "No fue posible eliminar el cliente porque tiene datos relacionados.";
                return RedirectToAction(nameof(Buscar), new { criterio = cliente.NumeroTelefono });
            }
        }

        [HttpGet]
        public async Task<IActionResult> RegistrarServicios(int id)
        {
            if (id <= 0)
            {
                return NotFound();
            }

            var model = await BuildRegistrarServiciosViewModelAsync(id);
            if (model == null)
            {
                return NotFound();
            }

            return View(model);
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ClientsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AgregarVisitaRapida(int id)
        {
            if (id <= 0)
            {
                return BadRequest();
            }

            ClientesModel? cliente = null;

            try
            {
                var executionStrategy = _context.Database.CreateExecutionStrategy();
                await executionStrategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync();

                    cliente = await _context.Clientes
                        .FirstOrDefaultAsync(c => c.Id == id);

                    if (cliente == null)
                    {
                        return;
                    }

                    var hoy = _businessDateTimeProvider.Today();
                    cliente.FechaUltimaVisita = hoy;

                    _context.ClienteVisitas.Add(new ClienteVisitas
                    {
                        ClienteId = cliente.Id,
                        NumeroTelefono = cliente.NumeroTelefono,
                        FechaVisita = hoy
                    });

                    await _context.SaveChangesAsync();
                    await transaction.CommitAsync();
                });

                if (cliente == null)
                {
                    return NotFound();
                }

                TempData["Mensaje"] = "Visita registrada correctamente.";
                return RedirectToAction(nameof(Buscar), new { criterio = cliente.NumeroTelefono });
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al agregar visita rapida para cliente {ClienteId}.", id);
                TempData["Error"] = "No fue posible registrar la visita.";
                return RedirectToAction(nameof(Buscar), new { criterio = cliente?.NumeroTelefono });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Guard bloqueo la visita rapida del cliente {ClienteId}.", id);
                TempData["Error"] = "No fue posible registrar la visita por una validacion de seguridad o consistencia.";
                return RedirectToAction(nameof(Buscar), new { criterio = cliente?.NumeroTelefono });
            }
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ClientsManage)]
        [ValidateAntiForgeryToken]
        [Consumes("application/x-www-form-urlencoded")]
        public async Task<IActionResult> RegistrarServicios(
            [Bind(
                nameof(ServicioRealizadoViewModel.ClienteId) + "," +
                nameof(ServicioRealizadoViewModel.DescripcionServicios))]
            ServicioRealizadoViewModel model)
        {
            if (!ModelState.IsValid)
            {
                var invalidModel = await BuildRegistrarServiciosViewModelAsync(model.ClienteId);
                if (invalidModel == null)
                {
                    return NotFound();
                }

                invalidModel.DescripcionServicios = model.DescripcionServicios;
                return View(invalidModel);
            }

            var cliente = await _context.Clientes
                .FirstOrDefaultAsync(c => c.Id == model.ClienteId);

            if (cliente == null)
            {
                return NotFound();
            }

            try
            {
                cliente.DescripcionServiciosRealizados = NormalizeOptionalString(model.DescripcionServicios);
                await _context.SaveChangesAsync();

                TempData["Mensaje"] = "Registro de servicios actualizado correctamente.";
                return RedirectToAction(nameof(Buscar), new { criterio = cliente.NumeroTelefono });
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al actualizar servicios del cliente {ClienteId}.", model.ClienteId);
                ModelState.AddModelError(string.Empty, "No fue posible actualizar el registro de servicios.");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Guard bloqueo el registro de servicios del cliente {ClienteId}.", model.ClienteId);
                ModelState.AddModelError(string.Empty, "No fue posible actualizar el registro por una validacion de seguridad o consistencia.");
            }

            var reloadModel = await BuildRegistrarServiciosViewModelAsync(model.ClienteId);
            if (reloadModel == null)
            {
                return NotFound();
            }

            reloadModel.DescripcionServicios = model.DescripcionServicios;
            return View(reloadModel);
        }

        /// <summary>
        /// Guarda una nota rápida sin salir del perfil del cliente. El cliente se valida
        /// contra el tenant actual dentro del servicio; el id que envía el navegador nunca
        /// se usa para leer datos sin ese filtro.
        /// </summary>
        [HttpPost]
        [RequirePermission(AppPermissions.ClientsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AgregarNota(
            int clienteId,
            string? texto,
            CancellationToken cancellationToken)
        {
            var resultado = await _clienteNotasService.AgregarNotaAsync(
                clienteId,
                texto,
                ResolveFuncionarioIdActual(),
                cancellationToken);

            if (resultado.ClienteNoEncontrado)
            {
                return NotFound(new { error = resultado.Error });
            }

            if (!resultado.Exitoso)
            {
                return BadRequest(new { error = resultado.Error });
            }

            return Ok(BuildNotaPayload(resultado.Nota!));
        }

        /// <summary>
        /// Edita una nota sin salir del perfil. La nota se resuelve por tenant + cliente + id
        /// dentro del servicio: un id de otra cuenta o de otro cliente devuelve 404.
        /// </summary>
        [HttpPost]
        [RequirePermission(AppPermissions.ClientsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ActualizarNota(
            int clienteId,
            int notaId,
            string? texto,
            CancellationToken cancellationToken)
        {
            var resultado = await _clienteNotasService.ActualizarNotaAsync(
                clienteId,
                notaId,
                texto,
                cancellationToken);

            if (resultado.ClienteNoEncontrado)
            {
                return NotFound(new { error = resultado.Error });
            }

            if (!resultado.Exitoso)
            {
                return BadRequest(new { error = resultado.Error });
            }

            return Ok(BuildNotaPayload(resultado.Nota!));
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ClientsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarNota(
            int clienteId,
            int notaId,
            CancellationToken cancellationToken)
        {
            var resultado = await _clienteNotasService.EliminarNotaAsync(
                clienteId,
                notaId,
                cancellationToken);

            if (resultado.ClienteNoEncontrado)
            {
                return NotFound(new { error = resultado.Error });
            }

            if (!resultado.Exitoso)
            {
                return BadRequest(new { error = resultado.Error });
            }

            return Ok(new { eliminado = true });
        }

        private static object BuildNotaPayload(ClienteServicioRealizadoItemViewModel nota) =>
            new
            {
                id = nota.Id,
                texto = nota.Notas,
                fecha = nota.FechaHora.ToString("dd/MM/yyyy"),
                hora = nota.FechaHora.ToString("hh:mm tt"),
                funcionario = nota.NombreFuncionario
            };

        /// <summary>
        /// Funcionario que escribe, cuando la cuenta corresponde a uno. Un administrador sin
        /// ficha de colaborador devuelve <c>null</c> en vez de atribuir la nota a nadie.
        /// </summary>
        private int? ResolveFuncionarioIdActual()
        {
            var claim = User.FindFirst(CustomClaimTypes.FuncionarioId)?.Value;

            return int.TryParse(claim, out var funcionarioId) && funcionarioId > 0
                ? funcionarioId
                : null;
        }

        [HttpPost]
        [RequirePermission(AppPermissions.ClientsManage)]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EliminarVisita(int id)
        {
            if (id <= 0)
            {
                return BadRequest();
            }

            var visita = await _context.ClienteVisitas
                .FirstOrDefaultAsync(v => v.Id == id);

            if (visita == null)
            {
                return NotFound();
            }

            try
            {
                _context.ClienteVisitas.Remove(visita);
                await _context.SaveChangesAsync();

                TempData["Mensaje"] = "Visita eliminada correctamente.";
                return RedirectToAction(nameof(Buscar), new { criterio = visita.NumeroTelefono });
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Error al eliminar visita {VisitaId}.", id);
                TempData["Error"] = "No fue posible eliminar la visita.";
                return RedirectToAction(nameof(Buscar), new { criterio = visita.NumeroTelefono });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogError(ex, "Guard bloqueo la eliminacion de la visita {VisitaId}.", id);
                TempData["Error"] = "No fue posible eliminar la visita por una validacion de seguridad o consistencia.";
                return RedirectToAction(nameof(Buscar), new { criterio = visita.NumeroTelefono });
            }
        }

        [HttpGet]
        public async Task<IActionResult> Autocompletado(string term)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                return Ok(Array.Empty<object>());
            }

            term = term.Trim();

            if (term.Length < AutocompleteMinLength)
            {
                return Ok(Array.Empty<object>());
            }

            var esBusquedaTelefonica = LooksLikePhoneFragment(term);
            var tenantWhatsAppEnabled = await _tenantWhatsAppFeatureService.IsWhatsAppEnabledForCurrentTenantAsync();
            var clientesQuery = _context.Clientes.AsNoTracking();
            var normalizedPhoneTerm = NormalizePhoneForComparison(term);

            clientesQuery = esBusquedaTelefonica
                ? ApplyPhoneAutocompleteSearch(clientesQuery, term)
                    .OrderByDescending(c => c.NumeroTelefono
                        .Replace(" ", string.Empty)
                        .Replace("-", string.Empty)
                        .Replace("(", string.Empty)
                        .Replace(")", string.Empty)
                        .Replace("+", string.Empty)
                        .Replace(".", string.Empty)
                        .Replace("/", string.Empty) == normalizedPhoneTerm)
                    .ThenBy(c => c.Nombre)
                    .ThenBy(c => c.Id)
                : clientesQuery
                    .Where(c => EF.Functions.Like(c.Nombre, $"%{term}%"))
                    .OrderBy(c => c.Nombre)
                    .ThenBy(c => c.Id);

            var clientes = await clientesQuery
                .Take(AutocompleteMaxResults)
                .Select(c => new
                {
                    id = c.Id,
                    nombre = c.Nombre,
                    telefono = c.NumeroTelefono,
                    correo = c.CorreoElectronico,
                    aceptaMensajesWhatsApp = tenantWhatsAppEnabled && c.AceptaMensajesWhatsApp
                })
                .ToListAsync();

            return Ok(clientes);
        }

        /// <summary>
        /// Estado de identidad de un cliente a partir de lo escrito en un formulario (hoy, el de
        /// "Nueva cita"). Es SOLO para la experiencia de usuario: al guardar, el backend vuelve a
        /// resolver la identidad dentro de la transacción, así que este resultado nunca decide nada.
        ///
        /// <para>
        /// Requiere el mismo permiso que el autocompletado (Clientes.Ver) porque devuelve datos de
        /// la base de Clientes. No existe ninguna variante pública de esta consulta.
        /// </para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ResolverIdentidad(
            string? nombre,
            string? telefono,
            CancellationToken cancellationToken)
        {
            var resolucion = await _clienteIdentityService.ResolveAsync(nombre, telefono, cancellationToken);

            // El consentimiento solo es información útil si el negocio tiene WhatsApp: mismo
            // criterio que el autocompletado, para no filtrar estado de un complemento inactivo.
            var tenantWhatsAppEnabled = await _tenantWhatsAppFeatureService
                .IsWhatsAppEnabledForCurrentTenantAsync(cancellationToken);

            return Ok(new
            {
                estado = resolucion.Status.ToString(),
                cliente = resolucion.SingleMatch is null
                    ? null
                    : new
                    {
                        id = resolucion.SingleMatch.ClienteId,
                        nombre = resolucion.SingleMatch.Nombre,
                        telefono = resolucion.SingleMatch.NumeroTelefono,
                        aceptaMensajesWhatsApp = tenantWhatsAppEnabled && resolucion.SingleMatch.AceptaMensajesWhatsApp
                    },
                coincidencias = resolucion.Matches.Select(m => new
                {
                    id = m.ClienteId,
                    nombre = m.Nombre,
                    telefono = m.NumeroTelefono,
                    aceptaMensajesWhatsApp = tenantWhatsAppEnabled && m.AceptaMensajesWhatsApp
                })
            });
        }

        [HttpGet]
        public async Task<IActionResult> ObtenerNotasServicio(int clienteId)
        {
            if (clienteId <= 0)
            {
                return Ok(new { descripcion = (string?)null });
            }

            var descripcion = await _context.Clientes
                .AsNoTracking()
                .Where(c => c.Id == clienteId)
                .Select(c => c.DescripcionServiciosRealizados)
                .FirstOrDefaultAsync();

            return Ok(new { descripcion });
        }

        private async Task ValidateTelefonoDisponibleAsync(string numeroTelefono, int? clienteIdActual = null)
        {
            var telefono = NormalizeRequiredString(numeroTelefono);
            if (string.IsNullOrWhiteSpace(telefono))
            {
                return;
            }

            var query = _context.Clientes
                .AsNoTracking()
                .Where(c => c.NumeroTelefono == telefono);

            if (clienteIdActual.HasValue)
            {
                query = query.Where(c => c.Id != clienteIdActual.Value);
            }

            if (await query.AnyAsync())
            {
                ModelState.AddModelError(
                    nameof(ClientesModel.NumeroTelefono),
                    "Este numero de telefono ya se encuentra registrado.");
            }
        }

        private async Task<ServicioRealizadoViewModel?> BuildRegistrarServiciosViewModelAsync(int clienteId)
        {
            var vm = await _context.Clientes
                .AsNoTracking()
                .Where(c => c.Id == clienteId)
                .Select(c => new ServicioRealizadoViewModel
                {
                    ClienteId = c.Id,
                    NumeroTelefono = c.NumeroTelefono,
                    NombreCliente = c.Nombre,
                    DescripcionServicios = c.DescripcionServiciosRealizados,
                    FechaUltimaVisita = c.FechaUltimaVisita
                })
                .FirstOrDefaultAsync();

            if (vm != null)
            {
                // Misma fuente que el perfil: dos pantallas no pueden mostrar totales distintos.
                var metricas = await _clienteVisitMetricsService.GetForClienteAsync(clienteId);
                vm.TotalVisitas = metricas.AttendedVisits;
            }

            return vm;
        }

        private static IQueryable<ClientesModel> ApplyExactPhoneSearch(
            IQueryable<ClientesModel> query,
            string criterio)
        {
            var normalizedPhone = NormalizePhoneForComparison(criterio);

            return query.Where(c =>
                c.NumeroTelefono == criterio ||
                c.NumeroTelefono
                    .Replace(" ", string.Empty)
                    .Replace("-", string.Empty)
                    .Replace("(", string.Empty)
                    .Replace(")", string.Empty)
                    .Replace("+", string.Empty)
                    .Replace(".", string.Empty)
                    .Replace("/", string.Empty) == normalizedPhone);
        }

        private static IQueryable<ClientesModel> ApplyPhoneAutocompleteSearch(
            IQueryable<ClientesModel> query,
            string term)
        {
            var normalizedPhone = NormalizePhoneForComparison(term);

            return query.Where(c =>
                EF.Functions.Like(c.NumeroTelefono, $"{term}%") ||
                EF.Functions.Like(
                    c.NumeroTelefono
                        .Replace(" ", string.Empty)
                        .Replace("-", string.Empty)
                        .Replace("(", string.Empty)
                        .Replace(")", string.Empty)
                        .Replace("+", string.Empty)
                        .Replace(".", string.Empty)
                        .Replace("/", string.Empty),
                    $"{normalizedPhone}%"));
        }

        private static void NormalizeCliente(ClientesModel cliente)
        {
            cliente.Nombre = NormalizeRequiredString(cliente.Nombre);
            cliente.NumeroTelefono = NormalizeRequiredString(cliente.NumeroTelefono);
            cliente.CorreoElectronico = NormalizeOptionalString(cliente.CorreoElectronico);
        }

        private void ApplyConsentAudit(ClientesModel cliente)
        {
            cliente.WhatsAppConsentUpdatedAtUtc = DateTime.UtcNow;
            cliente.WhatsAppConsentSource = WhatsAppConsentSources.ClienteForm;
            cliente.WhatsAppConsentTextVersion = WhatsAppConsentTextVersions.WaOptInV1;
            cliente.WhatsAppConsentCapturedByUserId = ResolveCurrentUserId();
        }

        private string? ResolveCurrentUserId() =>
            User.FindFirstValue(ClaimTypes.NameIdentifier);

        private async Task<bool> SetTenantWhatsAppEnabledViewDataAsync(CancellationToken cancellationToken = default)
        {
            var isEnabled = await _tenantWhatsAppFeatureService.IsWhatsAppEnabledForCurrentTenantAsync(cancellationToken);
            ViewData[TenantWhatsAppEnabledViewDataKey] = isEnabled;
            return isEnabled;
        }

        private static int NormalizePageNumber(int pageNumber, int totalPages)
        {
            if (pageNumber < 1)
            {
                return 1;
            }

            return Math.Min(pageNumber, totalPages);
        }

        private static int NormalizePageSize(int pageSize)
        {
            if (pageSize <= 0)
            {
                return DefaultPageSize;
            }

            return Math.Min(pageSize, MaxPageSize);
        }

        private static int CalculateTotalPages(int totalCount, int pageSize) =>
            totalCount == 0
                ? 1
                : (int)Math.Ceiling(totalCount / (double)pageSize);

        private static bool LooksLikePhoneForSearch(string criterio)
        {
            var digitCount = 0;

            foreach (var character in criterio)
            {
                if (char.IsDigit(character))
                {
                    digitCount++;
                    continue;
                }

                if (char.IsWhiteSpace(character) ||
                    character is '+' or '-' or '(' or ')' or '.' or '/')
                {
                    continue;
                }

                return false;
            }

            return digitCount >= 4;
        }

        private static bool LooksLikePhoneFragment(string criterio)
        {
            var digitCount = 0;

            foreach (var character in criterio)
            {
                if (char.IsDigit(character))
                {
                    digitCount++;
                    continue;
                }

                if (char.IsWhiteSpace(character) ||
                    character is '+' or '-' or '(' or ')' or '.' or '/')
                {
                    continue;
                }

                return false;
            }

            return digitCount >= AutocompleteMinLength;
        }

        private static string NormalizePhoneForComparison(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value
                    .Replace(" ", string.Empty, StringComparison.Ordinal)
                    .Replace("-", string.Empty, StringComparison.Ordinal)
                    .Replace("(", string.Empty, StringComparison.Ordinal)
                    .Replace(")", string.Empty, StringComparison.Ordinal)
                    .Replace("+", string.Empty, StringComparison.Ordinal)
                    .Replace(".", string.Empty, StringComparison.Ordinal)
                    .Replace("/", string.Empty, StringComparison.Ordinal);

        private static string NormalizeRequiredString(string? value) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

        private static string? NormalizeOptionalString(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
