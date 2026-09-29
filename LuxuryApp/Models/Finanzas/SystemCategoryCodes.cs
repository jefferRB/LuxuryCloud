namespace LuxuryApp.Models.Finanzas
{
    /// <summary>
    /// Identidad ESTRUCTURAL de las categorías de egreso que tienen significado financiero.
    ///
    /// <para>
    /// Regla que no se negocia: <b>ninguna fórmula financiera puede depender del nombre visible
    /// de una categoría</b>. El nombre es una etiqueta que el negocio puede cambiar cuando quiera;
    /// el <c>SystemCode</c> es estable y es lo único que decide comportamiento.
    /// </para>
    ///
    /// <para>
    /// Una categoría creada por el usuario tiene <c>SystemCode = null</c> y se comporta como gasto
    /// operativo normal.
    /// </para>
    /// </summary>
    public static class SystemCategoryCodes
    {
        /// <summary>
        /// Salida de caja de una liquidación ordinaria YA devengada. Se excluye del gasto operativo
        /// porque esa comisión ya se restó en la línea de "Liquidaciones del equipo".
        /// </summary>
        public const string EmployeeSettlement = "EmployeeSettlement";

        /// <summary>
        /// Pago de distribución a inversionistas. Se excluye del gasto operativo porque, si contara,
        /// pagarle al inversionista reduciría su propia participación (recursividad).
        /// </summary>
        public const string InvestorDistribution = "InvestorDistribution";

        /// <summary>
        /// Costo laboral extraordinario (vacaciones, bonos, incentivos, ajustes). NO está incluido
        /// en ninguna comisión devengada, así que SÍ reduce la ganancia como gasto normal.
        /// </summary>
        public const string ExtraordinaryLaborCost = "ExtraordinaryLaborCost";

        public const int MaxLength = 40;

        /// <summary>Códigos que el usuario NO puede elegir a mano en el formulario de egresos.</summary>
        public static readonly IReadOnlyCollection<string> AsignablesSoloPorElSistema =
            new[] { EmployeeSettlement, InvestorDistribution };

        public static readonly IReadOnlyCollection<string> Todos =
            new[] { EmployeeSettlement, InvestorDistribution, ExtraordinaryLaborCost };

        public static bool EsCodigoValido(string? systemCode) =>
            systemCode is not null && Todos.Contains(systemCode, StringComparer.Ordinal);

        public static bool EsSoloDelSistema(string? systemCode) =>
            systemCode is not null && AsignablesSoloPorElSistema.Contains(systemCode, StringComparer.Ordinal);

        // ── Compatibilidad con datos anteriores al SystemCode ──────────────────────────────
        // Mientras exista una categoría SIN código (por ejemplo, un tenant con nombres duplicados
        // que el backfill no pudo resolver sin adivinar), se conserva el criterio histórico por
        // nombre. NO es un retroceso: si la categoría TIENE código, el nombre deja de importar
        // por completo, que es justamente la garantía que se busca. Las filas que todavía
        // dependen del nombre se reportan con un script de verificación post-deploy.

        public const string NombreLegacyEmployeeSettlement = "Pago Funcionarios";
        public const string NombreLegacyInvestorDistribution = "Distribución a inversionistas";
        public const string NombreSugeridoExtraordinaryLaborCost = "Costo laboral extraordinario";

        public static bool EsLiquidacionDeColaboradores(string? systemCode, string? nombre) =>
            Resolver(systemCode, nombre, EmployeeSettlement, NombreLegacyEmployeeSettlement);

        public static bool EsDistribucionAInversionistas(string? systemCode, string? nombre) =>
            Resolver(systemCode, nombre, InvestorDistribution, NombreLegacyInvestorDistribution);

        /// <summary>
        /// El código manda siempre. El nombre solo decide cuando la categoría todavía no tiene
        /// código asignado.
        /// </summary>
        private static bool Resolver(string? systemCode, string? nombre, string codigo, string nombreLegacy) =>
            systemCode is not null
                ? string.Equals(systemCode, codigo, StringComparison.Ordinal)
                : string.Equals(nombre, nombreLegacy, StringComparison.OrdinalIgnoreCase);

        /// <summary>Nombre por defecto con el que se crea la categoría si todavía no existe.</summary>
        public static string NombrePorDefecto(string systemCode) => systemCode switch
        {
            EmployeeSettlement => NombreLegacyEmployeeSettlement,
            InvestorDistribution => NombreLegacyInvestorDistribution,
            ExtraordinaryLaborCost => NombreSugeridoExtraordinaryLaborCost,
            _ => throw new ArgumentOutOfRangeException(nameof(systemCode), systemCode, "Código de categoría del sistema desconocido.")
        };

        public static string DetallePorDefecto(string systemCode) => systemCode switch
        {
            EmployeeSettlement =>
                "Categoria del sistema: salida de caja de liquidaciones ya devengadas. No reduce la ganancia dos veces.",
            InvestorDistribution =>
                "Categoria del sistema: pagos de distribucion a inversionistas. No reduce la ganancia distribuible.",
            ExtraordinaryLaborCost =>
                "Vacaciones, bonos, incentivos y ajustes laborales que no forman parte de una liquidacion ordinaria.",
            _ => throw new ArgumentOutOfRangeException(nameof(systemCode), systemCode, "Código de categoría del sistema desconocido.")
        };
    }
}
