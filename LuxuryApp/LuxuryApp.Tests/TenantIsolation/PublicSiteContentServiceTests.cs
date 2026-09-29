using LuxuryApp.Models.Marketing;
using LuxuryApp.Models.SaaS;
using LuxuryApp.Services.PublicSite;
using LuxuryApp.Services.SaaS;
using LuxuryApp.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class PublicSiteContentServiceTests
    {
        [Fact]
        public async Task GetPlanCardsAsync_ShouldKeepBasicAvailableWhenWhatsApp800CheckoutUrlIsMissing()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Planes.AddRange(
                CreatePlan(Guid.NewGuid(), PlanCodes.Basic, "Basico", 8000m, maxFuncionarios: 1),
                CreatePlan(Guid.NewGuid(), PlanCodes.WhatsApp800, "WhatsApp 800", 12000m, monthlyMessageLimit: 800));

            await context.SaveChangesAsync();

            var service = CreateService(
                context,
                new OpcionesPago
                {
                    EnableValidationPlans = true
                },
                new OpcionesTilopay
                {
                    WebhookAccessToken = "token-seguro"
                },
                new TilopayRepeatOptions
                {
                    Enabled = true,
                    UseHostedLinks = true,
                    UseRecurringCheckoutForPublicPlans = true,
                    Basic = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7001,
                        Code = PlanCodes.Basic,
                        MonthlyPrice = 8000m,
                        CheckoutUrl = "https://tp.cr/l/basic"
                    },
                    WhatsApp800 = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7012,
                        Code = PlanCodes.WhatsApp800,
                        MonthlyPrice = 12000m,
                        MonthlyMessageLimit = 800
                    }
                });

            var basePlans = await service.GetPlanCardsAsync();

            var basic = Assert.Single(basePlans);
            Assert.Equal(PlanCodes.Basic, basic.Code);
            Assert.True(basic.CanStartCheckout);
            Assert.Null(basic.CheckoutAvailabilityMessage);
        }

        [Fact]
        public async Task GetPlanCardsAsync_ShouldKeepBasicProAndBusinessReadyForCheckoutWhenRecurringConfigIsComplete()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Planes.AddRange(
                CreatePlan(Guid.NewGuid(), PlanCodes.Basic, "Basico", 8000m, maxFuncionarios: 1),
                CreatePlan(Guid.NewGuid(), PlanCodes.Pro, "Pro", 20000m, maxFuncionarios: 3),
                CreatePlan(Guid.NewGuid(), PlanCodes.Business, "Business", 35000m, maxFuncionarios: 7));

            await context.SaveChangesAsync();

            var service = CreateService(
                context,
                new OpcionesPago
                {
                    EnableValidationPlans = true
                },
                new OpcionesTilopay
                {
                    WebhookAccessToken = "token-seguro"
                },
                new TilopayRepeatOptions
                {
                    Enabled = true,
                    UseHostedLinks = true,
                    UseRecurringCheckoutForPublicPlans = true,
                    Basic = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7001,
                        Code = PlanCodes.Basic,
                        MonthlyPrice = 8000m,
                        Currency = "CRC",
                        MaxFuncionarios = 1,
                        CheckoutUrl = "https://tp.cr/l/basic"
                    },
                    Pro = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7002,
                        Code = PlanCodes.Pro,
                        MonthlyPrice = 20000m,
                        Currency = "CRC",
                        MaxFuncionarios = 3,
                        CheckoutUrl = "https://tp.cr/l/pro"
                    },
                    Business = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7003,
                        Code = PlanCodes.Business,
                        MonthlyPrice = 35000m,
                        Currency = "CRC",
                        MaxFuncionarios = 7,
                        CheckoutUrl = "https://tp.cr/l/business"
                    }
                });

            var basePlans = (await service.GetPlanCardsAsync())
                .OrderBy(card => card.MonthlyPrice)
                .ToArray();

            Assert.Equal(3, basePlans.Length);
            Assert.Collection(
                basePlans,
                basic =>
                {
                    Assert.Equal(PlanCodes.Basic, basic.Code);
                    Assert.True(basic.CanStartCheckout);
                    Assert.Null(basic.CheckoutAvailabilityMessage);
                },
                pro =>
                {
                    Assert.Equal(PlanCodes.Pro, pro.Code);
                    Assert.True(pro.CanStartCheckout);
                    Assert.Null(pro.CheckoutAvailabilityMessage);
                },
                business =>
                {
                    Assert.Equal(PlanCodes.Business, business.Code);
                    Assert.True(business.CanStartCheckout);
                    Assert.Null(business.CheckoutAvailabilityMessage);
                });
        }

        [Fact]
        public async Task GetWhatsAppAddonCardsAsync_ShouldDisableOnlyPlanWithMissingCheckoutUrl()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Planes.AddRange(
                CreatePlan(Guid.NewGuid(), PlanCodes.WhatsApp400, "WhatsApp 400", 6000m, monthlyMessageLimit: 400),
                CreatePlan(Guid.NewGuid(), PlanCodes.WhatsApp800, "WhatsApp 800", 12000m, monthlyMessageLimit: 800));

            await context.SaveChangesAsync();

            var service = CreateService(
                context,
                new OpcionesPago
                {
                    EnableValidationPlans = true
                },
                new OpcionesTilopay
                {
                    WebhookAccessToken = "token-seguro"
                },
                new TilopayRepeatOptions
                {
                    Enabled = true,
                    UseHostedLinks = true,
                    WhatsApp400 = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7011,
                        Code = PlanCodes.WhatsApp400,
                        MonthlyPrice = 6000m,
                        MonthlyMessageLimit = 400,
                        CheckoutUrl = "https://tp.cr/l/wa400",
                        IsAddon = true
                    },
                    WhatsApp800 = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7012,
                        Code = PlanCodes.WhatsApp800,
                        MonthlyPrice = 12000m,
                        MonthlyMessageLimit = 800,
                        IsAddon = true
                    }
                });

            var addons = (await service.GetWhatsAppAddonCardsAsync()).OrderBy(card => card.MonthlyPrice).ToArray();

            Assert.Equal(2, addons.Length);
            Assert.True(addons[0].CanStartCheckout);
            Assert.False(addons[1].CanStartCheckout);
            Assert.Equal("Falta CheckoutUrl para WA800: TilopayRepeat:WhatsApp800:CheckoutUrl.", addons[1].CheckoutAvailabilityMessage);
        }

        [Fact]
        public async Task GetWhatsAppAddonCardsAsync_ShouldKeepWa400Wa800AndWa1200ReadyForCheckoutWhenRecurringConfigIsComplete()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Planes.AddRange(
                CreatePlan(Guid.NewGuid(), PlanCodes.WhatsApp400, "WhatsApp 400", 6000m, monthlyMessageLimit: 400),
                CreatePlan(Guid.NewGuid(), PlanCodes.WhatsApp800, "WhatsApp 800", 12000m, monthlyMessageLimit: 800),
                CreatePlan(Guid.NewGuid(), PlanCodes.WhatsApp1200, "WhatsApp 1200", 18000m, monthlyMessageLimit: 1200));

            await context.SaveChangesAsync();

            var service = CreateService(
                context,
                new OpcionesPago
                {
                    EnableValidationPlans = true
                },
                new OpcionesTilopay
                {
                    WebhookAccessToken = "token-seguro"
                },
                new TilopayRepeatOptions
                {
                    Enabled = true,
                    UseHostedLinks = true,
                    WhatsApp400 = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7011,
                        Code = PlanCodes.WhatsApp400,
                        MonthlyPrice = 6000m,
                        Currency = "CRC",
                        MonthlyMessageLimit = 400,
                        CheckoutUrl = "https://tp.cr/l/wa400",
                        IsAddon = true
                    },
                    WhatsApp800 = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7012,
                        Code = PlanCodes.WhatsApp800,
                        MonthlyPrice = 12000m,
                        Currency = "CRC",
                        MonthlyMessageLimit = 800,
                        CheckoutUrl = "https://tp.cr/l/wa800",
                        IsAddon = true
                    },
                    WhatsApp1200 = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7013,
                        Code = PlanCodes.WhatsApp1200,
                        MonthlyPrice = 18000m,
                        Currency = "CRC",
                        MonthlyMessageLimit = 1200,
                        CheckoutUrl = "https://tp.cr/l/wa1200",
                        IsAddon = true
                    }
                });

            var addons = (await service.GetWhatsAppAddonCardsAsync())
                .OrderBy(card => card.MonthlyPrice)
                .ToArray();

            Assert.Equal(3, addons.Length);
            Assert.Collection(
                addons,
                wa400 =>
                {
                    Assert.Equal(PlanCodes.WhatsApp400, wa400.Code);
                    Assert.True(wa400.CanStartCheckout);
                    Assert.Null(wa400.CheckoutAvailabilityMessage);
                },
                wa800 =>
                {
                    Assert.Equal(PlanCodes.WhatsApp800, wa800.Code);
                    Assert.True(wa800.CanStartCheckout);
                    Assert.Null(wa800.CheckoutAvailabilityMessage);
                },
                wa1200 =>
                {
                    Assert.Equal(PlanCodes.WhatsApp1200, wa1200.Code);
                    Assert.True(wa1200.CanStartCheckout);
                    Assert.Null(wa1200.CheckoutAvailabilityMessage);
                });
        }

        [Fact]
        public async Task GetPlanCardsAsync_ShouldReportExactMissingBasicCheckoutKey()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Planes.Add(CreatePlan(Guid.NewGuid(), PlanCodes.Basic, "Basico", 8000m, maxFuncionarios: 1));
            await context.SaveChangesAsync();

            var service = CreateService(
                context,
                new OpcionesPago
                {
                    EnableValidationPlans = true
                },
                new OpcionesTilopay
                {
                    WebhookAccessToken = "token-seguro"
                },
                new TilopayRepeatOptions
                {
                    Enabled = true,
                    UseHostedLinks = true,
                    UseRecurringCheckoutForPublicPlans = true,
                    Basic = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7001,
                        Code = PlanCodes.Basic,
                        MonthlyPrice = 8000m
                    }
                });

            var basic = Assert.Single(await service.GetPlanCardsAsync());

            Assert.False(basic.CanStartCheckout);
            Assert.Equal("Falta CheckoutUrl para BASIC: TilopayRepeat:Basic:CheckoutUrl.", basic.CheckoutAvailabilityMessage);
        }

        [Fact]
        public async Task GetInternalPlanCardsAsync_ShouldReturnTestPlanWhenValidationModeIsEnabled()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Planes.Add(CreatePlan(Guid.NewGuid(), PlanCodes.TestRecurring, "Prueba Tilopay", 1000m, maxFuncionarios: 1, isValidationPlan: true));
            await context.SaveChangesAsync();

            var service = CreateService(
                context,
                new OpcionesPago
                {
                    EnableValidationPlans = true
                },
                new OpcionesTilopay
                {
                    WebhookAccessToken = "token-seguro"
                },
                new TilopayRepeatOptions
                {
                    Enabled = true,
                    UseHostedLinks = true,
                    EnableTestRecurringPlan = true,
                    TestRecurring = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7099,
                        Code = PlanCodes.TestRecurring,
                        MonthlyPrice = 1000m,
                        CheckoutUrl = "https://tp.cr/l/test",
                        IsValidation = true
                    }
                });

            var plans = await service.GetInternalPlanCardsAsync();

            var testPlan = Assert.Single(plans);
            Assert.Equal(PlanCodes.TestRecurring, testPlan.Code);
            Assert.True(testPlan.CanStartCheckout);
        }

        [Fact]
        public async Task GetInternalPlanCardsAsync_ShouldHideTestPlanWhenValidationModeIsDisabled()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Planes.Add(CreatePlan(Guid.NewGuid(), PlanCodes.TestRecurring, "Prueba Tilopay", 1000m, maxFuncionarios: 1, isValidationPlan: true));
            await context.SaveChangesAsync();

            var service = CreateService(
                context,
                new OpcionesPago
                {
                    EnableValidationPlans = false
                },
                new OpcionesTilopay
                {
                    WebhookAccessToken = "token-seguro"
                },
                new TilopayRepeatOptions
                {
                    Enabled = true,
                    UseHostedLinks = true,
                    EnableTestRecurringPlan = true,
                    TestRecurring = new TilopayRepeatPlanOption
                    {
                        TilopayPlanId = 7099,
                        Code = PlanCodes.TestRecurring,
                        MonthlyPrice = 1000m,
                        CheckoutUrl = "https://tp.cr/l/test",
                        IsValidation = true
                    }
                });

            var plans = await service.GetInternalPlanCardsAsync();

            Assert.Empty(plans);
        }

        [Fact]
        public async Task GetPlanCardsAsync_ShouldServeFromCacheWithinTtlWithoutRequeryingDatabase()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Planes.Add(CreatePlan(Guid.NewGuid(), PlanCodes.Basic, "Basico", 8000m, maxFuncionarios: 1));
            await context.SaveChangesAsync();

            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = CreateService(
                context,
                new OpcionesPago { EnableValidationPlans = true },
                new OpcionesTilopay { WebhookAccessToken = "token-seguro" },
                new TilopayRepeatOptions(),
                cache);

            var first = await service.GetPlanCardsAsync();
            Assert.Single(first);

            // Segunda inserción directa en la base: si la consulta se repitiera, aparecería.
            context.Planes.Add(CreatePlan(Guid.NewGuid(), PlanCodes.Pro, "Pro", 20000m, maxFuncionarios: 3));
            await context.SaveChangesAsync();

            var second = await service.GetPlanCardsAsync();

            // Dentro del TTL la respuesta proviene del snapshot cacheado, no de una nueva consulta.
            Assert.Single(second);
            Assert.Equal(PlanCodes.Basic, Assert.Single(second).Code);
        }

        [Fact]
        public async Task GetPlanCardsAsync_ShouldCacheProjectedSnapshotsNotTrackedEntities()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Planes.Add(CreatePlan(Guid.NewGuid(), PlanCodes.Basic, "Basico", 8000m, maxFuncionarios: 1));
            await context.SaveChangesAsync();

            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = CreateService(
                context,
                new OpcionesPago { EnableValidationPlans = true },
                new OpcionesTilopay { WebhookAccessToken = "token-seguro" },
                new TilopayRepeatOptions(),
                cache);

            await service.GetPlanCardsAsync();

            Assert.True(cache.TryGetValue(PublicSiteContentService.AvailablePlansCacheKey, out var cached));
            var snapshots = Assert.IsAssignableFrom<IReadOnlyCollection<PublicPlanSnapshot>>(cached);
            Assert.NotEmpty(snapshots);
        }

        [Fact]
        public async Task GetPlanCardsAsync_ShouldNotCacheAnythingWhenRequestIsCancelled()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Planes.Add(CreatePlan(Guid.NewGuid(), PlanCodes.Basic, "Basico", 8000m, maxFuncionarios: 1));
            await context.SaveChangesAsync();

            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = CreateService(
                context,
                new OpcionesPago { EnableValidationPlans = true },
                new OpcionesTilopay { WebhookAccessToken = "token-seguro" },
                new TilopayRepeatOptions(),
                cache);

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.GetPlanCardsAsync(cancelled.Token));

            // Un visitante que cancela no debe dejar datos (ni vacíos ni parciales) en cache.
            Assert.False(cache.TryGetValue(PublicSiteContentService.AvailablePlansCacheKey, out _));

            // Una carga posterior sana sigue devolviendo los planes reales.
            var plans = await service.GetPlanCardsAsync();
            Assert.Single(plans);
        }

        // ── Preview comercial de la landing (calculador LC_M_/LC_A_) ─────────────────────

        [Fact]
        public async Task GetCommercialPricingPreviewAsync_UsesCalculatorPlans_ExcludesLegacyAndTest()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            // Ruido: legacy + TEST + add-on en la base. NO deben influir en el preview comercial.
            context.Planes.AddRange(
                CreatePlan(Guid.NewGuid(), PlanCodes.Basic, "Básico", 8000m, maxFuncionarios: 1),
                CreatePlan(Guid.NewGuid(), PlanCodes.Pro, "Pro", 20000m, maxFuncionarios: 3),
                CreatePlan(Guid.NewGuid(), PlanCodes.TestProdBasic100, "LuxuryCloud Test Producción", 100m, maxFuncionarios: 1),
                CreatePlan(Guid.NewGuid(), PlanCodes.WhatsApp400, "WhatsApp 400", 6000m, monthlyMessageLimit: 400));
            await context.SaveChangesAsync();

            var service = CreateService(
                context,
                new OpcionesPago { EnableValidationPlans = true },
                new OpcionesTilopay { WebhookAccessToken = "token-seguro" },
                RepeatWithCalculator(
                    CalcOption("LC_M_01", 8000m, 1, BillingCycle.Monthly),
                    CalcOption("LC_M_02", 15000m, 2, BillingCycle.Monthly),
                    CalcOption("LC_M_03", 20000m, 3, BillingCycle.Monthly)));

            var preview = await service.GetCommercialPricingPreviewAsync();

            Assert.True(preview.IsAvailable);
            Assert.Equal(8000m, preview.StartingMonthlyCharge); // LC_M_01, no ₡100 ni ₡6.000
            Assert.Equal(1, preview.MinWorkers);
            Assert.All(preview.Tiers, tier => Assert.InRange(tier.Workers, 1, 3));
            Assert.DoesNotContain(preview.Tiers, tier => tier.ChargeAmount == 100m);
            Assert.DoesNotContain(preview.Tiers, tier => tier.ChargeAmount == 6000m);
        }

        [Fact]
        public async Task GetCommercialPricingPreviewAsync_StartingCharge_IsOneWorkerMonthly_NotCheapestOverall()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            // Un TEST de ₡100 y un add-on de ₡6.000 más baratos que el plan base: no deben ganar.
            context.Planes.AddRange(
                CreatePlan(Guid.NewGuid(), PlanCodes.TestProdBasic100, "LuxuryCloud Test Producción", 100m, maxFuncionarios: 1),
                CreatePlan(Guid.NewGuid(), PlanCodes.WhatsApp400, "WhatsApp 400", 6000m, monthlyMessageLimit: 400));
            await context.SaveChangesAsync();

            var service = CreateService(
                context,
                new OpcionesPago { EnableValidationPlans = true },
                new OpcionesTilopay { WebhookAccessToken = "token-seguro" },
                RepeatWithCalculator(
                    CalcOption("LC_M_01", 8000m, 1, BillingCycle.Monthly),
                    CalcOption("LC_M_02", 15000m, 2, BillingCycle.Monthly)));

            var preview = await service.GetCommercialPricingPreviewAsync();

            Assert.Equal(8000m, preview.StartingMonthlyCharge);
        }

        [Fact]
        public async Task GetCommercialPricingPreviewAsync_WhatsAppFrom_ComesFromAddonCatalog_NotBase()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Planes.AddRange(
                CreatePlan(Guid.NewGuid(), PlanCodes.WhatsApp400, "WhatsApp 400", 6000m, monthlyMessageLimit: 400),
                CreatePlan(Guid.NewGuid(), PlanCodes.WhatsApp800, "WhatsApp 800", 12000m, monthlyMessageLimit: 800));
            await context.SaveChangesAsync();

            var service = CreateService(
                context,
                new OpcionesPago { EnableValidationPlans = true },
                new OpcionesTilopay { WebhookAccessToken = "token-seguro" },
                RepeatWithCalculator(CalcOption("LC_M_01", 8000m, 1, BillingCycle.Monthly)));

            var preview = await service.GetCommercialPricingPreviewAsync();

            Assert.Equal(8000m, preview.StartingMonthlyCharge);
            Assert.Equal(6000m, preview.WhatsAppFromCharge); // el add-on más barato, separado del base
        }

        [Fact]
        public async Task GetCommercialPricingPreviewAsync_UsesAnnualFromCatalog_WithoutDuplicatingFormula()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var service = CreateService(
                context,
                new OpcionesPago { EnableValidationPlans = true },
                new OpcionesTilopay { WebhookAccessToken = "token-seguro" },
                RepeatWithCalculator(
                    CalcOption("LC_M_01", 8000m, 1, BillingCycle.Monthly),
                    CalcOption("LC_A_01", 81600m, 1, BillingCycle.Annual, monthlyEquivalent: 6800m)));

            var preview = await service.GetCommercialPricingPreviewAsync();

            Assert.True(preview.HasAnnual);
            var annual = Assert.Single(preview.Tiers, tier => tier.Cycle == "Annual" && tier.Workers == 1);
            Assert.Equal(81600m, annual.ChargeAmount);          // valor exacto del catálogo
            Assert.Equal(6800m, annual.MonthlyEquivalentAmount); // equivalente del catálogo, no recalculado
        }

        [Fact]
        public async Task GetCommercialPricingPreviewAsync_NoCalculatorPlans_ReturnsUnavailable()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            var service = CreateService(
                context,
                new OpcionesPago { EnableValidationPlans = true },
                new OpcionesTilopay { WebhookAccessToken = "token-seguro" },
                new TilopayRepeatOptions()); // Calculator vacío

            var preview = await service.GetCommercialPricingPreviewAsync();

            Assert.False(preview.IsAvailable);
            Assert.Empty(preview.Tiers);
        }

        [Fact]
        public async Task GetCommercialPricingPreviewAsync_CacheHit_ReturnsSameInstanceWithoutRebuilding()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            context.Planes.Add(CreatePlan(Guid.NewGuid(), PlanCodes.WhatsApp400, "WhatsApp 400", 6000m, monthlyMessageLimit: 400));
            await context.SaveChangesAsync();

            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = CreateService(
                context,
                new OpcionesPago { EnableValidationPlans = true },
                new OpcionesTilopay { WebhookAccessToken = "token-seguro" },
                RepeatWithCalculator(CalcOption("LC_M_01", 8000m, 1, BillingCycle.Monthly)),
                cache);

            var first = await service.GetCommercialPricingPreviewAsync();

            // Un add-on más barato después del primer build: si se reconsultara, cambiaría el "desde".
            context.Planes.Add(CreatePlan(Guid.NewGuid(), PlanCodes.WhatsApp800, "WhatsApp barato", 3000m, monthlyMessageLimit: 800));
            await context.SaveChangesAsync();

            var second = await service.GetCommercialPricingPreviewAsync();

            Assert.Same(first, second); // misma instancia cacheada, no se reconstruyó
            Assert.Equal(6000m, second.WhatsAppFromCharge);
            Assert.IsType<CommercialPricingPreview>(
                cache.Get(PublicSiteContentService.CommercialPricingPreviewCacheKey));
        }

        [Fact]
        public async Task GetCommercialPricingPreviewAsync_DoesNotCacheWhenCancelled()
        {
            var (context, connection) = CreateSystemContext();
            using var disposableContext = context;
            using var disposableConnection = connection;

            using var cache = new MemoryCache(new MemoryCacheOptions());
            var service = CreateService(
                context,
                new OpcionesPago { EnableValidationPlans = true },
                new OpcionesTilopay { WebhookAccessToken = "token-seguro" },
                RepeatWithCalculator(CalcOption("LC_M_01", 8000m, 1, BillingCycle.Monthly)),
                cache);

            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.GetCommercialPricingPreviewAsync(cancelled.Token));

            Assert.False(cache.TryGetValue(PublicSiteContentService.CommercialPricingPreviewCacheKey, out _));

            var preview = await service.GetCommercialPricingPreviewAsync();
            Assert.True(preview.IsAvailable);
        }

        private static TilopayRepeatPlanOption CalcOption(
            string code,
            decimal charge,
            int workers,
            BillingCycle cycle,
            decimal? monthlyEquivalent = null) =>
            new()
            {
                Code = code,
                TilopayPlanId = 6000 + workers + (cycle == BillingCycle.Annual ? 100 : 0),
                BillingCycle = cycle,
                MonthlyPrice = charge,
                MonthlyEquivalentAmount = monthlyEquivalent,
                Currency = "CRC",
                MaxFuncionarios = workers,
                CheckoutUrl = $"https://tp.cr/l/{code}",
                IsPublic = true,
                UsesRecurringCheckout = true
            };

        private static TilopayRepeatOptions RepeatWithCalculator(params TilopayRepeatPlanOption[] options) =>
            new()
            {
                Enabled = true,
                UseHostedLinks = true,
                UseRecurringCheckoutForPublicPlans = true,
                Calculator = options.ToList()
            };

        private static Plan CreatePlan(
            Guid id,
            string code,
            string name,
            decimal monthlyPrice,
            int? maxFuncionarios = null,
            int? monthlyMessageLimit = null,
            bool isValidationPlan = false) =>
            new()
            {
                Id = id,
                Codigo = code,
                Nombre = name,
                PrecioMensual = monthlyPrice,
                Moneda = "CRC",
                MaxFuncionarios = maxFuncionarios,
                LimiteMensajesMensual = monthlyMessageLimit,
                EsPlanValidacion = isValidationPlan,
                Activo = true
            };

        private static PublicSiteContentService CreateService(
            ProyectoIdentity.Datos.ApplicationDbContext context,
            OpcionesPago paymentOptions,
            OpcionesTilopay tilopayOptions,
            TilopayRepeatOptions repeatOptions,
            IMemoryCache? cache = null) =>
            new(
                context,
                cache ?? new MemoryCache(new MemoryCacheOptions()),
                new SubscriptionPricingCatalog(Options.Create(repeatOptions)),
                Options.Create(paymentOptions),
                Options.Create(tilopayOptions),
                Options.Create(repeatOptions));

        private static (ProyectoIdentity.Datos.ApplicationDbContext Context, IDisposable Connection) CreateSystemContext()
        {
            var tenantProvider = new TestTenantProvider();
            return TestDbContextFactory.CreateSqliteContext(tenantProvider);
        }
    }
}
