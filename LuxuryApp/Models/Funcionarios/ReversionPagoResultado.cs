namespace LuxuryApp.Models.Funcionarios
{
    /// <summary>Desenlace de una reversión de pago. Ningún caso es una excepción: los tres son respuestas válidas.</summary>
    public enum ReversionPagoEstado
    {
        /// <summary>Se deshizo la operación completa (liquidación, detalles, distribuciones y egreso).</summary>
        Revertida = 0,

        /// <summary>Ya estaba revertida. Es el segundo click del usuario, no un error.</summary>
        YaRevertida = 1,

        /// <summary>No existe esa operación en este negocio (id inválido, o de otro tenant).</summary>
        NoEncontrada = 2
    }

    /// <summary>
    /// Resultado de revertir un pago. Lleva el monto y los colaboradores para poder confirmarle al
    /// usuario exactamente qué se deshizo.
    /// </summary>
    public sealed record ReversionPagoResultado(
        ReversionPagoEstado Estado,
        decimal MontoRevertido,
        IReadOnlyList<string> Funcionarios);
}
