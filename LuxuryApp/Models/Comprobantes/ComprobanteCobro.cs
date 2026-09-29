using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;

namespace LuxuryApp.Models.Comprobantes
{
    /// <summary>
    /// Comprobante digital INTERNO (no fiscal) generado al registrar un cobro.
    /// Toda la información del negocio y del cliente se guarda como "snapshot" al
    /// momento de la emisión, para que el comprobante sea inmutable aunque luego
    /// cambien los datos de origen. Multi-tenant: <see cref="ITenantEntity"/>.
    ///
    /// IMPORTANTE: NO es un comprobante electrónico validado por Hacienda. Los campos
    /// <c>Hacienda*</c> y <c>EsFiscal</c> quedan preparados para una fase futura y NO se
    /// usan todavía.
    /// </summary>
    public class ComprobanteCobro : ITenantEntity
    {
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        // ─────────────── Vínculos (validados contra el tenant en el servicio) ───────────────

        /// <summary>Cobro que originó el comprobante.</summary>
        public int CobroId { get; set; }
        public Cobro? Cobro { get; set; }

        public int? CitaId { get; set; }

        public int? ClienteId { get; set; }
        public ClientesModel? Cliente { get; set; }

        public int? FuncionarioId { get; set; }
        public Funcionario? Funcionario { get; set; }

        // ─────────────── Identificación ───────────────

        /// <summary>Número interno legible, único por tenant. Ej.: LC-DEMO-20260618-000001.</summary>
        [MaxLength(40)]
        public string NumeroInterno { get; set; } = string.Empty;

        /// <summary>Tipo de comprobante. Hoy siempre <see cref="ComprobanteTipo.ComprobanteInterno"/>.</summary>
        [MaxLength(40)]
        public string TipoComprobante { get; set; } = ComprobanteTipo.ComprobanteInterno;

        public ComprobanteEstadoEnvio EstadoEnvio { get; set; } = ComprobanteEstadoEnvio.Pending;

        /// <summary>
        /// Token aleatorio no adivinable para la ruta pública /comprobantes/{token}.
        /// Único global. Permite ver/descargar sin login y se puede regenerar.
        /// </summary>
        [MaxLength(64)]
        public string TokenPublico { get; set; } = string.Empty;

        // ─────────────── Destino del correo ───────────────

        [MaxLength(256)]
        public string EmailDestino { get; set; } = string.Empty;

        /// <summary>Versión normalizada (trim + lower) para comparaciones/idempotencia.</summary>
        [MaxLength(256)]
        public string EmailDestinoNormalizado { get; set; } = string.Empty;

        // ─────────────── Snapshot del cliente ───────────────

        [MaxLength(150)]
        public string NombreClienteSnapshot { get; set; } = string.Empty;

        [MaxLength(50)]
        public string? TelefonoClienteSnapshot { get; set; }

        // ─────────────── Snapshot del negocio ───────────────

        [MaxLength(150)]
        public string NombreNegocioSnapshot { get; set; } = string.Empty;

        [MaxLength(50)]
        public string? CedulaNegocioSnapshot { get; set; }

        [MaxLength(50)]
        public string? TelefonoNegocioSnapshot { get; set; }

        [MaxLength(256)]
        public string? EmailNegocioSnapshot { get; set; }

        [MaxLength(300)]
        public string? DireccionNegocioSnapshot { get; set; }

        // ─────────────── Importes ───────────────

        public DateTime FechaEmision { get; set; }

        [MaxLength(3)]
        public string Moneda { get; set; } = "CRC";

        public decimal Subtotal { get; set; }
        public decimal Descuento { get; set; }
        public decimal Impuesto { get; set; }
        public decimal Total { get; set; }

        [MaxLength(20)]
        public string MetodoPago { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Observacion { get; set; }

        // ─────────────── Envío (Resend) y auditoría ───────────────

        [MaxLength(80)]
        public string? ResendEmailId { get; set; }

        [MaxLength(500)]
        public string? ErrorEnvio { get; set; }

        public int IntentosEnvio { get; set; }

        public DateTime CreatedAt { get; set; }

        [MaxLength(450)]
        public string? CreatedByUserId { get; set; }

        public DateTime? SentAt { get; set; }

        // ─────────────── Preparación futura para Hacienda (NO usar todavía) ───────────────

        [MaxLength(60)]
        public string? HaciendaClave { get; set; }

        [MaxLength(40)]
        public string? HaciendaConsecutivo { get; set; }

        [MaxLength(400)]
        public string? HaciendaXmlPath { get; set; }

        [MaxLength(40)]
        public string? HaciendaEstado { get; set; }

        public string? HaciendaRespuesta { get; set; }

        /// <summary>Marca si el comprobante es fiscal. Hoy SIEMPRE false (no fiscal).</summary>
        public bool EsFiscal { get; set; }

        // ─────────────── Detalle ───────────────

        public ICollection<ComprobanteCobroLinea> Lineas { get; set; } = new List<ComprobanteCobroLinea>();
    }
}
