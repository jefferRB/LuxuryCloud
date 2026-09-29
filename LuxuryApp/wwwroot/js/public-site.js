document.addEventListener("DOMContentLoaded", () => {
    const navbar = document.querySelector("[data-public-navbar]");

    // Respeta prefers-reduced-motion: si el usuario pidió menos movimiento, AOS se
    // deshabilita (deja el contenido visible sin animar). Sin JS, el CSS ya garantiza
    // que el contenido de la landing sea visible.
    const prefersReducedMotion = window.matchMedia
        ? window.matchMedia("(prefers-reduced-motion: reduce)").matches
        : false;

    if (window.AOS) {
        window.AOS.init({
            duration: 700,
            easing: "ease-out-cubic",
            once: true,
            offset: 40,
            disable: prefersReducedMotion
        });
    }

    if (navbar) {
        const syncNavbarState = () => {
            navbar.classList.toggle("is-scrolled", window.scrollY > 12);
        };
        syncNavbarState();
        window.addEventListener("scroll", syncNavbarState, { passive: true });
    }

    // Password toggle (show/hide)
    document.querySelectorAll("[data-pw-toggle]").forEach(btn => {
        btn.addEventListener("click", () => {
            const wrap = btn.closest(".auth-input-wrap");
            if (!wrap) return;
            const input = wrap.querySelector("input");
            if (!input) return;
            const isPassword = input.type === "password";
            input.type = isPassword ? "text" : "password";
            const icon = btn.querySelector("i");
            if (icon) icon.className = isPassword ? "bi bi-eye-slash" : "bi bi-eye";
            btn.setAttribute("aria-label", isPassword ? "Ocultar contraseña" : "Mostrar contraseña");
        });
    });

    // Prevent double submit on auth forms
    document.querySelectorAll("[data-prevent-double-submit]").forEach(form => {
        form.addEventListener("submit", () => {
            const btn = form.querySelector("[type='submit']");
            if (btn && !btn.disabled) {
                setTimeout(() => { btn.disabled = true; }, 0);
            }
        });
    });

    // Minicalculador de precios (mejora progresiva). Sin JS, el servidor ya renderizó el
    // plan mensual de 1 integrante con su precio correcto; aquí solo se actualiza el texto.
    // Los valores y el formateo provienen del servidor (data-tiers), no se recalcula nada.
    document.querySelectorAll("[data-lp-calc]").forEach(root => {
        let tiers;
        try {
            tiers = JSON.parse(root.getAttribute("data-tiers") || "[]");
        } catch {
            return; // sin datos válidos, se conserva el render inicial del servidor
        }

        const workersSelect = root.querySelector("[data-lp-workers]");
        const cycleButtons = root.querySelectorAll("[data-lp-cycle]");
        const chargeEl = root.querySelector("[data-lp-charge]");
        const periodEl = root.querySelector("[data-lp-period]");
        const unitEl = root.querySelector("[data-lp-unit]");
        const equivalentEl = root.querySelector("[data-lp-equivalent]");
        const includedEl = root.querySelector("[data-lp-included]");

        if (!workersSelect || !chargeEl) return;

        const currentCycle = () => {
            const active = root.querySelector("[data-lp-cycle].is-active");
            return active ? active.getAttribute("data-lp-cycle") : "Monthly";
        };

        const render = () => {
            const workers = parseInt(workersSelect.value, 10);
            const cycle = currentCycle();
            const tier = tiers.find(t => t.workers === workers && t.cycle === cycle);
            if (!tier) return;

            chargeEl.textContent = "₡" + tier.charge;
            if (includedEl) includedEl.textContent = tier.workersLabel;

            const isAnnual = cycle === "Annual";
            if (periodEl) periodEl.textContent = isAnnual ? "Pago anual" : "Pago mensual";
            if (unitEl) unitEl.textContent = isAnnual ? "por año" : "por mes";
            if (equivalentEl) {
                if (isAnnual) {
                    equivalentEl.textContent = "Equivale a ₡" + tier.monthlyEq + " por mes";
                    equivalentEl.hidden = false;
                } else {
                    equivalentEl.hidden = true;
                }
            }
        };

        workersSelect.addEventListener("change", render);
        cycleButtons.forEach(btn => {
            btn.addEventListener("click", () => {
                cycleButtons.forEach(other => {
                    const isActive = other === btn;
                    other.classList.toggle("is-active", isActive);
                    other.setAttribute("aria-pressed", isActive ? "true" : "false");
                });
                render();
            });
        });
    });
});
