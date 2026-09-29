namespace LuxuryApp.Models.Inversionistas
{
    /// <summary>
    /// Contenido del correo y del PDF de un estado de cuenta, construido SIEMPRE desde el snapshot
    /// congelado. Nunca se recalcula al momento de enviar: si el cobro de un cliente cambia
    /// mañana, el documento que ya se envió sigue diciendo lo mismo.
    ///
    /// <para>
    /// Por privacidad NO contiene nombres de clientes, datos de colaboradores ni información de
    /// otros inversionistas: solo los agregados del periodo y la participación de quien recibe.
    /// </para>
    /// </summary>
    public sealed record InvestorStatementDocument
    {
        public int StatementId { get; init; }

        public Guid TenantId { get; init; }

        public string NombreNegocio { get; init; } = string.Empty;

        public string? LogoUrl { get; init; }

        public string? TelefonoNegocio { get; init; }

        public string? EmailNegocio { get; init; }

        public string? DireccionNegocio { get; init; }

        public string InversionistaNombre { get; init; } = string.Empty;

        public string InversionistaEmail { get; init; } = string.Empty;

        public string PeriodoEtiqueta { get; init; } = string.Empty;

        public DateOnly PeriodoInicio { get; init; }

        public DateOnly PeriodoFin { get; init; }

        public decimal IngresosNetos { get; init; }

        public decimal IvaExcluido { get; init; }

        public decimal GastosElegibles { get; init; }

        public decimal Liquidaciones { get; init; }

        public decimal AjustesPositivos { get; init; }

        public decimal AjustesNegativos { get; init; }

        public decimal PerdidaArrastrada { get; init; }

        public decimal PerdidaPendiente { get; init; }

        public decimal GananciaDistribuible { get; init; }

        public decimal ParticipacionPorcentaje { get; init; }

        public decimal ParticipacionCalculada { get; init; }

        public decimal TotalPagado { get; init; }

        public decimal SaldoPendiente { get; init; }

        public InvestorStatementStatus Estado { get; init; }

        public string EstadoTexto { get; init; } = string.Empty;

        public string EstadoPagoTexto { get; init; } = string.Empty;

        public DateTime FechaEmision { get; init; }

        /// <summary>Nombre sugerido del PDF adjunto.</summary>
        public string NombreArchivo =>
            $"estado-participacion-{PeriodoInicio:yyyyMMdd}-{PeriodoFin:yyyyMMdd}.pdf";

        /// <summary>Asunto oficial del correo.</summary>
        public string Asunto =>
            $"{(string.IsNullOrWhiteSpace(NombreNegocio) ? "Tu negocio" : NombreNegocio)} | Estado de participación — {PeriodoEtiqueta}";
    }
}
