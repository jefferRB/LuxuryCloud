using System.Text.Encodings.Web;
using LuxuryApp.Models.Reports;
using LuxuryApp.Services.Reports;

namespace LuxuryApp.Tests.TenantIsolation
{
    public class MonthlyReportEmailRendererTests
    {
        private static readonly MonthlyReportEmailRenderer Renderer = new();

        // Los valores dinámicos van HTML-encoded (los no ASCII quedan como &#x...;).
        private static string Encoded(string value) => HtmlEncoder.Default.Encode(value);

        private static MonthlyBusinessReportViewModel BuildFullReport() => new()
        {
            TenantId = Guid.NewGuid(),
            NombreNegocio = "Barbería Luxury",
            Mes = 6,
            Anio = 2026,
            MesNombre = "Junio",
            FechaGeneracion = new DateTime(2026, 7, 1, 8, 0, 0),
            TieneActividad = true,
            Ingresos = 500000m,
            Egresos = 120000m,
            GananciaReal = 322477.88m,
            MargenGanancia = 64.5m,
            Impuestos = 57522.12m,
            PagoFuncionarios = 90000m,
            TotalSinImpuestos = 442477.88m,
            ServiciosGeneradosMonto = 400000m,
            ProductosGeneradosMonto = 100000m,
            IngresosEfectivo = 200000m,
            IngresosSinpe = 150000m,
            IngresosTarjeta = 150000m,
            ServiciosRealizados = 80,
            ProductosVendidos = 15,
            CitasOnlineReservadas = 12,
            ServicioMasSolicitadoNombre = "Corte clásico",
            ServicioMasSolicitadoCantidad = 30,
            ProductoMasVendidoNombre = "Cera premium",
            ProductoMasVendidoCantidad = 8,
            FuncionarioEstrellaNombre = "Ana",
            FuncionarioEstrellaCantidadCitas = 40,
            DiaMasOcupado = "sábado",
            DiaMasOcupadoCantidad = 120,
            DiaMenosOcupado = "martes",
            DiaMenosOcupadoCantidad = 20,
            HoraMasOcupada = "15:00",
            HoraMasOcupadaCantidad = 45,
            HoraMenosOcupada = "9:00",
            HoraMenosOcupadaCantidad = 5,
            ResumenEjecutivoTexto = "Este mes tu negocio generó ₡500 000,00 de ingresos.",
            ComentarioMargen = "Tu negocio tuvo un margen saludable este mes.",
            ComentarioActividad = "Se realizaron 80 servicios.",
            ComentarioFuncionarioEstrella = "Felicidades, el colaborador estrella del mes fue Ana con 40 citas realizadas.",
            ComentarioOportunidad = "Tu día con menor movimiento es martes."
        };

        [Fact]
        public void RenderHtml_WithFullData_ShowsBusinessPeriodAmountsAndButton()
        {
            var html = Renderer.RenderHtml(BuildFullReport(), "https://app.luxurycloud.test/Dashboard");

            Assert.Contains(Encoded("Barbería Luxury"), html);
            Assert.Contains("Junio 2026", html);
            Assert.Contains(Encoded("₡"), html);
            Assert.Contains(Encoded("Corte clásico"), html);
            Assert.Contains("Cera premium", html);
            Assert.Contains("Ana", html);
            Assert.Contains("Ver dashboard completo", html);
            Assert.Contains("https://app.luxurycloud.test/Dashboard", html);
            Assert.Contains("Recibís este correo porque sos administrador del negocio.", html);

            // Compatible con clientes de correo: sin JavaScript ni CSS externo.
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RenderHtml_MissingTopPerformers_ShowsSinDatosSuficientes()
        {
            var report = BuildFullReport();
            report.ServicioMasSolicitadoNombre = string.Empty;
            report.ProductoMasVendidoNombre = string.Empty;
            report.FuncionarioEstrellaNombre = string.Empty;
            report.ComentarioFuncionarioEstrella = string.Empty;

            var html = Renderer.RenderHtml(report, dashboardUrl: null);

            Assert.Contains("Sin datos suficientes", html);
            Assert.DoesNotContain("Cera premium", html);
        }

        [Fact]
        public void RenderHtml_EncodesUntrustedValues()
        {
            var report = BuildFullReport();
            report.NombreNegocio = "<script>alert('x')</script>";
            report.ServicioMasSolicitadoNombre = "Corte <b>& spa</b>";

            var html = Renderer.RenderHtml(report, dashboardUrl: null);

            Assert.DoesNotContain("<script>alert", html);
            Assert.DoesNotContain("<b>& spa</b>", html);
        }

        [Fact]
        public void RenderHtml_WithoutDashboardUrl_HidesButton()
        {
            var html = Renderer.RenderHtml(BuildFullReport(), dashboardUrl: null);

            Assert.DoesNotContain("Ver dashboard completo", html);
        }

        [Fact]
        public void RenderHtml_DisabledSections_AreOmitted()
        {
            var report = BuildFullReport();
            report.IncluirDatosFinancieros = false;
            report.IncluirRecomendaciones = false;

            var html = Renderer.RenderHtml(report, dashboardUrl: null);

            Assert.DoesNotContain("Finanzas del mes", html);
            Assert.DoesNotContain("Lectura del mes", html);
            Assert.Contains("Operación del mes", html);
        }

        [Fact]
        public void RenderHtml_WithComparison_ShowsComparisonSection()
        {
            var report = BuildFullReport();
            report.IncluirComparativa = true;
            report.TieneComparativa = true;
            report.MesAnteriorNombre = "Mayo";
            report.IngresosMesAnterior = 400000m;
            report.VariacionIngresosPorcentaje = 25m;

            var html = Renderer.RenderHtml(report, dashboardUrl: null);

            Assert.Contains("Comparación contra", html);
            Assert.Contains("Mayo", html);
            Assert.Contains("25", html); // variación
        }

        [Fact]
        public void RenderHtml_WithoutPreviousData_DoesNotRenderComparisonTable()
        {
            var report = BuildFullReport();
            report.IncluirComparativa = true;
            report.TieneComparativa = false; // sin datos del mes anterior

            var html = Renderer.RenderHtml(report, dashboardUrl: null);

            Assert.DoesNotContain("Comparación contra", html);
        }

        [Fact]
        public void RenderHtml_DoesNotLeakOtherReportData()
        {
            // El renderer solo usa el reporte que recibe: no hay forma de filtrar otro tenant.
            var report = BuildFullReport();
            report.NombreNegocio = "Salón Alfa";

            var html = Renderer.RenderHtml(report, dashboardUrl: null);

            Assert.Contains(Encoded("Salón Alfa"), html);
            Assert.DoesNotContain("Salón Beta", html);
        }

        [Fact]
        public void RenderText_ContainsSummarySectionsAndFooter()
        {
            var text = Renderer.RenderText(BuildFullReport(), "https://app.luxurycloud.test/Dashboard");

            Assert.Contains("Resumen Ejecutivo Mensual - Barbería Luxury", text);
            // La sección financiera pasó a separar explícitamente el RESULTADO (devengado) de la
            // CAJA, para que "Ganancia del mes" signifique lo mismo que en el Dashboard.
            Assert.Contains("RESULTADO DEL MES", text);
            Assert.Contains("Ganancia del mes", text);
            Assert.Contains("Costos y gastos del mes", text);
            Assert.Contains("CAJA", text);
            Assert.Contains("Salidas de caja registradas", text);
            Assert.Contains("OPERACIÓN DEL MES", text);
            Assert.Contains("₡", text);
            Assert.Contains("https://app.luxurycloud.test/Dashboard", text);
            Assert.Contains("Este correo fue generado automáticamente por LuxuryCloud.", text);
        }
    }
}
