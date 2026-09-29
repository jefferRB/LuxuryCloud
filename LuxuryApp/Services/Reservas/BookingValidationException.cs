namespace LuxuryApp.Services.Reservas
{
    /// <summary>
    /// Error de validación de negocio en el flujo de reservas. El mensaje es seguro para
    /// mostrarse al usuario (no contiene detalles técnicos ni datos internos).
    /// </summary>
    public sealed class BookingValidationException : Exception
    {
        public BookingValidationException(string message) : base(message)
        {
        }

        public BookingValidationException(string message, string? field) : base(message)
        {
            Field = field;
        }

        /// <summary>Nombre del campo del formulario al que pertenece el error (para validación por campo). Null = error general.</summary>
        public string? Field { get; }
    }
}
