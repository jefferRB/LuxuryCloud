using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace LuxuryApp.Models.Inversionistas
{
    /// <summary>
    /// Acuerdo de participación de un inversionista: qué porcentaje de la ganancia distribuible
    /// le corresponde y desde/hasta cuándo.
    ///
    /// <para>
    /// Versionado: un cambio de porcentaje NO edita la fila vigente. Se cierra la vigente con
    /// <see cref="EffectiveTo"/> y se crea una nueva con la nueva fecha efectiva. Así los estados
    /// de cuenta históricos siguen explicando con qué acuerdo se calcularon.
    /// </para>
    ///
    /// <para>
    /// Sucursales: el dominio actual de LuxuryCloud no modela sucursales. Cuando existan, este es
    /// el punto de extensión natural (una columna <c>BranchId</c> nullable = todo el negocio).
    /// No se inventa el concepto en esta fase.
    /// </para>
    /// </summary>
    public class InvestorAgreement : ITenantEntity
    {
        [BindNever]
        public Guid TenantId { get; set; }

        [Key]
        public int Id { get; set; }

        public int InvestorId { get; set; }

        public TenantInvestor? Investor { get; set; }

        /// <summary>Porcentaje de participación sobre la ganancia distribuible (0,01 – 100).</summary>
        [Display(Name = "Porcentaje de participación")]
        [Range(0.01, 100, ErrorMessage = "El porcentaje debe estar entre 0,01 y 100.")]
        public decimal ParticipacionPorcentaje { get; set; }

        /// <summary>Primer día del primer periodo financiero cubierto por el acuerdo.</summary>
        [Display(Name = "Vigente desde")]
        public DateOnly EffectiveFrom { get; set; }

        /// <summary>Último día cubierto. Null = vigente indefinidamente.</summary>
        [Display(Name = "Vigente hasta")]
        public DateOnly? EffectiveTo { get; set; }

        [Display(Name = "Frecuencia")]
        public InvestorPayoutFrequency Frecuencia { get; set; } = InvestorPayoutFrequency.Mensual;

        /// <summary>
        /// Día del mes en que cierra el periodo, para acuerdos mensuales. Con corte el 20, los
        /// periodos van del 21 del mes anterior al 20 del mes actual, y el propio día 20 pertenece
        /// al periodo que cierra.
        ///
        /// <para>
        /// <c>null</c> = mes calendario (1 → último día). Es el valor de todos los acuerdos
        /// anteriores a esta función, y por eso conservan exactamente su comportamiento.
        /// </para>
        ///
        /// <para>
        /// Si el mes no tiene ese día (corte 31 en febrero) se usa el último día real del mes.
        /// La regla vive en <see cref="InvestorSettlementPeriodResolver"/> y en ningún otro lado.
        /// </para>
        ///
        /// <para>
        /// Vive acá y no en <c>Associate</c> a propósito: cambiar el corte cierra la versión
        /// vigente y crea una nueva, así los estados de cuenta ya emitidos siguen explicando con
        /// qué corte se calcularon.
        /// </para>
        /// </summary>
        [Range(1, 31, ErrorMessage = "El día de corte debe estar entre 1 y 31.")]
        [Display(Name = "Día de corte")]
        public int? DiaCorte { get; set; }

        [Display(Name = "Tratamiento de pérdidas")]
        public InvestorLossTreatment TratamientoPerdidas { get; set; } = InvestorLossTreatment.NoDistribution;

        /// <summary>Envío automático del estado de cuenta al cerrar el periodo.</summary>
        [Display(Name = "Envío automático")]
        public bool EnvioAutomatico { get; set; }

        [Display(Name = "Activo")]
        public bool Activo { get; set; } = true;

        [MaxLength(1000)]
        [Display(Name = "Notas del acuerdo")]
        public string? Notas { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        [MaxLength(450)]
        public string? CreatedByUserId { get; set; }

        [MaxLength(450)]
        public string? UpdatedByUserId { get; set; }

        /// <summary>True si el acuerdo cubre la fecha indicada (y está activo).</summary>
        public bool CubreFecha(DateOnly fecha) =>
            Activo &&
            EffectiveFrom <= fecha &&
            (EffectiveTo is null || EffectiveTo.Value >= fecha);

        /// <summary>True si el acuerdo se solapa con el rango indicado (ambos extremos inclusive).</summary>
        public bool SolapaRango(DateOnly desde, DateOnly hasta) =>
            EffectiveFrom <= hasta &&
            (EffectiveTo is null || EffectiveTo.Value >= desde);
    }
}
