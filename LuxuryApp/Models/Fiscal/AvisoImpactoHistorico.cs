namespace LuxuryApp.Models.Fiscal
{
    /// <summary>
    /// Textos del aviso que se muestra cuando una edición de configuración financiera puede
    /// reinterpretar transacciones ya registradas.
    ///
    /// <para>
    /// Viven acá y no dispersos en las vistas para que digan lo mismo en todos lados y para que los
    /// tests puedan verificar que el aviso realmente aparece. El tono es deliberadamente preciso:
    /// "puede modificar cómo se muestran algunos periodos históricos" y no "esto cambiará todos sus
    /// datos", que sería falso y enseñaría al usuario a ignorar la advertencia.
    /// </para>
    /// </summary>
    public static class AvisoImpactoHistorico
    {
        /// <summary>Nombre del campo del formulario que confirma el cambio.</summary>
        public const string CampoConfirmacion = "confirmarImpactoHistorico";

        public static string Servicio(int cobros) =>
            $"Este servicio tiene {Transacciones(cobros)} creados antes de que LuxuryCloud guardara " +
            "la configuración fiscal por transacción. Cambiar esta configuración puede modificar " +
            "cómo se muestran algunos periodos históricos. Los cobros registrados de ahora en " +
            "adelante no se ven afectados.";

        public static string Producto(int cobros) =>
            $"Este producto tiene {Transacciones(cobros)} creados antes de que LuxuryCloud guardara " +
            "la configuración fiscal por transacción. Cambiar esta configuración puede modificar " +
            "cómo se muestran algunos periodos históricos. Los cobros registrados de ahora en " +
            "adelante no se ven afectados.";

        public static string Negocio(int cobros) =>
            $"El negocio tiene {Transacciones(cobros)} creados antes de que LuxuryCloud guardara la " +
            "configuración fiscal por transacción, y esos cobros heredan la configuración de esta " +
            "pantalla. Cambiarla puede modificar cómo se muestran algunos periodos históricos. Los " +
            "cobros registrados de ahora en adelante no se ven afectados.";

        public static string Colaborador(int cobros) =>
            $"Este colaborador tiene {Transacciones(cobros)} creados antes de que LuxuryCloud " +
            "guardara la configuración de liquidación por transacción. Cambiar el porcentaje, la " +
            "base de comisión o la modalidad de IVA puede modificar cálculos históricos de esos " +
            "cobros, incluidos periodos ya pagados. Los cobros nuevos, que sí tienen su " +
            "configuración congelada, no se ven afectados.";

        /// <summary>Instrucción única para todos los formularios.</summary>
        public const string Confirmacion = "Marcá la casilla de confirmación para guardar el cambio.";

        private static string Transacciones(int cobros) =>
            cobros == 1 ? "1 cobro histórico" : $"{cobros:N0} cobros históricos";
    }
}
