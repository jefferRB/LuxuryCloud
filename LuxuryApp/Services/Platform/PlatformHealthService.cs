using LuxuryApp.Models.Platform;

namespace LuxuryApp.Services.Platform
{
    public sealed class PlatformHealthService : IPlatformHealthService
    {
        public PlatformTenantHealthViewModel ComputeHealth(
            bool canAccessApp,
            PlatformTenantUsageViewModel usage,
            bool whatsAppEnabled,
            bool hasWhatsAppRecentError,
            bool hasPendingCheckout,
            bool isExpiringSoon)
        {
            var reasons = new List<string>();

            if (!canAccessApp)
            {
                reasons.Add("Sin acceso comercial activo");
                return new PlatformTenantHealthViewModel { State = TenantHealthState.SinAcceso, Reasons = reasons };
            }

            var noActivity30d = !usage.HasRecentActivity30d && usage.Citas30d == 0 && usage.Cobros30d == 0;
            var noActivity14d = !usage.HasRecentActivity14d;
            var noActivity7d  = !usage.HasRecentActivity7d;

            // Acumular motivos
            if (noActivity30d)
                reasons.Add("Sin actividad en los últimos 30 días");
            else if (noActivity14d)
                reasons.Add("Sin actividad en los últimos 14 días");
            else if (noActivity7d)
                reasons.Add("Sin actividad en los últimos 7 días");

            if (hasPendingCheckout)
                reasons.Add("Tiene checkouts de pago pendientes");

            if (whatsAppEnabled && hasWhatsAppRecentError)
                reasons.Add("WhatsApp registra errores recientes");

            if (isExpiringSoon)
                reasons.Add("Suscripción próxima a vencer (menos de 7 días)");

            if (usage.BookingRequestsPending > 3)
                reasons.Add($"Reservas en línea pendientes sin atender: {usage.BookingRequestsPending}");

            // Determinar estado
            if (noActivity30d || noActivity14d)
                return new PlatformTenantHealthViewModel { State = TenantHealthState.Riesgo, Reasons = reasons };

            if (noActivity7d || hasPendingCheckout || (whatsAppEnabled && hasWhatsAppRecentError)
                || isExpiringSoon || usage.BookingRequestsPending > 3)
                return new PlatformTenantHealthViewModel { State = TenantHealthState.Atencion, Reasons = reasons };

            reasons.Add("Actividad reciente y acceso vigente");
            return new PlatformTenantHealthViewModel { State = TenantHealthState.Saludable, Reasons = reasons };
        }
    }
}
