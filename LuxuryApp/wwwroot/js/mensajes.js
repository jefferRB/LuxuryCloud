let chatActual = null;
let mensajeReply = null;


function formatearHora(fecha) {
    return new Date(fecha).toLocaleTimeString([], {
        hour: '2-digit',
        minute: '2-digit'
    });
}

function formatearDia(fecha) {

    const hoy = new Date();
    const fechaMsg = new Date(fecha);

    const esHoy =
        hoy.toDateString() === fechaMsg.toDateString();

    if (esHoy) return "Hoy";

    return fechaMsg.toLocaleDateString();
}

function escapeHtml(value) {
    return String(value ?? "")
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#039;");
}

const conversaciones = [
    {
        nombre: "Juan Pérez",
        telefono: "8888-8888",
        mensajes: [
            { texto: "Hola quiero info", tipo: "received", fecha: new Date(), leido: false },
            { texto: "Claro con gusto 👋", tipo: "sent", fecha: new Date(), leido: true }
        ]
    },
    {
        nombre: "María Gómez",
        telefono: "7777-7777",
        mensajes: [
            { texto: "Confirmar cita", tipo: "received", fecha: new Date(), leido: false }
        ]
    }
];

function inicializarMensajes() {
    const inputMensaje = document.getElementById("mensajeInput");
    const contenedorMensajes = document.getElementById("chatMessages");
    const listaChats = document.getElementById("chatList");

    if (!inputMensaje || !contenedorMensajes || !listaChats) {
        return;
    }

    cargarChats();

    inputMensaje.addEventListener("keypress", function (e) {
        if (e.key === "Enter") {
            e.preventDefault();
            enviarMensaje();
        }
    });

    contenedorMensajes.addEventListener("scroll", () => {

        if (!chatActual) return;

        const estaAbajo =
            contenedorMensajes.scrollHeight - contenedorMensajes.scrollTop <= contenedorMensajes.clientHeight + 50;

        if (estaAbajo) {
            marcarMensajesLeidos(chatActual);
            cargarChats();
            renderMensajes();
        }
    });

    inputMensaje.focus();
}

if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", inicializarMensajes);
} else {
    inicializarMensajes();
}

function cargarChats() {

    const contenedor = document.getElementById("chatList");
    contenedor.innerHTML = "";

    conversaciones.forEach((chat, index) => {

        const inicial = escapeHtml(chat.nombre.charAt(0));
        const noLeidos = contarNoLeidos(chat);
        const tieneNoLeidos = noLeidos > 0;

        const ultimoMensaje = chat.mensajes[chat.mensajes.length - 1];
        const nombre = escapeHtml(chat.nombre);
        const ultimoTexto = escapeHtml(ultimoMensaje.texto);

        contenedor.innerHTML += `
        <div class="chat-item ${tieneNoLeidos ? "chat-nuevo" : ""}" onclick="abrirChat(${index})">
            <div class="chat-avatar">${inicial}</div>
            <div>
                <strong>${nombre}</strong><br>
                <small>${ultimoTexto}</small>
            </div>
                ${noLeidos > 0 ? `<div class="badge-no-leidos">${noLeidos}</div>` : ""}

        </div>`;
    });
}

function abrirChat(index) {

    chatActual = conversaciones[index];

    marcarMensajesLeidos(chatActual);

    document.getElementById("chatHeader").innerHTML =
        `<strong>${escapeHtml(chatActual.nombre)}</strong><br><small>${escapeHtml(chatActual.telefono)}</small>`;

    ordenarConversaciones();
    cargarChats();
    renderMensajes();
}

function renderMensajes() {
    if (!chatActual) return;

    const contenedor = document.getElementById("chatMessages");
    //Detectar si el usuario esta abajo en los chats
    const estaAbajo =
        contenedor.scrollHeight - contenedor.scrollTop <= contenedor.clientHeight + 50;


    contenedor.innerHTML = "";

    let ultimoDia = "";

    chatActual.mensajes.forEach((msg, index) => {

        const dia = formatearDia(msg.fecha);
        const safeDia = escapeHtml(dia);
        const safeTexto = escapeHtml(msg.texto);
        const safeReplyTexto = msg.replyTo ? escapeHtml(msg.replyTo.texto) : "";
        const safeTipo = msg.tipo === "sent" ? "sent" : "received";

        // ⭐ Separador día
        if (dia !== ultimoDia) {
            contenedor.innerHTML += `
                <div class="chat-day">
                    ${safeDia}
                </div>`;
            ultimoDia = dia;
        }

        let estadoCheck = "";

        if (msg.tipo === "sent") {

            estadoCheck = msg.leido
                ? `<span class="message-status check leido">✔✔</span>`
                : `<span class="message-status check">✔✔</span>`;
        }

        contenedor.innerHTML += `
<div class="message ${safeTipo}">

    ${msg.replyTo ? `
        <div class="reply-preview">
            ${safeReplyTexto}
        </div>
    ` : ""}

    <div class="message-content">
        ${safeTexto}
    </div>

    <div class="message-actions">
        <span onclick="responderMensaje(${index})">⋮</span>
    </div>

    <span class="message-time">
        ${formatearHora(msg.fecha)}
        ${estadoCheck}
    </span>

</div>`;
    });

    // ⭐ AUTO SCROLL AL FINAL
    if (estaAbajo) {
        contenedor.scrollTop = contenedor.scrollHeight;
    }
}

function enviarMensaje() {

    const input = document.getElementById("mensajeInput");

    if (!input.value || !chatActual) return;

    chatActual.mensajes.push({
        texto: input.value,
        tipo: "sent",
        fecha: new Date(),
        leido: false,
        replyTo: mensajeReply

    });

    input.value = "";

    mensajeReply = null;
    cancelarReply();

    ordenarConversaciones();
    cargarChats();
    renderMensajes();

    // ⭐ Simulación respuesta cliente
    mostrarTyping();

    setTimeout(() => {

        ocultarTyping();
        chatActual.mensajes.forEach(m => {
            if (m.tipo === "sent") {
                m.leido = true;
            }
        });

        const contenedor = document.getElementById("chatMessages");

        const estaAbajo =
            contenedor.scrollHeight - contenedor.scrollTop <= contenedor.clientHeight + 50;

        chatActual.mensajes.push({
            texto: "Perfecto 👍",
            tipo: "received",
            fecha: new Date(),
            leido: estaAbajo // ⭐ SOLO si el usuario está viendo el chat
        });

        ordenarConversaciones();
        cargarChats();
        renderMensajes();

    }, 2000);
}

function ordenarConversaciones() {

    conversaciones.sort((a, b) => {

        const fechaA = a.mensajes[a.mensajes.length - 1].fecha;
        const fechaB = b.mensajes[b.mensajes.length - 1].fecha;

        return new Date(fechaB) - new Date(fechaA);
    });
}

function mostrarTyping() {

    const contenedor = document.getElementById("chatMessages");

    const typingHTML = `
    <div class="typing-indicator" id="typingIndicator">
        <div class="typing-dot"></div>
        <div class="typing-dot"></div>
        <div class="typing-dot"></div>
    </div>`;

    contenedor.innerHTML += typingHTML;

    contenedor.scrollTop = contenedor.scrollHeight;
}

function ocultarTyping() {

    const indicador = document.getElementById("typingIndicator");
    if (indicador) indicador.remove();
}

function marcarMensajesLeidos(chat) {

    chat.mensajes.forEach(m => {
        if (m.tipo === "received") {
            m.leido = true;
        }
    });

}
function contarNoLeidos(chat) {

    return chat.mensajes.filter(m =>
        m.tipo === "received" && !m.leido
    ).length;

}

function responderMensaje(index) {

    mensajeReply = chatActual.mensajes[index];

    mostrarReplyPreview();

    document.getElementById("mensajeInput").focus();
}

function mostrarReplyPreview() {

    if (!mensajeReply) return;

    document.getElementById("replyPreviewContainer").innerHTML = `
        <div class="reply-box">
            <span>${escapeHtml(mensajeReply.texto)}</span>
            <button onclick="cancelarReply()">✖</button>
        </div>
    `;
}

function cancelarReply() {
    mensajeReply = null;
    document.getElementById("replyPreviewContainer").innerHTML = "";
}
