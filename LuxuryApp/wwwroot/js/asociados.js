/*
    Módulo Asociados — comportamiento de la interfaz.

    Todo lo que hay acá es progresivo y cosmético: mostrar u ocultar secciones y marcar casillas
    desde un preset. La autorización y las reglas financieras viven en el servidor; si este archivo
    no cargara, el formulario seguiría funcionando y nada quedaría desprotegido.
*/
(function () {
    'use strict';

    var TIPO_INVERSIONISTA = 0;

    function mostrar(el, visible) {
        if (el) {
            el.style.display = visible ? '' : 'none';
        }
    }

    // ── Sección "Participación": aparece solo si la persona es inversionista ──
    function enlazarTipos() {
        var bloque = document.getElementById('assocParticipacion');
        if (!bloque) {
            return;
        }

        var checks = document.querySelectorAll('[data-assoc-tipo]');
        if (!checks.length) {
            return;
        }

        function sincronizar() {
            var esInversionista = false;
            checks.forEach(function (check) {
                if (check.checked && check.getAttribute('data-assoc-tipo') === String(TIPO_INVERSIONISTA)) {
                    esInversionista = true;
                }
            });
            mostrar(bloque, esInversionista);
        }

        checks.forEach(function (check) {
            check.addEventListener('change', sincronizar);
        });

        sincronizar();
    }

    // ── Sección "Acceso al sistema" ──
    function enlazarAcceso() {
        var toggle = document.getElementById('assocDarAcceso');
        var config = document.getElementById('assocAccesoConfig');
        if (!toggle || !config) {
            return;
        }

        function sincronizar() {
            mostrar(config, toggle.checked);
        }

        toggle.addEventListener('change', sincronizar);
        sincronizar();
    }

    // ── Contraseña temporal ──
    function enlazarModoCredencial() {
        var campo = document.getElementById('assocTempPwd');
        if (!campo) {
            return;
        }

        var radios = document.querySelectorAll('input[name="ModoCredencial"], input[name="modoCredencial"]');
        if (!radios.length) {
            return;
        }

        function sincronizar() {
            var temporal = false;
            radios.forEach(function (radio) {
                if (radio.checked && (radio.value === '1' || radio.value === 'temporal')) {
                    temporal = true;
                }
            });
            mostrar(campo, temporal);
        }

        radios.forEach(function (radio) {
            radio.addEventListener('change', sincronizar);
        });

        sincronizar();
    }

    // ── Presets de permisos ──
    // Marca lo que sugiere el preset y desmarca el resto. A partir de ahí el administrador
    // ajusta a mano: el preset no queda guardado en ningún lado.
    function enlazarPresets() {
        var botones = document.querySelectorAll('[data-assoc-preset]');
        if (!botones.length) {
            return;
        }

        botones.forEach(function (boton) {
            boton.addEventListener('click', function () {
                var crudo = boton.getAttribute('data-assoc-preset') || '';
                var deseados = crudo ? crudo.split(',') : [];
                var grid = boton.closest('form') || document;

                grid.querySelectorAll('input[name="permisos"]').forEach(function (check) {
                    check.checked = deseados.indexOf(check.value) !== -1;
                });
            });
        });
    }

    // ── Día de corte ──
    // Solo aplica a acuerdos mensuales, así que el campo aparece y desaparece con la frecuencia.
    //
    // OJO: acá NO se calculan períodos. Las fechas ("período actual", "próximo corte") las resuelve
    // el servidor con InvestorSettlementPeriodResolver. Si el usuario cambia el corte sin guardar,
    // lo único que hace este código es avisar que el resumen quedó desactualizado.
    var FRECUENCIA_MENSUAL = '2';

    function enlazarDiaDeCorte() {
        var selects = document.querySelectorAll('[data-assoc-frecuencia]');
        var campos = document.querySelectorAll('[data-assoc-corte-field]');
        if (!selects.length || !campos.length) {
            return;
        }

        function sincronizar() {
            var mensual = false;
            selects.forEach(function (select) {
                if (select.value === FRECUENCIA_MENSUAL) {
                    mensual = true;
                }
            });

            campos.forEach(function (campo) {
                mostrar(campo, mensual);
            });
        }

        selects.forEach(function (select) {
            select.addEventListener('change', function () {
                sincronizar();
                marcarResumenPendiente();
            });
        });

        sincronizar();
    }

    function marcarResumenPendiente() {
        var aviso = document.querySelector('[data-assoc-cutoff-pending]');
        var resumen = document.querySelector('[data-assoc-cutoff-summary]');

        if (aviso) {
            mostrar(aviso, true);
        }

        if (resumen) {
            resumen.style.opacity = '0.55';
        }
    }

    function enlazarAvisoDeCorte() {
        var input = document.querySelector('[data-assoc-corte-input]');
        if (!input) {
            return;
        }

        var original = input.value;
        input.addEventListener('input', function () {
            if (input.value !== original) {
                marcarResumenPendiente();
            }
        });
    }

    // ── Confirmación antes de bloquear un acceso ──
    function enlazarConfirmaciones() {
        document.querySelectorAll('[data-assoc-confirm]').forEach(function (form) {
            form.addEventListener('submit', function (evento) {
                if (!window.confirm(form.getAttribute('data-assoc-confirm'))) {
                    evento.preventDefault();
                }
            });
        });
    }

    function iniciar() {
        enlazarTipos();
        enlazarAcceso();
        enlazarModoCredencial();
        enlazarPresets();
        enlazarDiaDeCorte();
        enlazarAvisoDeCorte();
        enlazarConfirmaciones();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', iniciar);
    } else {
        iniciar();
    }
})();
