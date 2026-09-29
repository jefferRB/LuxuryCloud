// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.
(function () {
    const tipoItem = document.getElementById("TipoItem");
    const servicioContainer = document.getElementById("ServicioContainer");
    const productoContainer = document.getElementById("ProductoContainer");
    const servicioSelect = document.getElementById("ServicioId");
    const productoSelect = document.getElementById("ProductoId");
    const montoInput = document.getElementById("Monto");

    if (!tipoItem || !servicioContainer || !productoContainer || !servicioSelect || !productoSelect || !montoInput) {
        return;
    }

    function syncCobroTypeUi() {
        const isServicio = tipoItem.value !== "producto";

        servicioContainer.classList.toggle("d-none", !isServicio);
        productoContainer.classList.toggle("d-none", isServicio);

        servicioSelect.disabled = !isServicio;
        productoSelect.disabled = isServicio;
    }

    async function cargarPrecio(endpoint, id) {
        if (!id) {
            montoInput.value = "";
            return;
        }

        const response = await fetch(`${endpoint}?id=${encodeURIComponent(id)}`);
        const data = await response.json();

        if (data && data.precio !== undefined && data.precio !== null) {
            montoInput.value = data.precio;
        }
    }

    tipoItem.addEventListener("change", syncCobroTypeUi);
    servicioSelect.addEventListener("change", function () {
        cargarPrecio("/Cobros/ObtenerPrecioServicio", this.value);
    });
    productoSelect.addEventListener("change", function () {
        cargarPrecio("/Cobros/ObtenerPrecioProducto", this.value);
    });

    syncCobroTypeUi();
})();

function toggleProducto(id) {
    const tokenField = document.querySelector("#productos-antiforgery input[name='__RequestVerificationToken']")
        || document.querySelector("input[name='__RequestVerificationToken']");
    const headers = tokenField && tokenField.value
        ? { RequestVerificationToken: tokenField.value }
        : {};

    $.ajax({
        url: "/Productos/ToggleActivo",
        type: "POST",
        data: { id: id },
        headers: headers
    })
        .done(function () {
            location.reload();
        })
        .fail(function (xhr) {
            const message = xhr.responseJSON && xhr.responseJSON.message
                ? xhr.responseJSON.message
                : "No fue posible cambiar el estado del producto.";
            alert(message);
        });
}

(function () {
    // El catalogo de temas lo publica el partial _AppearanceBootstrap desde
    // AppearanceThemeCatalog (C#). Aca no se escribe una segunda lista de temas: agregar un tema
    // al catalogo lo habilita automaticamente en el selector, en la persistencia y en el applier.
    const appearanceConfig = window.luxuryAppearance || {};
    const presets = appearanceConfig.presets || {
        "classic-marble": { label: "Clasico marmol", background: "marble", surface: "classic" }
    };
    const defaultPreset = Object.prototype.hasOwnProperty.call(presets, appearanceConfig.defaultPreset)
        ? appearanceConfig.defaultPreset
        : Object.keys(presets)[0];
    const storageKeys = appearanceConfig.storageKeys || {
        preset: "luxury-appearance-preset",
        background: "luxury-bg-theme",
        surface: "luxury-surface-theme",
        legacy: "luxury-theme"
    };
    const presetStorageKey = storageKeys.preset;
    const backgroundStorageKey = storageKeys.background;
    const surfaceStorageKey = storageKeys.surface;
    const legacyThemeStorageKey = storageKeys.legacy;

    function distinct(values) {
        return values.filter(function (value, index) {
            return values.indexOf(value) === index;
        });
    }

    const supportedBackgroundThemes = distinct(Object.keys(presets).map(function (id) {
        return presets[id].background;
    }));
    const supportedSurfaceThemes = distinct(Object.keys(presets).map(function (id) {
        return presets[id].surface;
    }));
    const chartRegistry = new Set();

    function normalizePreset(preset) {
        return Object.prototype.hasOwnProperty.call(presets, preset)
            ? preset
            : defaultPreset;
    }

    // Botones antiguos usaban data-luxury-theme-option="<background>" en vez del id del preset.
    function resolvePresetFromLegacyOption(background) {
        return Object.keys(presets).find(function (id) {
            return presets[id].background === background;
        }) || defaultPreset;
    }

    function buildAppearance(preset) {
        const resolvedPreset = normalizePreset(preset);
        const definition = presets[resolvedPreset];

        return {
            preset: resolvedPreset,
            label: definition.label,
            background: definition.background,
            surface: definition.surface
        };
    }

    function resolvePresetFromParts(background, surface) {
        const match = Object.keys(presets).find(function (id) {
            return presets[id].background === background && presets[id].surface === surface;
        });

        return match || defaultPreset;
    }

    function readStoredAppearance() {
        try {
            const storedPreset = localStorage.getItem(presetStorageKey);
            if (presets[storedPreset]) {
                return buildAppearance(storedPreset);
            }

            const storedBackground = localStorage.getItem(backgroundStorageKey);
            const storedSurface = localStorage.getItem(surfaceStorageKey);

            if (supportedBackgroundThemes.includes(storedBackground) &&
                supportedSurfaceThemes.includes(storedSurface)) {
                return buildAppearance(resolvePresetFromParts(storedBackground, storedSurface));
            }

            const legacyTheme = localStorage.getItem(legacyThemeStorageKey);
            const legacyMatch = Object.keys(presets).find(function (id) {
                return presets[id].background === legacyTheme;
            });

            return buildAppearance(legacyMatch || defaultPreset);
        } catch (error) {
            return buildAppearance(defaultPreset);
        }
    }

    function writeStoredAppearance(appearance) {
        try {
            localStorage.setItem(presetStorageKey, appearance.preset);
            localStorage.setItem(backgroundStorageKey, appearance.background);
            localStorage.setItem(surfaceStorageKey, appearance.surface);
            localStorage.setItem(legacyThemeStorageKey, appearance.background);
        } catch (error) {
            // Ignore storage errors and keep the in-memory appearance applied.
        }
    }

    function getPrivateThemeTokens() {
        const source = document.body || document.documentElement;
        const styles = window.getComputedStyle(source);

        function read(name, fallback) {
            const value = styles.getPropertyValue(name).trim();
            return value || fallback;
        }

        return {
            chartText: read("--private-chart-text", "#334155"),
            chartMuted: read("--private-chart-muted", "#64748b"),
            chartGrid: read("--private-chart-grid", "rgba(148, 163, 184, 0.24)"),
            chartPalette: [
                read("--private-chart-accent", "#2563eb"),
                read("--private-chart-success", "#16a34a"),
                read("--private-chart-warning", "#d97706"),
                read("--private-chart-danger", "#dc2626"),
                read("--private-chart-info", "#0891b2")
            ],
            chartPaletteSoft: [
                read("--private-chart-accent-soft", "rgba(37, 99, 235, 0.72)"),
                read("--private-chart-success-soft", "rgba(22, 163, 74, 0.72)"),
                read("--private-chart-warning-soft", "rgba(217, 119, 6, 0.72)"),
                read("--private-chart-danger-soft", "rgba(220, 38, 38, 0.68)"),
                read("--private-chart-info-soft", "rgba(8, 145, 178, 0.68)")
            ]
        };
    }

    function getChartConfigOptions(chart) {
        if (!chart || !chart.config) {
            return null;
        }

        chart.config.options = chart.config.options || {};
        return chart.config.options;
    }

    function ensureOptionGroup(parent, key) {
        if (!parent[key] || typeof parent[key] !== "object" || Array.isArray(parent[key])) {
            parent[key] = {};
        }

        return parent[key];
    }

    function styleChart(chart) {
        const options = getChartConfigOptions(chart);

        if (!options) {
            return;
        }

        const tokens = getPrivateThemeTokens();
        options.color = tokens.chartText;

        const plugins = ensureOptionGroup(options, "plugins");
        const legend = ensureOptionGroup(plugins, "legend");
        const labels = ensureOptionGroup(legend, "labels");
        labels.color = tokens.chartText;

        if (options.scales && typeof options.scales === "object") {
            Object.keys(options.scales).forEach(function (scaleKey) {
                const scale = ensureOptionGroup(options.scales, scaleKey);
                scale.ticks = ensureOptionGroup(scale, "ticks");
                scale.grid = ensureOptionGroup(scale, "grid");
                scale.ticks.color = tokens.chartMuted;
                scale.grid.color = tokens.chartGrid;
            });
        }

        if (chart.data && Array.isArray(chart.data.datasets)) {
            chart.data.datasets.forEach(function (dataset, index) {
                const color = tokens.chartPalette[index % tokens.chartPalette.length];
                const softColor = tokens.chartPaletteSoft[index % tokens.chartPaletteSoft.length];

                if (dataset.luxuryUsePalette === true) {
                    const labelCount = Array.isArray(chart.data.labels) ? chart.data.labels.length : 0;
                    const itemCount = labelCount > 0 ? labelCount : 1;

                    dataset.backgroundColor = Array.from({ length: itemCount }, function (_, itemIndex) {
                        return tokens.chartPaletteSoft[itemIndex % tokens.chartPaletteSoft.length];
                    });
                    dataset.borderColor = Array.from({ length: itemCount }, function (_, itemIndex) {
                        return tokens.chartPalette[itemIndex % tokens.chartPalette.length];
                    });
                    return;
                }

                if (dataset.luxuryAutoBackground === true ||
                    dataset.backgroundColor === undefined ||
                    dataset.backgroundColor === null) {
                    dataset.backgroundColor = softColor;
                    dataset.luxuryAutoBackground = true;
                }

                if (dataset.luxuryAutoBorder === true ||
                    dataset.borderColor === undefined ||
                    dataset.borderColor === null) {
                    dataset.borderColor = color;
                    dataset.luxuryAutoBorder = true;
                }

                if (dataset.luxuryAutoPointBackground === true ||
                    dataset.pointBackgroundColor === undefined ||
                    dataset.pointBackgroundColor === null) {
                    dataset.pointBackgroundColor = color;
                    dataset.luxuryAutoPointBackground = true;
                }

                if (dataset.luxuryAutoPointBorder === true ||
                    dataset.pointBorderColor === undefined ||
                    dataset.pointBorderColor === null) {
                    dataset.pointBorderColor = color;
                    dataset.luxuryAutoPointBorder = true;
                }
            });
        }
    }

    function resolveChartForCanvas(canvas) {
        if (!window.Chart || !canvas || typeof window.Chart.getChart !== "function") {
            return null;
        }

        return window.Chart.getChart(canvas);
    }

    function destroyChartForCanvas(canvas) {
        const existingChart = resolveChartForCanvas(canvas);

        if (!existingChart) {
            return;
        }

        chartRegistry.delete(existingChart);

        if (typeof existingChart.destroy === "function") {
            existingChart.destroy();
        }
    }

    function refreshCharts() {
        if (window.Chart) {
            const tokens = getPrivateThemeTokens();
            window.Chart.defaults.color = tokens.chartText;
            window.Chart.defaults.borderColor = tokens.chartGrid;
        }

        chartRegistry.forEach(function (chart) {
            if (!chart || chart._destroyed === true || !chart.canvas) {
                chartRegistry.delete(chart);
                return;
            }

            try {
                styleChart(chart);
                if (typeof chart.update === "function") {
                    chart.update("none");
                }
            } catch (error) {
                chartRegistry.delete(chart);
            }
        });
    }

    window.luxuryGetPrivateThemeTokens = getPrivateThemeTokens;
    window.luxuryDestroyChartForCanvas = destroyChartForCanvas;
    window.luxuryRegisterChart = function (chart) {
        if (!chart) {
            return chart;
        }

        chartRegistry.add(chart);

        try {
            styleChart(chart);
        } catch (error) {
            chartRegistry.delete(chart);
        }

        return chart;
    };
    window.luxuryRefreshCharts = refreshCharts;

    function updateAppearanceOptionUi(appearance) {
        document.querySelectorAll("[data-luxury-appearance-current], [data-luxury-theme-current]").forEach(function (element) {
            element.textContent = appearance.label;
        });

        document.querySelectorAll("[data-luxury-appearance-option], [data-luxury-theme-option]").forEach(function (button) {
            const optionPreset = button.getAttribute("data-luxury-appearance-option");
            const legacyTheme = button.getAttribute("data-luxury-theme-option");
            const requestedPreset = normalizePreset(optionPreset || resolvePresetFromLegacyOption(legacyTheme));
            const isSelected = requestedPreset === appearance.preset;
            button.classList.toggle("is-selected", isSelected);
            button.setAttribute("aria-pressed", isSelected ? "true" : "false");

            const checkIcon = button.querySelector("[data-luxury-theme-check]");
            if (checkIcon) {
                checkIcon.classList.toggle("d-none", !isSelected);
            }
        });
    }

    function applyAppearance(preset) {
        const appearance = buildAppearance(preset);

        document.documentElement.setAttribute("data-luxury-appearance-preset", appearance.preset);
        document.documentElement.setAttribute("data-luxury-bg-theme", appearance.background);
        document.documentElement.setAttribute("data-luxury-surface-theme", appearance.surface);
        document.documentElement.setAttribute("data-luxury-theme", appearance.background);

        supportedBackgroundThemes.forEach(function (backgroundTheme) {
            document.documentElement.classList.remove("theme-bg-" + backgroundTheme);
            document.documentElement.classList.remove("theme-" + backgroundTheme);
        });

        supportedSurfaceThemes.forEach(function (surfaceTheme) {
            document.documentElement.classList.remove("surface-" + surfaceTheme);
        });

        document.documentElement.classList.add("theme-bg-" + appearance.background);
        document.documentElement.classList.add("theme-" + appearance.background);
        document.documentElement.classList.add("surface-" + appearance.surface);

        if (document.body) {
            supportedBackgroundThemes.forEach(function (backgroundTheme) {
                document.body.classList.remove("theme-bg-" + backgroundTheme);
                document.body.classList.remove("theme-" + backgroundTheme);
            });

            supportedSurfaceThemes.forEach(function (surfaceTheme) {
                document.body.classList.remove("surface-" + surfaceTheme);
            });

            document.body.classList.add("theme-bg-" + appearance.background);
            document.body.classList.add("theme-" + appearance.background);
            document.body.classList.add("surface-" + appearance.surface);
        }

        updateAppearanceOptionUi(appearance);
        refreshCharts();
        document.dispatchEvent(new CustomEvent("luxury:appearance-changed", { detail: appearance }));
        return appearance;
    }

    function handleAppearanceSelection(event) {
        const button = event.currentTarget;
        const requestedPreset = normalizePreset(button.getAttribute("data-luxury-appearance-option")
            || resolvePresetFromLegacyOption(button.getAttribute("data-luxury-theme-option")));
        const appearance = applyAppearance(requestedPreset);
        writeStoredAppearance(appearance);
    }

    function initializeAppearanceManager() {
        const initialAppearance = readStoredAppearance();
        applyAppearance(initialAppearance.preset);

        document.querySelectorAll("[data-luxury-appearance-option], [data-luxury-theme-option]").forEach(function (button) {
            if (button.dataset.luxuryAppearanceListenerInitialized === "true") {
                return;
            }

            button.dataset.luxuryAppearanceListenerInitialized = "true";
            button.addEventListener("click", handleAppearanceSelection);
        });
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initializeAppearanceManager);
    } else {
        initializeAppearanceManager();
    }
})();

(function () {
    const header = document.querySelector(".private-header");
    const handle = document.querySelector("[data-private-navbar-handle]");
    const desktopMedia = window.matchMedia("(min-width: 1200px)");

    if (!header || header.dataset.scrollBehaviorInitialized === "true") {
        return;
    }

    header.dataset.scrollBehaviorInitialized = "true";

    let lastScrollY = window.scrollY;
    let isHidden = false;
    let ticking = false;

    function applyHiddenState(shouldHide) {
        const hiddenState = Boolean(shouldHide);
        if (isHidden === hiddenState) {
            return;
        }

        header.classList.toggle("private-header-hidden", hiddenState);
        header.classList.toggle("private-header-revealed", !hiddenState);

        if (handle) {
            handle.setAttribute("aria-expanded", hiddenState ? "false" : "true");
        }

        isHidden = hiddenState;
    }

    function shouldFreezeState() {
        return header.matches(":hover")
            || header.contains(document.activeElement)
            || document.body.classList.contains("modal-open")
            || header.querySelector(".dropdown-menu.show") !== null
            || header.querySelector(".navbar-collapse.show") !== null;
    }

    function revealNavbar() {
        applyHiddenState(false);
    }

    function handlePointerEnter() {
        revealNavbar();
    }

    function handlePointerLeave() {
        queueNavbarUpdate();
    }

    function handleFocusIn() {
        revealNavbar();
    }

    function handleFocusOut() {
        window.requestAnimationFrame(function () {
            queueNavbarUpdate();
        });
    }

    function updateNavbarState() {
        ticking = false;

        const currentScrollY = Math.max(window.scrollY, 0);

        if (!desktopMedia.matches) {
            applyHiddenState(false);
            lastScrollY = currentScrollY;
            return;
        }

        if (shouldFreezeState()) {
            revealNavbar();
            lastScrollY = currentScrollY;
            return;
        }

        const scrollDelta = currentScrollY - lastScrollY;
        const nearTopThreshold = 40;
        const hideThreshold = 120;
        const deltaThreshold = 8;

        if (currentScrollY <= nearTopThreshold) {
            applyHiddenState(false);
        } else if (scrollDelta <= -deltaThreshold) {
            applyHiddenState(false);
        } else if (scrollDelta >= deltaThreshold && currentScrollY > hideThreshold) {
            applyHiddenState(true);
        }

        lastScrollY = currentScrollY;
    }

    function queueNavbarUpdate() {
        if (ticking) {
            return;
        }

        ticking = true;
        window.requestAnimationFrame(updateNavbarState);
    }

    header.addEventListener("mouseenter", handlePointerEnter);
    header.addEventListener("mouseleave", handlePointerLeave);
    header.addEventListener("focusin", handleFocusIn);
    header.addEventListener("focusout", handleFocusOut);

    if (handle) {
        handle.addEventListener("click", function () {
            revealNavbar();
            handle.focus();
        });
    }

    window.addEventListener("scroll", queueNavbarUpdate, { passive: true });
    window.addEventListener("resize", queueNavbarUpdate, { passive: true });

    if (typeof desktopMedia.addEventListener === "function") {
        desktopMedia.addEventListener("change", queueNavbarUpdate);
    } else if (typeof desktopMedia.addListener === "function") {
        desktopMedia.addListener(queueNavbarUpdate);
    }

    header.classList.add("private-header-revealed");
    if (handle) {
        handle.setAttribute("aria-expanded", "true");
    }
    queueNavbarUpdate();
})();
