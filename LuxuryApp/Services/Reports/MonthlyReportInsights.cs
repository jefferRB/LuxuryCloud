using System.Globalization;
using LuxuryApp.Models.Reports;
using LuxuryApp.Services.Comprobantes;

namespace LuxuryApp.Services.Reports
{
    /// <summary>
    /// Mensajes interpretativos del resumen mensual generados con reglas simples (sin IA).
    /// Centralizados para que correo, pruebas y futuras vistas usen exactamente el mismo texto.
    /// </summary>
    public static class MonthlyReportInsights
    {
        /// <summary>Umbral bajo de citas online para sugerir compartir el link de reservas.</summary>
        public const int CitasOnlineBajas = 3;

        private static readonly CultureInfo Culture = ComprobanteTextos.CulturaCR;

        public static void Apply(MonthlyBusinessReportViewModel report)
        {
            report.ResumenEjecutivoTexto = BuildResumenEjecutivo(report);
            report.ComentarioMargen = BuildComentarioMargen(report);
            report.ComentarioActividad = BuildComentarioActividad(report);
            report.ComentarioComparativa = BuildComentarioComparativa(report);
            report.ComentarioFuncionarioEstrella = BuildComentarioFuncionarioEstrella(report);
            report.ComentarioServicioTop = BuildComentarioServicioTop(report);
            report.ComentarioReservasOnline = BuildComentarioReservasOnline(report);
            report.ComentarioOportunidad = BuildComentarioOportunidad(report);
        }

        private static string BuildResumenEjecutivo(MonthlyBusinessReportViewModel r)
        {
            if (!r.TieneActividad)
            {
                return $"Durante {r.MesNombre} {r.Anio} no se registró actividad en el sistema. " +
                       "Si el negocio sí operó, revisá que los cobros y citas se estén registrando.";
            }

            return $"Este mes tu negocio generó {ComprobanteTextos.Colones(r.Ingresos)} de ingresos " +
                   $"y obtuvo una ganancia real de {ComprobanteTextos.Colones(r.GananciaReal)} " +
                   "después de egresos e impuestos.";
        }

        private static string BuildComentarioMargen(MonthlyBusinessReportViewModel r)
        {
            if (!r.TieneActividad || r.Ingresos <= 0)
            {
                return "Sin ingresos registrados este mes, no es posible calcular un margen.";
            }

            if (r.MargenGanancia >= 25m)
            {
                return "Tu negocio tuvo un margen muy saludable este mes.";
            }

            if (r.MargenGanancia >= 10m)
            {
                return "Tu negocio fue rentable, aunque todavía hay espacio para optimizar costos.";
            }

            if (r.MargenGanancia >= 0m)
            {
                return "Tu negocio cerró en positivo, pero con un margen ajustado.";
            }

            return "Este mes cerró con pérdida. Conviene revisar egresos, pagos y costos operativos.";
        }

        private static string BuildComentarioActividad(MonthlyBusinessReportViewModel r)
        {
            if (!r.TieneActividad)
            {
                return "No hubo servicios, productos ni citas registradas este mes.";
            }

            return $"Se realizaron {r.ServiciosRealizados} servicios y se vendieron " +
                   $"{r.ProductosVendidos} productos durante el mes.";
        }

        private static string BuildComentarioComparativa(MonthlyBusinessReportViewModel r)
        {
            if (!r.TieneComparativa)
            {
                return "No hay datos suficientes para comparar contra el mes anterior.";
            }

            // Ingresos: prioriza el mensaje financiero principal.
            var ingresos = DescribeVariacion(
                r.VariacionIngresosPorcentaje,
                r.Ingresos,
                r.IngresosMesAnterior,
                subieron: "Tus ingresos subieron {0} respecto al mes anterior.",
                bajaron: "Tus ingresos bajaron {0} respecto al mes anterior.",
                iguales: "Tus ingresos se mantuvieron respecto al mes anterior.",
                nuevo: "Este mes registraste ingresos donde el mes anterior no hubo movimiento.");

            var ganancia = r.VariacionGananciaPorcentaje.HasValue && r.VariacionGananciaPorcentaje.Value < 0
                ? " Tu ganancia bajó " + FormatPercent(Math.Abs(r.VariacionGananciaPorcentaje.Value)) +
                  " respecto al mes anterior; revisá egresos, pagos a funcionarios o impuestos."
                : string.Empty;

            var servicios = DescribeServiciosVariacion(r);

            return (ingresos + ganancia + servicios).Trim();
        }

        private static string DescribeServiciosVariacion(MonthlyBusinessReportViewModel r)
        {
            var diff = r.ServiciosRealizados - r.ServiciosRealizadosMesAnterior;
            if (diff > 0)
            {
                return $" Este mes realizaste {diff} servicios más que el mes anterior.";
            }

            if (diff < 0)
            {
                return $" Este mes realizaste {Math.Abs(diff)} servicios menos que el mes anterior.";
            }

            return string.Empty;
        }

        private static string BuildComentarioFuncionarioEstrella(MonthlyBusinessReportViewModel r)
        {
            if (string.IsNullOrWhiteSpace(r.FuncionarioEstrellaNombre))
            {
                return string.Empty;
            }

            return $"Felicidades, el colaborador estrella del mes fue {r.FuncionarioEstrellaNombre} " +
                   $"con {r.FuncionarioEstrellaCantidadCitas} citas realizadas.";
        }

        private static string BuildComentarioServicioTop(MonthlyBusinessReportViewModel r)
        {
            if (string.IsNullOrWhiteSpace(r.ServicioMasSolicitadoNombre))
            {
                return string.Empty;
            }

            return $"Tu servicio más solicitado fue {r.ServicioMasSolicitadoNombre}. Podrías crear " +
                   "paquetes o promociones combinadas para aumentar el ticket promedio.";
        }

        private static string BuildComentarioReservasOnline(MonthlyBusinessReportViewModel r)
        {
            if (r.CitasOnlineReservadas <= 0)
            {
                return "No recibiste reservas en línea este mes. Podrías compartir tu link de reservas " +
                       "en WhatsApp, Instagram o Facebook.";
            }

            return $"Recibiste {r.CitasOnlineReservadas} reservas en línea este mes. Este canal puede " +
                   "ayudarte a llenar espacios sin depender de mensajes manuales.";
        }

        private static string BuildComentarioOportunidad(MonthlyBusinessReportViewModel r)
        {
            if (r.TieneActividad && r.CitasOnlineReservadas < CitasOnlineBajas)
            {
                return "Tus citas en línea fueron pocas este mes. Podrías compartir más tu link " +
                       "de reservas en redes sociales o WhatsApp.";
            }

            if (!string.IsNullOrWhiteSpace(r.DiaMenosOcupado))
            {
                return $"El día con menor movimiento fue {r.DiaMenosOcupado}. Podría ser una " +
                       "oportunidad para promociones o campañas específicas.";
            }

            return string.Empty;
        }

        // ─────────────── Helpers ───────────────

        private static string DescribeVariacion(
            decimal? variacion,
            decimal actual,
            decimal anterior,
            string subieron,
            string bajaron,
            string iguales,
            string nuevo)
        {
            if (!variacion.HasValue)
            {
                // Mes anterior en cero. Si este mes hay movimiento, es "nuevo movimiento".
                return actual > 0 ? nuevo : iguales;
            }

            if (variacion.Value > 0)
            {
                return string.Format(Culture, subieron, FormatPercent(variacion.Value));
            }

            if (variacion.Value < 0)
            {
                return string.Format(Culture, bajaron, FormatPercent(Math.Abs(variacion.Value)));
            }

            return iguales;
        }

        private static string FormatPercent(decimal value) =>
            value.ToString("0.#", Culture) + "%";
    }
}
