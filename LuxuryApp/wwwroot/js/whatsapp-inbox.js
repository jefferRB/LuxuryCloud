/* ============================================================
   WhatsApp follow-up — whatsapp-inbox.js
   Para tenants CON add-on WhatsApp:
     1) Panel derecho "Citas de hoy" (mismo diseño que sin add-on),
        enriquecido con badge de estado de confirmación y "Ver chat".
     2) "Centro de confirmaciones WhatsApp" (sección inferior) con
        KPIs, filtros por rango/estado/funcionario/búsqueda y acciones.
   Reutiliza la infraestructura de calendar.js (apiFetchJson,
   currentDate, getFuncionariosActivos, editarCita, cancelarCita).
   NO modifica calendar.js.
   ============================================================ */
(function () {
    "use strict";

    let panelSequence = 0;
    let followSequence = 0;
    let panelItems = [];      // citas de hoy (futuras) del panel derecho
    let followItems = [];      // items del centro de confirmaciones (rango)

    function waEnabled() {
        return window.LUXURY_CALENDAR_CONFIG?.tenantWhatsAppEnabled === true;
    }

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

    /* ════════════════════════════════════════════════════════════
       1) PANEL DERECHO — "Citas de hoy" (futuras, con badge + chat)
       ════════════════════════════════════════════════════════════ */
    window.loadUpcomingAppointments = async function (funcionarioId = undefined) {
        const requestId = ++panelSequence;

        try {
            const dateStr = selectedDateStr();
            const resolvedFuncionarioId = selectedFuncionarioId(funcionarioId);

            if (typeof window.syncTodayAppointmentsHeader === "function") {
                window.syncTodayAppointmentsHeader();
            }

            const url = resolvedFuncionarioId
                ? `/WhatsAppInbox/Inbox?date=${encodeURIComponent(dateStr)}&funcionarioId=${encodeURIComponent(resolvedFuncionarioId)}`
                : `/WhatsAppInbox/Inbox?date=${encodeURIComponent(dateStr)}`;

            const data = await fetchJson(url);
            if (requestId !== panelSequence) return;

            const items = Array.isArray(data.items) ? data.items : [];
            // El servidor aplica la hora de negocio para la fecha seleccionada.
            panelItems = items;
            renderPanelList(applyPanelStatusFilter(panelItems), data.whatsAppEnabled === true);
        } catch (error) {
            if (error && error.name === "AbortError") return;
            console.error("Error cargando citas de hoy", error);
            renderPanelError();
        }
    };

    function applyPanelStatusFilter(items) {
        const filter = document.getElementById("statusFiltro")?.value || "";
        if (!filter) return items;

        switch (filter) {
            case "confirmados": return items.filter(i => i.estadoCitaKey === "confirmed");
            case "pendientes": return items.filter(i => i.waStatusKey === "pending" || i.waStatusKey === "not_sent");
            case "cancelados": return items.filter(i => i.estadoCitaKey === "cancelled");
            case "atencion": return items.filter(i => i.requiereAtencion);
            default: return items;
        }
    }

    function renderPanelList(items, enabled) {
        const lista = document.getElementById("listaTareas");
        if (!lista) return;
        lista.innerHTML = "";

        if (!items.length) {
            lista.appendChild(buildEmptyState(
                typeof window.getTodayAppointmentsEmptyTitle === "function"
                    ? window.getTodayAppointmentsEmptyTitle()
                    : "No hay citas pendientes para hoy",
                typeof window.getTodayAppointmentsEmptySubtitle === "function"
                    ? window.getTodayAppointmentsEmptySubtitle()
                    : "Las próximas citas aparecerán aquí conforme estén programadas.",
                "bi-calendar-x",
                "today-apt-empty-icon"));
            return;
        }

        items.forEach(item => lista.appendChild(buildPanelCard(item, enabled)));
    }

    function renderPanelError() {
        const lista = document.getElementById("listaTareas");
        if (!lista) return;
        lista.innerHTML = "";
        const li = document.createElement("li");
        li.className = "whatsapp-thread-card";
        li.textContent = "No fue posible cargar las citas.";
        lista.appendChild(li);
    }

    function buildPanelCard(item, enabled) {
        const li = document.createElement("li");
        li.className = "whatsapp-thread-card";

        const top = document.createElement("div");
        top.className = "whatsapp-thread-top";

        const avatar = document.createElement("div");
        avatar.className = "whatsapp-avatar";
        avatar.textContent = item.iniciales || "?";

        const main = document.createElement("div");
        main.className = "whatsapp-thread-main";

        const name = document.createElement("div");
        name.className = "whatsapp-thread-name";
        name.textContent = item.nombreCliente || "Cliente";

        const meta = document.createElement("div");
        meta.className = "whatsapp-thread-meta";
        meta.textContent = [item.horaLocal, item.servicioNombre].filter(Boolean).join(" • ");

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
        top.appendChild(buildStatusBadge(item.waStatusKey, item.waStatusLabel));
        li.appendChild(top);

        const actions = document.createElement("div");
        actions.className = "whatsapp-thread-actions";

        actions.appendChild(buildActionButton("Editar", "bi-pencil", "", () => {
            if (typeof editarCita === "function") editarCita(item.citaId);
        }));
        actions.appendChild(buildActionButton("Cancelar", "bi-x-circle", "action-icon-button-danger", () => {
            if (typeof cancelarCita === "function") cancelarCita(item.citaId);
        }));
        actions.appendChild(buildActionButton("Ver chat", "bi-chat-dots", "", () =>
            viewChat(item.citaId, item.nombreCliente)));

        li.appendChild(actions);
        return li;
    }

    /* ════════════════════════════════════════════════════════════
       2) CENTRO DE CONFIRMACIONES WHATSAPP (rango)
       ════════════════════════════════════════════════════════════ */
    async function loadFollowUp() {
        const list = document.getElementById("waFollowList");
        if (!list) return;

        const requestId = ++followSequence;

        const range = document.getElementById("waFollowRange")?.value || "5d";
        const status = document.getElementById("waFollowStatus")?.value || "";
        const funcionario = document.getElementById("waFollowFuncionario")?.value || "";

        const params = new URLSearchParams();
        params.set("range", range);
        if (status) params.set("status", status);
        if (funcionario) params.set("funcionarioId", funcionario);

        if (range === "custom") {
            const from = document.getElementById("waFollowFrom")?.value || "";
            const to = document.getElementById("waFollowTo")?.value || "";
            if (!from || !to) {
                list.innerHTML = '<div class="text-muted small" style="padding:1rem 0;">Seleccione un rango de fechas para ver el seguimiento.</div>';
                return;
            }
            params.set("from", from);
            params.set("to", to);
        }

        list.innerHTML = '<div class="text-muted small" style="padding:1rem 0;">Cargando seguimiento…</div>';

        try {
            const data = await fetchJson(`/WhatsAppInbox/FollowUp?${params.toString()}`);
            if (requestId !== followSequence) return;

            followItems = Array.isArray(data.items) ? data.items : [];
            const visibleItems = applySearch(followItems);
            renderFollowKpis(calculateStats(visibleItems));
            renderFollowList(visibleItems, data.whatsAppEnabled === true);
        } catch (error) {
            if (error && error.name === "AbortError") return;
            console.error("Error cargando el centro de confirmaciones", error);
            list.innerHTML = '<div class="text-danger small" style="padding:1rem 0;">No fue posible cargar el seguimiento.</div>';
        }
    }
    window.reloadWhatsAppFollowUp = loadFollowUp;

    function renderFollowKpis(stats) {
        setText("waKpiTotal", stats.totalTracking ?? 0);
        setText("waKpiConfirmadas", stats.confirmed ?? 0);
        setText("waKpiPendientes", stats.pending ?? 0);
        setText("waKpiEnviadas", stats.sent ?? 0);
        setText("waKpiFallidas", stats.failed ?? 0);
        const tasa = stats.confirmationRate;
        setText("waKpiTasa", (tasa || tasa === 0) ? `${tasa}%` : "—");
    }

    function applySearch(items) {
        const term = (document.getElementById("waFollowSearch")?.value || "").trim().toLowerCase();
        if (!term) return items;
        return items.filter(i =>
            (i.nombreCliente || "").toLowerCase().includes(term) ||
            (i.telefono || "").toLowerCase().includes(term));
    }

    function calculateStats(items) {
        const total = items.length;
        const confirmed = items.filter(i => i.estadoCitaKey === "confirmed").length;
        const pending = items.filter(isPendingItem).length;
        const sent = items.filter(i =>
            i.tieneEnvioWhatsApp === true ||
            i.waStatusKey === "sent" ||
            i.waStatusKey === "reminder").length;
        const failed = items.filter(i =>
            i.tieneFalloWhatsApp === true ||
            i.waStatusKey === "failed").length;
        const requiresAttention = items.filter(i => i.requiereAtencion === true).length;
        const confirmationRate = total > 0
            ? Math.round((confirmed * 1000) / total) / 10
            : 0;

        return {
            totalTracking: total,
            confirmed,
            pending,
            sent,
            failed,
            requiresAttention,
            confirmationRate
        };
    }

    function isPendingItem(item) {
        return item.estadoCitaKey !== "confirmed" &&
            item.estadoCitaKey !== "cancelled" &&
            (item.waStatusKey === "pending" || item.waStatusKey === "not_sent");
    }

    function renderFollowList(items, enabled) {
        const list = document.getElementById("waFollowList");
        if (!list) return;
        list.innerHTML = "";

        if (!items.length) {
            const empty = document.createElement("div");
            empty.className = "wa-followup-empty";
            empty.innerHTML =
                '<div class="whatsapp-inbox-empty-icon"><i class="bi bi-whatsapp"></i></div>' +
                '<div style="font-weight:700;color:var(--private-surface-text)">Sin citas en seguimiento</div>' +
                '<div style="font-size:.82rem">No hay citas para el rango seleccionado.</div>';
            list.appendChild(empty);
            return;
        }

        // Agrupar por día (DiaGrupo viene calculado en servidor).
        let currentGroup = null;
        items.forEach(item => {
            if (item.diaGrupo !== currentGroup) {
                currentGroup = item.diaGrupo;
                const heading = document.createElement("div");
                heading.className = "wa-followup-group";
                heading.textContent = currentGroup;
                list.appendChild(heading);
            }
            list.appendChild(buildFollowCard(item, enabled));
        });
    }

    function buildFollowCard(item, enabled) {
        const card = document.createElement("div");
        card.className = "wa-followup-card" + (item.requiereAtencion ? " wa-followup-card-attention" : "");

        const top = document.createElement("div");
        top.className = "whatsapp-thread-top";

        const avatar = document.createElement("div");
        avatar.className = "whatsapp-avatar";
        avatar.textContent = item.iniciales || "?";

        const main = document.createElement("div");
        main.className = "whatsapp-thread-main";

        const name = document.createElement("div");
        name.className = "whatsapp-thread-name";
        name.textContent = item.nombreCliente || "Cliente";

        const meta = document.createElement("div");
        meta.className = "whatsapp-thread-meta";
        meta.textContent = [item.horaLocal, item.servicioNombre, item.funcionarioNombre]
            .filter(Boolean).join(" • ");

        main.appendChild(name);
        main.appendChild(meta);

        if (item.telefono) {
            const tel = document.createElement("div");
            tel.className = "whatsapp-thread-sub";
            tel.textContent = item.telefono;
            main.appendChild(tel);
        }

        top.appendChild(avatar);
        top.appendChild(main);
        top.appendChild(buildStatusBadge(item.waStatusKey, item.waStatusLabel));
        card.appendChild(top);

        if (item.waSubText) {
            const sub = document.createElement("div");
            sub.className = "whatsapp-thread-sub";
            sub.textContent = item.waSubText;
            card.appendChild(sub);
        }

        const actions = document.createElement("div");
        actions.className = "whatsapp-thread-actions";

        if (enabled && item.puedeEnviar) {
            actions.appendChild(buildSendButton(item, "Enviar ahora", "action-icon-button-success", "bi-send"));
        }
        if (enabled && item.puedeReenviar) {
            actions.appendChild(buildSendButton(item, "Reenviar", "action-icon-button-primary", "bi-arrow-repeat"));
        }
        actions.appendChild(buildActionButton("Ver chat", "bi-chat-dots", "", () =>
            viewChat(item.citaId, item.nombreCliente)));
        actions.appendChild(buildActionButton("Editar", "bi-pencil", "", () => {
            if (typeof editarCita === "function") editarCita(item.citaId);
        }));
        actions.appendChild(buildActionButton("Cancelar", "bi-x-circle", "action-icon-button-danger", () => {
            if (typeof cancelarCita === "function") cancelarCita(item.citaId);
        }));

        card.appendChild(actions);
        return card;
    }

    /* ── Componentes compartidos ──────────────────────────────── */
    function buildStatusBadge(key, label) {
        const span = document.createElement("span");
        const map = {
            confirmed: "status-badge-confirmed",
            sent: "status-badge-sent",
            reminder: "status-badge-reminder",
            pending: "status-badge-pending",
            not_sent: "status-badge-pending",
            failed: "status-badge-failed",
            cancelled: "status-badge-cancelled",
            no_phone: "status-badge-neutral",
            no_consent: "status-badge-neutral"
        };
        span.className = "status-badge " + (map[key] || "status-badge-neutral");
        span.textContent = label || "—";
        return span;
    }

    function buildEmptyState(title, subtitle, iconName, extraClass) {
        const li = document.createElement("li");
        li.className = "whatsapp-inbox-empty";

        const icon = document.createElement("div");
        icon.className = "whatsapp-inbox-empty-icon" + (extraClass ? " " + extraClass : "");
        icon.innerHTML = `<i class="bi ${iconName || "bi-calendar-x"}"></i>`;

        const t = document.createElement("div");
        t.style.fontWeight = "700";
        t.style.color = "var(--private-surface-text)";
        t.textContent = title;

        const s = document.createElement("div");
        s.style.fontSize = "0.82rem";
        s.textContent = subtitle;

        li.appendChild(icon);
        li.appendChild(t);
        li.appendChild(s);
        return li;
    }

    function buildSendButton(item, label, variant, icon) {
        return buildActionButton(label, icon, variant, async (btn) => {
            const confirmed = window.confirm(
                `¿Enviar mensaje de WhatsApp a ${item.nombreCliente || "el cliente"}? Esta acción puede generar un costo.`);
            if (!confirmed) return;
            await sendConfirmation(item.citaId, btn);
        });
    }

    function buildActionButton(label, icon, variant, handler) {
        const btn = document.createElement("button");
        btn.type = "button";
        btn.className = "action-icon-button" + (variant ? " " + variant : "");
        btn.innerHTML = `<i class="bi ${icon}"></i><span>${label}</span>`;
        btn.addEventListener("click", () => handler(btn));
        return btn;
    }

    /* ── Acción: enviar / reenviar ────────────────────────────── */
    async function sendConfirmation(citaId, btn) {
        if (btn) {
            btn.disabled = true;
            btn.dataset.originalHtml = btn.innerHTML;
            btn.innerHTML = '<i class="bi bi-hourglass-split"></i><span>Enviando…</span>';
        }

        try {
            await fetchJson(`/WhatsAppInbox/Send/${citaId}`, { method: "POST" });
            waToast("Mensaje de WhatsApp en proceso de envío.", "success");
            await loadFollowUp();
            await window.loadUpcomingAppointments(document.getElementById("funcionarioFiltro")?.value || "");
        } catch (error) {
            console.error("Error enviando WhatsApp", error);
            waToast(extractError(error) || "No fue posible enviar el mensaje.", "error");
            if (btn) {
                btn.disabled = false;
                if (btn.dataset.originalHtml) btn.innerHTML = btn.dataset.originalHtml;
            }
        }
    }

    /* ── Acción: ver chat / log ───────────────────────────────── */
    async function viewChat(citaId, nombre) {
        const titleEl = document.getElementById("waChatModalTitle");
        const bodyEl = document.getElementById("waChatModalBody");
        if (!bodyEl) return;

        if (titleEl) {
            titleEl.textContent = nombre ? `Historial — ${nombre}` : "Historial de WhatsApp";
        }
        bodyEl.innerHTML = '<div class="text-muted small">Cargando…</div>';
        showChatModal();

        try {
            const logs = await fetchJson(`/WhatsAppInbox/Chat/${citaId}`);
            renderChatLogs(bodyEl, logs);
        } catch (error) {
            console.error("Error cargando historial WhatsApp", error);
            bodyEl.innerHTML = '<div class="text-danger small">No fue posible cargar el historial.</div>';
        }
    }

    function renderChatLogs(bodyEl, logs) {
        bodyEl.innerHTML = "";

        if (!Array.isArray(logs) || !logs.length) {
            bodyEl.innerHTML = '<div class="text-muted small">No hay mensajes registrados para esta cita.</div>';
            return;
        }

        logs.forEach(log => {
            const row = document.createElement("div");
            row.className = "wa-chat-log-row";

            const head = document.createElement("div");
            head.className = "wa-chat-log-head";

            const dir = document.createElement("span");
            dir.className = "wa-chat-log-dir";
            dir.textContent = `${log.direccion} · ${log.tipo}`;

            const time = document.createElement("span");
            time.className = "wa-chat-log-time";
            time.textContent = log.fechaHoraLocal;

            head.appendChild(dir);
            head.appendChild(time);

            const status = document.createElement("div");
            status.className = "wa-chat-log-status";
            status.textContent = "Estado: " + (log.estado || "—");

            row.appendChild(head);
            row.appendChild(status);

            if (log.error) {
                const err = document.createElement("div");
                err.className = "wa-chat-log-error";
                err.textContent = log.error;
                row.appendChild(err);
            }

            if (log.referenciaMensaje) {
                const ref = document.createElement("div");
                ref.className = "wa-chat-log-ref";
                ref.textContent = "Ref: " + log.referenciaMensaje;
                row.appendChild(ref);
            }

            bodyEl.appendChild(row);
        });
    }

    function showChatModal() {
        const modalEl = document.getElementById("waChatModal");
        if (!modalEl || typeof bootstrap === "undefined") return;
        bootstrap.Modal.getOrCreateInstance(modalEl).show();
    }

    /* ── Utilidades ───────────────────────────────────────────── */
    function setText(id, value) {
        const el = document.getElementById(id);
        if (el) el.textContent = value;
    }

    function extractError(error) {
        if (!error) return null;
        if (typeof error.bodyText === "string" && error.bodyText.trim()) return error.bodyText.trim();
        if (typeof error.message === "string") return error.message;
        return null;
    }

    let toastTimer = null;
    function waToast(message, type) {
        let toast = document.getElementById("waInboxToast");
        if (!toast) {
            toast = document.createElement("div");
            toast.id = "waInboxToast";
            toast.className = "wa-inbox-toast";
            document.body.appendChild(toast);
        }
        toast.textContent = message;
        toast.classList.remove("wa-inbox-toast-error", "wa-inbox-toast-success");
        toast.classList.add(type === "error" ? "wa-inbox-toast-error" : "wa-inbox-toast-success");
        toast.classList.add("is-visible");

        if (toastTimer) clearTimeout(toastTimer);
        toastTimer = setTimeout(() => toast.classList.remove("is-visible"), 4000);
    }

    async function populateFollowFuncionarios() {
        const select = document.getElementById("waFollowFuncionario");
        if (!select || typeof getFuncionariosActivos !== "function") return;
        try {
            const funcionarios = await getFuncionariosActivos();
            funcionarios.forEach(f => {
                const opt = document.createElement("option");
                opt.value = f.id;
                opt.textContent = f.nombre;
                select.appendChild(opt);
            });
        } catch (e) {
            console.error("No fue posible cargar funcionarios para el seguimiento", e);
        }
    }

    /* ── Eventos ──────────────────────────────────────────────── */
    document.addEventListener("DOMContentLoaded", function () {
        // Filtro de estado del panel derecho "Citas de hoy".
        const statusFilter = document.getElementById("statusFiltro");
        if (statusFilter) {
            statusFilter.addEventListener("change", function () {
                renderPanelList(applyPanelStatusFilter(panelItems), waEnabled());
            });
        }

        // Centro de confirmaciones: sólo si existe el panel (tenant con add-on).
        if (!document.getElementById("waFollowUpPanel")) return;

        populateFollowFuncionarios();

        const rangeSel = document.getElementById("waFollowRange");
        const customGroup = document.getElementById("waFollowCustomGroup");
        const customGroupTo = document.getElementById("waFollowCustomGroupTo");

        function syncCustomVisibility() {
            const isCustom = rangeSel?.value === "custom";
            customGroup?.classList.toggle("d-none", !isCustom);
            customGroupTo?.classList.toggle("d-none", !isCustom);
        }

        rangeSel?.addEventListener("change", function () {
            syncCustomVisibility();
            loadFollowUp();
        });
        document.getElementById("waFollowStatus")?.addEventListener("change", loadFollowUp);
        document.getElementById("waFollowFuncionario")?.addEventListener("change", loadFollowUp);
        document.getElementById("waFollowFrom")?.addEventListener("change", loadFollowUp);
        document.getElementById("waFollowTo")?.addEventListener("change", loadFollowUp);
        document.getElementById("waFollowRefresh")?.addEventListener("click", loadFollowUp);

        let searchTimer = null;
        document.getElementById("waFollowSearch")?.addEventListener("input", function () {
            if (searchTimer) clearTimeout(searchTimer);
            searchTimer = setTimeout(() => {
                const visibleItems = applySearch(followItems);
                renderFollowKpis(calculateStats(visibleItems));
                renderFollowList(visibleItems, waEnabled());
            }, 200);
        });

        syncCustomVisibility();
        loadFollowUp();
    });
})();
