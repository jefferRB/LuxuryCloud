using LuxuryApp.Models.Common;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Reservas
{
    /// <summary>
    /// Jornada de UN día de la semana para reservas online. Sustituye al par plano
    /// <c>OpenTime</c>/<c>CloseTime</c> + <c>WorkingDaysMask</c> de <see cref="TenantBookingSettings"/>,
    /// porque los negocios reales abren distinto cada día (miércoles 10–19, sábado 09–17...).
    ///
    /// <para>
    /// <b>Compatibilidad</b>: si un tenant no tiene ninguna fila, la jornada se deriva de las
    /// columnas antiguas (ver <see cref="BookingWeeklySchedule"/>), así un tenant que nunca abra
    /// esta pantalla se comporta exactamente igual que antes.
    /// </para>
    ///
    /// <para>
    /// <b>Zona horaria</b>: <see cref="OpenTime"/> y <see cref="CloseTime"/> son hora LOCAL del
    /// negocio (reloj de pared, America/Costa_Rica vía <c>IBusinessDateTimeProvider</c>), igual que
    /// <c>Cita.FechaHoraCita</c> y que los bloqueos recurrentes. Nunca se convierten a UTC.
    /// </para>
    /// </summary>
    public sealed class TenantBookingBusinessHour : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        public int Id { get; set; }

        /// <summary>Día de la semana (domingo = 0), único por tenant.</summary>
        public DayOfWeek DiaSemana { get; set; }

        /// <summary>Si el negocio abre ese día. Cuando es false, las horas se conservan pero no se usan.</summary>
        public bool IsEnabled { get; set; }

        public TimeOnly OpenTime { get; set; } = TenantBookingSettings.DefaultOpenTime;

        public TimeOnly CloseTime { get; set; } = TenantBookingSettings.DefaultCloseTime;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
