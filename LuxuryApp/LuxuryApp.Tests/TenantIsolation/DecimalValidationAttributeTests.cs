using System.ComponentModel.DataAnnotations;
using System.Globalization;
using LuxuryApp.Models.Finanzas;
using LuxuryApp.Models.Productos;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class DecimalValidationAttributeTests
    {
        [Fact]
        public void MoneyModels_ShouldValidateMinimumAmounts_UnderCostaRicaCulture()
        {
            var originalCulture = CultureInfo.CurrentCulture;
            var originalUiCulture = CultureInfo.CurrentUICulture;
            var costaRicaCulture = CultureInfo.GetCultureInfo("es-CR");

            try
            {
                CultureInfo.CurrentCulture = costaRicaCulture;
                CultureInfo.CurrentUICulture = costaRicaCulture;

                AssertValid(new Cobro
                {
                    FechaCobro = new DateTime(2026, 5, 26, 10, 30, 0),
                    NombreCliente = "Cliente",
                    FuncionarioId = 1,
                    Monto = 0.01m,
                    MetodoPago = "EFECTIVO"
                });

                AssertValid(new Egreso
                {
                    FechaEgreso = new DateTime(2026, 5, 26, 10, 30, 0),
                    Detalle = "Compra",
                    Monto = 0.01m,
                    MetodoPago = "EFECTIVO",
                    CategoriaId = 1
                });

                AssertValid(new Producto
                {
                    NombreProducto = "Shampoo",
                    PrecioProducto = 0.01m,
                    CantidadProducto = 1,
                    StockMinimo = 0
                });

                AssertValid(new Servicio
                {
                    Nombre = "Corte",
                    Precio = 0.01m
                });
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
                CultureInfo.CurrentUICulture = originalUiCulture;
            }
        }

        [Fact]
        public void MoneyModels_ShouldRejectZeroAmounts()
        {
            AssertInvalid(new Cobro
            {
                FechaCobro = new DateTime(2026, 5, 26, 10, 30, 0),
                NombreCliente = "Cliente",
                FuncionarioId = 1,
                Monto = 0m,
                MetodoPago = "EFECTIVO"
            });

            AssertInvalid(new Egreso
            {
                FechaEgreso = new DateTime(2026, 5, 26, 10, 30, 0),
                Detalle = "Compra",
                Monto = 0m,
                MetodoPago = "EFECTIVO",
                CategoriaId = 1
            });

            AssertInvalid(new Producto
            {
                NombreProducto = "Shampoo",
                PrecioProducto = 0m,
                CantidadProducto = 1,
                StockMinimo = 0
            });

            AssertInvalid(new Servicio
            {
                Nombre = "Corte",
                Precio = 0m
            });
        }

        private static void AssertValid(object model)
        {
            var results = new List<ValidationResult>();
            var exception = Record.Exception(() => Validator.TryValidateObject(
                model,
                new ValidationContext(model),
                results,
                validateAllProperties: true));

            Assert.Null(exception);
            Assert.Empty(results);
        }

        private static void AssertInvalid(object model)
        {
            var results = new List<ValidationResult>();
            var exception = Record.Exception(() => Validator.TryValidateObject(
                model,
                new ValidationContext(model),
                results,
                validateAllProperties: true));

            Assert.Null(exception);
            Assert.NotEmpty(results);
        }
    }
}
