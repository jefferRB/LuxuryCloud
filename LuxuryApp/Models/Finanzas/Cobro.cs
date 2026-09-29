using System.ComponentModel.DataAnnotations;
using LuxuryApp.Models.Calendar;
using LuxuryApp.Models.Common;
using LuxuryApp.Models.DataBase;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Productos;

namespace LuxuryApp.Models.Finanzas
{
    public class Cobro : ITenantEntity, Fiscal.ICobroRemuneracionSnapshot
    {
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Guid TenantId { get; set; }
        [Key]
        public int IdCobro { get; set; }

        [Required]
        [Display(Name = "Fecha")]
        public DateTime FechaCobro { get; set; }

        [Required]
        [Display(Name = "Nombre Cliente")]
        public string NombreCliente { get; set; } = string.Empty;

        [Display(Name = "Cliente registrado")]
        public int? ClienteId { get; set; }

        [Required]
        [Display(Name = "Funcionario")]
        public int FuncionarioId { get; set; }

        [Display(Name = "Servicio")]
        public int? ServicioId { get; set; }

        // Servicio personalizado: cuando el cobro proviene de una cita con servicio fuera
        // del catálogo (ServicioId == null y ProductoId == null). Guarda el nombre como
        // snapshot para que el detalle sobreviva aunque la cita se elimine. A efectos de
        // finanzas un cobro con este valor cuenta como SERVICIO.
        [Display(Name = "Servicio personalizado")]
        public string? ServicioNombrePersonalizado { get; set; }

        [Required]
        [Display(Name = "Monto")]
        [DecimalRange(0.01, 999999, ErrorMessage = "Debe indicar un monto mayor a cero y dentro del rango permitido.")]
        public decimal Monto { get; set; }

        [Required]
        [Display(Name = "Método de Pago")]
        public string MetodoPago { get; set; } = string.Empty;

        [Display(Name = "Observaciones")]
        public string? Observaciones { get; set; }

        // ── Snapshot fiscal de la transacción ────────────────────────────────────────────────
        // Congela la fiscalidad EFECTIVA que resolvió ITenantFiscalConfigService en el momento del
        // cobro (overrides del servicio/producto ya combinados con la configuración del tenant).
        // Cambiar mañana el IVA del servicio, su tarifa o la configuración del negocio afecta las
        // ventas de mañana: esta operación ya tiene su propia historia.
        //
        // NULL en los tres = cobro LEGACY, anterior a esta versión. Para esos el motor sigue
        // usando el catálogo actual, exactamente como antes. No se rellenan hacia atrás: no
        // podemos demostrar qué configuración regía en su momento y no vamos a inventarla.
        //
        // No existe el snapshot parcial: los tres van juntos (CK_Cobros_SnapshotFiscal).
        // Ver Models/Fiscal/CobroFiscalidadEfectiva.cs para la regla de lectura.

        /// <summary>¿La venta estaba gravada? NULL = cobro legacy sin snapshot.</summary>
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public bool? AplicaIvaSnapshot { get; set; }

        /// <summary>Tarifa de IVA efectiva en porcentaje (13 = 13 %). NULL = cobro legacy.</summary>
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public decimal? TarifaIvaSnapshot { get; set; }

        /// <summary>¿El monto ya traía el IVA dentro? NULL = cobro legacy.</summary>
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public bool? PrecioIncluyeIvaSnapshot { get; set; }

        // ── Snapshot de REMUNERACIÓN de la transacción ───────────────────────────────────────
        // Congela la configuración del colaborador vigente al momento del cobro: son exactamente
        // los seis valores que consume ILiquidacionFuncionarioService.Liquidar.
        //
        // Si mañana un colaborador pasa de 50 % a 55 %, este cobro se sigue liquidando al 50 %. El catálogo
        // describe el FUTURO; el snapshot describe la HISTORIA.
        //
        // NULL en los seis = cobro LEGACY: se liquida con la configuración actual del colaborador,
        // igual que antes de esta versión. No se rellenan hacia atrás.
        //
        // Van los seis juntos o ninguno (CK_Cobros_SnapshotRemuneracion): liquidar con el
        // porcentaje viejo y la modalidad de IVA nueva daría una cifra que no existió nunca.

        /// <summary>% de comisión sobre servicios. NULL = cobro legacy.</summary>
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public decimal? PorcentajeServicioSnapshot { get; set; }

        /// <summary>% de comisión sobre productos. NULL = cobro legacy.</summary>
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public decimal? PorcentajeProductoSnapshot { get; set; }

        /// <summary>Si la comisión se calcula sobre el total cobrado o sobre la base sin IVA.</summary>
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Fiscal.ComisionCalculadaSobre? ComisionCalculadaSobreSnapshot { get; set; }

        /// <summary>Empleado o independiente: decide si su IVA de factura entra en juego.</summary>
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Fiscal.TipoRelacionColaborador? TipoRelacionColaboradorSnapshot { get; set; }

        /// <summary>Modalidad de IVA del colaborador (no factura / incluido / adicional).</summary>
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public Fiscal.ModalidadIvaColaborador? ModalidadIvaColaboradorSnapshot { get; set; }

        /// <summary>Tarifa de IVA de la factura del colaborador, en porcentaje.</summary>
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        public decimal? TarifaIvaColaboradorSnapshot { get; set; }

        /// <summary>
        /// Nombre del servicio/producto TAL COMO SE LLAMABA al vender. NULL = cobro legacy, que
        /// sigue mostrando el nombre actual del catálogo.
        ///
        /// <para>
        /// Renombrar mañana "Corte" a "Corte Premium" no debe hacer que el Excel de marzo diga que
        /// en marzo se vendió "Corte Premium". Es historia, igual que el IVA.
        /// </para>
        /// </summary>
        [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever]
        [MaxLength(200)]
        public string? DetalleSnapshot { get; set; }

        // 🔗 Cita de origen (cuando el cobro se registra desde una cita del calendario).
        // Permite evitar doble cobro de la misma cita (índice único filtrado).
        public int? CitaId { get; set; }
        public Cita? Cita { get; set; }

        // 🔗 Navegación EF
        public Funcionario? Funcionario { get; set; }
        public Servicio? Servicio { get; set; }
        public ClientesModel? Cliente { get; set; }
        public int? ProductoId { get; set; }
        public Producto? Producto { get; set; }

        public ICollection<DetalleCobroProducto> ProductosVendidos { get; set; } = new List<DetalleCobroProducto>();

    }
}
