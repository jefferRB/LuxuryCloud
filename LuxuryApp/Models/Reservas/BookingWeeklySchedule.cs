using LuxuryApp.Models.Horarios;

namespace LuxuryApp.Models.Reservas
{
    /// <summary>Ventana de atención de un día concreto. Horas locales del negocio.</summary>
    public readonly record struct BookingDayWindow(bool Abierto, TimeOnly Apertura, TimeOnly Cierre)
    {
        public static readonly BookingDayWindow Cerrado = new(false, default, default);

        public int DuracionMinutos =>
            Abierto ? (int)(Cierre.ToTimeSpan() - Apertura.ToTimeSpan()).TotalMinutes : 0;
    }

    /// <summary>
    /// Jornada semanal del negocio para reservas online: FUENTE ÚNICA de "¿a qué hora abre este
    /// día?". La consume el motor de disponibilidad (horarios del día, próximos espacios y
    /// revalidación de un slot puntual) y la pantalla de configuración; no hay una segunda copia
    /// de esta regla en controladores, vistas ni JavaScript.
    ///
    /// <para>
    /// Es una función pura sobre datos ya cargados: no toca la base ni el reloj, así se puede
    /// probar directamente.
    /// </para>
    ///
    /// <para>
    /// <b>Compatibilidad hacia atrás</b>: con filas de <see cref="TenantBookingBusinessHour"/> esas
    /// mandan. Sin filas (tenant anterior a esta función, o creado antes de guardar la pantalla) se
    /// reconstruye la jornada desde <c>WorkingDaysMask</c> + <c>OpenTime</c>/<c>CloseTime</c>, que
    /// es exactamente el comportamiento previo.
    /// </para>
    /// </summary>
    public sealed class BookingWeeklySchedule
    {
        private readonly BookingDayWindow[] _dias;

        private BookingWeeklySchedule(BookingDayWindow[] dias)
        {
            _dias = dias;
        }

        /// <summary>True si el negocio abre al menos un día de la semana.</summary>
        public bool TieneAlgunDiaAbierto => _dias.Any(dia => dia.Abierto);

        public static BookingWeeklySchedule From(
            TenantBookingSettings? settings,
            IEnumerable<TenantBookingBusinessHour>? horarios)
        {
            var dias = new BookingDayWindow[7];

            var filas = horarios?
                .Where(hora => (int)hora.DiaSemana is >= 0 and <= 6)
                .ToList();

            if (filas is { Count: > 0 })
            {
                foreach (var fila in filas)
                {
                    // Una jornada inválida (cierre <= apertura) se trata como día cerrado en vez de
                    // producir slots imposibles. La validación de guardado ya la impide; esto cubre
                    // datos heredados o tocados fuera de la aplicación.
                    var valida = fila.IsEnabled && fila.CloseTime > fila.OpenTime;
                    dias[(int)fila.DiaSemana] = valida
                        ? new BookingDayWindow(true, fila.OpenTime, fila.CloseTime)
                        : BookingDayWindow.Cerrado;
                }

                return new BookingWeeklySchedule(dias);
            }

            // ── Compatibilidad: jornada plana de las columnas antiguas ──
            if (settings is null || settings.CloseTime <= settings.OpenTime)
            {
                return new BookingWeeklySchedule(dias);
            }

            for (var indice = 0; indice < 7; indice++)
            {
                dias[indice] = settings.IsWorkingDay((DayOfWeek)indice)
                    ? new BookingDayWindow(true, settings.OpenTime, settings.CloseTime)
                    : BookingDayWindow.Cerrado;
            }

            return new BookingWeeklySchedule(dias);
        }

        public BookingDayWindow GetDay(DayOfWeek dia) => _dias[(int)dia];

        public bool IsOpen(DayOfWeek dia) => _dias[(int)dia].Abierto;

        /// <summary>True si el bloque [inicio, fin) cabe COMPLETO dentro de la jornada de ese día.</summary>
        public bool Fits(DayOfWeek dia, TimeOnly inicio, TimeOnly fin)
        {
            var ventana = _dias[(int)dia];
            return ventana.Abierto && TimeIntervalMath.Contains(ventana.Apertura, ventana.Cierre, inicio, fin);
        }

        /// <summary>True si algún día del rango [desde, hasta] está abierto.</summary>
        public bool HasOpenDay(DateOnly desde, DateOnly hasta)
        {
            for (var fecha = desde; fecha <= hasta; fecha = fecha.AddDays(1))
            {
                if (IsOpen(fecha.DayOfWeek))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
