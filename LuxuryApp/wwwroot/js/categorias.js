function cargarCategorias() {
    $("#contenedorCategorias").load("/Categorias/ModalCategorias");
}

function obtenerCategoriaToken() {
    return $("#categoriasTokenContainer input[name='__RequestVerificationToken']").val()
        || $("input[name='__RequestVerificationToken']").first().val();
}

function renderCategoriaMessage(message, type) {
    const alertType = type || "danger";
    $("#categoriasMessageContainer").html(
        `<div class="alert alert-${alertType} alert-dismissible fade show" role="alert">
            ${message}
            <button type="button" class="btn-close" data-bs-dismiss="alert" aria-label="Close"></button>
        </div>`);
}

function toggleCategoria(id) {
    $("#categoriasMessageContainer").html("");

    $.ajax({
        url: "/Categorias/ToggleActivo",
        type: "POST",
        data: { id: id },
        headers: {
            "RequestVerificationToken": obtenerCategoriaToken()
        }
    }).done(function () {
        cargarCategorias();
    }).fail(function (response) {
        renderCategoriaMessage(response.responseText || "No fue posible actualizar el estado de la categoría.");
    });
}

function mostrarFormularioCategoria(id) {
    const url = id ? `/Categorias/FormCategoria?id=${id}` : "/Categorias/FormCategoria";
    $("#formCategoriaContainer").load(url);
}

function guardarCategoria() {
    const form = $("#formCategoria");
    $("#categoriasMessageContainer").html("");

    $.ajax({
        url: "/Categorias/Save",
        type: "POST",
        data: form.serialize(),
        headers: {
            "RequestVerificationToken": form.find("input[name='__RequestVerificationToken']").val(),
            "X-Requested-With": "XMLHttpRequest"
        },
        success: function (response) {
            if (typeof response === "string" && response.trim().startsWith("<")) {
                $("#formCategoriaContainer").html(response);
                return;
            }

            $("#formCategoriaContainer").html("");
            cargarCategorias();
        },
        error: function (response) {
            if (response.responseText && response.responseText.trim().startsWith("<")) {
                $("#formCategoriaContainer").html(response.responseText);
                return;
            }

            renderCategoriaMessage(response.responseText || "No fue posible guardar la categoría.");
        }
    });
}

function eliminarCategoria(id) {
    if (!confirm("¿Seguro que deseas eliminar esta categoría? Esta acción no se puede deshacer.")) {
        return;
    }

    $("#categoriasMessageContainer").html("");

    $.ajax({
        url: "/Categorias/Delete",
        type: "POST",
        data: { id: id },
        headers: {
            "RequestVerificationToken": obtenerCategoriaToken()
        }
    }).done(function () {
        $("#formCategoriaContainer").html("");
        cargarCategorias();
    }).fail(function (response) {
        renderCategoriaMessage(response.responseText || "No fue posible eliminar la categoría.");
    });
}
