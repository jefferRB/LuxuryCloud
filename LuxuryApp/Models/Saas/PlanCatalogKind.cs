namespace LuxuryApp.Models.SaaS
{
    /// <summary>
    /// Clasificacion de un plan del catalogo segun el rol que cumple comercialmente.
    /// Existe para que "plan base" y "add-on" dejen de ser el mismo tipo de fila indistinguible:
    /// un selector de plan BASE jamas debe ofrecer un paquete de WhatsApp, y la validacion
    /// server-side debe poder rechazarlo sin depender de strings magicos en la vista.
    /// </summary>
    public enum PlanCatalogKind
    {
        /// <summary>No se pudo clasificar (codigo nulo o desconocido). Se trata como no valido para plan base.</summary>
        Unknown = 0,

        /// <summary>Plan base comercial vigente de la calculadora: LC_M_01..11 / LC_A_01..11.</summary>
        BaseCommercial = 1,

        /// <summary>Add-on de mensajeria WhatsApp (WA400/WA800/WA1200). NUNCA es plan base.</summary>
        WhatsAppAddon = 2,

        /// <summary>Plan base historico previo a la calculadora (BASIC/PRO/BUSINESS). Solo migracion.</summary>
        LegacyBase = 3,

        /// <summary>Plan de prueba/validacion (EsPlanValidacion, TEST_RECURRING, TEST_PROD_BASIC_100).</summary>
        Validation = 4
    }
}
