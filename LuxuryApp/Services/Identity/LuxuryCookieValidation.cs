using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace LuxuryApp.Services.Identity
{
    /// <summary>
    /// Manejador de <see cref="CookieAuthenticationEvents.OnValidatePrincipal"/> de la cookie
    /// de aplicación de Identity. Se extrae de Program.cs para poder ejercitar el pipeline HTTP
    /// real en pruebas de integración (no solo la lógica del enforcer en pruebas unitarias).
    ///
    /// Orden y garantías (verificadas contra el comportamiento del framework):
    /// <list type="number">
    ///   <item>Tope absoluto de 90 días (<see cref="AbsoluteSessionLifetimeEnforcer"/>): si la
    ///   sesión venció se rechaza y se cierra ANTES de renovar o de tocar el stamp.</item>
    ///   <item><see cref="TenantSessionSecurityValidator"/>: usuario/estado/tenant/claims.</item>
    ///   <item><see cref="SecurityStampValidator"/> de Identity, que se conserva llamándolo
    ///   explícitamente al final (no se reemplaza el evento de Identity).</item>
    /// </list>
    /// Un <see cref="PrincipalContext{TOptions}.RejectPrincipal"/> pone el principal en null y
    /// <see cref="CookieAuthenticationHandler"/> ignora cualquier ShouldRenew previo, además de
    /// que <see cref="Microsoft.AspNetCore.Authentication.IAuthenticationService.SignOutAsync(Microsoft.AspNetCore.Http.HttpContext, string, Microsoft.AspNetCore.Authentication.AuthenticationProperties)"/>
    /// marca la respuesta para borrar la cookie: una sesión rechazada nunca se re-emite.
    /// </summary>
    public static class LuxuryCookieValidation
    {
        public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
        {
            if (context.Principal?.Identity?.IsAuthenticated == true)
            {
                // Tope absoluto de 90 días desde la autenticación original. La marca vive en
                // las propiedades del ticket (cifradas y firmadas), así que sobrevive al
                // sliding, a RefreshSignInAsync y a la regeneración del principal; no puede
                // falsificarse ni provenir de un campo del cliente.
                var sessionLifetime = context.HttpContext.RequestServices
                    .GetRequiredService<AbsoluteSessionLifetimeEnforcer>();
                var decision = sessionLifetime.Evaluate(context.Properties.Items);

                if (decision == AbsoluteSessionLifetimeEnforcer.Decision.Expired)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                    return;
                }

                if (decision == AbsoluteSessionLifetimeEnforcer.Decision.NeedsInitialization)
                {
                    // Se siembra la marca una sola vez y se reescribe el ticket para conservarla.
                    // Sesión nueva (contraseña/2FA/registro): la marca queda prácticamente en el
                    // momento de autenticación (primer request autenticado). Cookie legacy sin
                    // marca (emitida antes del despliegue): el tope de 90 días arranca en este
                    // primer request posterior al despliegue; no se reconstruye una fecha
                    // histórica inexistente. El framework re-protege context.Properties
                    // preservando IsPersistent, así que una cookie de sesión NO se vuelve
                    // persistente ni una cookie legacy de ~8h pasa a 30 días.
                    context.Properties.Items[AbsoluteSessionLifetimeEnforcer.SessionStartedItemKey] =
                        sessionLifetime.CreateStartMarker();
                    context.ShouldRenew = true;
                }

                // Debe correr ANTES que el security stamp validator: con
                // ValidationInterval = Zero el stamp validator regenera el principal desde la
                // BD en cada request, y los checks de claims obsoletos (tenant desalineado,
                // platform_super_admin revocado) nunca verían la cookie original.
                var validator = context.HttpContext.RequestServices
                    .GetRequiredService<TenantSessionSecurityValidator>();
                var isValid = await validator.ValidateAsync(context.Principal, context.HttpContext.RequestAborted);

                if (!isValid)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                    return;
                }
            }

            await SecurityStampValidator.ValidatePrincipalAsync(context);
        }
    }
}
