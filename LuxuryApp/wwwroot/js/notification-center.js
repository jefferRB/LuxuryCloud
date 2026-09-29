/*
 * Centro de Notificaciones - burbuja flotante.
 * Consume /Notificaciones/Resumen, refresca con polling liviano y marca leídas al abrir.
 * Todo el texto se inserta con textContent (sin innerHTML) para evitar inyección de HTML.
 * Los errores se manejan en silencio: nunca rompen la UI ni recargan la página.
 */
(function () {
    "use strict";

    var root = document.getElementById("luxNotif");
    if (!root) {
        return;
    }

    var POLL_INTERVAL_MS = 45000;

    var summaryUrl = root.getAttribute("data-summary-url");
    var markAllUrl = root.getAttribute("data-mark-all-url");
    var tokenInput = root.querySelector('input[name="__RequestVerificationToken"]');
    var antiForgeryToken = tokenInput ? tokenInput.value : "";

    var bubble = root.querySelector(".lux-notif__bubble");
    var badge = root.querySelector(".lux-notif__badge");
    var panel = root.querySelector(".lux-notif__panel");
    var list = root.querySelector(".lux-notif__list");
    var emptyState = root.querySelector(".lux-notif__empty");

    var isOpen = false;
    var isFetching = false;
    var unreadCount = 0;
    var pollTimer = null;

    function setBadge(count) {
        unreadCount = count > 0 ? count : 0;
        if (unreadCount > 0) {
            badge.textContent = unreadCount > 99 ? "99+" : String(unreadCount);
            badge.hidden = false;
            bubble.setAttribute("aria-label", "Notificaciones (" + unreadCount + " sin leer)");
        } else {
            badge.hidden = true;
            bubble.setAttribute("aria-label", "Notificaciones");
        }
    }

    function iconClassFor(key) {
        switch (key) {
            case "calendar-plus":
                return "bi-calendar-plus";
            case "calendar-x":
                return "bi-calendar-x";
            default:
                return "bi-bell";
        }
    }

    function buildItem(notif) {
        var hasAction = typeof notif.actionUrl === "string" && notif.actionUrl.length > 0;
        var item = document.createElement(hasAction ? "a" : "div");
        item.className = "lux-notif__item" + (notif.isRead ? "" : " is-unread");
        if (hasAction) {
            item.href = notif.actionUrl;
        }

        var iconWrap = document.createElement("span");
        var isDanger = notif.icon === "calendar-x";
        iconWrap.className = "lux-notif__icon" + (isDanger ? " lux-notif__icon--danger" : "");
        var icon = document.createElement("i");
        icon.className = "bi " + iconClassFor(notif.icon);
        iconWrap.appendChild(icon);

        var body = document.createElement("div");
        body.className = "lux-notif__body";

        var title = document.createElement("p");
        title.className = "lux-notif__item-title";
        title.textContent = notif.title || "";

        var message = document.createElement("p");
        message.className = "lux-notif__item-message";
        message.textContent = notif.message || "";

        var time = document.createElement("span");
        time.className = "lux-notif__time";
        time.textContent = notif.createdAtLabel || "";

        body.appendChild(title);
        body.appendChild(message);
        body.appendChild(time);

        item.appendChild(iconWrap);
        item.appendChild(body);
        return item;
    }

    function render(notifications) {
        // Limpia la lista sin innerHTML.
        while (list.firstChild) {
            list.removeChild(list.firstChild);
        }

        if (!notifications || notifications.length === 0) {
            list.hidden = true;
            emptyState.hidden = false;
            return;
        }

        emptyState.hidden = true;
        list.hidden = false;

        var fragment = document.createDocumentFragment();
        notifications.forEach(function (notif) {
            fragment.appendChild(buildItem(notif));
        });
        list.appendChild(fragment);
    }

    function fetchSummary() {
        if (isFetching || !summaryUrl) {
            return;
        }
        isFetching = true;

        fetch(summaryUrl, {
            method: "GET",
            credentials: "same-origin",
            headers: { "X-Requested-With": "XMLHttpRequest" }
        })
            .then(function (response) {
                if (!response.ok) {
                    throw new Error("HTTP " + response.status);
                }
                return response.json();
            })
            .then(function (data) {
                if (!data) {
                    return;
                }
                // Si el panel está abierto, ya marcamos como leídas: no re-subir el badge.
                if (!isOpen) {
                    setBadge(typeof data.unreadCount === "number" ? data.unreadCount : 0);
                }
                render(data.notifications);
            })
            .catch(function () {
                // Silencioso: la burbuja sigue visible aunque falle el fetch.
            })
            .finally(function () {
                isFetching = false;
            });
    }

    function markAllAsRead() {
        if (!markAllUrl || unreadCount === 0) {
            return;
        }
        // Optimista: bajamos el badge de inmediato.
        setBadge(0);

        fetch(markAllUrl, {
            method: "POST",
            credentials: "same-origin",
            headers: {
                "X-Requested-With": "XMLHttpRequest",
                "RequestVerificationToken": antiForgeryToken
            }
        })
            .then(function () {
                // Refresca la lista para que el resaltado de "no leída" desaparezca.
                fetchSummary();
            })
            .catch(function () {
                // Silencioso.
            });
    }

    function openPanel() {
        if (isOpen) {
            return;
        }
        isOpen = true;
        root.classList.add("is-open");
        bubble.setAttribute("aria-expanded", "true");
        markAllAsRead();
    }

    function closePanel() {
        if (!isOpen) {
            return;
        }
        isOpen = false;
        root.classList.remove("is-open");
        bubble.setAttribute("aria-expanded", "false");
    }

    function togglePanel() {
        if (isOpen) {
            closePanel();
        } else {
            openPanel();
        }
    }

    bubble.addEventListener("click", function (event) {
        event.stopPropagation();
        togglePanel();
    });

    // Clic fuera del panel lo cierra (no cierra al hacer clic dentro).
    document.addEventListener("click", function (event) {
        if (isOpen && !root.contains(event.target)) {
            closePanel();
        }
    });

    document.addEventListener("keydown", function (event) {
        if (event.key === "Escape" && isOpen) {
            closePanel();
            bubble.focus();
        }
    });

    // Pausa el polling cuando la pestaña no está visible (ahorra llamadas).
    document.addEventListener("visibilitychange", function () {
        if (document.hidden) {
            if (pollTimer) {
                window.clearInterval(pollTimer);
                pollTimer = null;
            }
        } else {
            fetchSummary();
            startPolling();
        }
    });

    function startPolling() {
        if (pollTimer) {
            return;
        }
        pollTimer = window.setInterval(fetchSummary, POLL_INTERVAL_MS);
    }

    // Arranque.
    fetchSummary();
    startPolling();
})();
