(function () {
    const platformModalSelector = ".platform-whatsapp-modal";
    const platformBackdropClass = "platform-modal-backdrop";
    const platformOpenClass = "platform-modal-open";

    function tagCurrentBackdrop() {
        const backdrops = document.querySelectorAll(".modal-backdrop");
        const currentBackdrop = backdrops[backdrops.length - 1];

        if (currentBackdrop) {
            currentBackdrop.classList.add(platformBackdropClass);
        }
    }

    function syncOpenState() {
        const hasOpenPlatformModal = document.querySelector(`${platformModalSelector}.show`) !== null;
        document.body.classList.toggle(platformOpenClass, hasOpenPlatformModal);
    }

    function initializePlatformModal(modal) {
        if (modal.parentElement !== document.body) {
            document.body.appendChild(modal);
        }

        if (modal.dataset.platformModalInitialized === "true") {
            return;
        }

        modal.dataset.platformModalInitialized = "true";
        modal.addEventListener("show.bs.modal", function () {
            document.body.classList.add(platformOpenClass);
            window.setTimeout(tagCurrentBackdrop, 0);
        });
        modal.addEventListener("shown.bs.modal", function () {
            tagCurrentBackdrop();
            syncOpenState();
        });
        modal.addEventListener("hidden.bs.modal", syncOpenState);
    }

    function initializePlatformModals() {
        document.querySelectorAll(platformModalSelector).forEach(initializePlatformModal);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initializePlatformModals);
    } else {
        initializePlatformModals();
    }
})();
