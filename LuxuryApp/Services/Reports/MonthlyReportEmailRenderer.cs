using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using LuxuryApp.Models.Reports;
using LuxuryApp.Services.Comprobantes;

namespace LuxuryApp.Services.Reports
{
    /// <summary>
    /// Plantilla del Resumen Ejecutivo Mensual: tabla de 600px, CSS inline, sin JavaScript
    /// ni CSS externo, compatible con Gmail/Outlook/móvil. Mismo lenguaje visual
    /// negro/gris/blanco de los demás correos del sistema (no depende del tema del tenant).
    /// Todos los valores dinámicos van HTML-encoded; los faltantes muestran
    /// "Sin datos suficientes" en lugar de nulls feos. No depende de imágenes externas.
    /// </summary>
    public sealed class MonthlyReportEmailRenderer : IMonthlyReportEmailRenderer
    {
        private const string SinDatos = "Sin datos suficientes";
        private const string PositiveColor = "#1a7f37";
        private const string NegativeColor = "#b42318";

        private static readonly HtmlEncoder Enc = HtmlEncoder.Default;
        private static readonly CultureInfo Culture = ComprobanteTextos.CulturaCR;

        public string RenderHtml(MonthlyBusinessReportViewModel r, string? dashboardUrl)
        {
            ArgumentNullException.ThrowIfNull(r);

            var negocio = Enc.Encode(FallbackText(r.NombreNegocio, "Tu negocio"));
            var periodo = Enc.Encode($"{r.MesNombre} {r.Anio}");

            var finanzasHtml = r.IncluirDatosFinancieros ? BuildFinanzasHtml(r) : string.Empty;
            var comparativaHtml = r.IncluirComparativa && r.TieneComparativa ? BuildComparativaHtml(r) : string.Empty;
            var operacionHtml = r.IncluirDatosOperativos ? BuildOperacionHtml(r) : string.Empty;
            var insightsHtml = r.IncluirRecomendaciones ? BuildInsightsHtml(r) : string.Empty;
            var botonHtml = BuildBotonHtml(dashboardUrl);

            return $"""
                <!DOCTYPE html>
                <html lang="es">
                <head>
                  <meta charset="UTF-8" />
                  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                  <title>Resumen Ejecutivo Mensual</title>
                </head>
                <body style="margin:0;padding:0;background:#f4f4f6;font-family:Arial,Helvetica,sans-serif;">
                  <table width="100%" cellpadding="0" cellspacing="0" style="background:#f4f4f6;padding:32px 0;">
                    <tr>
                      <td align="center">
                        <table width="600" cellpadding="0" cellspacing="0" style="background:#ffffff;border-radius:12px;overflow:hidden;box-shadow:0 4px 16px rgba(0,0,0,.08);max-width:600px;width:100%;">
                          <tr>
                            <td style="background:#0f0f10;padding:28px 32px;">
                              <span style="color:#c9a96a;font-size:12px;letter-spacing:2px;text-transform:uppercase;">LuxuryCloud Insights</span><br />
                              <span style="color:#ffffff;font-size:22px;font-weight:700;letter-spacing:.3px;">{negocio}</span><br />
                              <span style="color:#9a9a9e;font-size:14px;">Resumen Ejecutivo Mensual · {periodo}</span>
                            </td>
                          </tr>
                          <tr>
                            <td style="padding:32px 32px 8px;">
                              <p style="margin:0 0 12px;font-size:16px;color:#333;">Hola,</p>
                              <p style="margin:0 0 20px;font-size:15px;color:#555;line-height:1.6;">
                                Este es el resumen de rendimiento de tu negocio durante <strong>{periodo}</strong>.
                              </p>
                              <p style="margin:0 0 24px;font-size:15px;color:#333;line-height:1.6;">
                                {Enc.Encode(r.ResumenEjecutivoTexto)}
                              </p>

                              {finanzasHtml}
                              {comparativaHtml}
                              {operacionHtml}
                              {insightsHtml}
                              {botonHtml}
                            </td>
                          </tr>
                          <tr>
                            <td style="background:#f7f7f9;padding:20px 32px;border-top:1px solid #eee;">
                              <p style="margin:0 0 4px;font-size:12px;color:#aaa;">Este correo fue generado automáticamente por LuxuryCloud.</p>
                              <p style="margin:0 0 4px;font-size:12px;color:#aaa;">Recibís este correo porque sos administrador del negocio.</p>
                              <p style="margin:0;font-size:12px;color:#aaa;">&copy; {r.Anio} LuxuryCloud · Tu negocio, con claridad.</p>
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

        public string RenderText(MonthlyBusinessReportViewModel r, string? dashboardUrl)
        {
            ArgumentNullException.ThrowIfNull(r);

            var sb = new StringBuilder();
            sb.AppendLine($"Resumen Ejecutivo Mensual - {FallbackText(r.NombreNegocio, "Tu negocio")}");
            sb.AppendLine($"{r.MesNombre} {r.Anio}");
            sb.AppendLine();
            sb.AppendLine(r.ResumenEjecutivoTexto);
            sb.AppendLine();

            if (r.IncluirDatosFinancieros)
            {
                sb.AppendLine("RESULTADO DEL MES");
                sb.AppendLine($"Ingresos: {ComprobanteTextos.Colones(r.Ingresos)}");
                sb.AppendLine($"Ingresos netos: {ComprobanteTextos.Colones(r.TotalSinImpuestos)}");
                sb.AppendLine($"Costos y gastos del mes: {ComprobanteTextos.Colones(r.Egresos)}");
                sb.AppendLine($"Ganancia del mes: {ComprobanteTextos.Colones(r.GananciaReal)}");
                sb.AppendLine($"Margen: {Porcentaje(r.MargenGanancia)}");
                sb.AppendLine($"Impuestos: {ComprobanteTextos.Colones(r.Impuestos)}");
                sb.AppendLine();
                sb.AppendLine("CAJA");
                sb.AppendLine($"Salidas de caja registradas: {ComprobanteTextos.Colones(r.SalidasCaja)}");
                sb.AppendLine($"Pago a funcionarios (caja): {ComprobanteTextos.Colones(r.PagoFuncionarios)}");
                sb.AppendLine("Las salidas de caja pueden diferir de los costos del mes porque algunos");
                sb.AppendLine("pagos corresponden a produccion de otros periodos.");
                sb.AppendLine($"Efectivo: {ComprobanteTextos.Colones(r.IngresosEfectivo)} | " +
                              $"Tarjeta: {ComprobanteTextos.Colones(r.IngresosTarjeta)} | " +
                              $"SINPE: {ComprobanteTextos.Colones(r.IngresosSinpe)}");
                sb.AppendLine();
            }

            if (r.IncluirComparativa)
            {
                sb.AppendLine("COMPARACIÓN CONTRA EL MES ANTERIOR");
                if (r.TieneComparativa)
                {
                    sb.AppendLine($"Ingresos: {ComprobanteTextos.Colones(r.IngresosMesAnterior)} -> {ComprobanteTextos.Colones(r.Ingresos)} ({VariacionTexto(r.VariacionIngresosPorcentaje, r.Ingresos)})");
                    sb.AppendLine($"Ganancia real: {ComprobanteTextos.Colones(r.GananciaRealMesAnterior)} -> {ComprobanteTextos.Colones(r.GananciaReal)} ({VariacionTexto(r.VariacionGananciaPorcentaje, r.GananciaReal)})");
                    sb.AppendLine($"Servicios: {r.ServiciosRealizadosMesAnterior} -> {r.ServiciosRealizados} ({VariacionTexto(r.VariacionServiciosPorcentaje, r.ServiciosRealizados)})");
                    sb.AppendLine($"Productos: {r.ProductosVendidosMesAnterior} -> {r.ProductosVendidos} ({VariacionTexto(r.VariacionProductosPorcentaje, r.ProductosVendidos)})");
                    sb.AppendLine($"Citas en línea: {r.CitasOnlineMesAnterior} -> {r.CitasOnlineReservadas} ({VariacionTexto(r.VariacionCitasOnlinePorcentaje, r.CitasOnlineReservadas)})");
                }
                else
                {
                    sb.AppendLine(SinDatos + " del mes anterior.");
                }

                sb.AppendLine();
            }

            if (r.IncluirDatosOperativos)
            {
                sb.AppendLine("OPERACIÓN DEL MES");
                sb.AppendLine($"Servicios realizados: {r.ServiciosRealizados}");
                sb.AppendLine($"Productos vendidos: {r.ProductosVendidos}");
                sb.AppendLine($"Citas en línea: {r.CitasOnlineReservadas}");
                sb.AppendLine($"Servicio más solicitado: {NombreConCantidad(r.ServicioMasSolicitadoNombre, r.ServicioMasSolicitadoCantidad)}");
                sb.AppendLine($"Producto más vendido: {NombreConCantidad(r.ProductoMasVendidoNombre, r.ProductoMasVendidoCantidad)}");
                sb.AppendLine($"Colaborador estrella: {NombreConCantidad(r.FuncionarioEstrellaNombre, r.FuncionarioEstrellaCantidadCitas, "citas")}");
                sb.AppendLine();
            }

            if (r.IncluirRecomendaciones)
            {
                foreach (var mensaje in Mensajes(r))
                {
                    sb.AppendLine($"- {mensaje}");
                }

                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(dashboardUrl))
            {
                sb.AppendLine($"Ver dashboard completo: {dashboardUrl}");
                sb.AppendLine();
            }

            sb.AppendLine("Este correo fue generado automáticamente por LuxuryCloud.");
            sb.AppendLine("Recibís este correo porque sos administrador del negocio.");
            return sb.ToString();
        }

        // ─────────────── Bloques HTML ───────────────

        private static string BuildFinanzasHtml(MonthlyBusinessReportViewModel r)
        {
            var margen = Enc.Encode(Porcentaje(r.MargenGanancia));
            var gananciaColor = r.GananciaReal >= 0 ? PositiveColor : NegativeColor;

            return $"""
                <p style="margin:0 0 10px;font-size:13px;color:#888;letter-spacing:1px;text-transform:uppercase;">Finanzas del mes</p>
                <table width="100%" cellpadding="0" cellspacing="0" style="margin:0 0 20px;">
                  <tr>
                    <td width="50%" style="padding:4px 4px 4px 0;">
                      <table width="100%" cellpadding="0" cellspacing="0" style="border:1px solid #eee;border-radius:8px;">
                        <tr><td style="padding:14px 16px;">
                          <span style="font-size:12px;color:#888;">Ingresos</span><br />
                          <span style="font-size:18px;font-weight:700;color:#111;">{Colones(r.Ingresos)}</span>
                        </td></tr>
                      </table>
                    </td>
                    <td width="50%" style="padding:4px 0 4px 4px;">
                      <table width="100%" cellpadding="0" cellspacing="0" style="border:1px solid #eee;border-radius:8px;">
                        <tr><td style="padding:14px 16px;">
                          <span style="font-size:12px;color:#888;">Costos y gastos del mes</span><br />
                          <span style="font-size:18px;font-weight:700;color:#111;">{Colones(r.Egresos)}</span>
                        </td></tr>
                      </table>
                    </td>
                  </tr>
                  <tr>
                    <td width="50%" style="padding:4px 4px 4px 0;">
                      <table width="100%" cellpadding="0" cellspacing="0" style="border:1px solid #eee;border-radius:8px;">
                        <tr><td style="padding:14px 16px;">
                          <span style="font-size:12px;color:#888;">Ganancia del mes</span><br />
                          <span style="font-size:18px;font-weight:700;color:{gananciaColor};">{Colones(r.GananciaReal)}</span>
                        </td></tr>
                      </table>
                    </td>
                    <td width="50%" style="padding:4px 0 4px 4px;">
                      <table width="100%" cellpadding="0" cellspacing="0" style="border:1px solid #eee;border-radius:8px;">
                        <tr><td style="padding:14px 16px;">
                          <span style="font-size:12px;color:#888;">Margen</span><br />
                          <span style="font-size:18px;font-weight:700;color:#111;">{margen}</span>
                        </td></tr>
                      </table>
                    </td>
                  </tr>
                </table>

                <table width="100%" cellpadding="0" cellspacing="0" style="border:1px solid #eee;border-radius:8px;padding:8px 16px;margin:0 0 24px;">
                  <tr><td style="padding:4px 0;color:#888;font-size:13px;">Total sin impuestos</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Colones(r.TotalSinImpuestos)}</td></tr>
                  <tr><td style="padding:4px 0;color:#888;font-size:13px;">Impuestos</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Colones(r.Impuestos)}</td></tr>
                  <tr><td style="padding:4px 0;color:#888;font-size:13px;">Servicios (monto)</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Colones(r.ServiciosGeneradosMonto)}</td></tr>
                  <tr><td style="padding:4px 0;color:#888;font-size:13px;">Productos (monto)</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Colones(r.ProductosGeneradosMonto)}</td></tr>
                  <tr><td colspan="2" style="border-top:1px solid #eee;padding-top:8px;"></td></tr>
                  <tr><td style="padding:4px 0;color:#888;font-size:13px;">Efectivo</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Colones(r.IngresosEfectivo)}</td></tr>
                  <tr><td style="padding:4px 0;color:#888;font-size:13px;">Tarjeta</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Colones(r.IngresosTarjeta)}</td></tr>
                  <tr><td style="padding:4px 0;color:#888;font-size:13px;">SINPE</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Colones(r.IngresosSinpe)}</td></tr>
                </table>

                <p style="margin:0 0 10px;font-size:13px;color:#888;letter-spacing:1px;text-transform:uppercase;">Caja</p>
                <table width="100%" cellpadding="0" cellspacing="0" style="border:1px solid #eee;border-radius:8px;padding:8px 16px;margin:0 0 8px;">
                  <tr><td style="padding:4px 0;color:#888;font-size:13px;">Salidas de caja registradas</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Colones(r.SalidasCaja)}</td></tr>
                  <tr><td style="padding:4px 0;color:#888;font-size:13px;">Pago a funcionarios (caja)</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Colones(r.PagoFuncionarios)}</td></tr>
                </table>
                <p style="margin:0 0 24px;font-size:11px;color:#aaa;line-height:1.5;">
                  Las salidas de caja pueden diferir de los costos del mes porque algunos pagos
                  corresponden a producci&#243;n de otros per&#237;odos.
                </p>
                """;
        }

        private static string BuildComparativaHtml(MonthlyBusinessReportViewModel r)
        {
            var mesAnterior = Enc.Encode(FallbackText(r.MesAnteriorNombre, "mes anterior"));

            return $"""
                <p style="margin:0 0 10px;font-size:13px;color:#888;letter-spacing:1px;text-transform:uppercase;">Comparación contra {mesAnterior}</p>
                <table width="100%" cellpadding="0" cellspacing="0" style="border:1px solid #eee;border-radius:8px;padding:8px 16px;margin:0 0 24px;">
                  <tr>
                    <td style="padding:6px 0;color:#aaa;font-size:11px;text-transform:uppercase;">Indicador</td>
                    <td style="padding:6px 0;text-align:right;color:#aaa;font-size:11px;text-transform:uppercase;">{mesAnterior}</td>
                    <td style="padding:6px 0;text-align:right;color:#aaa;font-size:11px;text-transform:uppercase;">Este mes</td>
                    <td style="padding:6px 0;text-align:right;color:#aaa;font-size:11px;text-transform:uppercase;">Variación</td>
                  </tr>
                  {ComparativaRow("Ingresos", Colones(r.IngresosMesAnterior), Colones(r.Ingresos), r.VariacionIngresosPorcentaje, r.Ingresos)}
                  {ComparativaRow("Ganancia del mes", Colones(r.GananciaRealMesAnterior), Colones(r.GananciaReal), r.VariacionGananciaPorcentaje, r.GananciaReal)}
                  {ComparativaRow("Servicios", r.ServiciosRealizadosMesAnterior.ToString(Culture), r.ServiciosRealizados.ToString(Culture), r.VariacionServiciosPorcentaje, r.ServiciosRealizados)}
                  {ComparativaRow("Productos", r.ProductosVendidosMesAnterior.ToString(Culture), r.ProductosVendidos.ToString(Culture), r.VariacionProductosPorcentaje, r.ProductosVendidos)}
                  {ComparativaRow("Citas en línea", r.CitasOnlineMesAnterior.ToString(Culture), r.CitasOnlineReservadas.ToString(Culture), r.VariacionCitasOnlinePorcentaje, r.CitasOnlineReservadas)}
                </table>
                """;
        }

        private static string ComparativaRow(string label, string anterior, string actual, decimal? variacion, decimal actualValue)
        {
            var (texto, color) = VariacionBadge(variacion, actualValue);

            return $"""
                <tr>
                  <td style="padding:5px 0;color:#555;font-size:13px;">{Enc.Encode(label)}</td>
                  <td style="padding:5px 0;text-align:right;color:#999;font-size:13px;">{anterior}</td>
                  <td style="padding:5px 0;text-align:right;color:#111;font-size:13px;font-weight:600;">{actual}</td>
                  <td style="padding:5px 0;text-align:right;color:{color};font-size:13px;font-weight:600;">{Enc.Encode(texto)}</td>
                </tr>
                """;
        }

        private static string BuildOperacionHtml(MonthlyBusinessReportViewModel r)
        {
            var operativoExtra = string.IsNullOrWhiteSpace(r.DiaMasOcupado)
                ? string.Empty
                : $"""
                    <tr><td style="padding:4px 0;color:#888;font-size:13px;">Día más ocupado</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Enc.Encode(NombreConCantidad(r.DiaMasOcupado, r.DiaMasOcupadoCantidad, "citas"))}</td></tr>
                    <tr><td style="padding:4px 0;color:#888;font-size:13px;">Día con menos movimiento</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Enc.Encode(NombreConCantidad(r.DiaMenosOcupado, r.DiaMenosOcupadoCantidad, "citas"))}</td></tr>
                    <tr><td style="padding:4px 0;color:#888;font-size:13px;">Hora más ocupada</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Enc.Encode(FallbackText(r.HoraMasOcupada, SinDatos))}</td></tr>
                  """;

            return $"""
                <p style="margin:0 0 10px;font-size:13px;color:#888;letter-spacing:1px;text-transform:uppercase;">Operación del mes</p>
                <table width="100%" cellpadding="0" cellspacing="0" style="border:1px solid #eee;border-radius:8px;padding:8px 16px;margin:0 0 24px;">
                  <tr><td style="padding:4px 0;color:#888;font-size:13px;">Servicios realizados</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;font-weight:600;">{r.ServiciosRealizados}</td></tr>
                  <tr><td style="padding:4px 0;color:#888;font-size:13px;">Productos vendidos</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;font-weight:600;">{r.ProductosVendidos}</td></tr>
                  <tr><td style="padding:4px 0;color:#888;font-size:13px;">Citas en línea</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;font-weight:600;">{r.CitasOnlineReservadas}</td></tr>
                  <tr><td style="padding:4px 0;color:#888;font-size:13px;">Servicio más solicitado</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Enc.Encode(NombreConCantidad(r.ServicioMasSolicitadoNombre, r.ServicioMasSolicitadoCantidad))}</td></tr>
                  <tr><td style="padding:4px 0;color:#888;font-size:13px;">Producto más vendido</td><td style="padding:4px 0;text-align:right;color:#333;font-size:13px;">{Enc.Encode(NombreConCantidad(r.ProductoMasVendidoNombre, r.ProductoMasVendidoCantidad))}</td></tr>
                  {operativoExtra}
                </table>
                """;
        }

        private static string BuildInsightsHtml(MonthlyBusinessReportViewModel r)
        {
            var estrellaHtml = string.IsNullOrWhiteSpace(r.ComentarioFuncionarioEstrella)
                ? $"""
                    <p style="margin:0 0 16px;font-size:14px;color:#555;line-height:1.6;">
                      Colaborador estrella: {Enc.Encode(SinDatos)}.
                    </p>
                  """
                : $"""
                    <table width="100%" cellpadding="0" cellspacing="0" style="background:#faf7f0;border-radius:8px;margin:0 0 16px;">
                      <tr><td style="padding:14px 16px;">
                        <span style="font-size:14px;color:#333;line-height:1.6;">⭐ {Enc.Encode(r.ComentarioFuncionarioEstrella)}</span>
                      </td></tr>
                    </table>
                  """;

            var oportunidadHtml = string.IsNullOrWhiteSpace(r.ComentarioOportunidad)
                ? string.Empty
                : $"""
                    <table width="100%" cellpadding="0" cellspacing="0" style="background:#f7f7f9;border-radius:8px;margin:0 0 16px;">
                      <tr><td style="padding:14px 16px;">
                        <span style="font-size:12px;color:#888;letter-spacing:1px;text-transform:uppercase;">Oportunidad del mes</span><br />
                        <span style="font-size:14px;color:#333;line-height:1.6;">{Enc.Encode(r.ComentarioOportunidad)}</span>
                      </td></tr>
                    </table>
                  """;

            return $"""
                <p style="margin:0 0 10px;font-size:13px;color:#888;letter-spacing:1px;text-transform:uppercase;">Lectura del mes</p>
                <p style="margin:0 0 12px;font-size:14px;color:#555;line-height:1.6;">{Enc.Encode(r.ComentarioMargen)}</p>
                {OptionalParagraph(r.ComentarioComparativa)}
                {OptionalParagraph(r.ComentarioServicioTop)}
                {OptionalParagraph(r.ComentarioReservasOnline)}
                {estrellaHtml}
                {oportunidadHtml}
                """;
        }

        private static string OptionalParagraph(string? text) =>
            string.IsNullOrWhiteSpace(text)
                ? string.Empty
                : $"""<p style="margin:0 0 12px;font-size:14px;color:#555;line-height:1.6;">{Enc.Encode(text)}</p>""";

        private static string BuildBotonHtml(string? dashboardUrl)
        {
            if (string.IsNullOrWhiteSpace(dashboardUrl))
            {
                return string.Empty;
            }

            return $"""
                <table cellpadding="0" cellspacing="0" style="margin:8px 0 24px;">
                  <tr>
                    <td style="background:#0f0f10;border-radius:6px;">
                      <a href="{Enc.Encode(dashboardUrl)}"
                         style="display:inline-block;padding:13px 26px;color:#ffffff;font-size:15px;font-weight:600;text-decoration:none;border-radius:6px;">
                        Ver dashboard completo
                      </a>
                    </td>
                  </tr>
                </table>
                """;
        }

        // ─────────────── Helpers ───────────────

        private static IEnumerable<string> Mensajes(MonthlyBusinessReportViewModel r)
        {
            var mensajes = new[]
            {
                r.ComentarioMargen,
                r.ComentarioComparativa,
                r.ComentarioActividad,
                r.ComentarioServicioTop,
                r.ComentarioReservasOnline,
                r.ComentarioFuncionarioEstrella,
                r.ComentarioOportunidad
            };

            return mensajes.Where(m => !string.IsNullOrWhiteSpace(m));
        }

        private static (string Text, string Color) VariacionBadge(decimal? variacion, decimal actualValue)
        {
            if (!variacion.HasValue)
            {
                return actualValue > 0 ? ("Nuevo", PositiveColor) : ("—", "#999");
            }

            if (variacion.Value > 0)
            {
                return ("▲ " + FormatPercent(variacion.Value), PositiveColor);
            }

            if (variacion.Value < 0)
            {
                return ("▼ " + FormatPercent(Math.Abs(variacion.Value)), NegativeColor);
            }

            return ("=", "#999");
        }

        private static string VariacionTexto(decimal? variacion, decimal actualValue)
        {
            if (!variacion.HasValue)
            {
                return actualValue > 0 ? "nuevo movimiento" : "sin cambios";
            }

            if (variacion.Value > 0)
            {
                return "+" + FormatPercent(variacion.Value);
            }

            if (variacion.Value < 0)
            {
                return "-" + FormatPercent(Math.Abs(variacion.Value));
            }

            return "sin cambios";
        }

        private static string Colones(decimal monto) =>
            Enc.Encode(ComprobanteTextos.Colones(monto));

        private static string Porcentaje(decimal valor) =>
            valor.ToString("0.##", Culture) + "%";

        private static string FormatPercent(decimal valor) =>
            valor.ToString("0.#", Culture) + "%";

        private static string NombreConCantidad(string? nombre, int cantidad, string sufijo = "")
        {
            if (string.IsNullOrWhiteSpace(nombre))
            {
                return SinDatos;
            }

            var detalle = string.IsNullOrEmpty(sufijo) ? $"({cantidad})" : $"({cantidad} {sufijo})";
            return $"{nombre} {detalle}";
        }

        private static string FallbackText(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
