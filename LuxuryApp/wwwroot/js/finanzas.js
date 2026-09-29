/* ============================================================
   finanzas.js — Money input formatting (global para Cobros)
   ============================================================ */

function formatearMoneda(valor) {
    valor = String(valor).replace(/[^\d]/g, "");
    if (!valor) return "";
    return new Intl.NumberFormat("es-CR").format(valor);
}

function limpiarMoneda(valor) {
    return String(valor).replace(/[^\d]/g, "");
}

document.addEventListener("DOMContentLoaded", function () {
    document.querySelectorAll(".money-input").forEach(function (input) {
        if (input.value) {
            input.value = formatearMoneda(input.value);
        }
        input.addEventListener("input", function () {
            var limpio = limpiarMoneda(this.value);
            this.value = formatearMoneda(limpio);
        });
        var form = input.closest("form");
        if (form) {
            form.addEventListener("submit", function () {
                input.value = limpiarMoneda(input.value);
            });
        }
    });
});
