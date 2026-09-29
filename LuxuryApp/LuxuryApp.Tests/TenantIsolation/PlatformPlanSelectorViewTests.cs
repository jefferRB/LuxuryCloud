using LuxuryApp.Models.Platform;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Tests.Support;

namespace LuxuryApp.Tests.TenantIsolation
{
    /// <summary>
    /// El selector de plan base forzado de Mission Control no debe volver a mezclar add-ons de
    /// WhatsApp con planes comerciales. Se prueban las dos mitades del contrato:
    ///   - Lo que el controlador PROYECTA (BasePlanOptions / AdvancedPlanOptions).
    ///   - Lo que la VISTA lee (que el markup no vuelva a iterar el catalogo completo).
    /// </summary>
    public class PlatformPlanSelectorViewTests
    {
        private static readonly string TenantsViewPath =
            TestProjectPaths.ProjectPath("Views", "Platform", "Tenants.cshtml");

        [Fact]
        public void BasePlanOptions_SoloContienePlanesComercialesDeLaCalculadora()
        {
            var catalog = BuildFullCatalog();

            var basePlans = catalog
                .Where(plan => PlanCatalogRules.Classify(plan) == PlanCatalogKind.BaseCommercial)
                .ToList();

            Assert.NotEmpty(basePlans);
            Assert.All(basePlans, plan => Assert.True(PlanCodes.IsCalculatorPlanCode(plan.Codigo)));

            // Ningun paquete de WhatsApp puede aparecer como opcion de plan base.
            foreach (var addonCode in PlanCodes.WhatsAppAddons)
            {
                Assert.DoesNotContain(basePlans, plan =>
                    string.Equals(plan.Codigo, addonCode, StringComparison.OrdinalIgnoreCase));
            }

            // Tampoco legacy ni pruebas.
            Assert.DoesNotContain(basePlans, plan => plan.Codigo == PlanCodes.Basic);
            Assert.DoesNotContain(basePlans, plan => plan.Codigo == PlanCodes.Pro);
            Assert.DoesNotContain(basePlans, plan => plan.Codigo == PlanCodes.Business);
            Assert.DoesNotContain(basePlans, plan => plan.Codigo == PlanCodes.TestProdBasic100);
            Assert.DoesNotContain(basePlans, plan => plan.EsPlanValidacion);
        }

        [Fact]
        public void AdvancedPlanOptions_ContieneLegacyYPruebas_PeroNuncaAddons()
        {
            var catalog = BuildFullCatalog();

            var advanced = catalog
                .Where(PlanCatalogRules.IsAdvancedOnly)
                .Select(plan => plan.Codigo)
                .ToList();

            Assert.Contains(PlanCodes.Basic, advanced);
            Assert.Contains(PlanCodes.Pro, advanced);
            Assert.Contains(PlanCodes.Business, advanced);
            Assert.Contains(PlanCodes.TestProdBasic100, advanced);

            foreach (var addonCode in PlanCodes.WhatsAppAddons)
            {
                Assert.DoesNotContain(addonCode, advanced);
            }
        }

        [Fact]
        public void Catalogo_LosAddonsWhatsAppNoCaenEnNingunGrupoDelSelector()
        {
            var catalog = BuildFullCatalog();

            var selectable = catalog
                .Where(plan =>
                    PlanCatalogRules.Classify(plan) == PlanCatalogKind.BaseCommercial ||
                    PlanCatalogRules.IsAdvancedOnly(plan))
                .Select(plan => plan.Codigo)
                .ToList();

            foreach (var addonCode in PlanCodes.WhatsAppAddons)
            {
                Assert.DoesNotContain(addonCode, selectable);
            }
        }

        [Fact]
        public void VistaTenants_NoIteraElCatalogoCompletoEnElSelectorDePlan()
        {
            var markup = File.ReadAllText(TenantsViewPath);

            // El bug original: el <select name="forcedPlanId"> iteraba Model.AvailablePlans, que trae
            // TODO el catalogo activo (WA400, legacy y calculadora mezclados por precio).
            Assert.DoesNotContain("foreach (var plan in Model.AvailablePlans)", markup);
            Assert.Contains("foreach (var plan in Model.BasePlanOptions)", markup);
        }

        [Fact]
        public void VistaTenants_SeparaElSelectorLegacyEnUnCampoDistinto()
        {
            var markup = File.ReadAllText(TenantsViewPath);

            // Dos <select> con el MISMO name romperian el model binding: el avanzado usa su propio
            // campo y el controlador decide cual manda.
            Assert.Contains("name=\"legacyForcedPlanId\"", markup);
            Assert.Contains("<details", markup);
        }

        [Fact]
        public void VistaTenants_MuestraElLimiteEfectivoYNoSoloElNombreDelPlan()
        {
            var markup = File.ReadAllText(TenantsViewPath);

            Assert.Contains("EffectiveEmployeeLimit", markup);
            Assert.Contains("ActiveFuncionarios", markup);
        }

        [Fact]
        public void VistaTenants_MarcaCuandoElContactoNoEsUnAdministrador()
        {
            var markup = File.ReadAllText(TenantsViewPath);

            Assert.Contains("OwnerIsFallback", markup);
            Assert.Contains("OwnerWarnings", markup);
        }

        /// <summary>
        /// Catalogo con todas las familias mezcladas, como lo devuelve
        /// <c>Planes.Where(p =&gt; p.Activo)</c> en produccion.
        /// </summary>
        private static List<Plan> BuildFullCatalog()
        {
            var plans = new List<Plan>
            {
                new() { Id = Guid.NewGuid(), Codigo = PlanCodes.Basic, Nombre = "Básico", MaxFuncionarios = 2, PrecioMensual = 15_000m, Activo = true },
                new() { Id = Guid.NewGuid(), Codigo = PlanCodes.Pro, Nombre = "Pro", MaxFuncionarios = 7, PrecioMensual = 35_000m, Activo = true },
                new() { Id = Guid.NewGuid(), Codigo = PlanCodes.Business, Nombre = "Business", MaxFuncionarios = 15, PrecioMensual = 60_000m, Activo = true },
                new() { Id = Guid.NewGuid(), Codigo = PlanCodes.WhatsApp400, Nombre = "WhatsApp 400", LimiteMensajesMensual = 400, PrecioMensual = 6_000m, Activo = true },
                new() { Id = Guid.NewGuid(), Codigo = PlanCodes.WhatsApp800, Nombre = "WhatsApp 800", LimiteMensajesMensual = 800, PrecioMensual = 11_000m, Activo = true },
                new() { Id = Guid.NewGuid(), Codigo = PlanCodes.WhatsApp1200, Nombre = "WhatsApp 1200", LimiteMensajesMensual = 1200, PrecioMensual = 15_000m, Activo = true },
                new() { Id = Guid.NewGuid(), Codigo = PlanCodes.TestProdBasic100, Nombre = "LuxuryCloud Test Producción", MaxFuncionarios = 1, PrecioMensual = 100m, Activo = true },
                new() { Id = Guid.NewGuid(), Codigo = PlanCodes.TestRecurring, Nombre = "Test recurrente", MaxFuncionarios = 1, PrecioMensual = 100m, Activo = true, EsPlanValidacion = true }
            };

            // Los 22 planes de la calculadora (LC_M_01..11 y LC_A_01..11).
            var workers = 0;
            foreach (var code in PlanCodes.EnumerateCalculatorCodes())
            {
                var isAnnual = code.StartsWith(PlanCodes.CalculatorAnnualPrefix, StringComparison.OrdinalIgnoreCase);
                workers = (workers % PlanCodes.CalculatorMaxWorkers) + 1;

                plans.Add(new Plan
                {
                    Id = Guid.NewGuid(),
                    Codigo = code,
                    Nombre = $"LuxuryCloud {(isAnnual ? "Anual" : "Mensual")} {workers} funcionarios",
                    MaxFuncionarios = workers,
                    PrecioMensual = isAnnual ? workers * 120_000m : workers * 12_000m,
                    BillingCycle = isAnnual ? BillingCycle.Annual : BillingCycle.Monthly,
                    Activo = true
                });
            }

            return plans;
        }
    }
}
