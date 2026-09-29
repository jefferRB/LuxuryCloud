using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Funcionarios;
using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.BusinessTime;
using LuxuryApp.Services.Funcionarios;
using LuxuryApp.Services.Inversionistas;
using LuxuryApp.Services.Tenant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Tests.Support
{
    /// <summary>
    /// Armado de los servicios del módulo de inversionistas sobre SQLite en memoria, con las
    /// mismas implementaciones reales de fiscal y liquidaciones que usa producción (nada de dobles
    /// para el cálculo del dinero: si el motor fiscal cambia, estos tests lo detectan).
    /// </summary>
    internal static class InvestorTestSupport
    {
        public static LiquidacionSemanalService CreateLiquidacionService(
            ApplicationDbContext context,
            ITenantProvider tenantProvider) =>
            new(
                context,
                ControllerTestSupport.BusinessDateTimeProvider,
                new LuxuryApp.Services.Fiscal.TaxCalculationService(),
                new LuxuryApp.Services.Fiscal.LiquidacionFuncionarioService(),
                new LuxuryApp.Services.Fiscal.TenantFiscalConfigService(context, tenantProvider),
                ControllerTestSupport.CreateSystemCategoryService(context),
                new FakePlatformAuditService(),
                tenantProvider,
                NullLogger<LiquidacionSemanalService>.Instance);

        public static LuxuryApp.Services.Finanzas.PeriodProfitCalculationService CreateCalculationService(
            ApplicationDbContext context,
            ITenantProvider tenantProvider) =>
            new(context, CreateLiquidacionService(context, tenantProvider));

        public static InvestorService CreateInvestorService(
            ApplicationDbContext context,
            FakePlatformAuditService audit,
            IBusinessDateTimeProvider? clock = null) =>
            new(
                context,
                clock ?? ControllerTestSupport.BusinessDateTimeProvider,
                audit,
                NullLogger<InvestorService>.Instance);

        public static InvestorStatementService CreateStatementService(
            ApplicationDbContext context,
            ITenantProvider tenantProvider,
            FakePlatformAuditService audit,
            IInvestorService? investorService = null,
            IBusinessDateTimeProvider? clock = null)
        {
            investorService ??= CreateInvestorService(context, audit, clock);

            return new InvestorStatementService(
                context,
                investorService,
                CreateCalculationService(context, tenantProvider),
                clock ?? ControllerTestSupport.BusinessDateTimeProvider,
                new TenantDisplayNameService(context, tenantProvider, new HttpContextAccessor()),
                audit,
                NullLogger<InvestorStatementService>.Instance);
        }

        /// <summary>
        /// Lectura operacional (último corte emitido, ciclo en curso, saldo). Usa los servicios
        /// reales: el ciclo se calcula con el mismo motor que el estado de cuenta.
        /// </summary>
        public static InvestorCycleService CreateCycleService(
            ApplicationDbContext context,
            ITenantProvider tenantProvider,
            FakePlatformAuditService audit,
            IInvestorService? investorService = null,
            IInvestorStatementService? statementService = null,
            IBusinessDateTimeProvider? clock = null)
        {
            investorService ??= CreateInvestorService(context, audit, clock);
            statementService ??= CreateStatementService(context, tenantProvider, audit, investorService, clock);

            return new InvestorCycleService(
                context,
                investorService,
                statementService,
                CreateCalculationService(context, tenantProvider),
                clock ?? ControllerTestSupport.BusinessDateTimeProvider);
        }

        // ─────────────── Semillas ───────────────

        public static async Task<int> SeedInvestorAsync(
            ApplicationDbContext context,
            string nombre,
            string email,
            decimal porcentaje,
            DateOnly effectiveFrom,
            InvestorPayoutFrequency frecuencia = InvestorPayoutFrequency.Mensual,
            InvestorLossTreatment perdidas = InvestorLossTreatment.NoDistribution,
            DateOnly? effectiveTo = null)
        {
            var investor = new TenantInvestor
            {
                Nombre = nombre,
                Email = email,
                Activo = true
            };

            context.TenantInvestors.Add(investor);
            await context.SaveChangesAsync();

            context.InvestorAgreements.Add(new InvestorAgreement
            {
                InvestorId = investor.Id,
                ParticipacionPorcentaje = porcentaje,
                EffectiveFrom = effectiveFrom,
                EffectiveTo = effectiveTo,
                Frecuencia = frecuencia,
                TratamientoPerdidas = perdidas,
                Activo = true
            });

            await context.SaveChangesAsync();
            return investor.Id;
        }

        public static async Task<Funcionario> SeedFuncionarioAsync(
            ApplicationDbContext context,
            string nombre,
            decimal porcentajeGanancia = 0m,
            decimal porcentajeProducto = 0m,
            bool activo = true)
        {
            var puesto = new Puesto
            {
                NombrePuesto = $"Puesto {nombre}",
                Detalle = "General",
                Activo = true
            };

            context.Puestos.Add(puesto);
            await context.SaveChangesAsync();

            var funcionario = new Funcionario
            {
                Nombre = nombre,
                IdPuesto = puesto.IdPuesto,
                ColorCalendario = "#111111",
                PorcentajeGanancia = porcentajeGanancia,
                PorcentajeProducto = porcentajeProducto,
                ComisionCalculadaSobre = LuxuryApp.Models.Fiscal.ComisionCalculadaSobre.BaseSinIva,
                FechaIngreso = new DateTime(2026, 1, 1),
                Activo = activo
            };

            context.Funcionarios.Add(funcionario);
            await context.SaveChangesAsync();

            return funcionario;
        }

        /// <summary>Cobro de servicio EXENTO de IVA: aísla el cálculo del inversionista del motor fiscal.</summary>
        public static async Task SeedCobroSinIvaAsync(
            ApplicationDbContext context,
            int funcionarioId,
            DateTime fecha,
            decimal monto)
        {
            var servicio = new Servicio
            {
                Nombre = $"Servicio {Guid.NewGuid():N}",
                Precio = monto,
                DuracionMinutos = 60,
                Activo = true,
                AplicaIva = false
            };

            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();

            context.Cobros.Add(new Cobro
            {
                NombreCliente = "Cliente Test",
                FuncionarioId = funcionarioId,
                FechaCobro = fecha,
                Monto = monto,
                MetodoPago = "EFECTIVO",
                ServicioId = servicio.Id
            });

            await context.SaveChangesAsync();
        }

        /// <summary>Cobro de servicio con IVA incluido (13 % por defecto en CR).</summary>
        public static async Task SeedCobroConIvaAsync(
            ApplicationDbContext context,
            int funcionarioId,
            DateTime fecha,
            decimal monto)
        {
            var servicio = new Servicio
            {
                Nombre = $"Servicio {Guid.NewGuid():N}",
                Precio = monto,
                DuracionMinutos = 60,
                Activo = true,
                AplicaIva = true,
                PrecioIncluyeIva = true
            };

            context.Servicios.Add(servicio);
            await context.SaveChangesAsync();

            context.Cobros.Add(new Cobro
            {
                NombreCliente = "Cliente Test",
                FuncionarioId = funcionarioId,
                FechaCobro = fecha,
                Monto = monto,
                MetodoPago = "EFECTIVO",
                ServicioId = servicio.Id
            });

            await context.SaveChangesAsync();
        }

        public static async Task<int> SeedEgresoAsync(
            ApplicationDbContext context,
            string categoriaNombre,
            DateTime fecha,
            decimal monto)
        {
            var categoria = await context.Categorias
                .FirstOrDefaultAsync(current => current.Nombre == categoriaNombre);

            if (categoria is null)
            {
                categoria = new Categoria
                {
                    Nombre = categoriaNombre,
                    Detalle = "Test",
                    Activo = true
                };

                context.Categorias.Add(categoria);
                await context.SaveChangesAsync();
            }

            context.Egresos.Add(new Egreso
            {
                FechaEgreso = fecha,
                Detalle = $"Gasto {categoriaNombre}",
                Monto = monto,
                MetodoPago = "EFECTIVO",
                CategoriaId = categoria.Id
            });

            await context.SaveChangesAsync();
            return categoria.Id;
        }

        /// <summary>Guarda una política de cálculo explícita para el tenant actual.</summary>
        public static async Task SeedPolicyAsync(
            ApplicationDbContext context,
            Action<InvestorProfitPolicy>? configure = null)
        {
            var policy = new InvestorProfitPolicy();
            configure?.Invoke(policy);

            context.InvestorProfitPolicies.Add(policy);
            await context.SaveChangesAsync();
        }
    }
}
