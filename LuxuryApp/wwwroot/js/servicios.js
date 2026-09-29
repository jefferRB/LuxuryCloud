/* ============================================================
   servicios.js — Gestionar Servicios modal (global)
   Patron identico a puestos.js
   ============================================================ */

function obtenerServicioToken() {
    return $("#serviciosTokenContainer input[name='__RequestVerificationToken']").val()
        || $("input[name='__RequestVerificationToken']").first().val();
}

function cargarServicios() {
    $("#contenedorServicios").load("/Servicios/ModalServicios");
}

function renderServicioMessage(message, type) {
    var cls  = (type === "success") ? "svc-alert--success" : "svc-alert--danger";
    var icon = (type === "success") ? "bi-check-circle-fill" : "bi-exclamation-triangle-fill";
    $("#serviciosMessageContainer").html(
        '<div class="svc-alert ' + cls + '" role="alert">' +
            '<i class="bi ' + icon + '"></i>' +
            '<span>' + message + '</span>' +
        '</div>'
    );
}

function toggleServicio(id) {
    $("#serviciosMessageContainer").html("");
    $.ajax({
        url: "/Servicios/ToggleActivo",
        type: "POST",
        data: { id: id },
        headers: { "RequestVerificationToken": obtenerServicioToken() }
    }).done(function () {
        cargarServicios();
    }).fail(function (response) {
        renderServicioMessage(response.responseText || "No fue posible actualizar el estado del servicio.", "danger");
    });
}

function mostrarFormularioServicio(id) {
    var url = id ? "/Servicios/FormServicio?id=" + id : "/Servicios/FormServicio";
    var container = $("#formServicioContainer");
    container.show();
    container.load(url, function () {
        container.find(".money-input").each(function () {
            var input = this;
            if (input.value) input.value = formatearMoneda(input.value);
            $(input).on("input", function () {
                this.value = formatearMoneda(limpiarMoneda(this.value));
            });
        });
    });
    container[0].scrollIntoView({ behavior: "smooth", block: "nearest" });
}

function cerrarFormServicio() {
    var container = $("#formServicioContainer");
    container.hide();
    container.html("");
}

function guardarServicio() {
    var form = $("#formServicio");
    $("#serviciosMessageContainer").html("");

    form.find(".money-input").each(function () {
        this.value = limpiarMoneda(this.value);
    });

    $.ajax({
        url: "/Servicios/Save",
        type: "POST",
        data: form.serialize(),
        headers: {
            "RequestVerificationToken": form.find("input[name='__RequestVerificationToken']").val(),
            "X-Requested-With": "XMLHttpRequest"
        },
        success: function (response) {
            if (typeof response === "string" && response.trim().startsWith("<")) {
                $("#formServicioContainer").html(response);
                $("#formServicioContainer .money-input").each(function () {
                    var input = this;
                    if (input.value) input.value = formatearMoneda(input.value);
                    $(input).on("input", function () {
                        this.value = formatearMoneda(limpiarMoneda(this.value));
                    });
                });
                return;
            }
            cerrarFormServicio();
            cargarServicios();
        },
        error: function (response) {
            if (response.responseText && response.responseText.trim().startsWith("<")) {
                $("#formServicioContainer").html(response.responseText);
                return;
            }
            renderServicioMessage(response.responseText || "No fue posible guardar el servicio.", "danger");
        }
    });
}

function confirmarEliminarServicio(id, nombre) {
    var idInput  = document.getElementById("eliminarServicioId");
    var nombreEl = document.getElementById("eliminarServicioNombre");
    if (!idInput || !nombreEl) return;
    idInput.value = id;
    nombreEl.textContent = nombre;
    new bootstrap.Modal(document.getElementById("modalEliminarServicio")).show();
}

function eliminarServicio(id) {
    $("#serviciosMessageContainer").html("");
    $.ajax({
        url: "/Servicios/Eliminar",
        type: "POST",
        data: { id: id },
        headers: { "RequestVerificationToken": obtenerServicioToken() }
    }).done(function () {
        cerrarFormServicio();
        cargarServicios();
        renderServicioMessage("Servicio eliminado correctamente.", "success");
    }).fail(function (response) {
        renderServicioMessage(response.responseText || "No fue posible eliminar el servicio.", "danger");
    });
}
