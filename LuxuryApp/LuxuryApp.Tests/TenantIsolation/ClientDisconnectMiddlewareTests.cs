using LuxuryApp.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class ClientDisconnectMiddlewareTests
    {
        [Fact]
        public async Task Invoke_WhenClientAbortedAndOperationCanceled_RespondsWith499AndDoesNotThrow()
        {
            var middleware = new ClientDisconnectMiddleware(
                _ => throw new OperationCanceledException("EF Core canceló la consulta."),
                NullLogger<ClientDisconnectMiddleware>.Instance);

            var httpContext = new DefaultHttpContext
            {
                // Simula que el navegador abortó la request (cambio rápido de módulo).
                RequestAborted = new CancellationToken(canceled: true)
            };

            await middleware.Invoke(httpContext);

            // Cancelación esperada => 499 Client Closed Request, nunca 500.
            Assert.Equal(499, httpContext.Response.StatusCode);
            Assert.NotEqual(StatusCodes.Status500InternalServerError, httpContext.Response.StatusCode);
        }

        [Fact]
        public async Task Invoke_WhenClientAbortedAndTaskCanceled_TreatsInheritedExceptionAsExpected()
        {
            // TaskCanceledException hereda de OperationCanceledException: debe entrar por el mismo filtro.
            var middleware = new ClientDisconnectMiddleware(
                _ => throw new TaskCanceledException("La tarea de consulta fue cancelada."),
                NullLogger<ClientDisconnectMiddleware>.Instance);

            var httpContext = new DefaultHttpContext
            {
                RequestAborted = new CancellationToken(canceled: true)
            };

            await middleware.Invoke(httpContext);

            Assert.Equal(499, httpContext.Response.StatusCode);
        }

        [Fact]
        public async Task Invoke_WhenOperationCanceledButClientNotAborted_RethrowsAsRealError()
        {
            // OperationCanceledException NO originada por RequestAborted (p. ej. timeout real de SQL
            // con otro token) no se oculta: debe propagarse para tratarse como error real.
            var middleware = new ClientDisconnectMiddleware(
                _ => throw new OperationCanceledException("Timeout real de SQL, token ajeno."),
                NullLogger<ClientDisconnectMiddleware>.Instance);

            var httpContext = new DefaultHttpContext
            {
                RequestAborted = CancellationToken.None // el cliente NO abortó.
            };

            await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.Invoke(httpContext));
        }

        [Fact]
        public async Task Invoke_WhenNextCompletesNormally_PassesThroughWithoutTouchingStatus()
        {
            var nextCalled = false;
            var middleware = new ClientDisconnectMiddleware(
                _ =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                },
                NullLogger<ClientDisconnectMiddleware>.Instance);

            var httpContext = new DefaultHttpContext
            {
                RequestAborted = new CancellationToken(canceled: true)
            };

            await middleware.Invoke(httpContext);

            Assert.True(nextCalled);
            // Sin excepción no se toca el estado: sigue siendo el 200 por defecto.
            Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        }

        [Fact]
        public async Task Invoke_WhenNonCancellationExceptionThrown_Rethrows()
        {
            // Errores reales (NullReference, SQL, servicio externo) nunca se ocultan.
            var middleware = new ClientDisconnectMiddleware(
                _ => throw new InvalidOperationException("Error real de negocio."),
                NullLogger<ClientDisconnectMiddleware>.Instance);

            var httpContext = new DefaultHttpContext
            {
                RequestAborted = new CancellationToken(canceled: true)
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.Invoke(httpContext));
        }
    }
}
