using LuxuryApp.Models.SaaS;

namespace LuxuryApp.Services.Billing
{
    /// <summary>
    /// Qué le decimos al cliente cuando vuelve del checkout. Clase pura (sin BD ni HTTP) porque el
    /// bug que la originó fue exactamente una decisión de presentación:
    ///
    /// El cliente intentó bajar de WA800 a WA400, el webhook quedó en revisión manual por monto y el
    /// retorno mostró "Pago confirmado y suscripcion activa" con el plan BASE ("LuxuryCloud Mensual
    /// 3 funcionarios") y "Estado del pago: Sin registro local". Nada de eso era cierto para lo que
    /// el cliente acababa de comprar.
    ///
    /// ORDEN OBLIGATORIO de las preguntas (de aquí sale la corrección):
    ///   1. ¿El pago está en revisión manual?  ⇒ nunca éxito.
    ///   2. ¿No pudimos correlacionar el pago? ⇒ error explícito, nunca el estado del plan base.
    ///   3. Recién entonces, ¿el producto quedó activo?
    /// </summary>
    public static class CheckoutReturnMessaging
    {
        /// <summary>Retorno de un ADD-ON de WhatsApp (paquete), separado del plan base.</summary>
        public static void ApplyAddon(ResultadoCheckoutViewModel model)
        {
            ArgumentNullException.ThrowIfNull(model);

            if (model.EnRevisionManual)
            {
                model.MensajePrincipal = "Tu pago quedó en revisión manual.";
                model.MensajeSecundario =
                    "No aplicamos el cambio automáticamente: estamos revisando una diferencia con el proveedor de pagos. " +
                    "Tu paquete de WhatsApp actual sigue funcionando igual y te avisamos apenas se resuelva.";
                return;
            }

            if (model.CorrelacionFallida)
            {
                model.MensajePrincipal = "No pudimos identificar este pago dentro de tu cuenta.";
                model.MensajeSecundario =
                    "Si ya pagaste tu paquete de WhatsApp, contactá soporte antes de volver a intentarlo para no pagar dos veces.";
                return;
            }

            if (model.SuscripcionActiva)
            {
                model.MensajePrincipal = "¡Tu paquete de WhatsApp quedó activo!";
                model.MensajeSecundario = "Ya podés usar las automatizaciones de WhatsApp según el límite mensual de tu paquete.";
                return;
            }

            if (model.PagoAprobadoPorProveedor)
            {
                model.MensajePrincipal = "Tilopay aprobó tu pago. Estamos activando tu paquete de WhatsApp.";
                model.MensajeSecundario = model.DebeAutoActualizar
                    ? $"Esta pantalla se actualizará automáticamente en {model.SegundosAutoActualizacion} segundos."
                    : "Pulsá Actualizar estado para consultar el resultado final.";
                return;
            }

            model.MensajePrincipal = "Recibimos tu pago del paquete de WhatsApp. Esperando la confirmación del proveedor.";
            model.MensajeSecundario = "Mantendremos esta referencia ligada a tu cuenta hasta completar la activación.";
        }

        /// <summary>Retorno del PLAN BASE.</summary>
        public static void ApplyBasePlan(
            ResultadoCheckoutViewModel model,
            bool hasLocalPayment,
            string? requestedReference)
        {
            ArgumentNullException.ThrowIfNull(model);

            if (model.EnRevisionManual)
            {
                model.MensajePrincipal = "Tu pago quedó en revisión manual.";
                model.MensajeSecundario =
                    "No aplicamos el cambio automáticamente: estamos revisando una diferencia con el proveedor de pagos. " +
                    "Tu acceso actual no cambió y te avisamos apenas se resuelva.";
                return;
            }

            if (model.CorrelacionFallida)
            {
                model.MensajePrincipal = "No pudimos identificar este pago dentro de tu cuenta.";
                model.MensajeSecundario = string.IsNullOrWhiteSpace(requestedReference)
                    ? "El proveedor no envió una referencia utilizable. Si ya pagaste, contactá soporte antes de volver a intentarlo para no pagar dos veces."
                    : $"No encontramos un intento de pago local para la referencia {requestedReference}. Si ya pagaste, contactá soporte antes de volver a intentarlo para no pagar dos veces.";
                return;
            }

            if (model.SuscripcionActiva)
            {
                model.MensajePrincipal = "Pago confirmado y suscripcion activa.";
                model.MensajeSecundario = "Tu acceso ya esta habilitado para continuar dentro del sistema.";
                return;
            }

            if (!hasLocalPayment)
            {
                if (string.IsNullOrWhiteSpace(requestedReference))
                {
                    model.MensajePrincipal = "No pudimos confirmar automaticamente este pago.";
                    model.MensajeSecundario = "Si ya pagaste, revisa el estado actual de tu suscripcion o intenta actualizar en unos segundos.";
                    return;
                }

                model.MensajePrincipal = "No encontramos un pago asociado a esa referencia dentro de tu tenant.";
                model.MensajeSecundario = "Verifica que ingresaste con la cuenta que inicio el checkout antes de volver a consultar.";
                return;
            }

            if (model.PagoAprobadoPorProveedor)
            {
                model.MensajePrincipal = "Tilopay aprobo tu pago. Estamos activando tu suscripcion.";
                model.MensajeSecundario = model.DebeAutoActualizar
                    ? $"Esta pantalla se actualizara automaticamente en {model.SegundosAutoActualizacion} segundos."
                    : "Pulsa Actualizar estado para consultar nuevamente el resultado final.";
                return;
            }

            model.MensajePrincipal = "Tu pago fue recibido. Estamos esperando la confirmacion final del proveedor.";
            model.MensajeSecundario = "Mantendremos esta referencia ligada a tu tenant hasta completar la activacion.";
        }
    }
}
