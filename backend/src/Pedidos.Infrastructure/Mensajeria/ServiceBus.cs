using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pedidos.Application;

namespace Pedidos.Infrastructure.Mensajeria;

public class OpcionesMensajeria
{
    public const string Seccion = "Mensajeria";
    /// <summary>"EnMemoria" (desarrollo) o "ServiceBus".</summary>
    public string Modo { get; set; } = "EnMemoria";
    /// <summary>Ej. "mi-namespace.servicebus.windows.net". Con esto se usa identidad administrada, sin secretos.</summary>
    public string? Namespace { get; set; }
    public string Cola { get; set; } = "pedidos";
    public int MaxSesionesConcurrentes { get; set; } = 8;
}

/// <summary>Publica en una cola con sesiones: SessionId = PedidoId garantiza orden FIFO por pedido.</summary>
public class PublicadorServiceBus(ServiceBusClient cliente, IOptions<OpcionesMensajeria> opciones) : IPublicadorMensajes, IAsyncDisposable
{
    private readonly ServiceBusSender _sender = cliente.CreateSender(opciones.Value.Cola);

    public Task PublicarAsync(MensajeSaliente m, CancellationToken ct) =>
        _sender.SendMessageAsync(new ServiceBusMessage(BinaryData.FromString(m.CuerpoJson))
        {
            MessageId = m.MensajeId,        // con "duplicate detection" activado en la cola, un reenvío se descarta
            Subject = m.Tipo,
            SessionId = m.ClaveParticion,
            ContentType = "application/json",
        }, ct);

    public ValueTask DisposeAsync() => _sender.DisposeAsync();
}

/// <summary>
/// Consumidor real. Liquidación manual (AutoCompleteMessages = false):
///  - Completar      -> CompleteMessageAsync (se borra)
///  - Reintentar     -> AbandonMessageAsync (libera el lock; DeliveryCount++; al pasar MaxDeliveryCount va a la DLQ)
///  - DeadLetter     -> DeadLetterMessageAsync con motivo (revisión manual / reproceso)
/// El lock del mensaje se renueva solo mientras se procesa (MaxAutoLockRenewalDuration).
/// </summary>
public class ConsumidorServiceBusWorker(
    ServiceBusClient cliente, IOptions<OpcionesMensajeria> opciones, IServiceScopeFactory scopes,
    ILogger<ConsumidorServiceBusWorker> log) : BackgroundService
{
    private ServiceBusSessionProcessor? _procesador;

    protected override async Task ExecuteAsync(CancellationToken detener)
    {
        _procesador = cliente.CreateSessionProcessor(opciones.Value.Cola, new ServiceBusSessionProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentSessions = opciones.Value.MaxSesionesConcurrentes,
            MaxConcurrentCallsPerSession = 1, // orden estricto dentro de cada pedido
            MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5),
        });
        _procesador.ProcessMessageAsync += ProcesarAsync;
        _procesador.ProcessErrorAsync += e =>
        {
            log.LogError(e.Exception, "Error de Service Bus ({Origen}) en {Entidad}", e.ErrorSource, e.EntityPath);
            return Task.CompletedTask;
        };

        await _procesador.StartProcessingAsync(detener);
        try { await Task.Delay(Timeout.Infinite, detener); }
        catch (OperationCanceledException) { }
        await _procesador.StopProcessingAsync(CancellationToken.None);
    }

    private async Task ProcesarAsync(ProcessSessionMessageEventArgs args)
    {
        var msg = args.Message;
        using var scope = scopes.CreateScope();
        var procesador = scope.ServiceProvider.GetRequiredService<ProcesadorPedidoCreado>();
        var r = await procesador.ProcesarAsync(
            new MensajeEntrante(msg.MessageId, msg.Subject, msg.Body.ToString(), msg.DeliveryCount), args.CancellationToken);

        switch (r.Accion)
        {
            case Liquidacion.Completar:
                await args.CompleteMessageAsync(msg, args.CancellationToken);
                break;
            case Liquidacion.Reintentar:
                await args.AbandonMessageAsync(msg, cancellationToken: args.CancellationToken);
                break;
            case Liquidacion.DeadLetter:
                await args.DeadLetterMessageAsync(msg, "Procesamiento", r.Motivo, args.CancellationToken);
                break;
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        if (_procesador is not null) await _procesador.DisposeAsync();
        await base.StopAsync(ct);
    }
}
