namespace LuxuryApp.Models.Finanzas
{
    public class DashboardViewModel
    {
        public decimal TotalIngresosMes { get; set; }

        public decimal TotalEgresosMes { get; set; }

        public decimal GananciaNeta => TotalIngresosMes - TotalEgresosMes;

        public int CantidadClientes { get; set; }

        public int CantidadCitasMes { get; set; }
        public decimal ValorInventarioProductos { get; set; }
        public int TotalProductosInventario { get; set; }

        public int MesSeleccionado { get; set; }
        public int AnioSeleccionado { get; set; }

        public decimal TotalServicios { get; set; }

        public decimal TotalProductos { get; set; }

        public decimal TotalGenerado { get; set; }

        public decimal TotalSinImpuestos { get; set; }

        public decimal TotalImpuestos { get; set; }

        /// <summary>CAJA: dinero efectivamente pagado a colaboradores durante el mes.</summary>
        public decimal TotalPagadoFuncionarios { get; set; }

        /// <summary>
        /// DEVENGADO: liquidaciones que generó la producción del mes, se hayan pagado o no.
        /// Es la línea que resta la ganancia (y la que ve el inversionista).
        /// </summary>
        public decimal TotalPagadoFuncionariosAnalitico { get; set; }

        /// <summary>
        /// CAJA: suma de TODOS los egresos con <c>FechaEgreso</c> dentro del mes, exactamente lo
        /// mismo que muestra la pantalla /Egresos con ese rango.
        ///
        /// <para>
        /// Puede diferir de <see cref="TotalEgresosAnaliticos"/> y eso es correcto: un pago hecho
        /// en agosto puede corresponder a producción de julio. <b>No entra en la ganancia.</b>
        /// </para>
        /// </summary>
        public decimal SalidasCajaMes { get; set; }

        /// <summary>
        /// DEVENGADO: costos y gastos económicos del mes = liquidaciones del equipo generadas por
        /// la producción del mes + gastos operativos elegibles. Es lo que resta la ganancia.
        /// </summary>
        public decimal TotalEgresosAnaliticos { get; set; }

        /// <summary>
        /// Resultado de CAJA del mes (ingresos netos − salidas de caja). No es la ganancia del
        /// negocio: mezcla una base devengada con salidas de efectivo. Se conserva porque el
        /// Resumen Ejecutivo Mensual lo viene reportando así.
        /// </summary>
        public decimal ResultadoCajaMes => TotalSinImpuestos - SalidasCajaMes;

        /// <summary>
        /// GANANCIA DEL MES (devengado). Única fórmula de ganancia del sistema, servida por
        /// <c>IPeriodProfitCalculationService</c>: ingresos netos − costos y gastos del mes.
        /// </summary>
        public decimal ResultadoAnalitico => TotalSinImpuestos - TotalEgresosAnaliticos;
        public decimal IngresosEfectivo { get; set; }
        public decimal IngresosSinpe { get; set; }
        public decimal IngresosTarjeta { get; set; }
        public List<decimal> GananciaPorMes { get; set; } = new();
        public List<decimal> ResultadoAnaliticoPorMes { get; set; } = new();

        /// <summary>
        /// Desglose del mes tal como lo devolvió el motor único de ganancia. Es la fuente de
        /// <see cref="TotalSinImpuestos"/>, <see cref="TotalImpuestos"/>,
        /// <see cref="TotalEgresosAnaliticos"/> y <see cref="ResultadoAnalitico"/>: si alguien
        /// necesita explicar de dónde salió un número del Dashboard, sale de acá.
        /// </summary>
        public Services.Finanzas.PeriodProfitBreakdown? Desglose { get; set; }

        /// <summary>
        /// Participación de los asociados sobre la ganancia del mes.
        ///
        /// <para>
        /// Es <c>null</c> cuando no hay ninguna participación vigente en el periodo. En ese caso
        /// el bloque no existe: no viaja al HTML ni al ViewModel, no se esconde con CSS. Lo
        /// construye <c>IAssociateProfitAllocationService</c> a partir del MISMO motor de ganancia
        /// que usan los estados de cuenta.
        /// </para>
        /// </summary>
        public Asociados.AssociateAllocationKpiViewModel? ParticipacionAsociados { get; set; }
    }
}
