namespace LuxuryApp.Services.Billing
{
    /// <summary>
    /// Escalera de backoff para el reintento de cancelación del suscriptor viejo tras un cambio
    /// de plan. Regula el ritmo de los intentos REALES contra TiloPay.
    ///
    /// Diseño: agresiva al principio y lenta después. Mientras el viejo siga vivo hay riesgo de
    /// DOBLE COBRO en la próxima renovación, así que los primeros minutos importan; pero si el
    /// proveedor no da de baja tras varias horas, el problema es humano (soporte) y machacar el
    /// API no lo resuelve. NUNCA devuelve "nunca más": el reintento diario sigue indefinidamente
    /// hasta que la baja se verifique, porque un intent abandonado silenciosamente es exactamente
    /// el fallo que causó el doble suscriptor en producción.
    ///
    /// El worker corre cada N minutos (default 20), así que estos tiempos son un PISO, no un
    /// horario exacto: un backoff de 5 min se materializa en el siguiente pase del worker.
    /// </summary>
    public static class PlanChangeCancellationBackoff
    {
        /// <summary>
        /// Espera tras haber hecho <paramref name="attemptCount"/> intentos reales.
        /// 0 intentos ⇒ inmediato; luego 5m, 15m, 30m, 1h, 6h (×4) y finalmente diario.
        /// </summary>
        public static TimeSpan DelayAfterAttempt(int attemptCount) => attemptCount switch
        {
            <= 0 => TimeSpan.Zero,
            1 => TimeSpan.FromMinutes(5),
            2 => TimeSpan.FromMinutes(15),
            3 => TimeSpan.FromMinutes(30),
            4 => TimeSpan.FromHours(1),
            <= 8 => TimeSpan.FromHours(6),
            _ => TimeSpan.FromHours(24)
        };

        /// <summary>Momento del próximo intento permitido tras registrar el intento número <paramref name="attemptCount"/>.</summary>
        public static DateTime NextRetryUtc(DateTime nowUtc, int attemptCount) =>
            nowUtc.Add(DelayAfterAttempt(attemptCount));
    }
}
