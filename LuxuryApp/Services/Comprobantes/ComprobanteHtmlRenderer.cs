using System.Text;
using System.Text.Encodings.Web;
using LuxuryApp.Models.Comprobantes;

namespace LuxuryApp.Services.Comprobantes
{
    /// <summary>
    /// Plantilla de correo compatible con clientes de email: tabla de 600px, CSS inline,
    /// sin dependencia de frameworks externos. Mantiene el lenguaje visual neutro/premium
    /// de los demás correos del sistema (negro/gris/blanco) y NO depende del tema del tenant.
    /// </summary>
    public sealed class ComprobanteHtmlRenderer : IComprobanteHtmlRenderer
    {
        private static readonly HtmlEncoder Enc = HtmlEncoder.Default;

        public string RenderEmailHtml(ComprobanteCobro c, string? urlPublica)
        {
            var negocio = Enc.Encode(c.NombreNegocioSnapshot);
            var cliente = Enc.Encode(c.NombreClienteSnapshot);
            var numero = Enc.Encode(c.NumeroInterno);
            var fecha = Enc.Encode(ComprobanteTextos.FechaCorta(c.FechaEmision));
            var metodo = Enc.Encode(string.IsNullOrWhiteSpace(c.MetodoPago) ? "—" : c.MetodoPago);
            var total = Enc.Encode(ComprobanteTextos.Colones(c.Total, c.Moneda));
            var descripcion = Enc.Encode(DescripcionPrincipal(c));

            var botonHtml = string.IsNullOrWhiteSpace(urlPublica)
                ? string.Empty
                : $"""
                    <table cellpadding="0" cellspacing="0" style="margin:8px 0 24px;">
                      <tr>
                        <td style="background:#111111;border-radius:6px;">
                          <a href="{Enc.Encode(urlPublica)}"
                             style="display:inline-block;padding:13px 26px;color:#ffffff;font-size:15px;font-weight:600;text-decoration:none;border-radius:6px;">
                            Ver comprobante
                          </a>
                        </td>
                      </tr>
                    </table>
                  """;

            var observacionHtml = string.IsNullOrWhiteSpace(c.Observacion)
                ? string.Empty
                : $"""<tr><td style="padding:4px 0;color:#888;font-size:13px;">Observación</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Enc.Encode(c.Observacion!)}</td></tr>""";

            // Desglose fiscal básico (snapshot Fase 1). Solo si hay IVA registrado, para no
            // mostrar un desglose engañoso en comprobantes históricos sin snapshot.
            var desgloseHtml = c.Impuesto > 0
                ? $"""
                    <tr><td style="padding:4px 0;color:#888;font-size:13px;">Base sin IVA</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Enc.Encode(ComprobanteTextos.Colones(c.Subtotal, c.Moneda))}</td></tr>
                    <tr><td style="padding:4px 0;color:#888;font-size:13px;">IVA incluido</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Enc.Encode(ComprobanteTextos.Colones(c.Impuesto, c.Moneda))}</td></tr>
                  """
                : string.Empty;

            return $"""
                <!DOCTYPE html>
                <html lang="es">
                <head>
                  <meta charset="UTF-8" />
                  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                  <title>Comprobante de pago</title>
                </head>
                <body style="margin:0;padding:0;background:#f5f5f5;font-family:Arial,Helvetica,sans-serif;">
                  <table width="100%" cellpadding="0" cellspacing="0" style="background:#f5f5f5;padding:32px 0;">
                    <tr>
                      <td align="center">
                        <table width="600" cellpadding="0" cellspacing="0" style="background:#ffffff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,.08);max-width:600px;width:100%;">
                          <tr>
                            <td style="background:#111111;padding:24px 32px;">
                              <span style="color:#ffffff;font-size:20px;font-weight:700;letter-spacing:.4px;">{negocio}</span>
                            </td>
                          </tr>
                          <tr>
                            <td style="padding:32px 32px 8px;">
                              <p style="margin:0 0 12px;font-size:16px;color:#333;">Hola <strong>{cliente}</strong>,</p>
                              <p style="margin:0 0 20px;font-size:15px;color:#555;line-height:1.6;">
                                Gracias por tu pago en {negocio}. Adjuntamos tu comprobante digital.
                              </p>

                              <table width="100%" cellpadding="0" cellspacing="0" style="border:1px solid #eee;border-radius:8px;padding:8px 16px;margin:0 0 20px;">
                                <tr><td style="padding:4px 0;color:#888;font-size:13px;">Número</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;font-weight:600;">{numero}</td></tr>
                                <tr><td style="padding:4px 0;color:#888;font-size:13px;">Fecha</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{fecha}</td></tr>
                                <tr><td style="padding:4px 0;color:#888;font-size:13px;">Detalle</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{descripcion}</td></tr>
                                <tr><td style="padding:4px 0;color:#888;font-size:13px;">Método de pago</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{metodo}</td></tr>
                                {observacionHtml}
                                {desgloseHtml}
                                <tr><td colspan="2" style="border-top:1px solid #eee;padding-top:8px;"></td></tr>
                                <tr><td style="padding:6px 0;color:#111;font-size:16px;font-weight:700;">Total cobrado</td><td style="padding:6px 0;text-align:right;color:#111;font-size:18px;font-weight:700;">{total}</td></tr>
                              </table>

                              {botonHtml}

                              <p style="margin:0 0 4px;font-size:13px;color:#999;line-height:1.5;">{Enc.Encode(ComprobanteTextos.PieGenerado)}</p>
                              <p style="margin:0 0 16px;font-size:12px;color:#aaa;line-height:1.5;font-style:italic;">{Enc.Encode(ComprobanteTextos.LeyendaLegal)}</p>
                            </td>
                          </tr>
                          <tr>
                            <td style="background:#f9f9f9;padding:16px 32px;border-top:1px solid #eee;">
                              <p style="margin:0;font-size:12px;color:#aaa;">&copy; {c.FechaEmision.Year} LuxuryCloud</p>
                            </td>
                          </tr>
                        </table>
                      </td>
                    </tr>
                  </table>
                </body>
                </html>
                """;
        }

        public string RenderEmailText(ComprobanteCobro c, string? urlPublica)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Hola {c.NombreClienteSnapshot},");
            sb.AppendLine();
            sb.AppendLine($"Gracias por tu pago en {c.NombreNegocioSnapshot}. Adjuntamos tu comprobante digital.");
            sb.AppendLine();
            sb.AppendLine($"Número: {c.NumeroInterno}");
            sb.AppendLine($"Fecha: {ComprobanteTextos.FechaCorta(c.FechaEmision)}");
            sb.AppendLine($"Detalle: {DescripcionPrincipal(c)}");
            sb.AppendLine($"Método de pago: {(string.IsNullOrWhiteSpace(c.MetodoPago) ? "—" : c.MetodoPago)}");
            if (c.Impuesto > 0)
            {
                sb.AppendLine($"Base sin IVA: {ComprobanteTextos.Colones(c.Subtotal, c.Moneda)}");
                sb.AppendLine($"IVA incluido: {ComprobanteTextos.Colones(c.Impuesto, c.Moneda)}");
            }
            sb.AppendLine($"Total cobrado: {ComprobanteTextos.Colones(c.Total, c.Moneda)}");
            if (!string.IsNullOrWhiteSpace(urlPublica))
            {
                sb.AppendLine();
                sb.AppendLine($"Ver comprobante: {urlPublica}");
            }
            sb.AppendLine();
            sb.AppendLine(ComprobanteTextos.PieGenerado);
            sb.AppendLine(ComprobanteTextos.LeyendaLegal);
            return sb.ToString();
        }

        private static string DescripcionPrincipal(ComprobanteCobro c)
        {
            var primera = c.Lineas.FirstOrDefault();
            if (primera is null)
            {
                return "Pago";
            }

            return c.Lineas.Count > 1
                ? $"{primera.Descripcion} (+{c.Lineas.Count - 1})"
                : primera.Descripcion;
        }
    }
}
