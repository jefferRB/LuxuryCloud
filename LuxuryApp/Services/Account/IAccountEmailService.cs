namespace LuxuryApp.Services.Account
{
    public interface IAccountEmailService
    {
        Task SendPasswordResetEmailAsync(
            string toEmail,
            string displayName,
            string resetLink,
            CancellationToken cancellationToken = default);

        Task SendEmailConfirmationEmailAsync(
            string toEmail,
            string displayName,
            string confirmationLink,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Invitación al portal de funcionarios: el funcionario define su propia
        /// contraseña a través del enlace (token de restablecimiento).
        /// </summary>
        Task SendFuncionarioInvitationEmailAsync(
            string toEmail,
            string displayName,
            string setPasswordLink,
            string businessName,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Invitación genérica de acceso: misma plantilla y mismo transporte que la de
        /// funcionarios, con una frase que describe a qué se le está dando acceso a la persona.
        /// La usan los asociados (marketing, contabilidad, inversionistas con acceso…) para no
        /// duplicar infraestructura de correo.
        /// </summary>
        Task SendAccessInvitationEmailAsync(
            string toEmail,
            string displayName,
            string setPasswordLink,
            string businessName,
            string accessDescription,
            CancellationToken cancellationToken = default);
    }
}
