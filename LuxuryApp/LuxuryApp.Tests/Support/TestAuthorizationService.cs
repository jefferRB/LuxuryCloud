using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace LuxuryApp.Tests.Support
{
    /// <summary>
    /// Servicio de autorización de pruebas: responde lo mismo a cualquier política. Sirve para
    /// construir controladores que sólo lo usan para decidir qué pintar en la vista; la
    /// autorización real la aplican los atributos del pipeline, no este servicio.
    /// </summary>
    internal sealed class TestAuthorizationService : IAuthorizationService
    {
        public bool Allowed { get; set; } = true;

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(Allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            string policyName) =>
            Task.FromResult(Allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }
}
