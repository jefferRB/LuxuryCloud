using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Reports;

namespace LuxuryApp.Models.Platform
{
    /// <summary>Estado del resumen mensual de un tenant, visto desde la consola de Plataforma.</summary>
    public sealed record PlatformMonthlyReportRow
    {
        public required Guid TenantId { get; init; }
        public required string BusinessName { get; init; }
        public bool TenantActivo { get; init; }
        public bool HasSettings { get; init; }
        public bool IsEnabled { get; init; }
        public int SendDayOfMonth { get; init; }
        public int SendHour { get; init; }
        public int RecipientCount { get; init; }
        public int ExcludedCount { get; init; }

        // Último envío (cualquier tipo) según la bitácora.
        public DateTime? LastSendAt { get; init; }
        public string? LastStatus { get; init; }
        public bool? LastWasTest { get; init; }
        public string? LastError { get; init; }

        // Telemetría del scheduler.
        public DateTime? LastAutomaticRunAt { get; init; }
        public DateTime? LastAutomaticSentAt { get; init; }
        public string? LastAutomaticError { get; init; }
    }

    public sealed record PlatformMonthlyReportOverview
    {
        public required IReadOnlyList<PlatformMonthlyReportRow> Rows { get; init; }
        public bool SchedulerEnabled { get; init; }

        // KPIs del encabezado.
        public int TenantsConReporteActivo { get; init; }
        public int EnviadosEsteMes { get; init; }
        public int FallidosEsteMes { get; init; }

        /// <summary>Periodo (mes anterior) sugerido para envíos manuales desde el panel.</summary>
        public int DefaultYear { get; init; }
        public int DefaultMonth { get; init; }
    }

    /// <summary>Detalle editable de un tenant desde Plataforma.</summary>
    public sealed record PlatformMonthlyReportDetailViewModel
    {
        public required Guid TenantId { get; init; }
        public required string BusinessName { get; init; }
        public bool TenantActivo { get; init; }
        public bool HasSettings { get; init; }
        public bool SchedulerEnabled { get; init; }

        public required PlatformMonthlyReportSettingsForm Settings { get; init; }
        public required MonthlyReportRecipientResolution Recipients { get; init; }
        public required IReadOnlyList<TenantMonthlyReportEmailLog> Logs { get; init; }

        public DateTime? LastAutomaticRunAt { get; init; }
        public DateTime? LastAutomaticSentAt { get; init; }
        public string? LastAutomaticError { get; init; }

        public int DefaultYear { get; init; }
        public int DefaultMonth { get; init; }
    }

    /// <summary>Formulario de configuración del resumen mensual editado por el super admin.</summary>
    public sealed class PlatformMonthlyReportSettingsForm
    {
        [Display(Name = "Activar envío mensual")]
        public bool IsEnabled { get; set; }

        [Display(Name = "Enviar a los administradores del negocio")]
        public bool SendToAllAdmins { get; set; } = true;

        [Display(Name = "Requerir correo confirmado")]
        public bool RequireConfirmedEmail { get; set; }

        [Display(Name = "Usar correos adicionales")]
        public bool IncludeManualRecipients { get; set; } = true;

        [Display(Name = "Correos adicionales")]
        [MaxLength(1000)]
        public string? AdditionalRecipients { get; set; }

        [Display(Name = "Información financiera")]
        public bool IncludeFinancialData { get; set; } = true;

        [Display(Name = "Información operativa")]
        public bool IncludeOperationalData { get; set; } = true;

        [Display(Name = "Comparación contra el mes anterior")]
        public bool IncludeMonthOverMonth { get; set; } = true;

        [Display(Name = "Recomendaciones")]
        public bool IncludeRecommendations { get; set; } = true;

        [Display(Name = "Día de envío")]
        [Range(1, 28, ErrorMessage = "El día de envío debe estar entre 1 y 28.")]
        public int SendDayOfMonth { get; set; } = 1;

        [Display(Name = "Hora de envío")]
        [Range(0, 23, ErrorMessage = "La hora de envío debe estar entre 0 y 23.")]
        public int SendHour { get; set; } = 8;
    }
}
