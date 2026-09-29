using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.SaaS
{
    /// <summary>
    /// UNICA fuente de verdad para clasificar un plan del catalogo (base comercial / add-on
    /// WhatsApp / legacy / validacion) y para decidir si puede usarse como plan base forzado
    /// desde plataforma.
    ///
    /// Motivo: el selector de "plan forzado" cargaba <c>Planes.Where(p => p.Activo)</c> ordenado
    /// por precio y mezclaba paquetes WA con planes de la calculadora; la validacion server-side
    /// solo exigia <c>Activo</c>, asi que era posible guardar WA400 como plan base de un tenant
    /// (un paquete de mensajes no tiene MaxFuncionarios, de modo que el limite efectivo quedaba
    /// indefinido). Toda la clasificacion vive aqui para no duplicar reglas en vistas ni
    /// controladores.
    /// </summary>
    public static class PlanCatalogRules
    {
        /// <summary>
        /// Clasifica el plan. El orden de las reglas importa: validacion primero (un plan de prueba
        /// nunca debe pasar por comercial aunque su codigo lo parezca), luego add-ons, luego la
        /// calculadora y por ultimo el legacy conocido.
        /// </summary>
        public static PlanCatalogKind Classify(Plan? plan)
        {
            if (plan is null)
            {
                return PlanCatalogKind.Unknown;
            }

            return Classify(plan.Codigo, plan.EsPlanValidacion);
        }

        /// <summary>Sobrecarga por codigo, para clasificar sin cargar la entidad completa.</summary>
        public static PlanCatalogKind Classify(string? codigo, bool esPlanValidacion = false)
        {
            var code = codigo?.Trim();

            if (esPlanValidacion)
            {
                return PlanCatalogKind.Validation;
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                return PlanCatalogKind.Unknown;
            }

            if (PlanCodes.WhatsAppAddons.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                return PlanCatalogKind.WhatsAppAddon;
            }

            if (string.Equals(code, PlanCodes.TestRecurring, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, PlanCodes.TestProdBasic100, StringComparison.OrdinalIgnoreCase))
            {
                return PlanCatalogKind.Validation;
            }

            if (PlanCodes.IsCalculatorPlanCode(code))
            {
                return PlanCatalogKind.BaseCommercial;
            }

            if (PlanCodes.BasePlans.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                return PlanCatalogKind.LegacyBase;
            }

            return PlanCatalogKind.Unknown;
        }

        /// <summary>
        /// True cuando el plan puede definir el limite de funcionarios de un tenant. Un add-on de
        /// WhatsApp NUNCA califica; un plan sin clasificar tampoco (fail-closed).
        /// </summary>
        public static bool IsBasePlan(PlanCatalogKind kind) =>
            kind is PlanCatalogKind.BaseCommercial or PlanCatalogKind.LegacyBase or PlanCatalogKind.Validation;

        /// <inheritdoc cref="IsBasePlan(PlanCatalogKind)"/>
        public static bool IsBasePlan(Plan? plan) => IsBasePlan(Classify(plan));

        /// <summary>
        /// True cuando el plan solo debe ofrecerse en la seccion avanzada del selector (legacy o
        /// validacion): sigue siendo seleccionable para migrar un tenant historico, pero no aparece
        /// en la lista comercial normal y la eleccion se audita como legacy.
        /// </summary>
        public static bool IsAdvancedOnly(PlanCatalogKind kind) =>
            kind is PlanCatalogKind.LegacyBase or PlanCatalogKind.Validation;

        /// <inheritdoc cref="IsAdvancedOnly(PlanCatalogKind)"/>
        public static bool IsAdvancedOnly(Plan? plan) => IsAdvancedOnly(Classify(plan));

        /// <summary>Etiqueta corta para UI/auditoria. No se usa para decidir nada.</summary>
        public static string DescribeKind(PlanCatalogKind kind) => kind switch
        {
            PlanCatalogKind.BaseCommercial => "Plan base comercial",
            PlanCatalogKind.WhatsAppAddon => "Add-on WhatsApp",
            PlanCatalogKind.LegacyBase => "Plan base legacy",
            PlanCatalogKind.Validation => "Plan de prueba/validacion",
            _ => "Plan sin clasificar"
        };

        /// <summary>
        /// Orden estable para el selector de plan base: primero mensuales de la calculadora por
        /// cantidad de funcionarios, luego anuales, y al final lo avanzado. Evita el orden por
        /// precio que intercalaba paquetes WA entre LC_M_01 y LC_M_02.
        /// </summary>
        public static int SortKey(Plan plan)
        {
            var kind = Classify(plan);
            var cycleOffset = plan.BillingCycle == BillingCycle.Annual ? 100 : 0;
            var workers = plan.MaxFuncionarios ?? 99;

            return kind switch
            {
                PlanCatalogKind.BaseCommercial => cycleOffset + workers,
                PlanCatalogKind.LegacyBase => 1_000 + workers,
                PlanCatalogKind.Validation => 2_000 + workers,
                _ => 9_000
            };
        }
    }
}
