using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Models.Platform;
using LuxuryApp.Services.Inversionistas;
using LuxuryApp.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace LuxuryApp.Tests.Inversionistas
{
    /// <summary>
    /// Validaciones de acuerdos: tope del 100 %, solapes, cambios a mitad de periodo y versionado.
    /// </summary>
    public class InvestorServiceTests
    {
        [Fact]
        public async Task Crear_DosInversionistasQueSuman100_EsPermitido()
        {
            using var fixture = await Fixture.CreateAsync();

            await fixture.Service.CreateAsync(BuildForm("Socio A", "a@test.cr", 45m), "admin", default);
            await fixture.Service.CreateAsync(BuildForm("Socio B", "b@test.cr", 55m), "admin", default);

            var index = await fixture.Service.BuildIndexAsync();

            Assert.Equal(100m, index.ParticipacionTotalVigente);
            Assert.Equal(2, index.Inversionistas.Count);
        }

        [Fact]
        public async Task Crear_CuandoSuperaEl100_EsRechazado()
        {
            using var fixture = await Fixture.CreateAsync();

            await fixture.Service.CreateAsync(BuildForm("Socio A", "a@test.cr", 60m), "admin", default);

            var error = await Assert.ThrowsAsync<InvestorValidationException>(() =>
                fixture.Service.CreateAsync(BuildForm("Socio B", "b@test.cr", 45m), "admin", default));

            Assert.Contains("105", error.Message);
            Assert.Contains("40", error.Message);
            Assert.Equal(1, await fixture.Context.TenantInvestors.CountAsync());
        }

        [Fact]
        public async Task Crear_AcuerdosQueNoSeSolapan_NoSumanEntreSi()
        {
            using var fixture = await Fixture.CreateAsync();

            var primero = BuildForm("Socio A", "a@test.cr", 60m);
            primero.EffectiveFrom = new DateTime(2026, 1, 1);
            primero.EffectiveTo = new DateTime(2026, 6, 30);
            await fixture.Service.CreateAsync(primero, "admin", default);

            // Arranca justo cuando termina el anterior: no hay solape, así que 60 + 60 es válido.
            var segundo = BuildForm("Socio B", "b@test.cr", 60m);
            segundo.EffectiveFrom = new DateTime(2026, 7, 1);
            await fixture.Service.CreateAsync(segundo, "admin", default);

            Assert.Equal(2, await fixture.Context.TenantInvestors.CountAsync());
        }

        [Fact]
        public async Task Crear_AcuerdosQueSeSolapanYSuperan100_EsRechazado()
        {
            using var fixture = await Fixture.CreateAsync();

            var primero = BuildForm("Socio A", "a@test.cr", 60m);
            primero.EffectiveFrom = new DateTime(2026, 1, 1);
            primero.EffectiveTo = new DateTime(2026, 8, 31);
            await fixture.Service.CreateAsync(primero, "admin", default);

            var segundo = BuildForm("Socio B", "b@test.cr", 50m);
            segundo.EffectiveFrom = new DateTime(2026, 7, 1);

            await Assert.ThrowsAsync<InvestorValidationException>(() =>
                fixture.Service.CreateAsync(segundo, "admin", default));
        }

        [Fact]
        public async Task Crear_ConFechaAMitadDePeriodo_EsRechazadoConMensajeClaro()
        {
            using var fixture = await Fixture.CreateAsync();

            var form = BuildForm("Socio", "socio@test.cr", 45m);
            form.EffectiveFrom = new DateTime(2026, 7, 15);

            var error = await Assert.ThrowsAsync<InvestorValidationException>(() =>
                fixture.Service.CreateAsync(form, "admin", default));

            Assert.Contains("inicio de un periodo", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("01/08/2026", error.Message);
        }

        [Fact]
        public async Task Actualizar_CambioDePorcentaje_CierraElAcuerdoAnteriorYCreaUnoNuevo()
        {
            using var fixture = await Fixture.CreateAsync();

            var form = BuildForm("Socio", "socio@test.cr", 45m);
            form.EffectiveFrom = new DateTime(2026, 1, 1);
            var investorId = await fixture.Service.CreateAsync(form, "admin", default);

            // El proveedor de fecha fijo del test está en mayo 2026: el próximo periodo mensual
            // válido es junio.
            var cambio = BuildForm("Socio", "socio@test.cr", 30m);
            cambio.Id = investorId;
            cambio.EffectiveFrom = new DateTime(2026, 6, 1);

            await fixture.Service.UpdateAsync(investorId, cambio, "admin", default);

            var acuerdos = await fixture.Context.InvestorAgreements
                .OrderBy(agreement => agreement.EffectiveFrom)
                .ToListAsync();

            Assert.Equal(2, acuerdos.Count);
            Assert.Equal(45m, acuerdos[0].ParticipacionPorcentaje);
            Assert.Equal(new DateOnly(2026, 5, 31), acuerdos[0].EffectiveTo);
            Assert.Equal(30m, acuerdos[1].ParticipacionPorcentaje);
            Assert.Equal(new DateOnly(2026, 6, 1), acuerdos[1].EffectiveFrom);
            Assert.True(fixture.Audit.Contains(PlatformAuditActions.InvestorAgreementChanged));
        }

        [Fact]
        public async Task Actualizar_ConFechaRetroactiva_EsRechazado()
        {
            using var fixture = await Fixture.CreateAsync();

            var form = BuildForm("Socio", "socio@test.cr", 45m);
            form.EffectiveFrom = new DateTime(2026, 1, 1);
            var investorId = await fixture.Service.CreateAsync(form, "admin", default);

            var cambio = BuildForm("Socio", "socio@test.cr", 30m);
            cambio.Id = investorId;
            cambio.EffectiveFrom = new DateTime(2026, 2, 1);

            var error = await Assert.ThrowsAsync<InvestorValidationException>(() =>
                fixture.Service.UpdateAsync(investorId, cambio, "admin", default));

            Assert.Contains("retroactiv", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Crear_ConCorreoDuplicado_EsRechazado()
        {
            using var fixture = await Fixture.CreateAsync();

            await fixture.Service.CreateAsync(BuildForm("Socio A", "socio@test.cr", 20m), "admin", default);

            await Assert.ThrowsAsync<InvestorValidationException>(() =>
                fixture.Service.CreateAsync(BuildForm("Socio B", "SOCIO@test.cr", 20m), "admin", default));
        }

        [Fact]
        public async Task GetAgreementForDate_DevuelveLaVersionVigenteEnEsaFecha()
        {
            using var fixture = await Fixture.CreateAsync();

            var investorId = await InvestorTestSupport.SeedInvestorAsync(
                fixture.Context,
                "Socio",
                "socio@test.cr",
                45m,
                new DateOnly(2026, 1, 1),
                effectiveTo: new DateOnly(2026, 5, 31));

            fixture.Context.InvestorAgreements.Add(new InvestorAgreement
            {
                InvestorId = investorId,
                ParticipacionPorcentaje = 30m,
                EffectiveFrom = new DateOnly(2026, 6, 1),
                Frecuencia = InvestorPayoutFrequency.Mensual,
                Activo = true
            });
            await fixture.Context.SaveChangesAsync();

            var abril = await fixture.Service.GetAgreementForDateAsync(investorId, new DateOnly(2026, 4, 1));
            var julio = await fixture.Service.GetAgreementForDateAsync(investorId, new DateOnly(2026, 7, 1));

            Assert.Equal(45m, abril!.ParticipacionPorcentaje);
            Assert.Equal(30m, julio!.ParticipacionPorcentaje);
        }

        [Fact]
        public async Task Inversionistas_NoSeVenEntreTenants()
        {
            using var fixture = await Fixture.CreateAsync();

            await fixture.Service.CreateAsync(BuildForm("Socio A", "a@test.cr", 45m), "admin", default);

            fixture.TenantProvider.TenantId = Guid.NewGuid();

            var otroIndex = await fixture.Service.BuildIndexAsync();

            Assert.Empty(otroIndex.Inversionistas);
            Assert.Equal(0m, otroIndex.ParticipacionTotalVigente);
        }

        [Fact]
        public async Task Politica_SeGuardaYSeLeeConSusCategorias()
        {
            using var fixture = await Fixture.CreateAsync();

            var categoriaId = await InvestorTestSupport.SeedEgresoAsync(
                fixture.Context, "Alquiler", new DateTime(2026, 6, 1), 1_000m);

            await fixture.Service.SavePolicyAsync(
                new InvestorPolicyViewModel
                {
                    ExcluirIva = true,
                    IncluirLiquidaciones = true,
                    ModoCategoriasGasto = InvestorExpenseCategoryMode.SoloSeleccionadas,
                    CategoriasSeleccionadas = new List<int> { categoriaId },
                    FrecuenciaPorDefecto = InvestorPayoutFrequency.Quincenal
                },
                "admin",
                default);

            var policy = await fixture.Service.GetPolicyAsync();

            Assert.Equal(InvestorExpenseCategoryMode.SoloSeleccionadas, policy.ModoCategoriasGasto);
            Assert.Equal(InvestorPayoutFrequency.Quincenal, policy.FrecuenciaPorDefecto);
            Assert.Single(policy.CategoriasSeleccionadas);
            Assert.True(fixture.Audit.Contains(PlatformAuditActions.InvestorPolicyUpdated));
        }

        private static InvestorFormViewModel BuildForm(string nombre, string email, decimal porcentaje) =>
            new()
            {
                Nombre = nombre,
                Email = email,
                ParticipacionPorcentaje = porcentaje,
                // Primer día del mes en curso según el reloj fijo del test (2026-05-26): es inicio
                // de periodo mensual válido Y cubre "hoy", que es lo que mira la vigencia.
                EffectiveFrom = new DateTime(2026, 5, 1),
                Frecuencia = InvestorPayoutFrequency.Mensual,
                TratamientoPerdidas = InvestorLossTreatment.NoDistribution,
                Activo = true
            };

        private sealed class Fixture : IDisposable
        {
            public required ProyectoIdentity.Datos.ApplicationDbContext Context { get; init; }

            public required Microsoft.Data.Sqlite.SqliteConnection Connection { get; init; }

            public required TestTenantProvider TenantProvider { get; init; }

            public required FakePlatformAuditService Audit { get; init; }

            public required InvestorService Service { get; init; }

            public static Task<Fixture> CreateAsync()
            {
                var tenantProvider = new TestTenantProvider { TenantId = Guid.NewGuid() };
                var (context, connection) = TestDbContextFactory.CreateSqliteContext(tenantProvider);
                var audit = new FakePlatformAuditService();

                return Task.FromResult(new Fixture
                {
                    Context = context,
                    Connection = connection,
                    TenantProvider = tenantProvider,
                    Audit = audit,
                    Service = InvestorTestSupport.CreateInvestorService(context, audit)
                });
            }

            public void Dispose()
            {
                Context.Dispose();
                Connection.Dispose();
            }
        }
    }
}
