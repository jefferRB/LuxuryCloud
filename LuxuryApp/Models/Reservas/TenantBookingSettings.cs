using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using LuxuryApp.Models.SaaS;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Reservas
{
    /// <summary>
    /// Configuración de reservas online por tenant. Relación 1:1 con <see cref="Tenant"/>,
    /// siguiendo el mismo patrón que TenantWhatsAppSettings. Aquí vive el slug público,
    /// el interruptor de activación, las reglas de anticipación y la jornada del negocio
    /// usada para calcular los horarios disponibles.
    /// </summary>
    public sealed class TenantBookingSettings : ITenantEntity
    {
        public const int DefaultMinAdvanceMinutes = 120;
        public const int DefaultMaxDaysAhead = 30;
        public const int DefaultSlotIntervalMinutes = 30;
        // Lunes a sábado por defecto (se excluye domingo). Bit por DayOfWeek (Sun=0..Sat=6).
        public const int DefaultWorkingDaysMask = 0b0111_1110;

        public static readonly TimeOnly DefaultOpenTime = new(8, 0);
        public static readonly TimeOnly DefaultCloseTime = new(18, 0);

        public Guid Id { get; set; } = Guid.NewGuid();

        [BindNever]
        public Guid TenantId { get; set; }

        public Tenant? Tenant { get; set; }

        public bool PublicBookingEnabled { get; set; }

        /// <summary>
        /// Slug público único (minúsculas, números y guiones). Forma parte de la URL
        /// /reservar/{slug}. Nullable porque un tenant puede no haberlo definido aún.
        /// </summary>
        [MaxLength(80)]
        public string? PublicBookingSlug { get; set; }

        [MaxLength(40)]
        public string PublicBookingMode { get; set; } = PublicBookingModes.ManualApproval;

        public bool PublicBookingAllowEmployeeSelection { get; set; }

        public bool PublicBookingAllowAnyEmployee { get; set; } = true;

        /// <summary>Interruptor maestro: si se muestran fotos de funcionarios en el link público. Default true.</summary>
        public bool PublicBookingShowEmployeePhotos { get; set; } = true;

        public int PublicBookingMinAdvanceMinutes { get; set; } = DefaultMinAdvanceMinutes;

        public int PublicBookingMaxDaysAhead { get; set; } = DefaultMaxDaysAhead;

        [MaxLength(500)]
        public string? PublicBookingWelcomeMessage { get; set; }

        [MaxLength(500)]
        public string? PublicBookingConfirmationMessage { get; set; }

        // ── Jornada del negocio (base para el cálculo de disponibilidad) ──
        public TimeOnly OpenTime { get; set; } = DefaultOpenTime;

        public TimeOnly CloseTime { get; set; } = DefaultCloseTime;

        public int SlotIntervalMinutes { get; set; } = DefaultSlotIntervalMinutes;

        /// <summary>
        /// Días laborales como máscara de bits. Bit index = (int)DayOfWeek (domingo=0).
        /// </summary>
        public int WorkingDaysMask { get; set; } = DefaultWorkingDaysMask;

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        [MaxLength(450)]
        public string? UpdatedByUserId { get; set; }

        public bool IsWorkingDay(DayOfWeek day) => (WorkingDaysMask & (1 << (int)day)) != 0;
    }
}
