using System.ComponentModel.DataAnnotations;

namespace LuxuryApp.Services.Comprobantes
{
    /// <summary>Validación de correo compartida por los flujos de cobro.</summary>
    public static class ComprobanteEmailHelper
    {
        private static readonly EmailAddressAttribute Validator = new();

        public static bool EsValido(string? email) =>
            !string.IsNullOrWhiteSpace(email) && Validator.IsValid(email.Trim());
    }
}
