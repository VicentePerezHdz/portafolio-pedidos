using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pedidos.Application;
using Pedidos.Infrastructure.Persistencia;

namespace Pedidos.Infrastructure.Mensajeria;

/// <summary>
/// BackgroundService que vacía la bandeja de salida: lee eventos no enviados y los publica.
/// Si se cae después de publicar pero antes de marcar "enviado", el evento se publica otra vez:
/// por eso el MessageId es el Id del outbox (Service Bus descarta duplicados) y el consumidor usa inbox.
/// </summary>
public class PublicadorOutboxWorker(IServiceScopeFactory scopes, TimeProvider reloj, ILogger<PublicadorOutboxWorker> log)
    : BackgroundService
{
    public static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(2);
    private const int Lote = 20;

    protected override async Task ExecuteAsync(CancellationToken detener)
    {
        while (!detener.IsCancellationRequested)
        {
            try { await PublicarPendientesAsync(detener); }
            catch (OperationCanceledException) when (detener.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Error al vaciar el outbox"); }

            try { await Task.Delay(Intervalo, reloj, detener); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Público para poder llamarlo directo en pruebas. Devuelve cuántos publicó.</summary>
    public async Task<int> PublicarPendientesAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PedidosDbContext>();
        var publicador = scope.ServiceProvider.GetRequiredService<IPublicadorMensajes>();

        var pendientes = await db.Outbox.Where(o => o.EnviadoUtc == null)
            .OrderBy(o => o.CreadoUtc).Take(Lote).ToListAsync(ct);

        var enviados = 0;
        foreach (var o in pendientes)
        {
            try
            {
                await publicador.PublicarAsync(new MensajeSaliente(o.Id.ToString(), o.Tipo, o.CuerpoJson, o.ClaveParticion), ct);
                o.EnviadoUtc = reloj.GetUtcNow().UtcDateTime;
                enviados++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                o.Intentos++;
                o.UltimoError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                log.LogWarning(ex, "No se pudo publicar {OutboxId} (intento {Intentos})", o.Id, o.Intentos);
                break; // conserva el orden: no publica los siguientes antes que este
            }
        }
        await db.SaveChangesAsync(ct);
        return enviados;
    }
}
