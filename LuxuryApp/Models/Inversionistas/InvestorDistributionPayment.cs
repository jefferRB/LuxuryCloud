using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Inversionistas
{
    /// <summary>
    /// Pago realizado al inversionista contra un estado de cuenta.
    ///
    /// <para>
    /// Este registro NO crea un egreso automáticamente. Si el negocio quiere reflejar la salida
    /// de caja en Egresos, debe usar la categoría <see cref="InvestorDefaults.CategoriaDistribucionInversionistas"/>,
    /// que el motor de cálculo excluye siempre para que pagar al inversionista no reduzca la
    /// ganancia distribuible (y con ella su propia participación).
    /// </para>
    /// </summary>
    public class InvestorDistributionPayment : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        public int StatementId { get; set; }

        public InvestorStatement? Statement { get; set; }

        [Display(Name = "Fecha del pago")]
        public DateOnly Fecha { get; set; }

        /// <summary>
        /// Monto del pago. Positivo en un pago normal; negativo solo en una reversión explícita
        /// (<see cref="EsReversion"/>), que exige motivo y queda auditada.
        /// </summary>
        [Display(Name = "Monto")]
        public decimal Monto { get; set; }

        [MaxLength(30)]
        [Display(Name = "Método de pago")]
        public string MetodoPago { get; set; } = "EFECTIVO";

        [MaxLength(120)]
        [Display(Name = "Referencia")]
        public string? Referencia { get; set; }

        [MaxLength(500)]
        [Display(Name = "Notas")]
        public string? Notas { get; set; }

        /// <summary>Corrección de un pago mal registrado. Exige <see cref="Motivo"/>.</summary>
        public bool EsReversion { get; set; }

        [MaxLength(300)]
        public string? Motivo { get; set; }

        [MaxLength(450)]
        public string? RegistradoPorUserId { get; set; }

        [MaxLength(256)]
        public string? RegistradoPorEmail { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
