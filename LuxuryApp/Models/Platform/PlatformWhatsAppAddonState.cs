namespace LuxuryApp.Models.Platform
{
    public sealed class PlatformWhatsAppAddonState
    {
        // From TenantWhatsAppSettings
        public bool SettingsEnabled { get; init; }
        public bool SendConfirmationOnCreate { get; init; }
        public bool SendReminderThreeHoursBefore { get; init; }
        public int DailyMessageLimit { get; init; }
        public int TodayUsage { get; init; }
        public int MonthlyUsage30d { get; init; }
        public string TimeZoneId { get; init; } = "America/Costa_Rica";
        public string? Notes { get; init; }

        // From TenantSubscriptionAddons (clasificado por WhatsAppAddonEntitlementRules)
        public bool AddonActive { get; init; }
        public string? AddonCode { get; init; }
        public bool AddonIsManual { get; init; }
        public DateTime? AddonFechaFin { get; init; }
        public int? AddonMonthlyLimit { get; init; }

        /// <summary>Origen del add-on: ProviderRecurring / ManualGrant / Legacy. Fuente autoritativa.</summary>
        public LuxuryApp.Models.SaaS.WhatsAppAddonBillingSource AddonSource { get; init; }

        /// <summary>El acceso manual es indefinido (sin vencimiento).</summary>
        public bool AddonManualIndefinite { get; init; }

        /// <summary>Vigencia del acceso manual (null = indefinido). Para display "vigente hasta".</summary>
        public DateTime? AddonManualExpiresAtUtc { get; init; }

        /// <summary>Acceso manual VENCIDO pero fila aún activa: alerta operativa (no dinero, no envíos).</summary>
        public bool AddonManualExpired { get; init; }

        /// <summary>RIESGO DE DINERO: add-on recurrente pagado activo pero sin ProviderSubscriptionId.</summary>
        public bool AddonProviderRisk { get; init; }

        /// <summary>Existe una fila de add-on (aunque no sea entitlement efectivo), para el modal.</summary>
        public bool AddonExists { get; init; }

        // From WhatsAppMessageLogs
        public string? LastErrorCode { get; init; }
        public string? LastErrorMessage { get; init; }
        public DateTime? LastErrorAtUtc { get; init; }
        public DateTime? LastMessageSentUtc { get; init; }
    }
}
