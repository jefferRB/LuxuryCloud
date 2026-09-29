using System.Collections.Concurrent;

namespace LuxuryApp.Tests.Support
{
    /// <summary>
    /// <see cref="DelegatingHandler"/> que mantiene un "cookie jar" simple (por nombre) sobre el
    /// handler de un TestServer, para simular un cliente/navegador real: reenvía las cookies
    /// guardadas en cada request y aplica los <c>Set-Cookie</c> de cada respuesta, incluyendo el
    /// borrado (valor vacío) que emite SignOut. Cada instancia = un "navegador" independiente,
    /// por lo que dos handlers distintos representan dos sesiones separadas del mismo usuario.
    /// </summary>
    internal sealed class CookieJarHandler : DelegatingHandler
    {
        private readonly ConcurrentDictionary<string, string> _cookies = new(StringComparer.Ordinal);

        public CookieJarHandler(HttpMessageHandler inner) : base(inner)
        {
        }

        public string? GetCookie(string name) => _cookies.TryGetValue(name, out var v) ? v : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!_cookies.IsEmpty)
            {
                var header = string.Join("; ", _cookies
                    .Where(pair => !string.IsNullOrEmpty(pair.Value))
                    .Select(pair => $"{pair.Key}={pair.Value}"));
                if (header.Length > 0)
                {
                    request.Headers.Remove("Cookie");
                    request.Headers.Add("Cookie", header);
                }
            }

            var response = await base.SendAsync(request, cancellationToken);

            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var raw in setCookies)
                {
                    ApplySetCookie(raw);
                }
            }

            return response;
        }

        private void ApplySetCookie(string setCookie)
        {
            var firstSegment = setCookie.Split(';', 2)[0];
            var eq = firstSegment.IndexOf('=');
            if (eq <= 0)
            {
                return;
            }

            var name = firstSegment[..eq].Trim();
            var value = firstSegment[(eq + 1)..].Trim();

            // Valor vacío o max-age=0 = orden de borrado (por ejemplo, SignOut).
            var isDeletion = string.IsNullOrEmpty(value) ||
                setCookie.Contains("max-age=0", StringComparison.OrdinalIgnoreCase);

            if (isDeletion)
            {
                _cookies.TryRemove(name, out _);
            }
            else
            {
                _cookies[name] = value;
            }
        }
    }
}
