namespace LuxuryApp.Models.Reports
{
    /// <summary>De dónde proviene un destinatario del resumen mensual.</summary>
    public enum MonthlyReportRecipientSource
    {
        /// <summary>Cuenta administrador/dueño activa del negocio.</summary>
        Admin,

        /// <summary>Correo agregado manualmente en la configuración.</summary>
        Manual
    }

    /// <summary>Por qué un candidato fue excluido de la lista de envío.</summary>
    public enum MonthlyReportExclusionReason
    {
        Funcionario,
        InvalidEmail,
        NoEmail,
        Unconfirmed,
        InactiveUser,
        Duplicate
    }

    public sealed record MonthlyReportRecipient(
        string Email,
        string? DisplayName,
        MonthlyReportRecipientSource Source);

    public sealed record MonthlyReportExcludedRecipient(
        string Email,
        string? DisplayName,
        MonthlyReportRecipientSource Source,
        MonthlyReportExclusionReason Reason)
    {
        public string ReasonText => Reason switch
        {
            MonthlyReportExclusionReason.Funcionario => "Funcionario",
            MonthlyReportExclusionReason.InvalidEmail => "Correo inválido",
            MonthlyReportExclusionReason.NoEmail => "Sin correo",
            MonthlyReportExclusionReason.Unconfirmed => "Correo no confirmado",
            MonthlyReportExclusionReason.InactiveUser => "Usuario inactivo",
            MonthlyReportExclusionReason.Duplicate => "Duplicado",
            _ => "Excluido"
        };
    }

    /// <summary>
    /// Resultado de resolver los destinatarios de un tenant: incluidos (a quienes se enviará)
    /// y excluidos con el motivo, para poder mostrar una vista previa clara en la UI.
    /// </summary>
    public sealed record MonthlyReportRecipientResolution(
        IReadOnlyList<MonthlyReportRecipient> Included,
        IReadOnlyList<MonthlyReportExcludedRecipient> Excluded)
    {
        public static readonly MonthlyReportRecipientResolution Empty =
            new(Array.Empty<MonthlyReportRecipient>(), Array.Empty<MonthlyReportExcludedRecipient>());

        public IReadOnlyList<string> IncludedEmails =>
            Included.Select(r => r.Email).ToList();
    }
}
