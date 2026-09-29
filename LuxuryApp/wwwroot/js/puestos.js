function cargarPuestos() {
    $("#contenedorPuestos").load("/Puestos/ModalPuestos");
}

function obtenerPuestoToken() {
    return $("#puestosTokenContainer input[name='__RequestVerificationToken']").val()
        || $("input[name='__RequestVerificationToken']").first().val();
}

function renderPuestoMessage(message, type) {
    var cls  = (type === "success") ? "func-alert--success" : "func-alert--danger";
    var icon = (type === "success") ? "bi-check-circle-fill" : "bi-exclamation-triangle-fill";
    $("#puestosMessageContainer").html(
        '<div class="func-alert ' + cls + '" role="alert" style="margin-bottom:0.85rem;">' +
            '<i class="bi ' + icon + '"></i>' +
            '<span>' + message + '</span>' +
        '</div>'
    );
}

function togglePuesto(id) {
    $("#puestosMessageContainer").html("");

    $.ajax({
        url: "/Puestos/ToggleActivo",
        type: "POST",
        data: { id: id },
        headers: {
            "RequestVerificationToken": obtenerPuestoToken()
        }
    }).done(function () {
        cargarPuestos();
    }).fail(function (response) {
        renderPuestoMessage(response.responseText || "No fue posible actualizar el estado del puesto.");
    });
}

function mostrarFormularioPuesto(id) {
    var url = id ? "/Puestos/FormPuesto?id=" + id : "/Puestos/FormPuesto";
    var container = $("#formPuestoContainer");
    container.show();
    container.load(url);
    container[0].scrollIntoView({ behavior: "smooth", block: "nearest" });
}

function cerrarFormPuesto() {
    var container = $("#formPuestoContainer");
    container.hide();
    container.html("");
}

function guardarPuesto() {
    var form = $("#formPuesto");
    $("#puestosMessageContainer").html("");

    $.ajax({
        url: "/Puestos/Save",
        type: "POST",
        data: form.serialize(),
        headers: {
            "RequestVerificationToken": form.find("input[name='__RequestVerificationToken']").val(),
            "X-Requested-With": "XMLHttpRequest"
        },
        success: function (response) {
            if (typeof response === "string" && response.trim().startsWith("<")) {
                $("#formPuestoContainer").html(response);
                return;
            }

            cerrarFormPuesto();
            cargarPuestos();
        },
        error: function (response) {
            if (response.responseText && response.responseText.trim().startsWith("<")) {
                $("#formPuestoContainer").html(response.responseText);
                return;
            }

            renderPuestoMessage(response.responseText || "No fue posible guardar el puesto.");
        }
    });
}

function confirmarEliminarPuesto(id, nombre) {
    var idInput = document.getElementById("eliminarPuestoId");
    var nombreEl = document.getElementById("eliminarPuestoNombre");
    if (!idInput || !nombreEl) {
        return;
    }
    idInput.value = id;
    nombreEl.textContent = nombre;
    var modal = new bootstrap.Modal(document.getElementById("modalEliminarPuesto"));
    modal.show();
}

function eliminarPuesto(id) {
    $("#puestosMessageContainer").html("");

    $.ajax({
        url: "/Puestos/Delete",
        type: "POST",
        data: { id: id },
        headers: {
            "RequestVerificationToken": obtenerPuestoToken()
        }
    }).done(function () {
        cerrarFormPuesto();
        cargarPuestos();
        renderPuestoMessage("Puesto eliminado correctamente.", "success");
    }).fail(function (response) {
        renderPuestoMessage(response.responseText || "No fue posible eliminar el puesto.");
    });
}
