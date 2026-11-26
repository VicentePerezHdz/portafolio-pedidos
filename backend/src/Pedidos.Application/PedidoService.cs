using Microsoft.Extensions.Logging;
using Pedidos.Domain;

namespace Pedidos.Application;

/// <summary>Casos de uso de pedidos. Orquesta dominio + repositorio + outbox + unidad de trabajo.</summary>
public class PedidoService(
    IPedidoRepositorio repositorio,
    IBandejaSalida bandejaSalida,
    IUnidadDeTrabajo unidadDeTrabajo,
    TimeProvider reloj,
    ILogger<PedidoService> log)
{
    /// <summary>
    /// Crea un pedido y encola el evento PedidoCreadoV1 en la misma transacción.
    /// Si llega la misma clave de idempotencia, devuelve el pedido existente (no duplica).
    /// </summary>
    public async Task<(PedidoDto Pedido, bool Creado)> CrearAsync(CrearPedidoRequest req, string? claveIdempotencia, CancellationToken ct)
    {
        if (claveIdempotencia is not null)
        {
            var existente = await repositorio.ObtenerPorClaveIdempotenciaAsync(claveIdempotencia, ct);
            if (existente is not null) return (PedidoDto.De(existente), false);
        }

        var pedido = Pedido.Crear(req.ClienteId, req.Total, reloj.GetUtcNow().UtcDateTime, claveIdempotencia);
        repositorio.Agregar(pedido);
        bandejaSalida.Encolar(PedidoCreadoV1.Tipo,
            new PedidoCreadoV1(pedido.Id, pedido.ClienteId, pedido.Total, pedido.CreadoUtc),
            claveParticion: pedido.Id.ToString()); // mismo pedido => misma partición/sesión => orden garantizado

        await unidadDeTrabajo.GuardarAsync(ct);
        log.LogInformation("Pedido {PedidoId} creado para {ClienteId}", pedido.Id, pedido.ClienteId);
        return (PedidoDto.De(pedido), true);
    }

    public async Task<PedidoDto> ObtenerAsync(Guid id, CancellationToken ct) =>
        PedidoDto.De(await repositorio.ObtenerAsync(id, ct) ?? throw new NoEncontradoException($"No existe el pedido {id}."));

    public async Task<Pagina<PedidoDto>> ListarAsync(string? clienteId, int pagina, int tamano, CancellationToken ct)
    {
        pagina = Math.Max(1, pagina);
        tamano = Math.Clamp(tamano, 1, 100);
        var (elementos, total) = await repositorio.ListarAsync(clienteId, pagina, tamano, ct);
        return new Pagina<PedidoDto>(elementos.Select(PedidoDto.De).ToList(), pagina, tamano, total);
    }

    /// <summary>
    /// Cancela con concurrencia optimista: el cliente manda la versión que vio (If-Match).
    /// Si alguien más lo modificó antes, responde conflicto (409) en vez de pisar el cambio.
    /// </summary>
    public async Task<PedidoDto> CancelarAsync(Guid id, int versionEsperada, CancellationToken ct)
    {
        var pedido = await repositorio.ObtenerAsync(id, ct) ?? throw new NoEncontradoException($"No existe el pedido {id}.");
        if (pedido.Version != versionEsperada)
            throw new ConflictoConcurrenciaException($"El pedido cambió (versión actual {pedido.Version}). Vuelve a consultarlo.");
        pedido.Cancelar();
        await unidadDeTrabajo.GuardarAsync(ct);
        return PedidoDto.De(pedido);
    }
}
