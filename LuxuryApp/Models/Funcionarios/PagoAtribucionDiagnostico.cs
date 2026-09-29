namespace LuxuryApp.Models.Funcionarios
{
    /// <summary>
    /// Diagnóstico temporal de cómo se atribuyó cada pago al rango consultado en Liquidación.
    /// Permite comparar pagos incluidos/excluidos y el porqué (prorrateo por producción real).
    /// </summary>
    public sealed class PagoAtribucionDiagnostico
    {
        /// <summary>Origen del pago: "LEGACY" (PagosFuncionarios) o "LIQUIDACION" (LiquidacionSemanalDetalle).</summary>
        public string Origen { get; init; } = string.Empty;

        /// <summary>Id del pago (IdPago legacy o LiquidacionSemanalId).</summary>
        public int PagoId { get; init; }

        public int FuncionarioId { get; init; }

        public string FuncionarioNombre { get; init; } = string.Empty;

        /// <summary>Monto total del pago (encabezado).</summary>
        public decimal Monto { get; init; }

        public DateTime PeriodoInicio { get; init; }

        public DateTime PeriodoFin { get; init; }

        public DateTime FechaPago { get; init; }

        /// <summary>True si el pago aportó monto al rango (aplicado &gt; 0).</summary>
        public bool Incluido { get; init; }

        /// <summary>Fracción del pago atribuida al rango (0..1) según producción real.</summary>
        public decimal Fraccion { get; init; }

        /// <summary>Monto del pago aplicado al rango = Monto × Fracción.</summary>
        public decimal MontoAplicado { get; init; }

        /// <summary>Motivo legible de inclusión/exclusión.</summary>
        public string Motivo { get; init; } = string.Empty;
    }
}
