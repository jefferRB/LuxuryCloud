namespace LuxuryApp.Middleware
{
    /// <summary>
    /// Convierte las cancelaciones provocadas por el CLIENTE en una respuesta silenciosa
    /// 499 (Client Closed Request), sin registrarlas como error ni activar el
    /// UseExceptionHandler ("/Home/Error").
    ///
    /// Casos esperados en producción: el usuario cambia rápido de módulo, cierra la pestaña,
    /// refresca o hace doble click. El navegador aborta la petición anterior; ASP.NET Core
    /// dispara <see cref="HttpContext.RequestAborted"/> y cualquier consulta EF Core en vuelo
    /// (por ejemplo <c>ToListAsync(cancellationToken)</c>) lanza
    /// <see cref="OperationCanceledException"/> / <see cref="TaskCanceledException"/>.
    ///
    /// Regla estricta (no se ocultan errores reales):
    ///   - Solo se trata como cancelación esperada cuando
    ///     <c>context.RequestAborted.IsCancellationRequested == true</c>.
    ///   - Cualquier otra <see cref="OperationCanceledException"/> (timeout real de SQL,
    ///     token distinto, servicio externo) NO cumple el filtro y se relanza para que siga
    ///     tratándose como error real por el pipeline y el logging habitual.
    ///
    /// <see cref="TaskCanceledException"/> hereda de <see cref="OperationCanceledException"/>,
    /// por eso se captura la base con filtro en lugar de la derivada.
    /// </summary>
    public sealed class ClientDisconnectMiddleware
    {
        // Convención de facto (nginx) para "el cliente cerró la conexión antes de responder".
        private const int ClientClosedRequest = 499;

        private readonly RequestDelegate _next;
        private readonly ILogger<ClientDisconnectMiddleware> _logger;

        public ClientDisconnectMiddleware(
            RequestDelegate next,
            ILogger<ClientDisconnectMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task Invoke(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Cancelación ESPERADA: el cliente/navegador abortó la petición.
                // No es un error → se registra como Debug y se responde 499 sin cuerpo.
                _logger.LogDebug(
                    "Request cancelada por el cliente (RequestAborted). {Method} {Path}. Se responde 499 Client Closed Request.",
                    context.Request.Method,
                    context.Request.Path.Value);

                // Si la respuesta ya empezó a enviarse no se puede cambiar el estado;
                // en ese caso simplemente se traga la excepción para no romper el pipeline.
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = ClientClosedRequest;
                }
            }
        }
    }
}
