namespace LuxuryApp.Models.Funcionarios
{
    /// <summary>Estado del acceso al portal de un funcionario.</summary>
    public enum FuncionarioAccesoEstado
    {
        /// <summary>El funcionario no tiene cuenta de acceso.</summary>
        SinAcceso = 0,

        /// <summary>Tiene cuenta y puede iniciar sesión.</summary>
        AccesoActivo = 1,

        /// <summary>Tiene cuenta pero el acceso está bloqueado/desactivado.</summary>
        AccesoBloqueado = 2
    }

    /// <summary>Cómo se define la contraseña al habilitar el acceso por primera vez.</summary>
    public enum FuncionarioAccesoCredencialModo
    {
        /// <summary>Se crea con contraseña aleatoria y se envía invitación por correo para definirla.</summary>
        Invitacion = 0,

        /// <summary>El administrador define una contraseña temporal en el formulario.</summary>
        ContrasenaTemporal = 1
    }

    /// <summary>Información de acceso para mostrar en Editar Funcionario.</summary>
    public sealed class FuncionarioAccesoViewModel
    {
        public int FuncionarioId { get; init; }
        public string FuncionarioNombre { get; init; } = string.Empty;
        public bool FuncionarioActivo { get; init; }
        public FuncionarioAccesoEstado Estado { get; init; }
        public string? Email { get; init; }

        public bool TienePortal => Estado != FuncionarioAccesoEstado.SinAcceso;
    }

    /// <summary>Resultado de una operación sobre el acceso del funcionario.</summary>
    public sealed class FuncionarioAccesoResultado
    {
        public bool Exitoso { get; init; }
        public IReadOnlyList<string> Errores { get; init; } = Array.Empty<string>();

        public string? UserId { get; init; }
        public string? Email { get; init; }

        /// <summary>Nombre a mostrar en el correo (nombre del funcionario).</summary>
        public string? NombreParaCorreo { get; init; }

        /// <summary>
        /// Token de restablecimiento ya codificado (Base64Url) cuando se debe enviar
        /// invitación o enlace para definir contraseña. Null si no aplica.
        /// </summary>
        public string? EnlaceTokenCodificado { get; init; }

        /// <summary>True cuando el controlador debe enviar el correo de invitación.</summary>
        public bool RequiereCorreoInvitacion { get; init; }

        public static FuncionarioAccesoResultado Falla(params string[] errores) =>
            new() { Exitoso = false, Errores = errores };
    }
}
