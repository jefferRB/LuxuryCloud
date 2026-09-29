using System.Security.Claims;
using LuxuryApp.Services.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace LuxuryApp.Middleware
{
    public sealed class ContractAcceptanceMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ContractAcceptanceMiddleware> _logger;

        public ContractAcceptanceMiddleware(
            RequestDelegate next,
            ILogger<ContractAcceptanceMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task Invoke(HttpContext context, IContractService contractService)
        {
            var path = (context.Request.Path.Value ?? string.Empty).ToLowerInvariant();

            if (IsPublicPath(path))
            {
                await _next(context);
                return;
            }

            if (context.User?.Identity?.IsAuthenticated != true)
            {
                await _next(context);
                return;
            }

            // El contrato del SaaS lo acepta el dueño del negocio (Administrador), no el
            // funcionario. Las cuentas de funcionario quedan exentas de este gate.
            if (context.User.IsInRole(Services.Identity.AppRoles.Funcionario) &&
                !context.User.IsInRole(Services.Identity.AppRoles.Administrador))
            {
                await _next(context);
                return;
            }

            var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
            {
                _logger.LogWarning("Sesion autenticada sin NameIdentifier. Se cerrara la cookie actual.");
                await context.SignOutAsync(IdentityConstants.ApplicationScheme);
                context.Response.Redirect("/Accounts/Acceso");
                return;
            }

            var status = await contractService.GetAcceptanceStatusAsync(userId, context.RequestAborted);
            context.Items["ContractAcceptanceStatus"] = status;

            if (!status.BlocksApplicationAccess)
            {
                await _next(context);
                return;
            }

            var returnUrl = BuildCurrentReturnUrl(context);
            var redirectUrl = $"/Contract/Reaccept{returnUrl}";
            context.Response.Redirect(redirectUrl);
        }

        private static string BuildCurrentReturnUrl(HttpContext context)
        {
            var currentPath = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
            return QueryString.Create("returnurl", currentPath).ToString();
        }

        private static bool IsPublicPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "/")
            {
                return true;
            }

            return path.StartsWith("/accounts") ||
                path.StartsWith("/home") ||
                path.StartsWith("/privacidad") ||
                path.StartsWith("/eliminacion-datos") ||
                path.StartsWith("/error") ||
                path.StartsWith("/contract") ||
                path.StartsWith("/billing") ||
                path.StartsWith("/comprobantes") ||
                path.StartsWith("/reservar") ||
                path.StartsWith("/api/webhooks/meta-whatsapp");
        }
    }
}
