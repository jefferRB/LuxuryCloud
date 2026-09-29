namespace LuxuryApp.Models.Legal
{
    public static class ContractDocumentSeedData
    {
        public static readonly Guid InitialDocumentId = Guid.Parse("9D0D8C0B-4E22-44D1-B7D9-7BA6E95C52B1");
        public static readonly DateTime InitialEffectiveFromUtc = new(2026, 4, 21, 0, 0, 0, DateTimeKind.Utc);

        public static ContractDocument CreateInitialDocument()
        {
            var contentHtml = """
                <section class="contract-section">
                    <h2>1. Terminos y Condiciones</h2>
                    <p>Este documento corresponde a una version inicial editable del contrato de uso de LuxuryApp. Antes de salir a produccion debes reemplazar este texto por la version final aprobada por asesoria legal.</p>
                    <p>LuxuryApp presta un servicio SaaS para negocios de belleza, barberia, salon y operaciones relacionadas. El uso del servicio implica aceptar las reglas operativas, tecnicas y comerciales definidas en este contrato.</p>
                    <p>El cliente se compromete a utilizar la plataforma conforme a la ley aplicable, a no compartir accesos de manera indebida y a custodiar sus credenciales, usuarios y configuraciones internas.</p>
                    <p>LuxuryApp puede actualizar funciones, seguridad y procesos operativos para mejorar la disponibilidad, estabilidad y cumplimiento del servicio.</p>
                </section>
                <section class="contract-section">
                    <h2>2. Politica de Privacidad</h2>
                    <p>LuxuryApp trata la informacion necesaria para operar la cuenta, autenticar usuarios, administrar tenants, procesar pagos y mantener el funcionamiento del servicio.</p>
                    <p>El cliente declara que cuenta con la base legal necesaria para cargar datos de sus propios clientes, funcionarios y operaciones en la plataforma.</p>
                    <p>Debes reemplazar esta seccion por la politica de privacidad definitiva, incluyendo finalidades, base juridica, plazos de conservacion, medidas de seguridad, transferencias y canales de ejercicio de derechos.</p>
                </section>
                <section class="contract-section">
                    <h2>3. Politica de pagos, cancelaciones y reembolsos</h2>
                    <p>El acceso comercial a LuxuryApp depende del plan contratado, sus condiciones de cobro, renovacion, suspension y reactivacion.</p>
                    <p>Debes completar esta seccion con las condiciones finales de facturacion, fechas de corte, reglas de cancelacion, periodos de aviso, politica de mora y escenarios de reembolso permitidos o no permitidos.</p>
                    <p>Mientras esta version placeholder siga vigente, ninguna clausula aqui incluida debe considerarse texto legal final para produccion.</p>
                </section>
                <section class="contract-section">
                    <h2>4. Consentimiento de tratamiento de datos</h2>
                    <p>Al aceptar este contrato el usuario declara que ha leido el alcance del tratamiento de datos relacionado con la operacion de la cuenta, la seguridad del servicio y el soporte tecnico.</p>
                    <p>Debes sustituir este apartado por el consentimiento final aprobado, incluyendo categorias de datos, finalidad, responsables, encargados, revocatoria y demas extremos regulatorios aplicables.</p>
                    <p>La aceptacion registrada por el sistema conserva fecha, direccion IP, agente de usuario, version del documento y hash del contenido aceptado para fines de trazabilidad y cumplimiento.</p>
                </section>
                """;

            // Los raw string literals heredan los line endings del archivo fuente (CRLF en
            // checkouts Windows, LF en Linux). Sin normalizar, el hash del contrato cambia
            // según la máquina que genere una migración y EF re-emite un UpdateData que
            // alteraría el documento legal en producción. Producción tiene la versión LF.
            contentHtml = contentHtml.Replace("\r\n", "\n");

            var now = InitialEffectiveFromUtc;

            return new ContractDocument
            {
                Id = InitialDocumentId,
                Title = "Contrato de Uso del Servicio LuxuryApp",
                VersionNumber = "1.0.0",
                ContentHtml = contentHtml,
                ContentHash = ContractHashing.ComputeSha256(contentHtml),
                IsActive = true,
                EffectiveFromUtc = InitialEffectiveFromUtc,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
        }
    }
}
