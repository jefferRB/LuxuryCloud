using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using LuxuryApp.Models.Finanzas;

namespace LuxuryApp.Models.Funcionarios
{
    public class LiquidacionSemanal : ITenantEntity
    {
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        [Required]
        public DateTime SemanaInicio { get; set; }

        [Required]
        public DateTime SemanaFin { get; set; }

        [Required]
        public DateTime FechaPago { get; set; }

        [Required]
        public decimal MontoTotal { get; set; }

        [Required]
        [MaxLength(30)]
        public string Estado { get; set; } = LiquidacionSemanalDefaults.EstadoPagada;

        [MaxLength(500)]
        public string? Observacion { get; set; }

        [MaxLength(450)]
        public string? CreadoPor { get; set; }

        [Required]
        public DateTime FechaCreacion { get; set; }

        public int? EgresoId { get; set; }
        public Egreso? Egreso { get; set; }

        /// <summary>
        /// Clave de la INTENCIÓN de pago que originó esta liquidación. Se genera una vez al abrir
        /// el formulario y viaja en el POST.
        ///
        /// <para>
        /// Es la única defensa real contra el doble pago: un doble click, un reenvío del formulario
        /// o un reintento de EF tras perder el ACK del COMMIT llegan con la MISMA clave, y el índice
        /// único <c>UX_LiquidacionesSemanales_TenantId_IdempotencyKey</c> garantiza que solo exista
        /// una operación. Deshabilitar el botón o validar el pendiente son defensas de UX, no de
        /// integridad. Null en los pagos anteriores a esta función.
        /// </para>
        /// </summary>
        public Guid? IdempotencyKey { get; set; }

        public ICollection<LiquidacionSemanalDetalle> Detalles { get; set; } = new List<LiquidacionSemanalDetalle>();
        public ICollection<LiquidacionSemanalDistribucionMensual> DistribucionesMensuales { get; set; } = new List<LiquidacionSemanalDistribucionMensual>();
    }
}
