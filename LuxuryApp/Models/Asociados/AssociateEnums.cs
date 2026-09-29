namespace LuxuryApp.Models.Asociados
{
    /// <summary>
    /// Relación de negocio de un asociado. Los valores son estables (viajan en formularios y
    /// quedan guardados en base de datos): nunca reordenar ni reutilizar un número.
    ///
    /// <para>
    /// IMPORTANTE: el tipo NUNCA autoriza. Describe qué es la persona para el negocio; lo que
    /// puede hacer dentro del sistema lo deciden EXCLUSIVAMENTE los permisos
    /// (<see cref="AppPermissions"/>). Un asociado de tipo Marketing sin permisos no ve nada.
    /// </para>
    /// </summary>
    public enum AssociateType
    {
        Inversionista = 0,
        Socio = 1,
        Marketing = 2,
        Contabilidad = 3,
        Administracion = 4,
        Asesor = 5,
        Otro = 6
    }

    /// <summary>Estado del acceso al sistema de un asociado.</summary>
    public enum AssociateAccessState
    {
        /// <summary>No tiene cuenta: existe solo como registro del negocio.</summary>
        SinAcceso = 0,

        /// <summary>Tiene cuenta y puede iniciar sesión.</summary>
        AccesoActivo = 1,

        /// <summary>Tiene cuenta pero el acceso está bloqueado.</summary>
        AccesoBloqueado = 2
    }

    /// <summary>Cómo se define la contraseña al habilitar el acceso por primera vez.</summary>
    public enum AssociateCredentialMode
    {
        /// <summary>Cuenta con contraseña aleatoria + correo de invitación para definirla.</summary>
        Invitacion = 0,

        /// <summary>El administrador define una contraseña temporal en el formulario.</summary>
        ContrasenaTemporal = 1
    }

    /// <summary>Presets de permisos ofrecidos como ayuda de UX (nunca como regla rígida).</summary>
    public enum AssociatePermissionPreset
    {
        Personalizado = 0,
        Marketing = 1,
        Inversionista = 2,
        SoloLectura = 3,
        Contabilidad = 4
    }

    public static class AssociateTypeTexts
    {
        public static string Describe(AssociateType tipo) => tipo switch
        {
            AssociateType.Inversionista => "Inversionista",
            AssociateType.Socio => "Socio",
            AssociateType.Marketing => "Marketing",
            AssociateType.Contabilidad => "Contabilidad",
            AssociateType.Administracion => "Administración",
            AssociateType.Asesor => "Asesor",
            _ => "Otro"
        };

        /// <summary>Todos los tipos en orden de presentación.</summary>
        public static readonly IReadOnlyList<AssociateType> Todos = new[]
        {
            AssociateType.Inversionista,
            AssociateType.Socio,
            AssociateType.Marketing,
            AssociateType.Contabilidad,
            AssociateType.Administracion,
            AssociateType.Asesor,
            AssociateType.Otro
        };
    }
}
