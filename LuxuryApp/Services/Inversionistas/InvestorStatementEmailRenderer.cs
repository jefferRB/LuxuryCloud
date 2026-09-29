using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using LuxuryApp.Models.Inversionistas;
using LuxuryApp.Services.Comprobantes;

namespace LuxuryApp.Services.Inversionistas
{
    /// <summary>
    /// Plantilla del estado de participación: tabla de 600 px, CSS inline, sin JavaScript ni CSS
    /// externo → se ve bien en Gmail, Outlook y móvil. Mismo lenguaje visual negro/gris/blanco de
    /// los demás correos del sistema.
    ///
    /// <para>
    /// Privacidad: el correo NUNCA incluye nombres de clientes, datos de colaboradores ni la
    /// participación de otros inversionistas. Todos los valores dinámicos van HTML-encoded.
    /// </para>
    /// </summary>
    public sealed class InvestorStatementEmailRenderer : IInvestorStatementEmailRenderer
    {
        private static readonly HtmlEncoder Enc = HtmlEncoder.Default;
        private static readonly CultureInfo Culture = ComprobanteTextos.CulturaCR;

        private const string ColorNegativo = "#b42318";
        private const string ColorPositivo = "#1a7f37";

        public string RenderHtml(InvestorStatementDocument d)
        {
            ArgumentNullException.ThrowIfNull(d);

            var negocio = Enc.Encode(string.IsNullOrWhiteSpace(d.NombreNegocio) ? "Tu negocio" : d.NombreNegocio);
            var periodo = Enc.Encode(d.PeriodoEtiqueta);
            var inversionista = Enc.Encode(d.InversionistaNombre);

            var logoHtml = string.IsNullOrWhiteSpace(d.LogoUrl)
                ? string.Empty
                : $"""<img src="{Enc.Encode(d.LogoUrl!)}" alt="{negocio}" width="56" height="56" style="display:block;border-radius:10px;margin-bottom:12px;" />""";

            return $"""
                <!DOCTYPE html>
                <html lang="es">
                <head>
                  <meta charset="UTF-8" />
                  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                  <title>Estado de participación</title>
                </head>
                <body style="margin:0;padding:0;background:#f4f4f6;font-family:Arial,Helvetica,sans-serif;">
                  <table width="100%" cellpadding="0" cellspacing="0" style="background:#f4f4f6;padding:32px 0;">
                    <tr>
                      <td align="center">
                        <table width="600" cellpadding="0" cellspacing="0" style="background:#ffffff;border-radius:12px;overflow:hidden;box-shadow:0 4px 16px rgba(0,0,0,.08);max-width:600px;width:100%;">
                          <tr>
                            <td style="background:#0f0f10;padding:28px 32px;">
                              {logoHtml}
                              <span style="color:#c9a96a;font-size:12px;letter-spacing:2px;text-transform:uppercase;">Estado de participación</span><br />
                              <span style="color:#ffffff;font-size:22px;font-weight:700;letter-spacing:.3px;">{negocio}</span><br />
                              <span style="color:#9a9a9e;font-size:14px;">{periodo}</span>
                            </td>
                          </tr>
                          <tr>
                            <td style="padding:32px 32px 8px;">
                              <p style="margin:0 0 12px;font-size:16px;color:#333;">Hola {inversionista},</p>
                              <p style="margin:0 0 24px;font-size:15px;color:#555;line-height:1.6;">
                                Este es el resumen de tu participación en <strong>{negocio}</strong> correspondiente a <strong>{periodo}</strong>.
                              </p>

                              {BuildDesgloseHtml(d)}
                              {BuildParticipacionHtml(d)}
                              {BuildPagoHtml(d)}
                              {BuildPerdidaHtml(d)}

                              <p style="margin:24px 0 0;font-size:13px;color:#777;line-height:1.6;">
                                Adjuntamos el detalle en PDF. Este documento es un resumen financiero interno del negocio
                                y no constituye un comprobante fiscal.
                              </p>
                            </td>
                          </tr>
                          <tr>
                            <td style="background:#f7f7f9;padding:20px 32px;border-top:1px solid #eee;">
                              {BuildContactoHtml(d, negocio)}
                              <p style="margin:8px 0 0;font-size:12px;color:#aaa;">Generado automáticamente por LuxuryCloud.</p>
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

        public string RenderText(InvestorStatementDocument d)
        {
            ArgumentNullException.ThrowIfNull(d);

            var builder = new StringBuilder();
            builder.AppendLine($"{d.NombreNegocio} | Estado de participación — {d.PeriodoEtiqueta}");
            builder.AppendLine();
            builder.AppendLine($"Hola {d.InversionistaNombre},");
            builder.AppendLine();
            builder.AppendLine("Cómo se calculó la ganancia distribuible:");
            builder.AppendLine($"  Ingresos cobrados (sin IVA): {Colones(d.IngresosNetos)}");

            if (d.IvaExcluido > 0m)
            {
                builder.AppendLine($"  IVA excluido del cálculo: {Colones(d.IvaExcluido)}");
            }

            builder.AppendLine($"  Gastos operativos: -{Colones(d.GastosElegibles)}");
            builder.AppendLine($"  Liquidaciones del equipo: -{Colones(d.Liquidaciones)}");

            if (d.AjustesPositivos > 0m)
            {
                builder.AppendLine($"  Ajustes a favor: {Colones(d.AjustesPositivos)}");
            }

            if (d.AjustesNegativos > 0m)
            {
                builder.AppendLine($"  Ajustes en contra: -{Colones(d.AjustesNegativos)}");
            }

            if (d.PerdidaArrastrada > 0m)
            {
                builder.AppendLine($"  Pérdida de periodos anteriores: -{Colones(d.PerdidaArrastrada)}");
            }

            builder.AppendLine();
            builder.AppendLine($"Ganancia distribuible: {Colones(d.GananciaDistribuible)}");
            builder.AppendLine($"Participación acordada: {Porcentaje(d.ParticipacionPorcentaje)}");
            builder.AppendLine($"Tu participación: {Colones(d.ParticipacionCalculada)}");
            builder.AppendLine();
            builder.AppendLine($"Total pagado: {Colones(d.TotalPagado)}");
            builder.AppendLine($"Saldo pendiente: {Colones(d.SaldoPendiente)}");
            builder.AppendLine($"Estado del pago: {d.EstadoPagoTexto}");

            if (d.PerdidaPendiente > 0m)
            {
                builder.AppendLine();
                builder.AppendLine(
                    $"Queda una pérdida pendiente de {Colones(d.PerdidaPendiente)} que se descontará de próximos periodos.");
            }

            builder.AppendLine();
            builder.AppendLine("Este documento es un resumen financiero interno y no constituye un comprobante fiscal.");

            if (!string.IsNullOrWhiteSpace(d.TelefonoNegocio) || !string.IsNullOrWhiteSpace(d.EmailNegocio))
            {
                builder.AppendLine();
                builder.AppendLine($"Contacto: {string.Join(" · ", new[] { d.TelefonoNegocio, d.EmailNegocio }.Where(value => !string.IsNullOrWhiteSpace(value)))}");
            }

            return builder.ToString();
        }

        private static string BuildDesgloseHtml(InvestorStatementDocument d)
        {
            var filas = new StringBuilder();
            filas.Append(Fila("Ingresos cobrados (sin IVA)", Colones(d.IngresosNetos), null));

            if (d.IvaExcluido > 0m)
            {
                filas.Append(Fila("IVA excluido del cálculo", Colones(d.IvaExcluido), "#999999"));
            }

            filas.Append(Fila("Gastos operativos", "-" + Colones(d.GastosElegibles), ColorNegativo));
            filas.Append(Fila("Liquidaciones del equipo", "-" + Colones(d.Liquidaciones), ColorNegativo));

            if (d.AjustesPositivos > 0m)
            {
                filas.Append(Fila("Ajustes a favor", Colones(d.AjustesPositivos), ColorPositivo));
            }

            if (d.AjustesNegativos > 0m)
            {
                filas.Append(Fila("Ajustes en contra", "-" + Colones(d.AjustesNegativos), ColorNegativo));
            }

            if (d.PerdidaArrastrada > 0m)
            {
                filas.Append(Fila("Pérdida de periodos anteriores", "-" + Colones(d.PerdidaArrastrada), ColorNegativo));
            }

            return $"""
                <p style="margin:0 0 8px;font-size:13px;color:#888;text-transform:uppercase;letter-spacing:1px;">Cómo se calculó</p>
                <table width="100%" cellpadding="0" cellspacing="0" style="border-collapse:collapse;margin-bottom:20px;">
                  {filas}
                  <tr>
                    <td style="padding:12px 0 6px;border-top:2px solid #111;font-size:15px;color:#111;font-weight:700;">Ganancia distribuible</td>
                    <td align="right" style="padding:12px 0 6px;border-top:2px solid #111;font-size:15px;color:#111;font-weight:700;">{Enc.Encode(Colones(d.GananciaDistribuible))}</td>
                  </tr>
                </table>
                """;
        }

        private static string BuildParticipacionHtml(InvestorStatementDocument d) =>
            $"""
             <table width="100%" cellpadding="0" cellspacing="0" style="background:#0f0f10;border-radius:10px;margin-bottom:20px;">
               <tr>
                 <td style="padding:20px 24px;">
                   <span style="color:#9a9a9e;font-size:13px;">Tu participación ({Enc.Encode(Porcentaje(d.ParticipacionPorcentaje))})</span><br />
                   <span style="color:#ffffff;font-size:26px;font-weight:700;">{Enc.Encode(Colones(d.ParticipacionCalculada))}</span>
                 </td>
               </tr>
             </table>
             """;

        private static string BuildPagoHtml(InvestorStatementDocument d) =>
            $"""
             <table width="100%" cellpadding="0" cellspacing="0" style="border-collapse:collapse;margin-bottom:8px;">
               {Fila("Total pagado", Colones(d.TotalPagado), null)}
               {Fila("Saldo pendiente", Colones(d.SaldoPendiente), d.SaldoPendiente > 0m ? ColorNegativo : ColorPositivo)}
               {Fila("Estado del pago", d.EstadoPagoTexto, null)}
             </table>
             """;

        private static string BuildPerdidaHtml(InvestorStatementDocument d)
        {
            if (d.PerdidaPendiente <= 0m)
            {
                return string.Empty;
            }

            return $"""
                <table width="100%" cellpadding="0" cellspacing="0" style="background:#fff5f5;border-radius:8px;margin-top:16px;">
                  <tr>
                    <td style="padding:14px 18px;font-size:13px;color:#7a2018;line-height:1.6;">
                      Queda una <strong>pérdida pendiente de {Enc.Encode(Colones(d.PerdidaPendiente))}</strong>
                      que se descontará de las ganancias de los próximos periodos.
                    </td>
                  </tr>
                </table>
                """;
        }

        private static string BuildContactoHtml(InvestorStatementDocument d, string negocioEncoded)
        {
            var contacto = new List<string>();

            if (!string.IsNullOrWhiteSpace(d.TelefonoNegocio))
            {
                contacto.Add(Enc.Encode(d.TelefonoNegocio!));
            }

            if (!string.IsNullOrWhiteSpace(d.EmailNegocio))
            {
                contacto.Add(Enc.Encode(d.EmailNegocio!));
            }

            if (!string.IsNullOrWhiteSpace(d.DireccionNegocio))
            {
                contacto.Add(Enc.Encode(d.DireccionNegocio!));
            }

            var linea = contacto.Count == 0 ? string.Empty : string.Join(" · ", contacto);

            return $"""
                <p style="margin:0;font-size:12px;color:#888;"><strong>{negocioEncoded}</strong></p>
                <p style="margin:4px 0 0;font-size:12px;color:#aaa;">{linea}</p>
                """;
        }

        private static string Fila(string etiqueta, string valor, string? color) =>
            $"""
             <tr>
               <td style="padding:6px 0;font-size:14px;color:#555;">{Enc.Encode(etiqueta)}</td>
               <td align="right" style="padding:6px 0;font-size:14px;color:{color ?? "#111"};font-weight:600;">{Enc.Encode(valor)}</td>
             </tr>
             """;

        private static string Colones(decimal monto) => ComprobanteTextos.Colones(monto);

        private static string Porcentaje(decimal valor) => valor.ToString("0.##", Culture) + " %";
    }
}
