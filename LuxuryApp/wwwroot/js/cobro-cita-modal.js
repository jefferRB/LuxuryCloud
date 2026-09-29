/* ===========================================================================
   Cobro rápido por cita — modal compartido.
   Única fuente de verdad para abrir/enviar el cobro de una cita. Lo reutilizan:
     - Control de citas y cobros (control-cobros.js)
     - Calendario (calendar.js)
   Markup: Views/Shared/_RegistrarCobroModal.cshtml.  Endpoint: POST /Calendar/CobrarCita.
   Uso:
     window.cobroCitaModal.open(
        { citaId, cliente, servicio, precio, email, clienteId },
        function onSuccess(message) { ...refrescar vista... });
   =========================================================================== */
(function () {
    "use strict";

    var modalEl = document.getElementById("ccCobroModal");
    if (!modalEl) return; // El partial no está en esta página.

    var modal = window.bootstrap ? bootstrap.Modal.getOrCreateInstance(modalEl) : null;
    var onSuccessCb = null;

    function el(id) { return document.getElementById(id); }

    function token() {
        var t = document.querySelector(
            "#cobro-cita-antiforgery input[name='__RequestVerificationToken']");
        return t ? t.value : "";
    }

    function money(v) {
        return "₡" + Number(v).toLocaleString("es-CR", {
            minimumFractionDigits: 2,
            maximumFractionDigits: 2
        });
    }

    // ── Desglose fiscal en vivo ───────────────────────────────────────────────
    // El cálculo lo hace SIEMPRE el backend (motor fiscal central). El JS solo pide y
    // renderiza; no hay ninguna fórmula de IVA aquí.
    var fiscalTimer = null;
    var fiscalReqId = 0;

    function hideFiscal() {
        var card = el("ccFiscalCard");
        if (card) card.classList.add("d-none");
    }

    function renderFiscal(d) {
        var card = el("ccFiscalCard");
        if (!card) return;
        el("ccFiscalTotal").textContent = money(d.total);
        el("ccFiscalBase").textContent = money(d.baseSinIva);
        el("ccFiscalIva").textContent = money(d.iva);
        var lbl = el("ccFiscalIvaLabel");
        if (lbl) {
            lbl.textContent = d.aplicaIva
                ? ("IVA incluido " + Number(d.tarifaIva).toLocaleString("es-CR") + "%")
                : "IVA (exento)";
        }
        card.classList.remove("d-none");
    }

    function requestFiscal() {
        var citaId = el("ccCobroCitaId").value;
        var monto = Number(el("ccCobroMonto").value);
        if (!citaId || isNaN(monto) || monto <= 0) {
            hideFiscal();
            return;
        }
        var myReq = ++fiscalReqId;
        var url = "/Calendar/PreviewCobroFiscal?citaId=" + encodeURIComponent(citaId) +
            "&monto=" + encodeURIComponent(monto);
        fetch(url, {
            method: "GET",
            headers: { "X-Requested-With": "XMLHttpRequest" },
            credentials: "same-origin"
        })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (d) {
                if (myReq !== fiscalReqId) return; // respuesta obsoleta
                if (d) renderFiscal(d); else hideFiscal();
            })
            .catch(function () { /* silencioso: el desglose es informativo */ });
    }

    function scheduleFiscal() {
        if (fiscalTimer) clearTimeout(fiscalTimer);
        fiscalTimer = setTimeout(requestFiscal, 250);
    }

    function showError(msg) {
        var err = el("ccCobroError");
        if (!err) return;
        err.textContent = msg;
        err.classList.remove("d-none");
    }

    function clearError() {
        var err = el("ccCobroError");
        if (!err) return;
        err.textContent = "";
        err.classList.add("d-none");
    }

    /**
     * Abre el modal de cobro para una cita.
     * @param {Object} data  { citaId, cliente, servicio, precio, email, clienteId }
     * @param {Function} onSuccess  callback(message) tras un cobro exitoso (refrescar vista).
     */
    function open(data, onSuccess) {
        if (!modal) return;
        data = data || {};
        onSuccessCb = typeof onSuccess === "function" ? onSuccess : null;

        el("ccCobroCitaId").value = data.citaId != null ? String(data.citaId) : "";
        el("ccCobroCliente").textContent = data.cliente || "Cliente";
        el("ccCobroServicio").textContent = data.servicio || "Servicio";

        // Monto base: si la cita trae precio > 0 se precarga; si no (servicio personalizado
        // sin precio) el input arranca vacío y se muestra una ayuda. Nunca se bloquea.
        var precioNum = Number(data.precio);
        var tienePrecio = data.precio !== null && data.precio !== undefined &&
            data.precio !== "" && !isNaN(precioNum) && precioNum > 0;

        el("ccCobroMonto").value = tienePrecio ? precioNum : "";
        el("ccCobroEsperado").textContent = tienePrecio ? money(precioNum) : "Sin precio definido";

        var hint = el("ccCobroHint");
        if (hint) hint.classList.toggle("d-none", tienePrecio);

        el("ccCobroMetodo").value = "EFECTIVO";
        el("ccCobroObs").value = "";

        var enviarChk = el("ccCobroEnviar");
        var emailWrap = el("ccCobroEmailWrap");
        var emailInp = el("ccCobroEmail");
        var guardarWrap = el("ccCobroGuardarWrap");
        var guardarChk = el("ccCobroGuardarEmail");
        if (enviarChk) enviarChk.checked = false;
        if (emailWrap) {
            emailWrap.classList.add("d-none");
            emailWrap.dataset.clienteId = data.clienteId != null ? String(data.clienteId) : "";
        }
        if (emailInp) emailInp.value = data.email || "";
        if (guardarWrap) guardarWrap.classList.add("d-none");
        if (guardarChk) guardarChk.checked = false;

        clearError();
        hideFiscal();
        modal.show();
        requestFiscal(); // desglose inicial con el precio precargado
        var montoInput = el("ccCobroMonto");
        if (montoInput) setTimeout(function () { montoInput.focus(); }, 250);
    }

    // Recalcular el desglose en vivo cuando cambia el monto cobrado (pagos parciales, etc.).
    var montoInputGlobal = el("ccCobroMonto");
    if (montoInputGlobal) {
        montoInputGlobal.addEventListener("input", scheduleFiscal);
    }

    // ── Mostrar/ocultar el correo del comprobante ────────────────────────────
    var enviarChkGlobal = el("ccCobroEnviar");
    if (enviarChkGlobal) {
        enviarChkGlobal.addEventListener("change", function () {
            var emailWrap = el("ccCobroEmailWrap");
            var guardarWrap = el("ccCobroGuardarWrap");
            if (emailWrap) emailWrap.classList.toggle("d-none", !this.checked);
            var tieneCliente = emailWrap && emailWrap.dataset.clienteId;
            if (guardarWrap) guardarWrap.classList.toggle("d-none", !(this.checked && tieneCliente));
        });
    }

    // ── Enviar el cobro ──────────────────────────────────────────────────────
    var submitBtn = el("ccCobroSubmit");
    if (submitBtn) {
        submitBtn.addEventListener("click", function () {
            var citaId = el("ccCobroCitaId").value;
            var monto = el("ccCobroMonto").value;
            var metodo = el("ccCobroMetodo").value;
            var obs = el("ccCobroObs").value;

            var enviar = el("ccCobroEnviar");
            var emailInp = el("ccCobroEmail");
            var guardarChk = el("ccCobroGuardarEmail");
            var enviarComprobante = enviar && enviar.checked;
            var email = emailInp ? emailInp.value.trim() : "";

            clearError();

            if (!monto || Number(monto) <= 0) {
                showError("Indica un monto mayor a cero.");
                return;
            }

            if (enviarComprobante && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) {
                showError("Indica un correo válido para enviar el comprobante.");
                return;
            }

            submitBtn.disabled = true;
            var body = new URLSearchParams();
            body.set("citaId", citaId);
            body.set("monto", monto);
            body.set("metodoPago", metodo);
            body.set("observacion", obs);
            body.set("enviarComprobante", enviarComprobante ? "true" : "false");
            body.set("emailComprobante", email);
            body.set("guardarEmailEnCliente", (guardarChk && guardarChk.checked) ? "true" : "false");

            fetch("/Calendar/CobrarCita", {
                method: "POST",
                headers: {
                    "Content-Type": "application/x-www-form-urlencoded",
                    "RequestVerificationToken": token(),
                    "X-Requested-With": "XMLHttpRequest"
                },
                credentials: "same-origin",
                body: body.toString()
            })
                .then(function (r) { return r.json().then(function (d) { return { ok: r.ok, d: d }; }); })
                .then(function (res) {
                    if (!res.ok) {
                        showError((res.d && res.d.error) || "No fue posible registrar el cobro.");
                        return;
                    }
                    if (modal) modal.hide();
                    var message = (res.d && res.d.message) || "Cobro registrado correctamente.";
                    toast(message, false);
                    if (onSuccessCb) {
                        try { onSuccessCb(message); } catch (e) { /* no romper el flujo */ }
                    }
                })
                .catch(function () {
                    showError("Error de conexión. Intenta de nuevo.");
                })
                .finally(function () { submitBtn.disabled = false; });
        });
    }

    // ── Toast compartido (estilos .cc-toast de control-cobros.css) ───────────
    function toast(msg, isError) {
        var t = document.createElement("div");
        t.textContent = msg;
        t.className = "cc-toast " + (isError ? "cc-toast-error" : "cc-toast-success");
        document.body.appendChild(t);
        setTimeout(function () { t.classList.add("is-hiding"); }, 2600);
        setTimeout(function () { t.remove(); }, 3000);
    }

    window.cobroCitaModal = { open: open, toast: toast };
})();
