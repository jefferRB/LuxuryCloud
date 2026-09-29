(function () {
    "use strict";

    var root = document.getElementById("lcCalc");
    if (!root) {
        return;
    }

    var options = [];
    var config = {};
    try {
        options = JSON.parse(root.getAttribute("data-options") || "[]");
        config = JSON.parse(root.getAttribute("data-config") || "{}");
    } catch (e) {
        return;
    }

    var fmt = new Intl.NumberFormat("es-CR", {
        style: "currency",
        currency: config.currency || "CRC",
        maximumFractionDigits: 0
    });

    var q = function (role) { return root.querySelector('[data-role="' + role + '"]'); };

    var range = document.getElementById("lcCalcRange");
    var cycleButtons = root.querySelectorAll(".lc-calc-cycle");
    var form = q("form");
    var ctaText = q("cta-text");
    var cta = q("cta");
    var inWorkers = q("in-workers");
    var inCycle = q("in-cycle");

    var state = {
        workers: config.defaultWorkers || config.min || 1,
        cycle: config.defaultCycle === "Annual" ? "Annual" : "Monthly"
    };

    function findOption(workers, cycle) {
        for (var i = 0; i < options.length; i++) {
            if (options[i].workers === workers && options[i].cycle === cycle) {
                return options[i];
            }
        }
        return null;
    }

    function addPeriod(date, cycle) {
        var d = new Date(date.getTime());
        if (cycle === "Annual") {
            d.setFullYear(d.getFullYear() + 1);
        } else {
            d.setMonth(d.getMonth() + 1);
        }
        return d;
    }

    function formatDate(d) {
        var dd = String(d.getDate()).padStart(2, "0");
        var mm = String(d.getMonth() + 1).padStart(2, "0");
        return dd + "/" + mm + "/" + d.getFullYear();
    }

    function buildMarks() {
        var marks = q("marks");
        if (!marks) { return; }
        marks.innerHTML = "";
        for (var n = config.min; n <= config.max; n++) {
            var span = document.createElement("span");
            span.className = "lc-calc-mark";
            span.textContent = n;
            span.setAttribute("data-n", n);
            marks.appendChild(span);
        }
    }

    function maxAnnualSavingsPct() {
        var max = 0;
        for (var i = 0; i < options.length; i++) {
            if (options[i].cycle === "Annual" && options[i].available && options[i].savingsPct > max) {
                max = options[i].savingsPct;
            }
        }
        return max;
    }

    function setCycleTag() {
        var tag = q("cycle-tag");
        var pct = maxAnnualSavingsPct();
        if (tag && pct > 0) {
            tag.textContent = "ahorrá hasta " + pct + "%";
        }
    }

    function resolveCtaLabel(option) {
        if (!config.hasActive || !config.currentWorkers) {
            return { label: "Suscribirme", disabled: false };
        }
        var sameWorkers = option.workers === config.currentWorkers;
        var sameCycle = state.cycle === config.currentCycle;
        if (sameWorkers && sameCycle) {
            return { label: "Tu plan actual", disabled: true };
        }
        if (option.workers > config.currentWorkers) {
            return { label: "Aumentar funcionarios", disabled: false };
        }
        return { label: "Cambiar plan", disabled: false };
    }

    function render() {
        // Marca activa del ciclo
        cycleButtons.forEach(function (btn) {
            btn.classList.toggle("is-active", btn.getAttribute("data-cycle") === state.cycle);
            btn.setAttribute("aria-selected", btn.getAttribute("data-cycle") === state.cycle ? "true" : "false");
        });

        // Marcadores del slider
        root.querySelectorAll(".lc-calc-mark").forEach(function (m) {
            var n = parseInt(m.getAttribute("data-n"), 10);
            m.classList.toggle("is-active", n === state.workers);
            m.classList.toggle("is-disabled", n < config.min);
        });

        q("workers").textContent = state.workers;
        q("workers-label").textContent = state.workers === 1 ? "funcionario" : "funcionarios";

        var option = findOption(state.workers, state.cycle);
        var isAnnual = state.cycle === "Annual";

        q("pay-label").textContent = isAnnual ? "Pago anual" : "Pago mensual";

        var unavailable = q("unavailable");
        var savingsRow = q("savings-row");
        var annualNote = q("annual-note");

        if (!option || !option.available) {
            q("charge").textContent = "No disponible";
            q("equivalent").textContent = "";
            q("limit").textContent = "—";
            q("code").textContent = option ? option.code : "—";
            q("renewal").textContent = "—";
            savingsRow.hidden = true;
            if (annualNote) { annualNote.hidden = true; }
            unavailable.hidden = false;
            unavailable.textContent = "Esta combinación no está disponible por configuración. Elegí otra cantidad o ciclo, o contactá soporte.";
            cta.setAttribute("disabled", "disabled");
            inWorkers.value = state.workers;
            inCycle.value = state.cycle;
            return;
        }

        unavailable.hidden = true;
        q("charge").textContent = fmt.format(option.charge);
        q("limit").textContent = state.workers + (state.workers === 1 ? " funcionario" : " funcionarios");
        q("code").textContent = option.code;

        if (isAnnual) {
            q("equivalent").textContent = "Pago anual; equivale a " + fmt.format(option.monthlyEq) + "/mes";
            if (option.savings > 0) {
                savingsRow.hidden = false;
                q("savings").textContent = fmt.format(option.savings) +
                    (option.savingsPct > 0 ? " (" + option.savingsPct + "%)" : "");
            } else {
                savingsRow.hidden = true;
            }
            if (annualNote) {
                annualNote.hidden = false;
                annualNote.textContent = "El plan anual se cobra hoy por adelantado (" + fmt.format(option.charge) +
                    "). Tu próxima renovación es dentro de 12 meses.";
            }
        } else {
            q("equivalent").textContent = "por mes";
            savingsRow.hidden = true;
            if (annualNote) { annualNote.hidden = true; }
        }

        // Para el PLAN ACTUAL mostramos la próxima renovación real (fecha efectiva, ya con el
        // expire del proveedor). Solo si el usuario mueve el slider a otro plan mostramos el
        // estimado "hoy + 1 ciclo", que es un preview de lo que contrataría.
        var isCurrentPlan = config.hasActive &&
            config.currentWorkers === state.workers &&
            config.currentCycle === state.cycle;
        if (isCurrentPlan && config.currentRenewal) {
            q("renewal").textContent = config.currentRenewal;
        } else {
            q("renewal").textContent = formatDate(addPeriod(new Date(), state.cycle));
        }

        var ctaInfo = resolveCtaLabel(option);
        ctaText.textContent = ctaInfo.label;
        if (ctaInfo.disabled) {
            cta.setAttribute("disabled", "disabled");
        } else {
            cta.removeAttribute("disabled");
        }

        // Aviso "tu plan actual" cuando aplica
        var current = q("current");
        if (config.hasActive && config.currentCode) {
            current.hidden = false;
            current.textContent = "Plan actual: " + config.currentCode;
        }

        inWorkers.value = state.workers;
        inCycle.value = state.cycle;
    }

    function showFloorNote() {
        var floor = q("floor");
        if (floor && config.activeFuncionarios && config.min > 1) {
            floor.hidden = false;
            floor.textContent = "Tu negocio tiene " + config.activeFuncionarios +
                " funcionarios activos: no podés elegir un plan menor.";
        }
    }

    // Eventos
    range.addEventListener("input", function () {
        var v = parseInt(range.value, 10);
        if (v < config.min) { v = config.min; range.value = v; }
        state.workers = v;
        render();
    });

    cycleButtons.forEach(function (btn) {
        btn.addEventListener("click", function () {
            state.cycle = btn.getAttribute("data-cycle") === "Annual" ? "Annual" : "Monthly";
            render();
        });
    });

    // Defensa anti doble-click en el frontend (el backend es la defensa real)
    if (form) {
        form.addEventListener("submit", function () {
            cta.setAttribute("disabled", "disabled");
            var spin = q("spin");
            if (spin) { spin.hidden = false; }
        });
    }

    buildMarks();
    setCycleTag();
    showFloorNote();
    render();
})();
