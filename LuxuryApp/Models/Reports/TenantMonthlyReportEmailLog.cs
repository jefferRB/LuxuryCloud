using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Reports
{
    /// <summary>Estados posibles de un intento de envío del resumen mensual.</summary>
    public static class MonthlyReportEmailStatus
    {
        public const string Pending = "Pending";
        public const string Sent = "Sent";
        public const string Failed = "Failed";
        public const string Skipped = "Skipped";
    }

    /// <summary>
    /// Bitácora de envíos del Resumen Ejecutivo Mensual. Se registra CADA intento
    /// (prueba o real, exitoso o fallido). La idempotencia del envío real se apoya en
    /// esta tabla: un mismo tenant/año/mes/correo con Status = Sent e IsTest = false
    /// no vuelve a enviarse (además existe índice único filtrado en BD como garantía dura).
    /// </summary>
    public sealed class TenantMonthlyReportEmailLog : ITenantEntity
    {
        public int Id { get; set; }

        [BindNever]
        public Guid TenantId { get; set; }

        public int ReportYear { get; set; }

        /// <summary>Mes del reporte (1-12).</summary>
        public int ReportMonth { get; set; }

        [Required]
        [MaxLength(256)]
        public string RecipientEmail { get; set; } = string.Empty;

        [Required]
        [MaxLength(200)]
        public string Subject { get; set; } = string.Empty;

        /// <summary>Ver <see cref="MonthlyReportEmailStatus"/>.</summary>
        [Required]
        [MaxLength(20)]
        public string Status { get; set; } = MonthlyReportEmailStatus.Pending;

        /// <summary>true = envío de prueba (puede repetirse); false = envío real (idempotente).</summary>
        public bool IsTest { get; set; }

        /// <summary>Usuario que disparó el envío (auditoría). Null si lo dispara un proceso (Fase 2).</summary>
        [MaxLength(450)]
        public string? TriggeredByUserId { get; set; }

        /// <summary>Id del mensaje devuelto por Resend, para trazabilidad con el proveedor.</summary>
        [MaxLength(100)]
        public string? ProviderMessageId { get; set; }

        [MaxLength(500)]
        public string? ErrorMessage { get; set; }

        /// <summary>SHA-256 (hex) de los datos principales del reporte enviado, para auditoría.</summary>
        [MaxLength(64)]
        public string? ContentHash { get; set; }

        /// <summary>Fecha/hora local del negocio (America/Costa_Rica) del registro del intento.</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>Fecha/hora local del negocio en que el proveedor aceptó el correo.</summary>
        public DateTime? SentAt { get; set; }
    }
}
