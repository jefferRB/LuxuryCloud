namespace LuxuryApp.Models.DataBase
{
    /// <summary>Resultado de intentar registrar una nota de servicio.</summary>
    public sealed record ClienteNotaResultado
    {
        public bool Exitoso { get; init; }

        /// <summary>Motivo del rechazo, listo para mostrar al usuario. <c>null</c> si fue exitoso.</summary>
        public string? Error { get; init; }

        /// <summary>Nota creada, para pintarla sin volver a consultar. <c>null</c> si falló.</summary>
        public ClienteServicioRealizadoItemViewModel? Nota { get; init; }

        /// <summary>
        /// El recurso no existe dentro del tenant actual: el cliente, o la nota pedida. No se
        /// distingue "no existe" de "es de otro negocio", para no filtrar su existencia.
        /// </summary>
        public bool ClienteNoEncontrado { get; init; }

        public static ClienteNotaResultado Ok(ClienteServicioRealizadoItemViewModel nota) =>
            new() { Exitoso = true, Nota = nota };

        /// <summary>Eliminación correcta: no hay nota que devolver.</summary>
        public static ClienteNotaResultado Eliminado() =>
            new() { Exitoso = true };

        public static ClienteNotaResultado Invalido(string error) =>
            new() { Exitoso = false, Error = error };

        public static ClienteNotaResultado NoEncontrado() =>
            new() { Exitoso = false, ClienteNoEncontrado = true, Error = "El cliente no existe." };

        public static ClienteNotaResultado NotaNoEncontrada() =>
            new() { Exitoso = false, ClienteNoEncontrado = true, Error = "La nota ya no existe." };
    }
}
