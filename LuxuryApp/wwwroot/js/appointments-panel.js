/* ============================================================
   Appointments Panel — appointments-panel.js
   Panel "Citas de hoy" para tenants sin add-on WhatsApp.
   Reutiliza la infraestructura de calendar.js (apiFetchJson,
   currentDate, editarCita, cancelarCita). NO modifica calendar.js.
   ============================================================ */
(function () {
    "use strict";

    let panelSequence = 0;

    function fetchJson(url, options) {
        if (typeof apiFetchJson === "function") {
            return apiFetchJson(url, options);
        }
        return fetch(url, options).then(r => r.json());
    }

    function selectedDateStr() {
        if (typeof window.getTodayAppointmentsSelectedDateStr === "function") {
            return window.getTodayAppointmentsSelectedDateStr();
        }

        const base = (typeof currentDate !== "undefined" && currentDate) ? currentDate : new Date();
        if (typeof formatLocalDate === "function") {
            return formatLocalDate(base);
        }
        const pad = n => String(n).padStart(2, "0");
        return `${base.getFullYear()}-${pad(base.getMonth() + 1)}-${pad(base.getDate())}`;
    }

    function selectedFuncionarioId(funcionarioId) {
        if (typeof window.resolveTodayAppointmentsFuncionarioFilter === "function") {
            return window.resolveTodayAppointmentsFuncionarioFilter(funcionarioId);
        }

        return funcionarioId || "";
    }

    /* ── Override del cargador de calendar.js ──────────────────── */
    window.loadUpcomingAppointments = async function (funcionarioId = undefined) {
        const requestId = ++panelSequence;

        try {
            const dateStr = selectedDateStr();
            const resolvedFuncionarioId = selectedFuncionarioId(funcionarioId);

            if (typeof window.syncTodayAppointmentsHeader === "function") {
                window.syncTodayAppointmentsHeader();
            }

            const url = resolvedFuncionarioId
                ? `/Calendar/GetUpcomingAppointments?date=${encodeURIComponent(dateStr)}&funcionarioId=${encodeURIComponent(resolvedFuncionarioId)}`
                : `/Calendar/GetUpcomingAppointments?date=${encodeURIComponent(dateStr)}`;

            const data = await fetchJson(url);

            if (requestId !== panelSequence) return;

            const items = Array.isArray(data) ? data : [];
            renderThreadList(items);
        } catch (error) {
            if (error && error.name === "AbortError") return;
            console.error("Error cargando citas del día", error);
            renderThreadError();
        }
    };

    /* ── Formatear hora local ──────────────────────────────────── */
    function formatHora(fechaHoraCita) {
        if (!fechaHoraCita) return "—";
        const parts = fechaHoraCita.split("T");
        return parts.length >= 2 ? parts[1].slice(0, 5) : fechaHoraCita;
    }

    /* ── Iniciales del nombre ──────────────────────────────────── */
    function buildIniciales(nombre) {
        if (!nombre) return "?";
        return nombre.trim().split(/\s+/).map(w => w[0] || "").join("").slice(0, 2).toUpperCase();
    }

    /* ── Lista de citas (panel derecho) ────────────────────────── */
    function renderThreadList(items) {
        const lista = document.getElementById("listaTareas");
        if (!lista) return;

        lista.innerHTML = "";

        if (!items.length) {
            lista.appendChild(buildEmptyState());
            return;
        }

        items.forEach(item => lista.appendChild(buildAppointmentCard(item)));
    }

    function renderThreadError() {
        const lista = document.getElementById("listaTareas");
        if (!lista) return;
        lista.innerHTML = "";
        const li = document.createElement("li");
        li.className = "whatsapp-thread-card";
        li.textContent = "No fue posible cargar las citas.";
        lista.appendChild(li);
    }

    function buildEmptyState() {
        const li = document.createElement("li");
        li.className = "whatsapp-inbox-empty";

        const icon = document.createElement("div");
        icon.className = "whatsapp-inbox-empty-icon today-apt-empty-icon";
        icon.innerHTML = '<i class="bi bi-calendar-x"></i>';

        const title = document.createElement("div");
        title.style.fontWeight = "700";
        title.style.color = "var(--private-surface-text)";
        title.textContent = typeof window.getTodayAppointmentsEmptyTitle === "function"
            ? window.getTodayAppointmentsEmptyTitle()
            : "No hay citas pendientes para hoy";

        const sub = document.createElement("div");
        sub.style.fontSize = "0.82rem";
        sub.textContent = typeof window.getTodayAppointmentsEmptySubtitle === "function"
            ? window.getTodayAppointmentsEmptySubtitle()
            : "Las próximas citas aparecerán aquí conforme estén programadas.";

        li.appendChild(icon);
        li.appendChild(title);
        li.appendChild(sub);
        return li;
    }

    function buildAppointmentCard(item) {
        const hora = formatHora(item.fechaHoraCita);

        const li = document.createElement("li");
        li.className = "whatsapp-thread-card";

        const top = document.createElement("div");
        top.className = "whatsapp-thread-top";

        const avatar = document.createElement("div");
        avatar.className = "whatsapp-avatar";
        avatar.textContent = buildIniciales(item.nombreCliente);

        const main = document.createElement("div");
        main.className = "whatsapp-thread-main";

        const name = document.createElement("div");
        name.className = "whatsapp-thread-name";
        name.textContent = item.nombreCliente || "Cliente";

        const meta = document.createElement("div");
        meta.className = "whatsapp-thread-meta";
        meta.textContent = [hora, item.servicioNombre].filter(Boolean).join(" • ");

        main.appendChild(name);
        main.appendChild(meta);

        if (item.funcionarioNombre) {
            const sub = document.createElement("div");
            sub.className = "whatsapp-thread-sub";
            sub.textContent = item.funcionarioNombre;
            main.appendChild(sub);
        }

        top.appendChild(avatar);
        top.appendChild(main);
        li.appendChild(top);

        const actions = document.createElement("div");
        actions.className = "whatsapp-thread-actions";

        actions.appendChild(buildActionButton("Editar", "bi-pencil", "", () => {
            if (typeof editarCita === "function") editarCita(item.id);
        }));
        actions.appendChild(buildActionButton("Cancelar", "bi-x-circle", "action-icon-button-danger", () => {
            if (typeof cancelarCita === "function") cancelarCita(item.id);
        }));

        li.appendChild(actions);
        return li;
    }

    /* ── Componentes UI ────────────────────────────────────────── */
    function buildActionButton(label, icon, variant, handler) {
        const btn = document.createElement("button");
        btn.type = "button";
        btn.className = "action-icon-button" + (variant ? " " + variant : "");
        btn.innerHTML = `<i class="bi ${icon}"></i><span>${label}</span>`;
        btn.addEventListener("click", () => handler(btn));
        return btn;
    }
})();
