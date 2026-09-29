using LuxuryApp.Models.Fiscal;
using LuxuryApp.Services.Fiscal;
using Xunit;

namespace LuxuryApp.Tests.Fiscal
{
    /// <summary>
    /// Verifica el motor fiscal con los ejemplos reales del negocio (CR, IVA 13% incluido).
    /// </summary>
    public class TaxCalculationServiceTests
    {
        private readonly TaxCalculationService _sut = new();

        [Theory]
        // total (IVA incluido) → base sin IVA, IVA incluido
        [InlineData(10000, 8849.56, 1150.44)]
        [InlineData(36000, 31858.41, 4141.59)]
        [InlineData(95000, 84070.80, 10929.20)]
        [InlineData(209000, 184955.75, 24044.25)]
        public void Calcular_PrecioIncluyeIva_SeparaBaseEIva(decimal total, decimal baseEsperada, decimal ivaEsperado)
        {
            var r = _sut.Calcular(total, 13m, priceIncludesTax: true, taxable: true);

            Assert.Equal(total, r.GrossTotal);
            Assert.Equal(baseEsperada, r.NetBase);
            Assert.Equal(ivaEsperado, r.TaxAmount);
            // Invariante: el total siempre es base + IVA exacto.
            Assert.Equal(r.GrossTotal, r.NetBase + r.TaxAmount);
        }

        [Fact]
        public void Calcular_IvaExcluido_SumaIvaEncima()
        {
            var r = _sut.Calcular(1000m, 13m, priceIncludesTax: false, taxable: true);

            Assert.Equal(1000m, r.NetBase);
            Assert.Equal(130m, r.TaxAmount);
            Assert.Equal(1130m, r.GrossTotal);
        }

        [Fact]
        public void Calcular_NoSujeto_TodoEsBaseSinIva()
        {
            var r = _sut.Calcular(10000m, 13m, priceIncludesTax: true, taxable: false);

            Assert.Equal(10000m, r.GrossTotal);
            Assert.Equal(10000m, r.NetBase);
            Assert.Equal(0m, r.TaxAmount);
        }

        [Fact]
        public void Calcular_TarifaCero_NoAplicaIva()
        {
            var r = _sut.Calcular(10000m, 0m, priceIncludesTax: true, taxable: true);

            Assert.Equal(10000m, r.NetBase);
            Assert.Equal(0m, r.TaxAmount);
        }

        [Fact]
        public void Sumar_PorLineas_AgregaBaseEIva()
        {
            var lineas = new[]
            {
                new TaxLineInput { TotalOrBase = 10000m, TaxRatePercent = 13m, PriceIncludesTax = true, Taxable = true },
                new TaxLineInput { TotalOrBase = 10000m, TaxRatePercent = 13m, PriceIncludesTax = true, Taxable = true }
            };

            var r = _sut.Sumar(lineas);

            Assert.Equal(20000m, r.GrossTotal);
            Assert.Equal(17699.12m, r.NetBase);  // 8849.56 * 2
            Assert.Equal(2300.88m, r.TaxAmount);  // 1150.44 * 2
        }

        [Fact]
        public void Sumar_MezclaSujetoYExento_SoloGravaLoSujeto()
        {
            var lineas = new[]
            {
                new TaxLineInput { TotalOrBase = 10000m, TaxRatePercent = 13m, PriceIncludesTax = true, Taxable = true },
                new TaxLineInput { TotalOrBase = 5000m, TaxRatePercent = 13m, PriceIncludesTax = true, Taxable = false }
            };

            var r = _sut.Sumar(lineas);

            Assert.Equal(15000m, r.GrossTotal);
            Assert.Equal(13849.56m, r.NetBase);   // 8849.56 + 5000 (exento)
            Assert.Equal(1150.44m, r.TaxAmount);
        }
    }
}
