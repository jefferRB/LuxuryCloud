namespace LuxuryApp.Models.Finanzas
{
    /// <summary>
    /// Datos para exportar Ingresos a Excel: resumen agregado (todo el filtro) + TODAS las filas
    /// filtradas (sin paginar), cada una con su desglose fiscal correcto (IVA incluido).
    /// </summary>
    public sealed class CobroExportViewModel
    {
        public CobroIndexViewModel Resumen { get; init; } = new();

        public List<CobroExportRow> Filas { get; init; } = new();
    }

    public sealed class CobroExportRow
    {
        public DateTime FechaCobro { get; init; }
        public string NombreCliente { get; init; } = string.Empty;
        public string FuncionarioNombre { get; init; } = string.Empty;
        public bool EsServicio { get; init; }
        public string Detalle { get; init; } = string.Empty;
        public string MetodoPago { get; init; } = string.Empty;

        /// <summary>Total cobrado al cliente (IVA incluido).</summary>
        public decimal Monto { get; init; }

        /// <summary>Base sin IVA = Total / 1.13.</summary>
        public decimal BaseSinIva { get; init; }

        /// <summary>IVA incluido = Total − Base.</summary>
        public decimal IvaIncluido { get; init; }

        /// <summary>Monto que corresponde al colaborador (comisión).</summary>
        public decimal MontoColaborador { get; init; }

        /// <summary>Monto que queda al negocio = Base sin IVA − comisión.</summary>
        public decimal MontoNegocio { get; init; }
    }
}
