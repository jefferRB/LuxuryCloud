namespace LuxuryApp.Models.Inversionistas
{
    /// <summary>
    /// Frecuencia del periodo financiero del acuerdo. Los valores son estables (viajan en la URL
    /// y quedan congelados en el snapshot del estado de cuenta): nunca reordenar.
    /// </summary>
    public enum InvestorPayoutFrequency
    {
        Semanal = 0,
        Quincenal = 1,
        Mensual = 2
    }

    /// <summary>
    /// Qué pasa cuando la ganancia distribuible del periodo es negativa.
    /// </summary>
    public enum InvestorLossTreatment
    {
        /// <summary>La pérdida NO se arrastra: el distribuible es cero y el periodo siguiente arranca limpio.</summary>
        NoDistribution = 0,

        /// <summary>La pérdida queda pendiente y se descuenta de ganancias futuras antes de repartir.</summary>
        CarryForward = 1
    }

    /// <summary>
    /// Ciclo de vida del estado de cuenta. Draft es el único recalculable; a partir de
    /// Finalized los valores quedan congelados (snapshot inmutable).
    /// </summary>
    public enum InvestorStatementStatus
    {
        Draft = 0,
        Finalized = 1,
        Sent = 2,
        PartiallyPaid = 3,
        Paid = 4,
        Voided = 5
    }

    /// <summary>
    /// Qué se resta como "liquidaciones de colaboradores".
    /// </summary>
    public enum InvestorSettlementBasis
    {
        /// <summary>Lo devengado por el equipo en el periodo (total a pagar según la liquidación). Default.</summary>
        Devengado = 0,

        /// <summary>Solo lo efectivamente pagado y aplicado a la producción del periodo.</summary>
        Pagado = 1
    }

    /// <summary>
    /// Cómo se eligen las categorías de gasto que reducen la ganancia distribuible.
    /// </summary>
    public enum InvestorExpenseCategoryMode
    {
        Todas = 0,
        SoloSeleccionadas = 1,
        TodasExceptoSeleccionadas = 2
    }

    /// <summary>Resultado del intento de envío de un estado de cuenta.</summary>
    public enum InvestorStatementEmailStatus
    {
        Pending = 0,
        Sent = 1,
        Failed = 2,
        Skipped = 3
    }
}
