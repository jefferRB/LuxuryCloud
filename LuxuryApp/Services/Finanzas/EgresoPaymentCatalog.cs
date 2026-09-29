using Microsoft.AspNetCore.Mvc.Rendering;

namespace LuxuryApp.Services.Finanzas
{
    internal static class EgresoPaymentCatalog
    {
        private static readonly string[] AllowedValues =
        {
            "EFECTIVO",
            "TARJETA",
            "SINPE"
        };

        public static bool IsAllowed(string? metodoPago) =>
            !string.IsNullOrWhiteSpace(metodoPago)
            && AllowedValues.Contains(metodoPago.Trim(), StringComparer.OrdinalIgnoreCase);

        public static List<SelectListItem> BuildSelectList() =>
            new()
            {
                new SelectListItem { Value = "EFECTIVO", Text = "Efectivo" },
                new SelectListItem { Value = "TARJETA", Text = "Tarjeta" },
                new SelectListItem { Value = "SINPE", Text = "Sinpe" }
            };
    }
}
