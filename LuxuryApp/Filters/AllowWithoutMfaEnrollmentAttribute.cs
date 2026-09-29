namespace LuxuryApp.Filters
{
    /// <summary>
    /// Excluye una acción del redirect forzado de <see cref="RequireMfaEnrollmentFilter"/>.
    /// Solo deben marcarse las rutas del propio enrolamiento, la verificación de código del
    /// login, el logout, el gate de contrato y la página de acceso denegado: cualquier otra
    /// exclusión abre un agujero en la obligatoriedad del MFA para superadmins.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class AllowWithoutMfaEnrollmentAttribute : Attribute
    {
    }
}
