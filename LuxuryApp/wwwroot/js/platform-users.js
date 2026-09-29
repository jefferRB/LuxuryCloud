// Validación de UX para la zona peligrosa de desactivación de usuarios.
// La verdad la valida el servidor; esto solo evita errores obvios antes de enviar.
(function () {
    "use strict";

    var form = document.getElementById("deactivate-form");
    if (!form) {
        return;
    }

    var submitButton = document.getElementById("deactivate-submit");
    var ackCheck = document.getElementById("ack-check");
    var emailInput = form.querySelector('[name="ConfirmationEmail"]');
    var tenantInput = form.querySelector('[name="ConfirmationTenantName"]');
    var reasonInput = form.querySelector('[name="Reason"]');

    // textContent/dataset: nunca interpolamos HTML del servidor.
    var expectedEmail = (form.dataset.expectedEmail || "").trim().toLowerCase();
    var expectedTenant = (form.dataset.expectedTenant || "").trim().toLowerCase();

    function normalize(el) {
        return el && el.value ? el.value.trim().toLowerCase() : "";
    }

    function evaluate() {
        var emailOk = normalize(emailInput) === expectedEmail && expectedEmail.length > 0;
        var tenantOk = normalize(tenantInput) === expectedTenant && expectedTenant.length > 0;
        var reasonOk = reasonInput && reasonInput.value.trim().length >= 5;
        var ackOk = ackCheck && ackCheck.checked;

        if (submitButton) {
            submitButton.disabled = !(emailOk && tenantOk && reasonOk && ackOk);
        }
    }

    [emailInput, tenantInput, reasonInput].forEach(function (el) {
        if (el) {
            el.addEventListener("input", evaluate);
        }
    });
    if (ackCheck) {
        ackCheck.addEventListener("change", evaluate);
    }

    evaluate();
})();
