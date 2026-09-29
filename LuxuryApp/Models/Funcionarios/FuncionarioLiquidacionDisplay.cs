using System.Globalization;
using LuxuryApp.Models.Fiscal;

namespace LuxuryApp.Models.Funcionarios
{
    /// <summary>
    /// Textos y estados de presentación para la Liquidación Semanal. Centraliza el mapeo de
    /// enums fiscales a etiquetas en español para no duplicarlo entre la vista y el Excel.
    /// NO contiene cálculo fiscal (solo presentación de datos ya calculados).
    /// </summary>
    public static class FuncionarioLiquidacionDisplay
    {
        public static string Relacion(TipoRelacionColaborador tipo) => tipo switch
        {
            TipoRelacionColaborador.Independiente => "Independiente",
            TipoRelacionColaborador.AlquilerSilla => "Alquiler de silla",
            _ => "Empleado"
        };

        public static string ComisionSobre(ComisionCalculadaSobre sobre) => sobre switch
        {
            ComisionCalculadaSobre.BaseSinIva => "Base sin IVA",
            _ => "Total cobrado"
        };

        /// <summary>Explicación corta (para la card cerrada) que cambia según la regla de comisión.</summary>
        public static string ExplicacionComisionCorta(ComisionCalculadaSobre sobre) => sobre switch
        {
            ComisionCalculadaSobre.BaseSinIva =>
                "El pago se calcula sobre la base sin IVA. El IVA de venta se separa antes de calcular la comisión.",
            _ =>
                "El pago se calcula sobre el total cobrado. La comisión toma como base el monto con IVA incluido."
        };

        /// <summary>Explicación larga (para el expandible) que cambia según la regla de comisión.</summary>
        public static string ExplicacionComisionLarga(ComisionCalculadaSobre sobre) => sobre switch
        {
            ComisionCalculadaSobre.BaseSinIva =>
                "La comisión se calcula sobre la base sin IVA. El IVA de venta se separa del precio " +
                "cobrado al cliente y no forma parte de la comisión.",
            _ =>
                "La comisión se calcula sobre el total cobrado. En este caso el monto con IVA incluido " +
                "sí forma parte de la base usada para calcular la comisión."
        };

        private static readonly CultureInfo CrCulture = CultureInfo.GetCultureInfo("es-CR");

        private static string Crc(decimal monto) => "₡" + monto.ToString("N0", CrCulture);

        private static string Pct(decimal porcentaje) =>
            porcentaje == Math.Truncate(porcentaje)
                ? porcentaje.ToString("0", CrCulture) + "%"
                : porcentaje.ToString("0.##", CrCulture) + "%";

        /// <summary>
        /// Mini fórmula de la comisión, PURAMENTE presentacional: formatea los valores YA
        /// calculados por el motor fiscal (no recalcula nada). Ej.: "50% × ₡42.000 = ₡21.000".
        /// Si hay servicios y productos con reglas propias, muestra ambos sumandos.
        /// </summary>
        public static string FormulaComision(PagoFuncionarioVM f)
        {
            var haySer = f.TotalServicios > 0 && f.Porcentaje > 0;
            var hayPro = f.TotalProductos > 0 && f.PorcentajeProducto > 0;

            // Caso simple (solo servicios o solo productos): "50% × ₡42.000 = ₡21.000".
            if (haySer && !hayPro)
            {
                return $"{Pct(f.Porcentaje)} × {Crc(BaseDesde(f.BaseComisionServicios, f.Porcentaje))} = {Crc(f.BaseComisionServicios)}";
            }

            if (hayPro && !haySer)
            {
                return $"{Pct(f.PorcentajeProducto)} × {Crc(BaseDesde(f.BaseComisionProductos, f.PorcentajeProducto))} = {Crc(f.BaseComisionProductos)}";
            }

            // Servicios y productos con reglas propias: mostrar los dos sumandos ya calculados.
            if (haySer && hayPro)
            {
                return $"Servicios {Crc(f.BaseComisionServicios)} + Productos {Crc(f.BaseComisionProductos)} = {Crc(f.BaseComisionServicios + f.BaseComisionProductos)}";
            }

            return string.Empty;

            // La base mostrada se deriva de la comisión YA calculada (comisión ÷ %), para no
            // duplicar la lógica fiscal aquí. Redondeo solo de presentación.
            static decimal BaseDesde(decimal comision, decimal porcentaje) =>
                porcentaje > 0 ? Math.Round(comision * 100m / porcentaje, 0, MidpointRounding.ToEven) : 0m;
        }

        /// <summary>Etiqueta de la modalidad de IVA del colaborador.</summary>
        public static string Modalidad(ModalidadIvaColaborador m) => m switch
        {
            ModalidadIvaColaborador.IvaIncluido => "IVA incluido en su parte",
            ModalidadIvaColaborador.IvaAdicional => "IVA adicional",
            _ => "No factura IVA"
        };

        /// <summary>El colaborador factura IVA en alguna modalidad (B o C) y es independiente.</summary>
        public static bool FacturaIva(PagoFuncionarioVM f) =>
            f.TipoRelacionColaborador == TipoRelacionColaborador.Independiente
            && f.ModalidadIvaColaborador != ModalidadIvaColaborador.NoFactura;

        private static string Divisor(decimal tarifa)
        {
            var d = 1m + (tarifa / 100m);
            return d.ToString("0.##", CrCulture);
        }

        /// <summary>
        /// Explicación del IVA del colaborador con montos reales, según su modalidad.
        /// A: no factura. B: IVA incluido dentro de su parte. C: IVA adicional sobre la comisión.
        /// </summary>
        public static string ExplicacionIva(PagoFuncionarioVM f)
        {
            var sobre = f.ComisionCalculadaSobre == ComisionCalculadaSobre.BaseSinIva
                ? "la base sin IVA"
                : "el total cobrado";

            if (FacturaIva(f) && f.ModalidadIvaColaborador == ModalidadIvaColaborador.IvaIncluido)
            {
                return $"Se calcula el % del colaborador sobre {sobre}. El monto resultante " +
                       $"({Crc(f.MontoColaborador)}) ya incluye su IVA, por lo que se descompone en base " +
                       $"({Crc(f.BaseColaborador)}) e IVA ({Crc(f.IvaColaborador)}). El IVA neto del negocio " +
                       $"es el IVA de venta menos el IVA facturado por el colaborador.";
            }

            if (FacturaIva(f) && f.ModalidadIvaColaborador == ModalidadIvaColaborador.IvaAdicional)
            {
                return $"El colaborador factura IVA adicional sobre su comisión. El total a pagar es la base " +
                       $"de comisión ({Crc(f.BaseColaborador)}) más el IVA colaborador ({Crc(f.IvaColaborador)}).";
            }

            // A) No factura IVA.
            return f.ComisionCalculadaSobre == ComisionCalculadaSobre.BaseSinIva
                ? $"El IVA de venta ({Crc(f.IvaVentaIncluido)}) se separa antes de calcular la comisión " +
                  "y no forma parte de la base de pago. No se paga IVA adicional al colaborador."
                : $"La comisión se calcula sobre el total cobrado. El IVA de venta ({Crc(f.IvaVentaIncluido)}) " +
                  "queda del lado del negocio. No se paga IVA adicional al colaborador.";
        }

        /// <summary>
        /// Fórmula del tratamiento del IVA del colaborador (para el expandible), con los montos ya
        /// calculados por el motor. Vacía si no factura IVA.
        /// </summary>
        public static string FormulaIvaColaborador(PagoFuncionarioVM f)
        {
            if (!FacturaIva(f))
            {
                return string.Empty;
            }

            if (f.ModalidadIvaColaborador == ModalidadIvaColaborador.IvaIncluido)
            {
                return $"{Crc(f.MontoColaborador)} ÷ {Divisor(f.TarifaIvaColaborador)} = {Crc(f.BaseColaborador)} base · " +
                       $"IVA colaborador {Crc(f.IvaColaborador)} · " +
                       $"IVA neto negocio = {Crc(f.IvaVentaIncluido)} − {Crc(f.IvaColaborador)} = {Crc(f.IvaNetoNegocio)}";
            }

            // IVA adicional (C)
            return $"Base {Crc(f.BaseColaborador)} + IVA colaborador {Crc(f.IvaColaborador)} = {Crc(f.TotalAPagarColaborador)}";
        }

        /// <summary>
        /// Estado de factura del colaborador para el badge. No hay modelo de "factura recibida",
        /// así que los estados son limitados y ninguno bloquea el pago. Tono: neutral|info|warning.
        /// </summary>
        public static (string Texto, string Tono) EstadoFactura(PagoFuncionarioVM f)
        {
            if (!FacturaIva(f))
            {
                return ("No aplica", "neutral");
            }

            return f.RequiereFacturaAntesDePagar
                ? ("Pendiente factura", "warning")
                : ("Factura IVA", "info");
        }

        /// <summary>Advertencia suave: independiente que exige factura antes de pagar (sin bloqueo).</summary>
        public static bool RequiereFacturaPendiente(PagoFuncionarioVM f) =>
            FacturaIva(f) && f.RequiereFacturaAntesDePagar;
    }
}
