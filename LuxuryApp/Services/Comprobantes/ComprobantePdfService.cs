using LuxuryApp.Models.Comprobantes;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace LuxuryApp.Services.Comprobantes
{
    /// <summary>
    /// PDF profesional tipo factura usando QuestPDF (licencia Community).
    /// El diseño es neutro/elegante y no depende del tema del tenant (un PDF se ve igual
    /// en cualquier cliente). La leyenda legal de no validez fiscal es obligatoria.
    /// </summary>
    public sealed class ComprobantePdfService : IComprobantePdfService
    {
        // Paleta neutra premium (negro/gris/blanco) consistente con los correos del sistema.
        private const string ColorTinta = "#111111";
        private const string ColorGris = "#666666";
        private const string ColorGrisClaro = "#EEEEEE";
        private const string ColorFondoSuave = "#F7F7F7";

        public byte[] Generar(ComprobanteCobro c)
        {
            ArgumentNullException.ThrowIfNull(c);

            var document = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(36);
                    // Sin FontFamily explícita: usa la fuente Lato embebida en QuestPDF, así el
                    // PDF se ve igual en Windows y en el servidor Linux (no depende de fuentes del SO).
                    page.DefaultTextStyle(x => x.FontSize(10).FontColor(ColorTinta));

                    page.Header().Element(e => ComponerEncabezado(e, c));
                    page.Content().Element(e => ComponerContenido(e, c));
                    page.Footer().Element(e => ComponerPie(e, c));
                });
            });

            return document.GeneratePdf();
        }

        private static void ComponerEncabezado(IContainer container, ComprobanteCobro c)
        {
            container.PaddingBottom(12).Column(col =>
            {
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(neg =>
                    {
                        neg.Item().Text(c.NombreNegocioSnapshot).FontSize(18).Bold().FontColor(ColorTinta);

                        if (!string.IsNullOrWhiteSpace(c.CedulaNegocioSnapshot))
                            neg.Item().Text($"Cédula: {c.CedulaNegocioSnapshot}").FontSize(9).FontColor(ColorGris);
                        if (!string.IsNullOrWhiteSpace(c.DireccionNegocioSnapshot))
                            neg.Item().Text(c.DireccionNegocioSnapshot!).FontSize(9).FontColor(ColorGris);
                        if (!string.IsNullOrWhiteSpace(c.TelefonoNegocioSnapshot))
                            neg.Item().Text($"Tel: {c.TelefonoNegocioSnapshot}").FontSize(9).FontColor(ColorGris);
                        if (!string.IsNullOrWhiteSpace(c.EmailNegocioSnapshot))
                            neg.Item().Text(c.EmailNegocioSnapshot!).FontSize(9).FontColor(ColorGris);
                    });

                    row.ConstantItem(190).AlignRight().Column(doc =>
                    {
                        doc.Item().Text("COMPROBANTE INTERNO").FontSize(12).Bold().FontColor(ColorTinta);
                        doc.Item().Text("No fiscal").FontSize(8).FontColor(ColorGris);
                        doc.Item().PaddingTop(6).Text(c.NumeroInterno).FontSize(11).Bold();
                        doc.Item().Text($"Emitido: {ComprobanteTextos.Fecha(c.FechaEmision)}").FontSize(9).FontColor(ColorGris);
                    });
                });

                col.Item().PaddingTop(8).LineHorizontal(1).LineColor(ColorGrisClaro);
            });
        }

        private static void ComponerContenido(IContainer container, ComprobanteCobro c)
        {
            container.Column(col =>
            {
                // Datos del cliente y método de pago
                col.Item().PaddingTop(4).Row(row =>
                {
                    row.RelativeItem().Background(ColorFondoSuave).Padding(10).Column(cli =>
                    {
                        cli.Item().Text("Cliente").FontSize(8).Bold().FontColor(ColorGris);
                        cli.Item().Text(c.NombreClienteSnapshot).FontSize(11).Bold();
                        if (!string.IsNullOrWhiteSpace(c.EmailDestino))
                            cli.Item().Text(c.EmailDestino).FontSize(9).FontColor(ColorGris);
                        if (!string.IsNullOrWhiteSpace(c.TelefonoClienteSnapshot))
                            cli.Item().Text(c.TelefonoClienteSnapshot!).FontSize(9).FontColor(ColorGris);
                    });

                    row.ConstantItem(12);

                    row.RelativeItem().Background(ColorFondoSuave).Padding(10).Column(pago =>
                    {
                        pago.Item().Text("Método de pago").FontSize(8).Bold().FontColor(ColorGris);
                        pago.Item().Text(string.IsNullOrWhiteSpace(c.MetodoPago) ? "—" : c.MetodoPago).FontSize(11).Bold();
                        pago.Item().Text($"Moneda: {c.Moneda}").FontSize(9).FontColor(ColorGris);
                    });
                });

                // Tabla de líneas (con desglose fiscal por línea)
                col.Item().PaddingTop(16).Table(table =>
                {
                    table.ColumnsDefinition(cols =>
                    {
                        cols.RelativeColumn(4.2f);   // Descripción
                        cols.RelativeColumn(1.6f);   // Tipo
                        cols.RelativeColumn(1.1f);   // Cant.
                        cols.RelativeColumn(2);      // Base sin IVA
                        cols.RelativeColumn(1.7f);   // IVA
                        cols.RelativeColumn(2);      // Total
                    });

                    table.Header(header =>
                    {
                        header.Cell().Element(CeldaEncabezado).Text("Descripción");
                        header.Cell().Element(CeldaEncabezado).Text("Tipo");
                        header.Cell().Element(CeldaEncabezado).AlignRight().Text("Cant.");
                        header.Cell().Element(CeldaEncabezado).AlignRight().Text("Base sin IVA");
                        header.Cell().Element(CeldaEncabezado).AlignRight().Text("IVA");
                        header.Cell().Element(CeldaEncabezado).AlignRight().Text("Total");
                    });

                    foreach (var linea in c.Lineas)
                    {
                        table.Cell().Element(Celda).Text(linea.Descripcion);
                        table.Cell().Element(Celda).Text(TipoLineaLegible(linea.TipoLinea));
                        table.Cell().Element(Celda).AlignRight().Text(linea.Cantidad.ToString("#,##0.##", ComprobanteTextos.CulturaCR));
                        table.Cell().Element(Celda).AlignRight().Text(ComprobanteTextos.Colones(linea.Subtotal, c.Moneda));
                        table.Cell().Element(Celda).AlignRight().Text(ComprobanteTextos.Colones(linea.Impuesto, c.Moneda));
                        table.Cell().Element(Celda).AlignRight().Text(ComprobanteTextos.Colones(linea.Total, c.Moneda));
                    }
                });

                // Totales. Snapshot fiscal de Fase 1: Subtotal = base sin IVA, Impuesto = IVA
                // incluido. Fallback para comprobantes históricos sin snapshot (Impuesto == 0):
                // se muestra solo el TOTAL para no informar un desglose engañoso.
                col.Item().PaddingTop(10).Row(row =>
                {
                    row.RelativeItem();
                    row.ConstantItem(240).Column(tot =>
                    {
                        if (c.Impuesto > 0)
                        {
                            FilaTotal(tot, "Base sin IVA", ComprobanteTextos.Colones(c.Subtotal, c.Moneda), false);
                            if (c.Descuento > 0)
                                FilaTotal(tot, "Descuento", "-" + ComprobanteTextos.Colones(c.Descuento, c.Moneda), false);
                            FilaTotal(tot, "IVA incluido", ComprobanteTextos.Colones(c.Impuesto, c.Moneda), false);
                        }
                        else if (c.Descuento > 0)
                        {
                            FilaTotal(tot, "Descuento", "-" + ComprobanteTextos.Colones(c.Descuento, c.Moneda), false);
                        }

                        tot.Item().PaddingTop(4).LineHorizontal(1).LineColor(ColorGrisClaro);
                        FilaTotal(tot, "TOTAL COBRADO", ComprobanteTextos.Colones(c.Total, c.Moneda), true);
                    });
                });

                if (!string.IsNullOrWhiteSpace(c.Observacion))
                {
                    col.Item().PaddingTop(14).Column(obs =>
                    {
                        obs.Item().Text("Observación").FontSize(8).Bold().FontColor(ColorGris);
                        obs.Item().Text(c.Observacion!).FontSize(9);
                    });
                }
            });
        }

        private static void ComponerPie(IContainer container, ComprobanteCobro c)
        {
            container.PaddingTop(10).Column(col =>
            {
                col.Item().LineHorizontal(1).LineColor(ColorGrisClaro);
                col.Item().PaddingTop(6).Text(ComprobanteTextos.PieGenerado).FontSize(8).FontColor(ColorGris);
                col.Item().PaddingTop(2).Text(ComprobanteTextos.LeyendaLegal).FontSize(7.5f).Italic().FontColor(ColorGris);
            });
        }

        private static void FilaTotal(ColumnDescriptor col, string etiqueta, string valor, bool fuerte)
        {
            col.Item().PaddingVertical(2).Row(row =>
            {
                row.RelativeItem().Text(etiqueta).FontSize(fuerte ? 12 : 9).Bold().FontColor(fuerte ? ColorTinta : ColorGris);
                row.ConstantItem(120).AlignRight().Text(valor).FontSize(fuerte ? 12 : 9).Bold();
            });
        }

        private static string TipoLineaLegible(string tipo) => tipo switch
        {
            ComprobanteTipoLinea.Servicio => "Servicio",
            ComprobanteTipoLinea.Producto => "Producto",
            _ => "Otro"
        };

        private static IContainer CeldaEncabezado(IContainer container) =>
            container.Background(ColorTinta).PaddingVertical(6).PaddingHorizontal(6)
                .DefaultTextStyle(x => x.FontColor(Colors.White).Bold().FontSize(9));

        private static IContainer Celda(IContainer container) =>
            container.BorderBottom(1).BorderColor(ColorGrisClaro).PaddingVertical(6).PaddingHorizontal(6);
    }
}
