namespace LuxuryApp.Models.Layout
{
    /// <summary>
    /// Definicion de un tema visual del espacio privado.
    /// <para><b>Id</b>: identificador estable persistido en localStorage. Nunca se renombra.</para>
    /// <para><b>Background</b>: sufijo de la clase de fondo (<c>theme-bg-{Background}</c>).</para>
    /// <para><b>Surface</b>: sufijo de la clase de superficie (<c>surface-{Surface}</c>), que es
    /// la que define los tokens <c>--private-*</c> de cards, tablas, inputs y acentos.</para>
    /// </summary>
    /// <param name="Id">Identificador estable del tema.</param>
    /// <param name="Label">Nombre visible en el selector de apariencia.</param>
    /// <param name="Description">Copy corto mostrado bajo el nombre.</param>
    /// <param name="Background">Sufijo de la clase de fondo del <c>body</c>.</param>
    /// <param name="Surface">Sufijo de la clase de superficie del <c>body</c>.</param>
    public sealed record AppearanceTheme(
        string Id,
        string Label,
        string Description,
        string Background,
        string Surface)
    {
        /// <summary>Clase del preview usado por la tarjeta del selector.</summary>
        public string PreviewCssClass => $"private-theme-card-preview-{Background}";
    }
}
