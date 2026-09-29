namespace LuxuryApp.Services.Payments
{
    /// <summary>
    /// Bloqueo de negocio: el tenant tiene un pago recurrente reciente en revision manual y
    /// abrir otro checkout podria crear una segunda suscripcion viva en el proveedor
    /// (TiloPay Repeat no tiene API de cancelacion, el doble cobro seria mensual).
    /// El mensaje es seguro para mostrarse al usuario final.
    /// </summary>
    public sealed class RecurringCheckoutBlockedException : InvalidOperationException
    {
        public RecurringCheckoutBlockedException(string message)
            : base(message)
        {
        }
    }
}
