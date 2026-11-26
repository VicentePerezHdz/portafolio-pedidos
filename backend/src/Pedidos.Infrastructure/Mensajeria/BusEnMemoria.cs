using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pedidos.Application;

namespace Pedidos.Infrastructure.Mensajeria;

/// <summary>
/// Broker en memoria para desarrollo y pruebas (sin Azure). Imita lo esencial de Service Bus:
/// entrega con contador, reintentos con abandon y una dead-letter queue.
/// </summary>
public class BusEnMemoria : IPublicadorMensajes
{
    public const int MaxEntregas = 5; // como MaxDeliveryCount de la cola

    private readonly Channel<MensajeEntrante> _cola = Channel.CreateUnbounded<MensajeEntrante>();
    public ConcurrentQueue<(MensajeEntrante Mensaje, string Motivo)> DeadLetter { get; } = new();

    public ChannelReader<MensajeEntrante> Lector => _cola.Reader;

    public Task PublicarAsync(MensajeSaliente m, CancellationToken ct) =>
        _cola.Writer.WriteAsync(new MensajeEntrante(m.MensajeId, m.Tipo, m.CuerpoJson, NumeroEntrega: 1), ct).AsTask();

    public ValueTask ReencolarAsync(MensajeEntrante m, CancellationToken ct) =>
        _cola.Writer.WriteAsync(m with { NumeroEntrega = m.NumeroEntrega + 1 }, ct);
}

public class ConsumidorBusEnMemoriaWorker(BusEnMemoria bus, IServiceScopeFactory scopes, ILogger<ConsumidorBusEnMemoriaWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken detener)
    {
        try
        {
            await foreach (var m in bus.Lector.ReadAllAsync(detener))
                await ProcesarUnoAsync(m, detener);
        }
        catch (OperationCanceledException) { }
    }

    public async Task ProcesarUnoAsync(MensajeEntrante m, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var procesador = scope.ServiceProvider.GetRequiredService<ProcesadorPedidoCreado>();
        var r = await procesador.ProcesarAsync(m, ct);

        switch (r.Accion)
        {
            case Liquidacion.Completar:
                break;
            case Liquidacion.Reintentar when m.NumeroEntrega < BusEnMemoria.MaxEntregas:
                await bus.ReencolarAsync(m, ct);
                break;
            case Liquidacion.Reintentar:
                bus.DeadLetter.Enqueue((m, "MaxDeliveryCountExceeded"));
                break;
            case Liquidacion.DeadLetter:
                bus.DeadLetter.Enqueue((m, r.Motivo ?? "Sin motivo"));
                log.LogWarning("Mensaje {MensajeId} a dead-letter: {Motivo}", m.MensajeId, r.Motivo);
                break;
        }
    }
}
