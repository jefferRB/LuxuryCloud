// Mejora progresiva del menu responsive de la landing publica.
// El menu abre/cierra por defecto con un checkbox (funciona sin JS). Este script
// solo agrega: cerrar al elegir una opcion, cerrar con Escape y cerrar al tocar
// fuera, ademas de reflejar el estado en aria-expanded. Sin librerias ni CDN.
(function () {
    'use strict';

    var toggle = document.getElementById('tpp-nav-toggle');
    if (!toggle) {
        return;
    }

    var shell = document.querySelector('.tpp-nav-shell');
    var menuButton = document.querySelector('.tpp-nav-menu-button');
    var links = document.querySelectorAll('.tpp-nav-links a');

    function syncState() {
        if (menuButton) {
            menuButton.setAttribute('aria-expanded', toggle.checked ? 'true' : 'false');
        }
    }

    function closeMenu() {
        if (!toggle.checked) {
            return;
        }
        toggle.checked = false;
        syncState();
    }

    // Reflejar estado inicial y cambios manuales (tap en la hamburguesa).
    toggle.addEventListener('change', syncState);
    syncState();

    // Cerrar al elegir cualquier opcion del menu (Inicio, Servicios, Reservar, etc.).
    Array.prototype.forEach.call(links, function (link) {
        link.addEventListener('click', closeMenu);
    });

    // Cerrar con la tecla Escape.
    document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape') {
            closeMenu();
        }
    });

    // Cerrar al tocar fuera del navbar mientras el menu esta abierto.
    document.addEventListener('click', function (event) {
        if (!toggle.checked || !shell) {
            return;
        }
        if (!shell.contains(event.target)) {
            closeMenu();
        }
    });
})();
