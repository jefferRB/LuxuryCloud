using LuxuryApp.Models.DataBase;
using LuxuryApp.Services.BusinessTime;
using Microsoft.EntityFrameworkCore;
using ProyectoIdentity.Datos;

namespace LuxuryApp.Services.Clientes
{
    /// <inheritdoc cref="IClienteNotasService"/>
    public sealed class ClienteNotasService : IClienteNotasService
    {
        private readonly ApplicationDbContext _context;
        private readonly IBusinessDateTimeProvider _businessDateTimeProvider;

        public ClienteNotasService(
            ApplicationDbContext context,
            IBusinessDateTimeProvider businessDateTimeProvider)
        {
            _context = context;
            _businessDateTimeProvider = businessDateTimeProvider;
        }

        public async Task<IReadOnlyList<ClienteServicioRealizadoItemViewModel>> GetNotasAsync(
            int clienteId,
            CancellationToken cancellationToken = default)
        {
            if (clienteId <= 0)
            {
                return Array.Empty<ClienteServicioRealizadoItemViewModel>();
            }

            return await _context.ClienteServiciosRealizados
                .AsNoTracking()
                .Where(registro => registro.ClienteId == clienteId)
                .OrderByDescending(registro => registro.FechaHora)
                .ThenByDescending(registro => registro.Id)
                .Select(registro => new ClienteServicioRealizadoItemViewModel
                {
                    Id = registro.Id,
                    FechaHora = registro.FechaHora,
                    NombreServicio = registro.Servicio != null ? registro.Servicio.Nombre : null,
                    NombreFuncionario = registro.Funcionario != null ? registro.Funcionario.Nombre : null,
                    Origen = registro.Origen,
                    Monto = registro.Monto,
                    Notas = registro.Notas
                })
                .ToListAsync(cancellationToken);
        }

        public async Task<ClienteNotaResultado> AgregarNotaAsync(
            int clienteId,
            string? texto,
            int? funcionarioId,
            CancellationToken cancellationToken = default)
        {
            if (clienteId <= 0)
            {
                return ClienteNotaResultado.NoEncontrado();
            }

            if (!TryNormalizarTexto(texto, out var textoNormalizado, out var errorTexto))
            {
                return ClienteNotaResultado.Invalido(errorTexto);
            }

            // El cliente se verifica contra el contexto filtrado por tenant: un id de otro
            // negocio simplemente no aparece, así que nunca se escribe una nota cruzada.
            var clienteExiste = await _context.Clientes
                .AsNoTracking()
                .AnyAsync(cliente => cliente.Id == clienteId, cancellationToken);

            if (!clienteExiste)
            {
                return ClienteNotaResultado.NoEncontrado();
            }

            // Solo se guarda el funcionario cuando quien escribe realmente lo es; un
            // administrador sin ficha de colaborador deja el campo vacío en vez de inventarlo.
            var funcionarioValido = funcionarioId.HasValue && funcionarioId.Value > 0
                && await _context.Funcionarios
                    .AsNoTracking()
                    .AnyAsync(funcionario => funcionario.IdFuncionario == funcionarioId.Value, cancellationToken);

            var ahora = _businessDateTimeProvider.Now();

            var registro = new ClienteServicioRealizado
            {
                ClienteId = clienteId,
                FuncionarioId = funcionarioValido ? funcionarioId : null,
                FechaHora = ahora,
                CreadoEn = ahora,
                Notas = textoNormalizado,
                Origen = OrigenServicioRealizado.Manual
            };

            _context.ClienteServiciosRealizados.Add(registro);
            await _context.SaveChangesAsync(cancellationToken);

            var nombreFuncionario = funcionarioValido
                ? await _context.Funcionarios
                    .AsNoTracking()
                    .Where(funcionario => funcionario.IdFuncionario == funcionarioId!.Value)
                    .Select(funcionario => funcionario.Nombre)
                    .FirstOrDefaultAsync(cancellationToken)
                : null;

            return ClienteNotaResultado.Ok(new ClienteServicioRealizadoItemViewModel
            {
                Id = registro.Id,
                FechaHora = registro.FechaHora,
                NombreFuncionario = nombreFuncionario,
                Origen = registro.Origen,
                Notas = registro.Notas
            });
        }

        public async Task<ClienteNotaResultado> ActualizarNotaAsync(
            int clienteId,
            int notaId,
            string? texto,
            CancellationToken cancellationToken = default)
        {
            if (!TryNormalizarTexto(texto, out var textoNormalizado, out var errorTexto))
            {
                return ClienteNotaResultado.Invalido(errorTexto);
            }

            var registro = await BuscarNotaDelClienteAsync(clienteId, notaId, cancellationToken);

            if (registro is null)
            {
                return ClienteNotaResultado.NotaNoEncontrada();
            }

            registro.Notas = textoNormalizado;
            await _context.SaveChangesAsync(cancellationToken);

            var nombreFuncionario = registro.FuncionarioId.HasValue
                ? await _context.Funcionarios
                    .AsNoTracking()
                    .Where(funcionario => funcionario.IdFuncionario == registro.FuncionarioId.Value)
                    .Select(funcionario => funcionario.Nombre)
                    .FirstOrDefaultAsync(cancellationToken)
                : null;

            return ClienteNotaResultado.Ok(new ClienteServicioRealizadoItemViewModel
            {
                Id = registro.Id,
                FechaHora = registro.FechaHora,
                NombreFuncionario = nombreFuncionario,
                Origen = registro.Origen,
                Monto = registro.Monto,
                Notas = registro.Notas
            });
        }

        public async Task<ClienteNotaResultado> EliminarNotaAsync(
            int clienteId,
            int notaId,
            CancellationToken cancellationToken = default)
        {
            var registro = await BuscarNotaDelClienteAsync(clienteId, notaId, cancellationToken);

            if (registro is null)
            {
                // Ya no existe: para quien pidió borrarla el objetivo está cumplido, pero se
                // informa para que la UI no deje una tarjeta fantasma en pantalla.
                return ClienteNotaResultado.NotaNoEncontrada();
            }

            _context.ClienteServiciosRealizados.Remove(registro);
            await _context.SaveChangesAsync(cancellationToken);

            return ClienteNotaResultado.Eliminado();
        }

        /// <summary>
        /// Localiza la nota acotada por tenant (filtro global) Y por cliente. Es el único punto
        /// por el que entran actualización y borrado, así que ningún id del navegador puede
        /// alcanzar la nota de otra cuenta o de otro cliente.
        /// </summary>
        private async Task<ClienteServicioRealizado?> BuscarNotaDelClienteAsync(
            int clienteId,
            int notaId,
            CancellationToken cancellationToken)
        {
            if (clienteId <= 0 || notaId <= 0)
            {
                return null;
            }

            return await _context.ClienteServiciosRealizados
                .FirstOrDefaultAsync(
                    registro => registro.Id == notaId && registro.ClienteId == clienteId,
                    cancellationToken);
        }

        private static bool TryNormalizarTexto(
            string? texto,
            out string textoNormalizado,
            out string error)
        {
            textoNormalizado = texto?.Trim() ?? string.Empty;

            if (textoNormalizado.Length == 0)
            {
                error = "Escribe la nota antes de guardar.";
                return false;
            }

            if (textoNormalizado.Length > IClienteNotasService.NotaMaxLength)
            {
                error = $"La nota no puede superar {IClienteNotasService.NotaMaxLength} caracteres.";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }
}
