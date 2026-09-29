using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.Comprobantes;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace LuxuryApp.Services.Inversionistas
{
    /// <summary>
    /// PDF del estado de participación con QuestPDF (licencia Community), mismo lenguaje visual
    /// neutro/premium que <c>ComprobantePdfService</c>. Incluye la leyenda de que es un resumen
    /// financiero interno y no un comprobante fiscal.
    /// </summary>
    public sealed class InvestorStatementPdfService : IInvestorStatementPdfService
    {
        private const string ColorTinta = "#111111";
        private const string ColorGris = "#666666";
        private const string ColorGrisClaro = "#EEEEEE";
        private const string ColorFondoSuave = "#F7F7F7";
        private const string ColorNegativo = "#B42318";

        public const string LeyendaInterna =
            "Este documento es un resumen financiero interno del negocio para efectos de la participación acordada. " +
            "No constituye un comprobante fiscal ni un documento tributario.";

        public byte[] Generar(InvestorStatementDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);

            var pdf = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(36);
                    // Sin FontFamily explícita: usa la fuente embebida de QuestPDF, así el PDF se ve
                    // igual en Windows y en el servidor Linux de producción.
                    page.DefaultTextStyle(style => style.FontSize(10).FontColor(ColorTinta));

                    page.Header().Element(element => ComponerEncabezado(element, document));
                    page.Content().Element(element => ComponerContenido(element, document));
                    page.Footer().Element(ComponerPie);
                });
            });

            return pdf.GeneratePdf();
        }

        private static void ComponerEncabezado(IContainer container, InvestorStatementDocument document)
        {
            container.PaddingBottom(12).Column(column =>
            {
                column.Item().Row(row =>
                {
                    row.RelativeItem().Column(negocio =>
                    {
                        negocio.Item().Text(document.NombreNegocio).FontSize(18).Bold().FontColor(ColorTinta);

                        if (!string.IsNullOrWhiteSpace(document.DireccionNegocio))
                        {
                            negocio.Item().Text(document.DireccionNegocio!).FontSize(9).FontColor(ColorGris);
                        }

                        if (!string.IsNullOrWhiteSpace(document.TelefonoNegocio))
                        {
                            negocio.Item().Text($"Tel: {document.TelefonoNegocio}").FontSize(9).FontColor(ColorGris);
                        }

                        if (!string.IsNullOrWhiteSpace(document.EmailNegocio))
                        {
                            negocio.Item().Text(document.EmailNegocio!).FontSize(9).FontColor(ColorGris);
                        }
                    });

                    row.ConstantItem(200).AlignRight().Column(doc =>
                    {
                        doc.Item().Text("ESTADO DE PARTICIPACIÓN").FontSize(12).Bold().FontColor(ColorTinta);
                        doc.Item().Text("Resumen interno · No fiscal").FontSize(8).FontColor(ColorGris);
                        doc.Item().PaddingTop(6).Text(document.PeriodoEtiqueta).FontSize(11).Bold();
                        doc.Item().Text($"Emitido: {ComprobanteTextos.FechaCorta(document.FechaEmision)}")
                            .FontSize(9).FontColor(ColorGris);
                    });
                });

                column.Item().PaddingTop(8).LineHorizontal(1).LineColor(ColorGrisClaro);
            });
        }

        private static void ComponerContenido(IContainer container, InvestorStatementDocument document)
        {
            container.Column(column =>
            {
                column.Item().PaddingTop(4).Row(row =>
                {
                    row.RelativeItem().Background(ColorFondoSuave).Padding(10).Column(inversionista =>
                    {
                        inversionista.Item().Text("Inversionista").FontSize(8).Bold().FontColor(ColorGris);
                        inversionista.Item().Text(document.InversionistaNombre).FontSize(11).Bold();
                        inversionista.Item().Text(document.InversionistaEmail).FontSize(9).FontColor(ColorGris);
                    });

                    row.ConstantItem(12);

                    row.RelativeItem().Background(ColorFondoSuave).Padding(10).Column(periodo =>
                    {
                        periodo.Item().Text("Periodo").FontSize(8).Bold().FontColor(ColorGris);
                        periodo.Item().Text(document.PeriodoEtiqueta).FontSize(11).Bold();
                        periodo.Item()
                            .Text($"{document.PeriodoInicio:dd/MM/yyyy} al {document.PeriodoFin:dd/MM/yyyy}")
                            .FontSize(9).FontColor(ColorGris);
                    });
                });

                // Desglose completo: nunca se muestra solo el número final.
                column.Item().PaddingTop(18).Text("Cómo se calculó la ganancia distribuible")
                    .FontSize(11).Bold();

                column.Item().PaddingTop(8).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.RelativeColumn(6);
                        columns.RelativeColumn(3);
                    });

                    table.Header(header =>
                    {
                        header.Cell().Element(CeldaEncabezado).Text("Concepto");
                        header.Cell().Element(CeldaEncabezado).AlignRight().Text("Monto");
                    });

                    FilaTabla(table, "Ingresos cobrados (sin IVA)", document.IngresosNetos);

                    if (document.IvaExcluido > 0m)
                    {
                        FilaTabla(table, "IVA excluido del cálculo", document.IvaExcluido, informativa: true);
                    }

                    FilaTabla(table, "Gastos operativos", -document.GastosElegibles);
                    FilaTabla(table, "Liquidaciones del equipo", -document.Liquidaciones);

                    if (document.AjustesPositivos > 0m)
                    {
                        FilaTabla(table, "Ajustes a favor", document.AjustesPositivos);
                    }

                    if (document.AjustesNegativos > 0m)
                    {
                        FilaTabla(table, "Ajustes en contra", -document.AjustesNegativos);
                    }

                    if (document.PerdidaArrastrada > 0m)
                    {
                        FilaTabla(table, "Pérdida de periodos anteriores", -document.PerdidaArrastrada);
                    }
                });

                column.Item().PaddingTop(12).Row(row =>
                {
                    row.RelativeItem();
                    row.ConstantItem(260).Column(totales =>
                    {
                        FilaTotal(totales, "Ganancia distribuible", ComprobanteTextos.Colones(document.GananciaDistribuible), false);
                        FilaTotal(totales, "Participación acordada", $"{document.ParticipacionPorcentaje:0.##} %", false);

                        totales.Item().PaddingTop(4).LineHorizontal(1).LineColor(ColorGrisClaro);
                        FilaTotal(totales, "PARTICIPACIÓN", ComprobanteTextos.Colones(document.ParticipacionCalculada), true);

                        totales.Item().PaddingTop(6);
                        FilaTotal(totales, "Total pagado", ComprobanteTextos.Colones(document.TotalPagado), false);
                        FilaTotal(totales, "Saldo pendiente", ComprobanteTextos.Colones(document.SaldoPendiente), false);
                    });
                });

                if (document.PerdidaPendiente > 0m)
                {
                    column.Item().PaddingTop(14).Background(ColorFondoSuave).Padding(10).Column(aviso =>
                    {
                        aviso.Item().Text("Pérdida pendiente").FontSize(9).Bold().FontColor(ColorNegativo);
                        aviso.Item().Text(
                                $"Queda una pérdida pendiente de {ComprobanteTextos.Colones(document.PerdidaPendiente)} " +
                                "que se descontará de las ganancias de los próximos periodos.")
                            .FontSize(9).FontColor(ColorGris);
                    });
                }

                column.Item().PaddingTop(14).Text($"Estado del pago: {document.EstadoPagoTexto}")
                    .FontSize(10).Bold();
            });
        }

        private static void ComponerPie(IContainer container)
        {
            container.PaddingTop(10).Column(column =>
            {
                column.Item().LineHorizontal(1).LineColor(ColorGrisClaro);
                column.Item().PaddingTop(6).Text(LeyendaInterna).FontSize(7.5f).Italic().FontColor(ColorGris);
            });
        }

        private static void FilaTabla(TableDescriptor table, string concepto, decimal monto, bool informativa = false)
        {
            table.Cell().Element(Celda).Text(concepto).FontColor(informativa ? ColorGris : ColorTinta);
            table.Cell().Element(Celda).AlignRight()
                .Text(ComprobanteTextos.Colones(monto))
                .FontColor(monto < 0m ? ColorNegativo : (informativa ? ColorGris : ColorTinta));
        }

        private static void FilaTotal(ColumnDescriptor column, string etiqueta, string valor, bool fuerte)
        {
            column.Item().PaddingVertical(2).Row(row =>
            {
                row.RelativeItem().Text(etiqueta)
                    .FontSize(fuerte ? 12 : 9).Bold()
                    .FontColor(fuerte ? ColorTinta : ColorGris);
                row.ConstantItem(130).AlignRight().Text(valor).FontSize(fuerte ? 12 : 9).Bold();
            });
        }

        private static IContainer CeldaEncabezado(IContainer container) =>
            container.Background(ColorTinta).PaddingVertical(6).PaddingHorizontal(6)
                .DefaultTextStyle(style => style.FontColor(Colors.White).Bold().FontSize(9));

        private static IContainer Celda(IContainer container) =>
            container.BorderBottom(1).BorderColor(ColorGrisClaro).PaddingVertical(6).PaddingHorizontal(6);
    }
}
