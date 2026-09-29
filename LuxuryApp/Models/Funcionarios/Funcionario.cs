using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Common;
using LuxuryApp.Models.Fiscal;

namespace LuxuryApp.Models.Funcionarios
{
    public class Funcionario : ITenantEntity
    {

        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }
        [Key]
        public int IdFuncionario { get; set; }

        [Required]
        [MaxLength(100)]
        public string Nombre { get; set; } = string.Empty;

        [MaxLength(20)]
        public string? Telefono { get; set; }

        [Required]
        public int IdPuesto { get; set; }

        public Puesto? Puesto { get; set; }

        [Required]
        public string ColorCalendario { get; set; } = string.Empty;

        [Range(0, 100)]
        public decimal PorcentajeGanancia { get; set; }
        public decimal PorcentajeProducto { get; set; }

        // Si es true (comportamiento histórico), la comisión del funcionario se calcula
        // sobre la base SIN impuestos (Producción - IVA). Si es false, se calcula sobre
        // la producción total. Default true para no alterar cálculos existentes.
        // COMPATIBILIDAD: se conserva pero la fuente de verdad es ComisionCalculadaSobre.
        // Al guardar desde la UI se mantienen sincronizados (true ↔ BaseSinIva).
        public bool RebajarImpuestosAntesDeComision { get; set; } = true;

        // ─────────────── Configuración fiscal del colaborador ───────────────

        /// <summary>Tipo de relación: Empleado, Independiente o AlquilerSilla.</summary>
        public TipoRelacionColaborador TipoRelacionColaborador { get; set; } = TipoRelacionColaborador.Empleado;

        /// <summary>
        /// Base sobre la que se calcula la comisión. Fuente de verdad; se sincroniza con
        /// <see cref="RebajarImpuestosAntesDeComision"/> (BaseSinIva ↔ true).
        /// </summary>
        public ComisionCalculadaSobre ComisionCalculadaSobre { get; set; } = ComisionCalculadaSobre.BaseSinIva;

        /// <summary>
        /// Si el colaborador independiente factura IVA. COMPATIBILIDAD: se conserva y se sincroniza
        /// con <see cref="ModalidadIvaColaborador"/> (NoFactura ↔ false). La fuente de verdad del
        /// tratamiento del IVA es <see cref="ModalidadIvaColaborador"/>.
        /// </summary>
        public bool ColaboradorFacturaIva { get; set; }

        /// <summary>
        /// Modalidad de IVA del colaborador (fuente de verdad). A = no factura, B = IVA incluido
        /// dentro de su parte (caso principal), C = IVA adicional sobre la comisión.
        /// </summary>
        public ModalidadIvaColaborador ModalidadIvaColaborador { get; set; } = ModalidadIvaColaborador.NoFactura;

        /// <summary>Tarifa de IVA de la factura del colaborador, en porcentaje. Default 13.</summary>
        [Range(0, 100)]
        public decimal TarifaIvaFacturaColaborador { get; set; } = FiscalDefaults.TarifaIvaPorDefecto;

        /// <summary>Si se requiere factura del colaborador antes de pagarle. Default false.</summary>
        public bool RequiereFacturaAntesDePagar { get; set; }

        public DateTime FechaIngreso { get; set; }

        public bool Activo { get; set; }

        /// <summary>
        /// Id de la cuenta de acceso (AspNetUsers) vinculada a este funcionario,
        /// si el administrador le habilitó acceso al portal. Null = sin acceso.
        /// La relación es 1:1 dentro del tenant.
        /// </summary>
        [MaxLength(450)]
        public string? AppUsuarioId { get; set; }

        // ─────────────── Foto opcional (mostrable en reservas online) ───────────────

        /// <summary>URL pública relativa de la foto (p. ej. /uploads/tenants/{tenant}/funcionarios/{guid}.jpg). Null = sin foto.</summary>
        [MaxLength(400)]
        public string? FotoUrl { get; set; }

        /// <summary>Ruta física relativa dentro de wwwroot, para poder borrar el archivo. Null = sin foto.</summary>
        [MaxLength(400)]
        public string? FotoStoragePath { get; set; }

        /// <summary>Fecha UTC de la última actualización de la foto.</summary>
        public DateTime? FotoActualizadaUtc { get; set; }

        /// <summary>Si la foto puede mostrarse a los clientes en el link público de reservas. Default true.</summary>
        public bool MostrarFotoEnReservas { get; set; } = true;

        /// <summary>
        /// Descripción pública corta del profesional (bio) que puede mostrarse en la landing
        /// pública del negocio, junto a su foto. Null/vacío = no se muestra. Texto plano.
        /// </summary>
        [MaxLength(280)]
        public string? DescripcionPublica { get; set; }
    }

}
