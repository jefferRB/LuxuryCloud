using System.Globalization;

namespace LuxuryApp.Models.Inversionistas
{
    /// <summary>
    /// Tono visual de un estado. La semántica es fija en todo el módulo:
    /// azul/neutro = en curso, verde = cerrado sin saldo, ámbar = pendiente o parcial,
    /// rojo = SOLO algo que requiere atención real (anulado). Un período abierto es
    /// comportamiento normal y NUNCA se pinta de rojo.
    /// </summary>
    public enum InvestorVisualTone
    {
        Neutral = 0,
        Success = 1,
        Warning = 2,
        Danger = 3
    }

    public static class InvestorVisuals
    {
        /// <summary>Tono de un estado de cuenta según su etapa y su saldo.</summary>
        public static InvestorVisualTone Tone(InvestorStatementStatus estado, decimal saldoPendiente) => estado switch
        {
            InvestorStatementStatus.Voided => InvestorVisualTone.Danger,
            InvestorStatementStatus.Draft => InvestorVisualTone.Neutral,
            InvestorStatementStatus.Paid => InvestorVisualTone.Success,
            _ => saldoPendiente > 0m ? InvestorVisualTone.Warning : InvestorVisualTone.Success
        };

        /// <summary>Sufijo de clase CSS (<c>inv-badge--success</c>, etc.).</summary>
        public static string CssModifier(InvestorVisualTone tono) => tono switch
        {
            InvestorVisualTone.Success => "success",
            InvestorVisualTone.Warning => "warning",
            InvestorVisualTone.Danger => "danger",
            _ => "neutral"
        };

        /// <summary>
        /// Etiqueta corta orientada al dueño del negocio, no al modelo de datos: lo que quiere
        /// saber es si ya pagó, si debe algo o si el corte todavía no está firme.
        /// </summary>
        public static string EstadoCorto(InvestorStatementStatus estado, decimal saldoPendiente) => estado switch
        {
            InvestorStatementStatus.Voided => "Anulado",
            InvestorStatementStatus.Draft => "Borrador",
            InvestorStatementStatus.Paid => "Pagado",
            InvestorStatementStatus.PartiallyPaid => "Pago parcial",
            _ => saldoPendiente > 0m ? "Pendiente de pago" : "Cerrado"
        };
    }

    /// <summary>
    /// ÚLTIMO CORTE REALMENTE EMITIDO. No es una fecha teórica del resolver: sale de un
    /// <see cref="InvestorStatement"/> que existe en la base y ya congeló su snapshot.
    ///
    /// <para>
    /// Un borrador NO cuenta como corte emitido: todavía puede cambiar de monto y no entra en el
    /// saldo pendiente. Por eso la tarjeta y el saldo siempre dicen lo mismo.
    /// </para>
    /// </summary>
    public sealed class InvestorLastStatementViewModel
    {
        public int StatementId { get; init; }

        public DateOnly FechaCorte { get; init; }

        public DateOnly PeriodoInicio { get; init; }

        public DateOnly PeriodoFin { get; init; }

        public string PeriodoEtiqueta { get; init; } = string.Empty;

        public decimal GananciaDistribuible { get; init; }

        public decimal ParticipacionPorcentaje { get; init; }

        public decimal ParticipacionCalculada { get; init; }

        public decimal TotalPagado { get; init; }

        public decimal SaldoPendiente { get; init; }

        public InvestorStatementStatus Estado { get; init; }

        public DateTime? EnviadoAtUtc { get; init; }

        public InvestorVisualTone Tono => InvestorVisuals.Tone(Estado, SaldoPendiente);

        public string EstadoTexto => InvestorVisuals.EstadoCorto(Estado, SaldoPendiente);
    }

    /// <summary>
    /// CICLO EN CURSO: el período contractual todavía abierto. Es un cálculo <b>live</b> que puede
    /// cambiar hasta el cierre; no es deuda, no es un <see cref="InvestorStatement"/> y jamás entra
    /// en el saldo pendiente.
    /// </summary>
    public sealed class InvestorCurrentCycleViewModel
    {
        public int InvestorId { get; init; }

        public string InvestorNombre { get; init; } = string.Empty;

        public DateOnly PeriodoInicio { get; init; }

        public DateOnly PeriodoFin { get; init; }

        public string PeriodoEtiqueta { get; init; } = string.Empty;

        /// <summary>
        /// Hasta qué día se calculó: <c>min(hoy, fin del período)</c>. Nunca incluye días futuros
        /// aunque el rango contractual los contenga.
        /// </summary>
        public DateOnly CalculadoAl { get; init; }

        /// <summary>Cierre del período en curso. Es el próximo corte.</summary>
        public DateOnly ProximoCorte => PeriodoFin;

        public string CorteTexto { get; init; } = string.Empty;

        public decimal GananciaDistribuible { get; init; }

        public decimal ParticipacionPorcentaje { get; init; }

        public decimal ParticipacionEstimada { get; init; }

        public bool TieneAcuerdoVigente { get; init; }

        public InvestorCalculationBreakdownViewModel Desglose { get; init; } = new();

        /// <summary>Días que faltan para el corte, para redactar la frase sin calcular en la vista.</summary>
        public int DiasParaElCorte => Math.Max(PeriodoFin.DayNumber - CalculadoAl.DayNumber, 0);

        /// <summary>Estado de cuenta ya emitido para ESTE período (caso raro: generación manual).</summary>
        public int? EstadoExistenteId { get; init; }
    }

    /// <summary>
    /// Resumen operacional del inversionista: responde "cuánto cerró el último corte, cuánto se
    /// pagó, cuánto sigue pendiente, cuánto lleva el ciclo actual y cuándo vuelve a cerrar" sin
    /// obligar a entender acuerdos, rangos ni fechas arbitrarias.
    /// </summary>
    public sealed class InvestorFinancialSummaryViewModel
    {
        public int InvestorId { get; init; }

        public string InvestorNombre { get; init; } = string.Empty;

        public decimal? PorcentajeVigente { get; init; }

        /// <summary>"Corte mensual · día 20". Sale del resolver.</summary>
        public string CorteTexto { get; init; } = string.Empty;

        // ─── A) Último corte (snapshot inmutable) ───

        public InvestorLastStatementViewModel? UltimoCorte { get; init; }

        /// <summary>
        /// Fecha del PRIMER corte que se va a emitir. Solo tiene valor cuando todavía no hay
        /// ningún estado emitido: es lo que se muestra en vez de inventar un "último corte".
        /// </summary>
        public DateOnly? PrimerCorte { get; init; }

        // ─── B) Ciclo actual (cálculo live) ───

        public InvestorCurrentCycleViewModel? CicloActual { get; init; }

        // ─── C) Saldo pendiente (solo de estados emitidos) ───

        public decimal SaldoPendienteTotal { get; init; }

        public int EstadosConSaldo { get; init; }

        public int EstadosEmitidos { get; init; }

        /// <summary>Borradores sin finalizar. No suman al saldo, pero tampoco se esconden.</summary>
        public int Borradores { get; init; }

        /// <summary>True si el tenant tiene activada la generación automática de estados.</summary>
        public bool GeneracionAutomatica { get; init; }

        /// <summary>Últimos cortes (incluye borradores, marcados como tales).</summary>
        public IReadOnlyList<InvestorStatementListItemViewModel> CortesRecientes { get; init; } =
            Array.Empty<InvestorStatementListItemViewModel>();

        public bool TieneCortes => UltimoCorte is not null;

        public bool TieneParticipacion => PorcentajeVigente.HasValue;

        /// <summary>Frase del empty state, redactada con el nombre y la fecha reales.</summary>
        public string EmptyStateTitulo => $"Aún no hay cortes emitidos para {InvestorNombre}.";

        public string EmptyStateDetalle
        {
            get
            {
                if (PrimerCorte is null)
                {
                    return "Configurá su participación para que empiece a acumular un período.";
                }

                var fecha = PrimerCorte.Value.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("es-CR"));
                var cierre = GeneracionAutomatica
                    ? "El estado se generará automáticamente después de cerrar ese día."
                    : "Después de cerrar el período vas a poder generar su estado de cuenta.";

                return $"Su primer corte con la configuración actual será el {fecha}. {cierre}";
            }
        }
    }

    /// <summary>Navegación entre cortes emitidos del mismo inversionista.</summary>
    public sealed record InvestorStatementNavigation(
        int? AnteriorId,
        DateOnly? AnteriorCorte,
        int? SiguienteId,
        DateOnly? SiguienteCorte)
    {
        public static readonly InvestorStatementNavigation Vacia = new(null, null, null, null);

        public bool TieneNavegacion => AnteriorId.HasValue || SiguienteId.HasValue;
    }
}
