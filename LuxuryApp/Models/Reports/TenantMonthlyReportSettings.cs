using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Reports
{
    /// <summary>
    /// Configuración por tenant del Resumen Ejecutivo Mensual (LuxuryCloud Insights).
    /// <para>
    /// Fase 2: el envío automático lo dispara <c>MonthlyReportSchedulerService</c> cuando
    /// <see cref="IsEnabled"/> es true y llega el día/hora configurados, pero solo si el
    /// feature flag global <c>MonthlyReports:SchedulerEnabled</c> está activo. El envío real
    /// sigue siendo idempotente (ver <see cref="TenantMonthlyReportEmailLog"/>).
    /// </para>
    /// </summary>
    public sealed class TenantMonthlyReportSettings : ITenantEntity
    {
        public int Id { get; set; }

        [BindNever]
        public Guid TenantId { get; set; }

        /// <summary>Envío mensual habilitado. Default false: nada se envía sin acción explícita.</summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Incluir a los administradores/dueños del negocio. La resolución es dinámica al
        /// momento del envío: los administradores nuevos se incluyen automáticamente.
        /// </summary>
        public bool SendToOwnerEmail { get; set; } = true;

        /// <summary>
        /// Enviar a todos los administradores activos del negocio (resolución dinámica).
        /// Alias operativo de <see cref="SendToOwnerEmail"/>; se mantienen sincronizados.
        /// </summary>
        public bool SendToAllAdmins { get; set; } = true;

        /// <summary>
        /// Solo incluir destinatarios con correo confirmado (Identity <c>EmailConfirmed</c>).
        /// Aplica a administradores; los correos manuales se consideran verificados por el dueño.
        /// </summary>
        public bool RequireConfirmedEmail { get; set; }

        /// <summary>Usar la lista de <see cref="AdditionalRecipients"/> (correos manuales).</summary>
        public bool IncludeManualRecipients { get; set; } = true;

        /// <summary>
        /// Correos adicionales separados por coma. Se validan antes de cada envío y nunca
        /// pueden pertenecer a cuentas de funcionarios del tenant.
        /// </summary>
        [MaxLength(1000)]
        public string? AdditionalRecipients { get; set; }

        public bool IncludeFinancialData { get; set; } = true;

        public bool IncludeOperationalData { get; set; } = true;

        /// <summary>Mensajes interpretativos (margen, oportunidad, colaborador estrella).</summary>
        public bool IncludeRecommendations { get; set; } = true;

        /// <summary>Incluir comparación contra el mes anterior en el reporte.</summary>
        public bool IncludeMonthOverMonth { get; set; } = true;

        /// <summary>Día del mes para el envío automático. 1-28 para evitar meses cortos.</summary>
        [Range(1, 28)]
        public int SendDayOfMonth { get; set; } = 1;

        /// <summary>Hora local (America/Costa_Rica) para el envío automático.</summary>
        [Range(0, 23)]
        public int SendHour { get; set; } = 8;

        // ─────────────── Telemetría del scheduler (Fase 2) ───────────────

        /// <summary>Última vez que el scheduler evaluó este tenant (haya enviado o no).</summary>
        public DateTime? LastAutomaticRunAt { get; set; }

        /// <summary>Último envío automático exitoso (al menos un correo enviado).</summary>
        public DateTime? LastAutomaticSentAt { get; set; }

        /// <summary>Año/mes (aaaamm) del último periodo procesado automáticamente. Anti-reproceso.</summary>
        public int? LastAutomaticPeriod { get; set; }

        [MaxLength(500)]
        public string? LastAutomaticError { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }
    }
}
