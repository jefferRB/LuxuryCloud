namespace LuxuryApp.Services.Tilopay
{
    /// <summary>Un suscriptor recurrente devuelto por getSuscriptorRepeat, parseado de forma tolerante.</summary>
    public sealed record TilopaySubscriber
    {
        /// <summary>ID único del suscriptor en TiloPay (campo "id" o "id_suscriptor" del payload).</summary>
        public required string SubscriberId { get; init; }

        public string? Email { get; init; }

        /// <summary>Estado crudo reportado por TiloPay (1=Activo, 3=Pausado, 4=Eliminado, si viene).</summary>
        public string? Status { get; init; }

        /// <summary>Fecha de creación si el payload la incluye (para desempatar por recencia).</summary>
        public DateTime? CreatedAtUtc { get; init; }

        /// <summary>
        /// Vencimiento REAL en el proveedor, ya convertido a UTC (fin del día Costa Rica). TiloPay
        /// lo manda como fecha sin hora ("2026-09-15"); es la fuente de verdad de la próxima fecha
        /// de cobro cuando el suscriptor está Active. Null si el payload no lo trae o no se pudo parsear.
        /// </summary>
        public DateTime? ExpiresAtUtc { get; init; }

        /// <summary>El expire crudo tal cual lo devolvió TiloPay ("2026-09-15"), para auditoría.</summary>
        public string? ExpiresRaw { get; init; }

        /// <summary>Plan id de TiloPay si viene en la fila.</summary>
        public int? TilopayPlanId { get; init; }
    }

    public enum SubscriberResolutionStatus
    {
        /// <summary>Exactamente un suscriptor válido para el email/plan.</summary>
        Found,

        /// <summary>Ningún suscriptor para el email/plan (aún no propagado o inexistente).</summary>
        NotFound,

        /// <summary>Más de un suscriptor válido: NO se elige a ciegas, requiere revisión.</summary>
        Ambiguous,

        /// <summary>La consulta al proveedor falló (red/timeout/credenciales/shape inesperado).</summary>
        Error
    }

    /// <summary>Resultado de resolver el id_suscriptor por (plan, email). Nunca elige a ciegas.</summary>
    public sealed record SubscriberResolutionResult
    {
        public required SubscriberResolutionStatus Status { get; init; }
        public TilopaySubscriber? Subscriber { get; init; }

        /// <summary>Cantidad de candidatos que coincidieron por email (para diagnóstico).</summary>
        public int MatchCount { get; init; }

        /// <summary>Detalle seguro para logs/auditoría (sin secretos ni email en claro).</summary>
        public string? Detail { get; init; }

        public bool IsFound => Status == SubscriberResolutionStatus.Found && Subscriber is not null;

        public static SubscriberResolutionResult Found(TilopaySubscriber subscriber, int matchCount) =>
            new() { Status = SubscriberResolutionStatus.Found, Subscriber = subscriber, MatchCount = matchCount };

        public static SubscriberResolutionResult NotFound(string? detail = null) =>
            new() { Status = SubscriberResolutionStatus.NotFound, MatchCount = 0, Detail = detail };

        public static SubscriberResolutionResult Ambiguous(int matchCount, string? detail = null) =>
            new() { Status = SubscriberResolutionStatus.Ambiguous, MatchCount = matchCount, Detail = detail };

        public static SubscriberResolutionResult Failed(string? detail = null) =>
            new() { Status = SubscriberResolutionStatus.Error, MatchCount = 0, Detail = detail };
    }

    /// <summary>Resultado de una operación admin sobre un suscriptor (pause/reactivate/delete/edit/url).</summary>
    public sealed record TilopayAdminOperationResult
    {
        public required bool Succeeded { get; init; }

        /// <summary>Para recurrentUrl: la URL devuelta por TiloPay.</summary>
        public string? Url { get; init; }

        /// <summary>Mensaje seguro para logs/UI (sin secretos).</summary>
        public string? Message { get; init; }

        /// <summary>
        /// Para recurrentUrl: qué contrato de body funcionó (p.ej. "id_plan" o "id_plan+aliases"),
        /// para auditar sin exponer la URL. Null cuando no aplica.
        /// </summary>
        public string? Contract { get; init; }

        /// <summary>Para recurrentUrl: diagnóstico sanitizado de la respuesta del proveedor (sin URL completa).</summary>
        public RecurrentUrlDiagnostics? RecurrentDiagnostics { get; init; }

        /// <summary>
        /// True cuando TiloPay respondió éxito HTTP pero la verificación posterior
        /// (getSuscriptorRepeat) mostró al suscriptor todavía Activo o no pudo confirmarse.
        /// Un 200 sin verificación NUNCA cuenta como cancelación real.
        /// </summary>
        public bool VerificationFailed { get; init; }

        public static TilopayAdminOperationResult Ok(string? message = null, string? url = null) =>
            new() { Succeeded = true, Message = message, Url = url };

        public static TilopayAdminOperationResult Fail(string message) =>
            new() { Succeeded = false, Message = message };

        public static TilopayAdminOperationResult FailVerification(string message) =>
            new() { Succeeded = false, Message = message, VerificationFailed = true };
    }

    /// <summary>
    /// Diagnóstico SANITIZADO de una respuesta de recurrentUrl (para logs/auditoría). NUNCA lleva la
    /// URL completa: solo host + path enmascarado (el token va en el path de los links tp.cr, así que
    /// se recorta), presencia de cada campo, y el type/message del proveedor.
    /// </summary>
    public sealed record RecurrentUrlDiagnostics
    {
        /// <summary>Contrato de body usado ("id_plan" / "id_plan+aliases").</summary>
        public required string Contract { get; init; }

        /// <summary>True si el contrato usado fue el fallback (id_plan+aliases): el enlace es sospechoso.</summary>
        public bool UsedFallbackContract => string.Equals(Contract, "id_plan+aliases", StringComparison.Ordinal);

        public int HttpStatus { get; init; }
        public string? ProviderType { get; init; }
        public string? ProviderMessage { get; init; }
        public bool HasUrlRenew { get; init; }
        public bool HasUrlRegister { get; init; }

        /// <summary>Campo del que se tomó la URL ("url_renew" / "url_register" / null si ninguna).</summary>
        public string? SelectedField { get; init; }

        /// <summary>host + path enmascarado (sin el token). Ej: "tp.cr/l/*** (2 segs)".</summary>
        public string? UrlHostPathMasked { get; init; }
    }

    /// <summary>Estados admitidos por editSuscriptorRepeat.</summary>
    public enum TilopaySubscriberStatus
    {
        Active = 1,
        Paused = 3,
        Deleted = 4
    }

    /// <summary>Veredicto sobre el plan DESTINO de un checkout: ¿es seguro crear un suscriptor nuevo?</summary>
    public enum TargetSubscriberVerdict
    {
        /// <summary>Nadie cobrando en el destino (cero o solo inactivos): hosted checkout seguro.</summary>
        Free,

        /// <summary>Exactamente un suscriptor ACTIVO del mismo email.</summary>
        SingleActive,

        /// <summary>Más de uno activo: ya hay riesgo de doble cobro sin que nosotros toquemos nada.</summary>
        MultipleActive,

        /// <summary>Algún status que no sabemos clasificar: nunca se asume libre.</summary>
        UnknownStatus,

        /// <summary>El proveedor no respondió de forma concluyente.</summary>
        ProviderError
    }

    /// <summary>
    /// Clasificación de los suscriptores del plan destino que coinciden por email. Describe, no
    /// decide: quién bloquea o deja pasar es el pre-check del checkout.
    /// </summary>
    public sealed record TargetSubscriberAssessment
    {
        public required TargetSubscriberVerdict Verdict { get; init; }

        public IReadOnlyList<TilopaySubscriber> Active { get; init; } = Array.Empty<TilopaySubscriber>();

        /// <summary>Eliminados/cancelados: la señal de que volver a este plan es legítimo.</summary>
        public IReadOnlyList<TilopaySubscriber> Inactive { get; init; } = Array.Empty<TilopaySubscriber>();

        /// <summary>
        /// Suscriptores que impiden dar el plan por libre sin ser un activo limpio: status
        /// desconocido O pausado. Un pausado puede volver a cobrar al reactivarse, así que para el
        /// checkout cuenta como bloqueante igual que un status que no entendemos.
        /// </summary>
        public IReadOnlyList<TilopaySubscriber> Unknown { get; init; } = Array.Empty<TilopaySubscriber>();

        public string? Detail { get; init; }

        public static TargetSubscriberAssessment Error(string detail) =>
            new() { Verdict = TargetSubscriberVerdict.ProviderError, Detail = detail };

        /// <summary>
        /// Clasifica los suscriptores que ya coinciden por email. Función pura: sin ella, cada
        /// consumidor (servicio real, fakes de test) inventaría su propia tabla y volveríamos al
        /// problema original de tener la regla del dinero escrita en varios lados.
        ///
        /// Precedencia deliberada: un status desconocido (o pausado) gana sobre cualquier conteo de
        /// activos. Si no entendemos una fila, o está pausada y podría reactivarse, no sabemos
        /// cuántos cobran, y adivinar cuesta dinero real.
        /// </summary>
        public static TargetSubscriberAssessment FromMatches(IReadOnlyList<TilopaySubscriber> matches, int tilopayPlanId)
        {
            var active = matches.Where(m => ProviderSubscriberStatusRules.IsProviderSubscriberActive(m.Status)).ToList();
            var inactive = matches.Where(m => ProviderSubscriberStatusRules.IsProviderSubscriberInactive(m.Status)).ToList();
            // Pausado se agrupa con Unknown a propósito: para el checkout ambos son "no confirmado
            // libre". Un pausado NO es una baja (puede volver a cobrar), así que bloquear/revisar es
            // lo seguro, igual que con un status que no sabemos clasificar.
            var unknown = matches
                .Where(m =>
                {
                    var state = ProviderSubscriberStatusRules.Classify(m.Status);
                    return state == ProviderSubscriberState.Unknown || state == ProviderSubscriberState.Paused;
                })
                .ToList();

            var verdict = unknown.Count > 0
                ? TargetSubscriberVerdict.UnknownStatus
                : active.Count switch
                {
                    0 => TargetSubscriberVerdict.Free,
                    1 => TargetSubscriberVerdict.SingleActive,
                    _ => TargetSubscriberVerdict.MultipleActive
                };

            return new TargetSubscriberAssessment
            {
                Verdict = verdict,
                Active = active,
                Inactive = inactive,
                Unknown = unknown,
                Detail = $"plan {tilopayPlanId}: {active.Count} activo(s), {inactive.Count} inactivo(s), {unknown.Count} con status desconocido."
            };
        }
    }
}
