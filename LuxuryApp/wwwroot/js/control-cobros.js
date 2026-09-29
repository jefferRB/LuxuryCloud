/* ===========================================================================
   Control de citas y cobros - interaccion de la vista admin.
   - Filtros (Dia/Semana/Mes, fecha, funcionario, estado, busqueda) por AJAX.
   - El cobro rapido se delega al modal compartido (window.cobroCitaModal), el mismo
     que usa el Calendario. Aqui solo se abre con los datos de la fila y se refresca
     la tabla/KPIs al terminar (sin recargar la pagina).
   =========================================================================== */
(function () {
    "use strict";

    var resultsEl = document.getElementById("cc-results");
    if (!resultsEl) return;

    var segWrap = document.getElementById("cc-rango");
    var fechaEl = document.getElementById("cc-fecha");
    var funcEl = document.getElementById("cc-funcionario");
    var estadoEl = document.getElementById("cc-estado");
    var buscarEl = document.getElementById("cc-buscar");

    function toast(msg, isError) {
        if (window.cobroCitaModal && window.cobroCitaModal.toast) {
            window.cobroCitaModal.toast(msg, isError);
        }
    }

    function rangoActivo() {
        var b = segWrap ? segWrap.querySelector(".cc-seg-btn.active") : null;
        return b ? b.dataset.rango : "dia";
    }

    function buildParams() {
        var p = new URLSearchParams();
        p.set("rango", rangoActivo());
        if (fechaEl && fechaEl.value) p.set("fecha", fechaEl.value);
        if (funcEl && funcEl.value) p.set("funcionarioId", funcEl.value);
        if (estadoEl && estadoEl.value) p.set("estado", estadoEl.value);
        if (buscarEl && buscarEl.value.trim()) p.set("buscar", buscarEl.value.trim());
        return p;
    }

    var cargando = false;
    function loadResults() {
        if (cargando) return;
        cargando = true;
        resultsEl.classList.add("cc-loading");
        fetch("/Calendar/ControlCobrosData?" + buildParams().toString(), {
            headers: { "X-Requested-With": "XMLHttpRequest" },
            credentials: "same-origin"
        })
            .then(function (r) {
                if (!r.ok) throw new Error("No se pudieron cargar los datos.");
                return r.text();
            })
            .then(function (html) { resultsEl.innerHTML = html; })
            .catch(function () { toast("No se pudieron cargar los datos. Intenta de nuevo.", true); })
            .finally(function () {
                cargando = false;
                resultsEl.classList.remove("cc-loading");
            });
    }

    if (segWrap) {
        segWrap.addEventListener("click", function (e) {
            var btn = e.target.closest(".cc-seg-btn");
            if (!btn || btn.classList.contains("active")) return;
            segWrap.querySelectorAll(".cc-seg-btn").forEach(function (b) { b.classList.remove("active"); });
            btn.classList.add("active");
            loadResults();
        });
    }
    if (fechaEl) fechaEl.addEventListener("change", loadResults);
    if (funcEl) funcEl.addEventListener("change", loadResults);
    if (estadoEl) estadoEl.addEventListener("change", loadResults);
    if (buscarEl) {
        var t = null;
        buscarEl.addEventListener("input", function () {
            clearTimeout(t);
            t = setTimeout(loadResults, 350);
        });
    }

    // Cobro rapido: abre el modal compartido con los datos de la fila y refresca al terminar.
    document.addEventListener("click", function (e) {
        var btn = e.target.closest(".js-cc-cobrar");
        if (!btn || !window.cobroCitaModal) return;
        window.cobroCitaModal.open({
            citaId: btn.dataset.citaId,
            cliente: btn.dataset.cliente,
            servicio: btn.dataset.servicio,
            precio: btn.dataset.precio,
            email: btn.dataset.email,
            clienteId: btn.dataset.clienteId
        }, loadResults);
    });
})();
