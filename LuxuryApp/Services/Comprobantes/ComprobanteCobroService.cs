using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using LuxuryApp.Models.Comprobantes;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Fiscal;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Fiscal;
using LuxuryApp.Services.Tenant;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Comprobantes
{
    public sealed class ComprobanteCobroService : IComprobanteCobroService
    {
        private readonly ApplicationDbContext _context;
        private readonly ITenantProvider _tenantProvider;
        private readonly IBusinessDateTimeProvider _clock;
        private readonly IComprobantePdfService _pdfService;
        private readonly IComprobanteEmailService _emailService;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IConfiguration _configuration;
        private readonly ITenantDisplayNameService _displayNameService;
        private readonly ITaxCalculationService _taxService;
        private readonly ITenantFiscalConfigService _fiscalConfig;
        private readonly ILogger<ComprobanteCobroService> _logger;

        public ComprobanteCobroService(
            ApplicationDbContext context,
            ITenantProvider tenantProvider,
            IBusinessDateTimeProvider clock,
            IComprobantePdfService pdfService,
            IComprobanteEmailService emailService,
            IHttpContextAccessor httpContextAccessor,
            IConfiguration configuration,
            ITenantDisplayNameService displayNameService,
            ITaxCalculationService taxService,
            ITenantFiscalConfigService fiscalConfig,
            ILogger<ComprobanteCobroService> logger)
        {
            _context = context;
            _tenantProvider = tenantProvider;
            _clock = clock;
            _pdfService = pdfService;
            _emailService = emailService;
            _httpContextAccessor = httpContextAccessor;
            _configuration = configuration;
            _displayNameService = displayNameService;
            _taxService = taxService;
            _fiscalConfig = fiscalConfig;
            _logger = logger;
        }

        public byte[] GenerarPdf(ComprobanteCobro comprobante) => _pdfService.Generar(comprobante);

        public async Task<ComprobanteCobro?> CrearYEnviarDesdeCobroAsync(
            int cobroId,
            string emailDestino,
            bool guardarEmailEnCliente,
            string? createdByUserId,
            int? funcionarioScopeId,
            CancellationToken cancellationToken = default)
        {
            emailDestino = (emailDestino ?? string.Empty).Trim();

            // 1) Idempotencia por CobroId: si ya hay comprobante no cancelado, no creamos otro.
            var existente = await _context.ComprobantesCobro
                .Include(c => c.Lineas)
                .FirstOrDefaultAsync(
                    c => c.CobroId == cobroId && c.EstadoEnvio != ComprobanteEstadoEnvio.Cancelled,
                    cancellationToken);

            if (existente is not null)
            {
                if (funcionarioScopeId.HasValue && existente.FuncionarioId != funcionarioScopeId.Value)
                {
                    _logger.LogWarning(
                        "Funcionario {Funcionario} intentó tocar comprobante {Id} ajeno.",
                        funcionarioScopeId, existente.Id);
                    return null;
                }

                // Ya enviado: nada que hacer (no duplicar). Pendiente/Fallido: reintentamos el envío.
                if (existente.EstadoEnvio == ComprobanteEstadoEnvio.Sent)
                {
                    return existente;
                }

                await EnviarInternoAsync(existente, cancellationToken);
                return existente;
            }

            // 2) Cargar el cobro (filtro global de tenant aplica) con sus relaciones.
            var cobro = await _context.Cobros
                .Include(c => c.Servicio)
                .Include(c => c.Producto)
                .Include(c => c.Cliente)
                .Include(c => c.Funcionario)
                .Include(c => c.ProductosVendidos)
                .FirstOrDefaultAsync(c => c.IdCobro == cobroId, cancellationToken);

            if (cobro is null)
            {
                _logger.LogWarning("No se encontró el cobro {CobroId} para generar comprobante.", cobroId);
                return null;
            }

            // 🔒 Portal: el cobro debe ser del funcionario del claim.
            if (funcionarioScopeId.HasValue && cobro.FuncionarioId != funcionarioScopeId.Value)
            {
                _logger.LogWarning(
                    "Funcionario {Funcionario} intentó emitir comprobante de cobro ajeno {CobroId}.",
                    funcionarioScopeId, cobroId);
                return null;
            }

            // Guardar el correo en el perfil del cliente si se pidió (cliente cargado dentro
            // del filtro de tenant → seguro). Best-effort: si falla, no rompe el comprobante.
            if (guardarEmailEnCliente &&
                cobro.Cliente is not null &&
                !string.IsNullOrWhiteSpace(emailDestino) &&
                !string.Equals(cobro.Cliente.CorreoElectronico, emailDestino, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    cobro.Cliente.CorreoElectronico = emailDestino;
                    await _context.SaveChangesAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo guardar el correo en el perfil del cliente del cobro {CobroId}.", cobroId);
                    try { _context.Entry(cobro.Cliente).Property(c => c.CorreoElectronico).IsModified = false; }
                    catch { /* ignore */ }
                }
            }

            // 3) Crear el comprobante (numeración + snapshot + insert). NUNCA propaga una
            // excepción técnica al controlador: ante un fallo se loguea y se devuelve null o,
            // si fue una carrera de doble submit, se reutiliza el comprobante existente.
            ComprobanteCobro comprobante;
            try
            {
                var tenantId = _tenantProvider.GetTenantId();
                var nombreNegocio = await _displayNameService.GetTenantDisplayNameAsync(tenantId, cancellationToken);

                var numero = await GenerarNumeroInternoAsync(tenantId, nombreNegocio, _clock.Now(), cancellationToken);

                // Desglose fiscal del cobro (IVA incluido por defecto en CR). Snapshot inmutable.
                var tenantFiscal = await _fiscalConfig.ObtenerAsync(cancellationToken);
                var desglose = CalcularDesgloseFiscal(cobro, tenantFiscal);

                comprobante = new ComprobanteCobro
                {
                    CobroId = cobro.IdCobro,
                    CitaId = cobro.CitaId,
                    ClienteId = cobro.ClienteId,
                    FuncionarioId = cobro.FuncionarioId,
                    NumeroInterno = numero,
                    TipoComprobante = ComprobanteTipo.ComprobanteInterno,
                    EstadoEnvio = ComprobanteEstadoEnvio.Pending,
                    TokenPublico = GenerarToken(),
                    EmailDestino = emailDestino,
                    EmailDestinoNormalizado = emailDestino.ToLowerInvariant(),
                    NombreClienteSnapshot = string.IsNullOrWhiteSpace(cobro.NombreCliente) ? "Cliente" : cobro.NombreCliente,
                    TelefonoClienteSnapshot = cobro.Cliente?.NumeroTelefono,
                    NombreNegocioSnapshot = nombreNegocio,
                    FechaEmision = _clock.Now(),
                    Moneda = "CRC",
                    MetodoPago = cobro.MetodoPago,
                    Observacion = cobro.Observaciones,
                    Subtotal = desglose.NetBase,
                    Descuento = 0m,
                    Impuesto = desglose.TaxAmount,
                    Total = desglose.GrossTotal,
                    IntentosEnvio = 0,
                    CreatedAt = _clock.Now(),
                    CreatedByUserId = createdByUserId,
                    EsFiscal = false
                };

                comprobante.Lineas.Add(ConstruirLinea(cobro, desglose));

                _context.ComprobantesCobro.Add(comprobante);
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (EsViolacionUnica(ex))
            {
                // Doble submit concurrente: el índice único TenantId+CobroId protege la integridad.
                _logger.LogWarning(ex, "Comprobante duplicado (carrera) para cobro {CobroId}; se reutiliza el existente.", cobroId);
                var dup = await _context.ComprobantesCobro
                    .Include(c => c.Lineas)
                    .FirstOrDefaultAsync(c => c.CobroId == cobroId && c.EstadoEnvio != ComprobanteEstadoEnvio.Cancelled, cancellationToken);
                if (dup is null)
                {
                    return null;
                }
                if (dup.EstadoEnvio != ComprobanteEstadoEnvio.Sent)
                {
                    await EnviarInternoAsync(dup, cancellationToken);
                }
                return dup;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo crear el comprobante del cobro {CobroId}.", cobroId);
                return null;
            }

            // 4) Enviar (fuera de toda transacción). Best-effort: si falla, queda Failed.
            await EnviarInternoAsync(comprobante, cancellationToken);
            return comprobante;
        }

        public async Task<ComprobanteCobro?> ReenviarAsync(
            int comprobanteId,
            int? funcionarioScopeId,
            CancellationToken cancellationToken = default)
        {
            var comprobante = await _context.ComprobantesCobro
                .Include(c => c.Lineas)
                .FirstOrDefaultAsync(c => c.Id == comprobanteId, cancellationToken);

            if (comprobante is null)
            {
                return null;
            }

            if (funcionarioScopeId.HasValue && comprobante.FuncionarioId != funcionarioScopeId.Value)
            {
                _logger.LogWarning(
                    "Funcionario {Funcionario} intentó reenviar comprobante {Id} ajeno.",
                    funcionarioScopeId, comprobanteId);
                return null;
            }

            if (comprobante.EstadoEnvio == ComprobanteEstadoEnvio.Cancelled)
            {
                return comprobante;
            }

            await EnviarInternoAsync(comprobante, cancellationToken);
            return comprobante;
        }

        public Task<ComprobanteCobro?> ObtenerParaAppAsync(
            int comprobanteId,
            int? funcionarioScopeId,
            CancellationToken cancellationToken = default)
        {
            var query = _context.ComprobantesCobro
                .AsNoTracking()
                .Include(c => c.Lineas)
                .Where(c => c.Id == comprobanteId);

            if (funcionarioScopeId.HasValue)
            {
                query = query.Where(c => c.FuncionarioId == funcionarioScopeId.Value);
            }

            return query.FirstOrDefaultAsync(cancellationToken)!;
        }

        public async Task<ComprobanteCobro?> ObtenerPorTokenPublicoAsync(
            string token,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length < 16)
            {
                return null;
            }

            // Ruta pública sin login: ignoramos el filtro de tenant (no hay contexto) y
            // resolvemos exclusivamente por el token largo aleatorio (único global).
            return await _context.ComprobantesCobro
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Include(c => c.Lineas)
                .FirstOrDefaultAsync(c => c.TokenPublico == token, cancellationToken);
        }

        // ─────────────────────────── Internos ───────────────────────────

        private async Task EnviarInternoAsync(ComprobanteCobro comprobante, CancellationToken cancellationToken)
        {
            // Best-effort total: este método NUNCA lanza. Cualquier fallo deja el comprobante
            // en Failed (reintentable) y el cobro intacto.
            try
            {
                comprobante.IntentosEnvio++;

                byte[] pdf;
                try
                {
                    pdf = _pdfService.Generar(comprobante);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error generando PDF del comprobante {Numero}.", comprobante.NumeroInterno);
                    comprobante.EstadoEnvio = ComprobanteEstadoEnvio.Failed;
                    comprobante.ErrorEnvio = "No fue posible generar el PDF.";
                    await _context.SaveChangesAsync(cancellationToken);
                    return;
                }

                var urlPublica = ConstruirUrlPublica(comprobante.TokenPublico);
                var result = await _emailService.EnviarComprobanteCobroAsync(comprobante, pdf, urlPublica, cancellationToken);

                if (result.Success)
                {
                    comprobante.EstadoEnvio = ComprobanteEstadoEnvio.Sent;
                    comprobante.ResendEmailId = result.ResendEmailId;
                    comprobante.ErrorEnvio = null;
                    comprobante.SentAt = _clock.Now();
                }
                else
                {
                    comprobante.EstadoEnvio = ComprobanteEstadoEnvio.Failed;
                    comprobante.ErrorEnvio = Truncar(result.Error, 500);
                }

                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inesperado al enviar el comprobante {Numero}.", comprobante.NumeroInterno);
                try
                {
                    comprobante.EstadoEnvio = ComprobanteEstadoEnvio.Failed;
                    comprobante.ErrorEnvio = Truncar("Error inesperado al enviar el comprobante.", 500);
                    await _context.SaveChangesAsync(cancellationToken);
                }
                catch (Exception persistEx)
                {
                    _logger.LogError(persistEx, "No se pudo persistir el estado Failed del comprobante {Numero}.", comprobante.NumeroInterno);
                }
            }
        }

        private static bool EsViolacionUnica(DbUpdateException ex) =>
            ex.InnerException is SqlException sqlEx && (sqlEx.Number == 2601 || sqlEx.Number == 2627);

        /// <summary>
        /// Resuelve la fiscalidad efectiva del cobro y calcula su desglose Base/IVA.
        ///
        /// <para>
        /// Usa la MISMA regla que Dashboard, Ingresos, Excel y liquidaciones
        /// (<see cref="CobroFiscalidadEfectiva"/>): manda el snapshot congelado del cobro y el
        /// catálogo actual solo entra para cobros legacy. Así el comprobante que recibe el cliente
        /// no puede decir un IVA distinto del que reporta el negocio por el mismo cobro.
        /// </para>
        /// </summary>
        private TaxBreakdown CalcularDesgloseFiscal(Cobro cobro, TenantFiscalConfig tenant)
        {
            bool aplicaIvaCatalogo;
            decimal? tarifaCatalogo;
            bool? incluyeCatalogo;

            if (cobro.Servicio is not null)
            {
                aplicaIvaCatalogo = cobro.Servicio.AplicaIva;
                tarifaCatalogo = cobro.Servicio.TarifaIva;
                incluyeCatalogo = cobro.Servicio.PrecioIncluyeIva;
            }
            else if (cobro.Producto is not null)
            {
                aplicaIvaCatalogo = cobro.Producto.AplicaIva;
                tarifaCatalogo = cobro.Producto.TarifaIva;
                incluyeCatalogo = cobro.Producto.PrecioIncluyeIva;
            }
            else
            {
                // Servicio personalizado / pago sin catálogo: config del tenant.
                aplicaIvaCatalogo = true;
                tarifaCatalogo = null;
                incluyeCatalogo = null;
            }

            var fiscal = CobroFiscalidadEfectiva.Resolver(
                cobro.AplicaIvaSnapshot,
                cobro.TarifaIvaSnapshot,
                cobro.PrecioIncluyeIvaSnapshot,
                aplicaIvaCatalogo,
                tarifaCatalogo,
                incluyeCatalogo);

            var linea = _fiscalConfig.ResolverLinea(
                cobro.Monto, fiscal.AplicaIva, fiscal.TarifaIva, fiscal.PrecioIncluyeIva, tenant);
            return _taxService.Calcular(linea.TotalOrBase, linea.TaxRatePercent, linea.PriceIncludesTax, linea.Taxable);
        }

        private static ComprobanteCobroLinea ConstruirLinea(Cobro cobro, TaxBreakdown desglose)
        {
            string descripcion;
            string tipo;
            int? servicioId = null;
            int? productoId = null;

            // El nombre histórico manda: el comprobante describe lo que se vendió ese día, no
            // cómo se llama hoy el servicio en el catálogo.
            if (cobro.ServicioId.HasValue)
            {
                descripcion = cobro.DetalleSnapshot ?? cobro.Servicio?.Nombre ?? "Servicio";
                tipo = ComprobanteTipoLinea.Servicio;
                servicioId = cobro.ServicioId;
            }
            else if (cobro.ProductoId.HasValue)
            {
                descripcion = cobro.DetalleSnapshot ?? cobro.Producto?.NombreProducto ?? "Producto";
                tipo = ComprobanteTipoLinea.Producto;
                productoId = cobro.ProductoId;
            }
            else if (!string.IsNullOrWhiteSpace(cobro.ServicioNombrePersonalizado))
            {
                // Servicio personalizado (cita fuera de catálogo): se trata como servicio.
                descripcion = cobro.ServicioNombrePersonalizado;
                tipo = ComprobanteTipoLinea.Servicio;
            }
            else
            {
                descripcion = "Pago";
                tipo = ComprobanteTipoLinea.Otro;
            }

            var cantidad = cobro.ProductosVendidos?.Sum(d => d.Cantidad) ?? 0;
            if (cantidad <= 0)
            {
                cantidad = 1;
            }

            return new ComprobanteCobroLinea
            {
                Descripcion = descripcion,
                TipoLinea = tipo,
                Cantidad = cantidad,
                PrecioUnitario = cobro.Monto,
                Subtotal = desglose.NetBase,
                Impuesto = desglose.TaxAmount,
                Total = desglose.GrossTotal,
                ServicioId = servicioId,
                ProductoId = productoId
            };
        }

        /// <summary>
        /// Genera el siguiente número interno del tenant de forma atómica y concurrencia-segura.
        /// Se ejecuta como COMANDO escalar (DbCommand.ExecuteScalarAsync), NO como consulta EF
        /// componible: el batch usa UPDLOCK+HOLDLOCK para serializar el incremento por tenant y
        /// crea la fila de secuencia si no existe. El índice único TenantId+NumeroInterno sigue
        /// siendo la red de seguridad final. Formato: LC-{SHORTCODE}-{yyyyMMdd}-{secuencia:000000}.
        /// </summary>
        private async Task<string> GenerarNumeroInternoAsync(
            Guid tenantId,
            string nombreNegocio,
            DateTime fechaEmision,
            CancellationToken cancellationToken)
        {
            const string sql = """
                SET NOCOUNT ON;
                DECLARE @n bigint;
                UPDATE [ComprobanteCobroSecuencias] WITH (UPDLOCK, HOLDLOCK)
                   SET @n = UltimoNumero = UltimoNumero + 1
                 WHERE TenantId = @tid;
                IF @@ROWCOUNT = 0
                BEGIN
                    INSERT INTO [ComprobanteCobroSecuencias] (TenantId, UltimoNumero) VALUES (@tid, 1);
                    SET @n = 1;
                END
                SELECT @n;
                """;

            var connection = _context.Database.GetDbConnection();
            var abrir = connection.State != ConnectionState.Open;
            if (abrir)
            {
                await connection.OpenAsync(cancellationToken);
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;

                // Respeta la transacción EF en curso, si la hubiera.
                var currentTx = _context.Database.CurrentTransaction?.GetDbTransaction();
                if (currentTx is not null)
                {
                    command.Transaction = currentTx;
                }

                var tidParam = command.CreateParameter();
                tidParam.ParameterName = "@tid";
                tidParam.Value = tenantId;
                command.Parameters.Add(tidParam);

                var scalar = await command.ExecuteScalarAsync(cancellationToken);
                var secuencia = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);

                return $"LC-{ShortCode(nombreNegocio)}-{fechaEmision:yyyyMMdd}-{secuencia:000000}";
            }
            finally
            {
                if (abrir)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private static string ShortCode(string nombreNegocio)
        {
            var letras = new string((nombreNegocio ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .ToArray())
                .ToUpperInvariant();

            return letras.Length >= 4 ? letras[..4] : (letras.Length > 0 ? letras : "LC");
        }

        private static string GenerarToken()
        {
            // 32 bytes → 43 chars base64url: no adivinable y URL-safe.
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes)
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=');
        }

        private string? ConstruirUrlPublica(string token)
        {
            var request = _httpContextAccessor.HttpContext?.Request;
            string? baseUrl = null;

            if (request is not null)
            {
                baseUrl = $"{request.Scheme}://{request.Host}";
            }
            else
            {
                baseUrl = _configuration["Payments:PublicBaseUrl"];
            }

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return null;
            }

            return $"{baseUrl.TrimEnd('/')}/comprobantes/{token}";
        }

        private static string? Truncar(string? value, int max) =>
            string.IsNullOrEmpty(value) ? value : (value.Length <= max ? value : value[..max]);
    }
}
