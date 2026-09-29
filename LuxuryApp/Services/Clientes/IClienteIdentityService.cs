namespace LuxuryApp.Services.Clientes
{
    /// <summary>
    /// Responsabilidad ÚNICA de identificar a un cliente del negocio a partir de lo que se escribe
    /// en un formulario (nombre + teléfono) y, cuando corresponde, darlo de alta.
    ///
    /// <para>
    /// La señal de identidad es el <b>teléfono normalizado</b>. El nombre NO identifica a nadie:
    /// dos personas pueden llamarse igual y la misma persona se escribe de diez formas distintas.
    /// </para>
    ///
    /// <para>
    /// Todas las consultas van contra el contexto del tenant actual (global query filter + RLS).
    /// Nunca recibe ni acepta un TenantId del exterior.
    /// </para>
    /// </summary>
    public interface IClienteIdentityService
    {
        Task<ClienteIdentityResolution> ResolveAsync(
            string? nombre,
            string? telefono,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Cliente ya existente por Id, validado contra el tenant actual. <c>null</c> si no existe
        /// o pertenece a otro negocio.
        /// </summary>
        Task<ClienteIdentityMatch?> FindByIdAsync(int clienteId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Crea el cliente. Se llama SIEMPRE después de re-resolver (backend autoritativo) y dentro
        /// de la transacción de quien lo invoca, para que no queden clientes huérfanos si la
        /// operación principal falla.
        /// </summary>
        Task<ClienteIdentityMatch> RegisterAsync(
            ClienteRegistrationRequest request,
            CancellationToken cancellationToken = default);
    }
}
