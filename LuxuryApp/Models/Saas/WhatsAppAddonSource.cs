namespace LuxuryApp.Models.SaaS
{
    /// <summary>
    /// Origen/fuente comercial de un add-on de WhatsApp. Es la señal AUTORITATIVA para decidir
    /// riesgo, vigencia y presentación — nunca se infiere de strings mágicos (MANUAL-...) ni de
    /// "TilopayRecurringPlanId is null".
    /// </summary>
    public enum WhatsAppAddonBillingSource
    {
        /// <summary>Add-on pagado por TiloPay Repeat (suscriptor recurrente). Ciclo por FechaFin/provider.</summary>
        ProviderRecurring = 0,

        /// <summary>Acceso manual otorgado por plataforma (cortesía/canje/interno/prueba). No cobra por TiloPay.</summary>
        ManualGrant = 1,

        /// <summary>Add-on histórico/test sin fuente clara. Nunca es entitlement efectivo; solo informativo.</summary>
        Legacy = 2
    }

    /// <summary>Tipo de acceso manual, para clasificar el acuerdo comercial.</summary>
    public enum ManualWhatsAppGrantType
    {
        Courtesy = 0,
        Barter = 1,
        Internal = 2,
        Trial = 3,
        Other = 4
    }

    /// <summary>Desenlace de otorgar/revocar un acceso manual desde plataforma.</summary>
    public enum ManualWhatsAppGrantOutcome
    {
        Granted,
        Revoked,
        /// <summary>Bloqueado: el tenant tiene un add-on TiloPay activo (requiere override explícito o billing).</summary>
        BlockedProviderRecurringActive,
        Invalid,
        NoChange
    }

    public sealed record ManualWhatsAppGrantResult(ManualWhatsAppGrantOutcome Outcome, string Message)
    {
        public bool Success => Outcome is ManualWhatsAppGrantOutcome.Granted or ManualWhatsAppGrantOutcome.Revoked;
    }

    /// <summary>
    /// Resultado de clasificar un add-on: si da acceso comercial efectivo AHORA, si es riesgo de
    /// dinero, si es manual (vigente o vencido) o legacy, su vigencia y la cuota mensual efectiva.
    /// </summary>
    public sealed record WhatsAppAddonEntitlement
    {
        public required WhatsAppAddonBillingSource Source { get; init; }

        /// <summary>Hay acceso comercial WhatsApp EFECTIVO ahora (habilita envíos si además hay settings).</summary>
        public required bool IsEffective { get; init; }

        /// <summary>
        /// RIESGO DE DINERO: add-on ProviderRecurring activo, recurrente (TilopayRecurringPlanId
        /// presente) pero SIN ProviderSubscriptionId. Los manuales/legacy NUNCA son riesgo de dinero.
        /// </summary>
        public required bool IsProviderRisk { get; init; }

        public required bool IsManualGrant { get; init; }

        /// <summary>Manual grant activo pero VENCIDO: alerta operativa (no dinero), no permite envíos.</summary>
        public required bool IsManualGrantExpired { get; init; }

        public required bool IsLegacy { get; init; }

        public required bool IsIndefinite { get; init; }

        /// <summary>Vigencia efectiva (null = indefinido o sin fecha). Para manual = ManualGrantExpiresAtUtc.</summary>
        public DateTime? ExpiresAtUtc { get; init; }

        public required int MonthlyMessageLimit { get; init; }
    }

    /// <summary>
    /// Reglas de clasificación de add-ons WhatsApp. ÚNICA fuente de verdad: la consumen el send-gate,
    /// el summary del cliente, BillingHealth y la consola de plataforma. Estática y sin dependencias
    /// (recibe el reloj) para que no diverjan copias (antes había una réplica en PlatformWhatsAppStatusService).
    /// </summary>
    public static class WhatsAppAddonEntitlementRules
    {
        public static WhatsAppAddonEntitlement Classify(TenantSubscriptionAddon addon, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(addon);
            return Classify(
                addon.BillingSource,
                addon.Estado,
                addon.FechaFin,
                addon.FechaFinGraciaUtc,
                addon.ProviderSubscriptionId,
                addon.TilopayRecurringPlanId,
                addon.IsManualGrantIndefinite,
                addon.ManualGrantExpiresAtUtc,
                addon.RevokedAtUtc,
                addon.MonthlyMessageLimit,
                nowUtc);
        }

        public static WhatsAppAddonEntitlement Classify(
            WhatsAppAddonBillingSource source,
            EstadoSuscripcion estado,
            DateTime? fechaFin,
            DateTime? fechaFinGraciaUtc,
            string? providerSubscriptionId,
            int? tilopayRecurringPlanId,
            bool isManualGrantIndefinite,
            DateTime? manualGrantExpiresAtUtc,
            DateTime? revokedAtUtc,
            int monthlyMessageLimit,
            DateTime nowUtc)
        {
            switch (source)
            {
                case WhatsAppAddonBillingSource.Legacy:
                    return new WhatsAppAddonEntitlement
                    {
                        Source = WhatsAppAddonBillingSource.Legacy,
                        IsEffective = false,
                        IsProviderRisk = false,
                        IsManualGrant = false,
                        IsManualGrantExpired = false,
                        IsLegacy = true,
                        IsIndefinite = false,
                        ExpiresAtUtc = fechaFin,
                        MonthlyMessageLimit = monthlyMessageLimit
                    };

                case WhatsAppAddonBillingSource.ManualGrant:
                {
                    var revoked = revokedAtUtc is not null;
                    var estadoActive = estado == EstadoSuscripcion.Activa;
                    var expiresAt = isManualGrantIndefinite ? (DateTime?)null : manualGrantExpiresAtUtc;
                    var expired = !isManualGrantIndefinite && expiresAt is { } e && e < nowUtc;
                    var effective = estadoActive && !revoked && !expired;
                    var expiredStillActive = estadoActive && !revoked && expired;
                    return new WhatsAppAddonEntitlement
                    {
                        Source = WhatsAppAddonBillingSource.ManualGrant,
                        IsEffective = effective,
                        IsProviderRisk = false,
                        IsManualGrant = true,
                        IsManualGrantExpired = expiredStillActive,
                        IsLegacy = false,
                        IsIndefinite = isManualGrantIndefinite,
                        ExpiresAtUtc = expiresAt,
                        MonthlyMessageLimit = monthlyMessageLimit
                    };
                }

                default: // ProviderRecurring
                {
                    var active = IsProviderAddonActive(estado, fechaFin, fechaFinGraciaUtc, nowUtc);
                    // Riesgo SOLO si es un recurrente real (tiene plan recurrente) que perdió el
                    // suscriptor. Exigir TilopayRecurringPlanId != null evita falsos positivos con
                    // filas manuales/legacy que aún tengan BillingSource por defecto ProviderRecurring.
                    var providerRisk = active &&
                                       string.IsNullOrWhiteSpace(providerSubscriptionId) &&
                                       tilopayRecurringPlanId is not null;
                    return new WhatsAppAddonEntitlement
                    {
                        Source = WhatsAppAddonBillingSource.ProviderRecurring,
                        IsEffective = active,
                        IsProviderRisk = providerRisk,
                        IsManualGrant = false,
                        IsManualGrantExpired = false,
                        IsLegacy = false,
                        IsIndefinite = false,
                        ExpiresAtUtc = fechaFin,
                        MonthlyMessageLimit = monthlyMessageLimit
                    };
                }
            }
        }

        /// <summary>
        /// Actividad de un add-on ProviderRecurring: Activo (o en gracia). Espeja exactamente
        /// SuscripcionService.GetEffectiveStatusInternal para el add-on (sin provider-expiry ni
        /// cancel-at-period-end, que el add-on no usa en esta ruta).
        /// </summary>
        private static bool IsProviderAddonActive(
            EstadoSuscripcion estado,
            DateTime? fechaFin,
            DateTime? fechaFinGraciaUtc,
            DateTime nowUtc)
        {
            if (estado == EstadoSuscripcion.Activa)
            {
                if (!fechaFin.HasValue || fechaFin.Value >= nowUtc)
                {
                    return true;
                }

                return fechaFinGraciaUtc.HasValue && fechaFinGraciaUtc.Value >= nowUtc;
            }

            if (estado == EstadoSuscripcion.Morosa)
            {
                return fechaFinGraciaUtc.HasValue && fechaFinGraciaUtc.Value >= nowUtc;
            }

            return false;
        }
    }
}
