// Portal del funcionario — calendario. Reutiliza el look del calendario admin
// (clases day-grid/time-column/cita-bloque) contra endpoints SEGUROS del portal.
// El FuncionarioId siempre lo resuelve el backend desde el claim.
(function () {
    "use strict";

    var CFG = window.MP_CAL || {};
    var INICIO = 6, FIN = 22, INTERVALO = 30, ALTO_SLOT = 30;

    // Lee y valida Inicio/Fin/Intervalo desde los controles. Persisten en variables de módulo.
    function readConfig() {
        var iEl = document.getElementById("mpInicio");
        var fEl = document.getElementById("mpFin");
        var nEl = document.getElementById("mpIntervalo");
        if (!iEl || !fEl || !nEl) return true;
        var i = parseInt(iEl.value, 10), f = parseInt(fEl.value, 10), n = parseInt(nEl.value, 10);
        if (isNaN(i) || isNaN(f) || isNaN(n) || i < 0 || f > 24 || i >= f || n < 5) {
            alert("Revisa los valores de Inicio, Fin e Intervalo (Inicio debe ser menor que Fin).");
            return false;
        }
        INICIO = i; FIN = f; INTERVALO = n;
        try {
            localStorage.setItem("luxury.portal.calendar.startHour", String(i));
            localStorage.setItem("luxury.portal.calendar.endHour", String(f));
            localStorage.setItem("luxury.portal.calendar.interval", String(n));
        } catch (e) { /* localStorage no disponible */ }
        return true;
    }

    // Carga preferencias guardadas (solo de este navegador/usuario). Ignora valores corruptos.
    function loadStoredConfig() {
        try {
            var i = parseInt(localStorage.getItem("luxury.portal.calendar.startHour"), 10);
            var f = parseInt(localStorage.getItem("luxury.portal.calendar.endHour"), 10);
            var n = parseInt(localStorage.getItem("luxury.portal.calendar.interval"), 10);
            if (isNaN(i) || isNaN(f) || isNaN(n) || i < 0 || f > 24 || i >= f || [15, 30, 45, 60].indexOf(n) === -1) return;
            var iEl = document.getElementById("mpInicio"), fEl = document.getElementById("mpFin"), nEl = document.getElementById("mpIntervalo");
            if (iEl) iEl.value = i;
            if (fEl) fEl.value = f;
            if (nEl) nEl.value = n;
            INICIO = i; FIN = f; INTERVALO = n;
        } catch (e) { /* ignora */ }
    }
    loadStoredConfig();

    function pad(n) { return String(n).padStart(2, "0"); }
    function fmtAmPm(h, m) {
        var ap = h >= 12 ? "p. m." : "a. m.";
        var hh = h % 12; if (hh === 0) hh = 12;
        return hh + ":" + pad(m) + " " + ap;
    }
    function token() {
        var el = document.querySelector("#calendar-antiforgery input[name=__RequestVerificationToken]");
        return el ? el.value : "";
    }

    /* ─────────── Selects de mes/año ─────────── */
    document.querySelectorAll(".mp-cal-jump").forEach(function (sel) {
        sel.addEventListener("change", function () {
            var head = sel.closest(".mp-cal-monthhead");
            var m = head.querySelector("[data-kind=month]").value;
            var y = head.querySelector("[data-kind=year]").value;
            window.location.href = "/MiPortal/Calendario?fecha=" + y + "-" + pad(m) + "-01";
        });
    });

    /* ─────────── Servicio catálogo / personalizado ─────────── */
    document.querySelectorAll(".js-servicio-select").forEach(function (sel) {
        var wrap = sel.closest("form").querySelector(".js-personalizado-wrap");
        function sync() { if (wrap) wrap.style.display = sel.value ? "none" : "block"; }
        sel.addEventListener("change", sync); sync();
    });

    /* ─────────── Autocomplete de clientes ─────────── */
    document.querySelectorAll(".js-cliente-nombre").forEach(function (input) {
        var form = input.closest("form");
        var results = form.querySelector(".js-cliente-results");
        var clienteId = form.querySelector(".js-cliente-id");
        var telefono = form.querySelector("[name=telefonoCliente]");
        var timer = null, controller = null;
        function hide() { results.style.display = "none"; results.innerHTML = ""; }
        function render(items) {
            results.innerHTML = "";
            if (!items.length) { hide(); return; }
            items.forEach(function (c) {
                var row = document.createElement("button");
                row.type = "button"; row.className = "lp-ac-item";
                row.textContent = c.telefono ? (c.nombre + " · " + c.telefono) : c.nombre;
                row.addEventListener("click", function () {
                    input.value = c.nombre || "";
                    if (clienteId) clienteId.value = c.id || "";
                    if (telefono && c.telefono) telefono.value = c.telefono;
                    hide();
                });
                results.appendChild(row);
            });
            results.style.display = "block";
        }
        input.addEventListener("input", function () {
            if (clienteId) clienteId.value = "";
            var term = input.value.trim();
            if (timer) clearTimeout(timer);
            if (term.length < 3) { hide(); return; }
            timer = setTimeout(function () {
                if (controller) controller.abort();
                controller = new AbortController();
                fetch("/MiPortal/Clientes/Autocompletado?term=" + encodeURIComponent(term), { signal: controller.signal })
                    .then(function (r) { return r.ok ? r.json() : []; })
                    .then(render)
                    .catch(function () { });
            }, 300);
        });
        input.addEventListener("blur", function () { setTimeout(hide, 180); });
    });

    /* ─────────── Rellenar modal de edición (botones estáticos) ─────────── */
    function fillEdit(ds) {
        var modal = document.getElementById("editarCitaModal");
        if (!modal) return false;
        var f = modal.querySelector("form");
        f.querySelector("[name=citaId]").value = ds.citaId || "";
        f.querySelector("[name=fechaHora]").value = ds.fecha || "";
        f.querySelector("[name=nombreCliente]").value = ds.nombre || "";
        f.querySelector("[name=telefonoCliente]").value = ds.telefono || "";
        f.querySelector("[name=clienteId]").value = ds.clienteId || "";
        var sel = f.querySelector("[name=servicioId]");
        sel.value = ds.servicioId || "";
        f.querySelector("[name=servicioPersonalizado]").value = ds.servicioPersonalizado || "";
        var dur = f.querySelector("[name=duracionMinutos]");
        if (dur) dur.value = ds.duracion || "30";
        sel.dispatchEvent(new Event("change"));
        return true;
    }
    function fillCobro(ds) {
        if (typeof window.MP_fillCobro === "function") { return window.MP_fillCobro(ds); }
        var citaIdEl = document.getElementById("cobroCitaId");
        if (!citaIdEl) return false;
        citaIdEl.value = ds.citaId || "";
        return true;
    }

    document.querySelectorAll(".js-editar-cita").forEach(function (btn) {
        btn.addEventListener("click", function () {
            if (fillEdit(btn.dataset)) {
                bootstrap.Modal.getOrCreateInstance(document.getElementById("editarCitaModal")).show();
            }
        });
    });

    function submitCancelar(citaId, fecha) {
        if (!confirm("¿Cancelar esta cita? Esta acción no se puede deshacer.")) return;
        var f = document.createElement("form");
        f.method = "post"; f.action = "/MiPortal/Calendario/CancelarCita";
        f.innerHTML =
            '<input type="hidden" name="__RequestVerificationToken" value="' + token() + '">' +
            '<input type="hidden" name="citaId" value="' + citaId + '">' +
            '<input type="hidden" name="fecha" value="' + (fecha || "") + '">';
        document.body.appendChild(f); f.submit();
    }

    /* ─────────── Vista diaria (modal) ─────────── */
    var diaModalEl = document.getElementById("diaModal");
    var diaCurrent = null;
    var diaController = null;

    function abrirDia(fecha) {
        if (!readConfig()) return;
        diaCurrent = fecha;
        renderDia();
        bootstrap.Modal.getOrCreateInstance(diaModalEl).show();
    }

    function openTargetAfterDia(showFn) {
        // Cierra el modal diario y, cuando termine, abre el modal destino (sin backdrops apilados).
        diaModalEl.addEventListener("hidden.bs.modal", function once() {
            diaModalEl.removeEventListener("hidden.bs.modal", once);
            showFn();
        }, { once: true });
        bootstrap.Modal.getOrCreateInstance(diaModalEl).hide();
    }

    function renderDia() {
        if (!diaModalEl || !diaCurrent) return;
        var title = document.getElementById("diaModalTitle");
        var body = document.getElementById("diaModalBody");
        var d = new Date(diaCurrent + "T00:00:00");
        title.textContent = d.toLocaleDateString("es-CR", { weekday: "long", day: "numeric", month: "long", year: "numeric" });
        document.querySelector(".js-dia-hoy").classList.toggle("d-none", diaCurrent === CFG.hoy);
        body.innerHTML = '<div class="text-center text-muted py-4">Cargando…</div>';

        if (diaController) diaController.abort();
        diaController = new AbortController();
        fetch(CFG.citasDiaUrl + "?fecha=" + encodeURIComponent(diaCurrent), { signal: diaController.signal })
            .then(function (r) { return r.ok ? r.json() : []; })
            .then(function (citas) { drawDia(body, citas); })
            .catch(function (e) { if (e.name !== "AbortError") body.innerHTML = '<div class="text-danger py-3">No se pudo cargar el día.</div>'; });
    }

    function openCrear(fecha, hour, min) {
        var modal = document.getElementById("nuevaCitaModal");
        if (!modal) return;
        var f = modal.querySelector("form");
        var fh = f.querySelector("[name=fechaHora]");
        if (fh) fh.value = fecha + "T" + pad(hour) + ":" + pad(min);
        openTargetAfterDia(function () { bootstrap.Modal.getOrCreateInstance(modal).show(); });
    }

    function postResize(citaId, duracion) {
        return fetch("/MiPortal/Calendario/RedimensionarCita", {
            method: "POST",
            headers: { "RequestVerificationToken": token(), "Content-Type": "application/x-www-form-urlencoded" },
            body: "citaId=" + encodeURIComponent(citaId) + "&duracionMinutos=" + encodeURIComponent(duracion)
        }).then(function (r) {
            if (r.ok) return { ok: true };
            return r.json().then(function (j) { return { ok: false, error: (j && j.error) || "No se pudo cambiar la duración." }; })
                .catch(function () { return { ok: false, error: "No se pudo cambiar la duración." }; });
        });
    }

    function drawDia(body, citas) {
        var minutosVisibles = (FIN - INICIO) * 60;
        var totalSlots = Math.ceil(minutosVisibles / INTERVALO);
        var colHeight = totalSlots * ALTO_SLOT;

        var grid = document.createElement("div");
        grid.className = "day-grid";

        var timeCol = document.createElement("div");
        timeCol.className = "time-column";
        for (var i = 0; i < totalSlots; i++) {
            var h = INICIO + Math.floor((i * INTERVALO) / 60);
            var m = (i * INTERVALO) % 60;
            var slot = document.createElement("div");
            slot.className = "time-slot";
            slot.textContent = fmtAmPm(h, m);
            timeCol.appendChild(slot);
        }

        var cont = document.createElement("div");
        cont.className = "funcionarios-container";
        var col = document.createElement("div");
        col.className = "funcionario-column";
        col.style.position = "relative";
        col.style.minHeight = colHeight + "px";

        for (var s = 0; s < totalSlots; s++) {
            var line = document.createElement("div");
            line.className = "slot-line";
            if (CFG.puedeCrear) {
                line.style.cursor = "pointer";
                (function (idx) {
                    line.addEventListener("click", function () {
                        var mins = INICIO * 60 + idx * INTERVALO;
                        openCrear(diaCurrent, Math.floor(mins / 60), mins % 60);
                    });
                })(s);
            }
            col.appendChild(line);
        }

        (citas || []).forEach(function (c) {
            var startMin = c.horaMinutos - INICIO * 60;
            if (startMin < 0 || startMin >= minutosVisibles) return;
            var top = (startMin / INTERVALO) * ALTO_SLOT;
            var height = Math.max(((c.duracion || INTERVALO) / INTERVALO) * ALTO_SLOT, 22);
            var esDescanso = !c.esCita;

            // Reutiliza la clase .cita-bloque del módulo principal (tipografía idéntica).
            var block = document.createElement("div");
            block.className = "cita-bloque";
            block.style.top = top + "px";
            block.style.height = (height - 2) + "px";
            block.style.backgroundColor = esDescanso ? "#6c757d" : (CFG.color || "#004445");

            var title = document.createElement("div");
            title.style.fontWeight = "600";
            var detail = document.createElement("div");
            detail.style.fontSize = "11px";
            if (esDescanso) {
                title.textContent = "☕ DESCANSO";
                detail.textContent = (c.duracion || INTERVALO) + " min";
            } else {
                title.textContent = c.cliente || "Sin cliente";
                detail.style.opacity = "0.9";
                detail.textContent = c.servicio || "Sin servicio";
            }
            block.appendChild(title);
            block.appendChild(detail);

            // Click en la cita → editar (sin botones incrustados, como el módulo principal).
            if (!esDescanso && CFG.puedeEditar) {
                block.addEventListener("click", function (e) {
                    if (e.target.closest(".cita-resize-handle")) return;
                    var ds = { citaId: c.id, fecha: c.fechaHoraInput, nombre: c.nombreClienteRaw || c.cliente, telefono: c.telefono || "",
                        clienteId: c.clienteId || "", servicioId: c.servicioId || "", servicioPersonalizado: c.servicioPersonalizado || "", duracion: c.duracion || INTERVALO };
                    if (fillEdit(ds)) {
                        openTargetAfterDia(function () { bootstrap.Modal.getOrCreateInstance(document.getElementById("editarCitaModal")).show(); });
                    }
                });

                // Handle de resize vertical (reutiliza .cita-resize-handle del módulo principal).
                var handle = document.createElement("div");
                handle.className = "cita-resize-handle";
                handle.title = "Arrastra para cambiar la duración";
                block.appendChild(handle);
                wireResize(handle, block, c);
            }
            col.appendChild(block);
        });

        cont.appendChild(col);
        grid.appendChild(timeCol);
        grid.appendChild(cont);
        body.innerHTML = "";
        body.appendChild(grid);
    }

    function wireResize(handle, block, c) {
        var dragging = false, startY = 0, startH = 0;
        handle.addEventListener("pointerdown", function (e) {
            e.preventDefault(); e.stopPropagation();
            dragging = true; startY = e.clientY; startH = block.offsetHeight;
            handle.setPointerCapture(e.pointerId);
        });
        handle.addEventListener("pointermove", function (e) {
            if (!dragging) return;
            var h = Math.max(ALTO_SLOT, startH + (e.clientY - startY));
            block.style.height = h + "px";
        });
        handle.addEventListener("pointerup", function (e) {
            if (!dragging) return;
            dragging = false;
            try { handle.releasePointerCapture(e.pointerId); } catch (ex) { }
            var slots = Math.max(1, Math.round(block.offsetHeight / ALTO_SLOT));
            var nuevaDuracion = slots * INTERVALO;
            if (nuevaDuracion === (c.duracion || INTERVALO)) { renderDia(); return; }
            postResize(c.id, nuevaDuracion).then(function (res) {
                if (res.ok) { renderDia(); }
                else { alert(res.error); renderDia(); }
            });
        });
    }

    function mkBtn(label, color, handler) {
        var b = document.createElement("button");
        b.type = "button";
        b.style.background = color; b.style.color = "#fff";
        b.textContent = label;
        b.addEventListener("click", function (e) { e.stopPropagation(); handler(); });
        return b;
    }

    var aplicarBtn = document.getElementById("mpAplicar");
    if (aplicarBtn) {
        aplicarBtn.addEventListener("click", function () {
            if (!readConfig()) return;
            // Si el modal diario está abierto, recalcula la grilla; si no, queda listo para la próxima apertura.
            if (diaModalEl && diaModalEl.classList.contains("show")) { renderDia(); }
        });
    }

    document.querySelectorAll(".js-abrir-dia").forEach(function (btn) {
        btn.addEventListener("click", function () { abrirDia(btn.dataset.fecha || CFG.hoy); });
    });
    document.querySelectorAll(".js-dia-cell").forEach(function (cell) {
        cell.addEventListener("click", function () { abrirDia(cell.dataset.fecha); });
    });

    if (diaModalEl) {
        function shiftDia(days) {
            var d = new Date(diaCurrent + "T00:00:00");
            d.setDate(d.getDate() + days);
            diaCurrent = d.getFullYear() + "-" + pad(d.getMonth() + 1) + "-" + pad(d.getDate());
            renderDia();
        }
        diaModalEl.querySelector(".js-dia-prev").addEventListener("click", function () { shiftDia(-1); });
        diaModalEl.querySelector(".js-dia-next").addEventListener("click", function () { shiftDia(1); });
        diaModalEl.querySelector(".js-dia-hoy").addEventListener("click", function () { diaCurrent = CFG.hoy; renderDia(); });
    }

    /* ─────────── Control de citas y cobros: Día/Semana/Mes sin recargar ─────────── */
    var mpcCard = document.getElementById("mpcCard");
    if (mpcCard) {
        var mpcBody = document.getElementById("mpcBody");
        var mpcController = null;
        document.querySelectorAll(".js-mpc-tab").forEach(function (tab) {
            tab.addEventListener("click", function () {
                var rango = tab.dataset.rango;
                var fecha = mpcCard.dataset.fecha;
                document.querySelectorAll(".js-mpc-tab").forEach(function (t) { t.classList.toggle("mpc-tab--on", t === tab); });
                mpcBody.style.opacity = ".45";
                mpcBody.style.transition = "opacity .15s";
                if (mpcController) mpcController.abort();
                mpcController = new AbortController();
                fetch("/MiPortal/Calendario/Control?fecha=" + encodeURIComponent(fecha) + "&rango=" + encodeURIComponent(rango), { signal: mpcController.signal })
                    .then(function (r) { return r.ok ? r.text() : Promise.reject(new Error("http")); })
                    .then(function (html) { mpcBody.innerHTML = html; mpcBody.style.opacity = "1"; })
                    .catch(function (e) { if (e && e.name !== "AbortError") mpcBody.style.opacity = "1"; });
            });
        });
    }

    /* ─────────── Red de seguridad anti-backdrop huérfano ─────────── */
    document.addEventListener("hidden.bs.modal", function () {
        setTimeout(function () {
            if (!document.querySelector(".modal.show")) {
                document.querySelectorAll(".modal-backdrop").forEach(function (b) { b.remove(); });
                document.body.classList.remove("modal-open");
                document.body.style.removeProperty("overflow");
                document.body.style.removeProperty("padding-right");
            }
        }, 200);
    });
})();
