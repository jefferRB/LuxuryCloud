namespace LuxuryApp.Models.Fiscal
{
    /// <summary>
    /// Configuración de remuneración con la que se liquida un cobro: la CONGELADA al registrarlo
    /// (snapshot) o, para cobros anteriores al sistema de snapshots, la que tiene hoy el colaborador.
    ///
    /// <para>
    /// Son exactamente los seis valores que consume <c>ILiquidacionFuncionarioService.Liquidar</c>.
    /// Ni uno más: no se congela lo que no participa en el cálculo.
    /// </para>
    ///
    /// <para>
    /// <b>Esto NO calcula comisión.</b> Entrega los INPUTS efectivos; la fórmula sigue siendo
    /// propiedad exclusiva del motor. Si un día la comisión cambia de fórmula, cambia en un solo
    /// lugar y este tipo ni se entera.
    /// </para>
    ///
    /// <para>
    /// Es un <c>record struct</c> a propósito: la igualdad por valor es lo que permite agrupar
    /// cobros por "misma configuración" sin escribir un comparador a mano.
    /// </para>
    /// </summary>
    public readonly record struct CobroRemuneracionEfectiva(
        decimal PorcentajeServicios,
        decimal PorcentajeProductos,
        ComisionCalculadaSobre ComisionCalculadaSobre,
        TipoRelacionColaborador TipoRelacion,
        ModalidadIvaColaborador ModalidadIva,
        decimal TarifaIvaColaborador,
        bool DesdeSnapshot)
    {
        /// <summary>
        /// Resuelve la remuneración efectiva de un cobro.
        ///
        /// <para>
        /// O manda el snapshot COMPLETO, o manda la configuración actual del colaborador. Nunca se
        /// mezclan: liquidar con el porcentaje viejo y la modalidad de IVA nueva daría un número que
        /// no existió nunca. El CHECK constraint CK_Cobros_SnapshotRemuneracion garantiza a nivel de
        /// base que no existan snapshots a medias, y acá se respeta esa garantía.
        /// </para>
        /// </summary>
        public static CobroRemuneracionEfectiva Resolver(
            ICobroRemuneracionSnapshot snapshot,
            CobroRemuneracionEfectiva actual)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            if (snapshot.PorcentajeServicioSnapshot is not { } porcentajeServicios ||
                snapshot.PorcentajeProductoSnapshot is not { } porcentajeProductos ||
                snapshot.ComisionCalculadaSobreSnapshot is not { } comisionSobre ||
                snapshot.TipoRelacionColaboradorSnapshot is not { } tipoRelacion ||
                snapshot.ModalidadIvaColaboradorSnapshot is not { } modalidadIva ||
                snapshot.TarifaIvaColaboradorSnapshot is not { } tarifaColaborador)
            {
                return actual with { DesdeSnapshot = false };
            }

            return new CobroRemuneracionEfectiva(
                porcentajeServicios,
                porcentajeProductos,
                comisionSobre,
                tipoRelacion,
                modalidadIva,
                tarifaColaborador,
                DesdeSnapshot: true);
        }

        /// <summary>
        /// Clave de agrupación: la configuración SIN el origen. Dos cobros que se liquidan igual
        /// tienen que caer en el mismo grupo aunque uno traiga snapshot y el otro sea legacy —
        /// si no, un periodo mixto daría un resultado distinto al de un periodo homogéneo con los
        /// mismos números, que es precisamente lo que no puede pasar.
        /// </summary>
        public CobroRemuneracionEfectiva ClaveDeAgrupacion() => this with { DesdeSnapshot = false };
    }

    /// <summary>
    /// Los seis campos de remuneración congelados en un cobro. Los implementan la entidad
    /// <c>Cobro</c> y las proyecciones que leen cobros desde la base.
    ///
    /// <para>Todos NULL = cobro LEGACY. Todos con valor = historia completa. No hay punto medio.</para>
    /// </summary>
    public interface ICobroRemuneracionSnapshot
    {
        decimal? PorcentajeServicioSnapshot { get; }
        decimal? PorcentajeProductoSnapshot { get; }
        ComisionCalculadaSobre? ComisionCalculadaSobreSnapshot { get; }
        TipoRelacionColaborador? TipoRelacionColaboradorSnapshot { get; }
        ModalidadIvaColaborador? ModalidadIvaColaboradorSnapshot { get; }
        decimal? TarifaIvaColaboradorSnapshot { get; }
    }
}
