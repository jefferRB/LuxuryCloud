namespace LuxuryApp.Services.Finanzas
{
    /// <summary>
    /// ¿Cuántas transacciones YA REGISTRADAS se reinterpretarían si se cambia esta configuración?
    ///
    /// <para>
    /// Desde la Fase 4 cada cobro nuevo congela su fiscalidad, así que editar el catálogo solo
    /// afecta ventas futuras. Pero los cobros anteriores al deploy no tienen snapshot y se siguen
    /// interpretando con la configuración vigente: para ellos, cambiar el IVA de un servicio SÍ
    /// mueve los números de meses ya cerrados. Es exactamente lo que pasó con LIMPIEZA FACIAL.
    /// </para>
    ///
    /// <para>
    /// No se bloquea la edición —el administrador necesitó corregir ese IVA y tenía razón—, pero sí
    /// se le avisa con números concretos y se le pide confirmar. El aviso se calcula en el servidor
    /// y se valida en el POST: esconderlo con JavaScript no sería una garantía.
    /// </para>
    /// </summary>
    public interface ILegacyFinancialImpactService
    {
        /// <summary>Cobros de este servicio SIN snapshot fiscal (anteriores a la Fase 4).</summary>
        Task<int> ContarCobrosLegacyDeServicioAsync(int servicioId, CancellationToken cancellationToken = default);

        /// <summary>Cobros de este producto SIN snapshot fiscal.</summary>
        Task<int> ContarCobrosLegacyDeProductoAsync(int productoId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Cobros del negocio SIN snapshot fiscal. La configuración del tenant es la que heredan
        /// todos los servicios y productos que no traen override propio, así que el alcance es
        /// mucho más amplio que el de una ficha suelta.
        /// </summary>
        Task<int> ContarCobrosLegacyDelNegocioAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Cobros del colaborador SIN snapshot de remuneración.
        ///
        /// <para>
        /// Solo esos se recalculan con la configuración vigente al cambiarla. Los cobros posteriores
        /// al snapshot de remuneración conservan el porcentaje, la base de comisión y la modalidad
        /// de IVA con los que nacieron, y no se ven afectados.
        /// </para>
        /// </summary>
        Task<int> ContarProduccionHistoricaDeColaboradorAsync(int funcionarioId, CancellationToken cancellationToken = default);
    }
}
