using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pedidos.Domain;

namespace Pedidos.Application;

/// <summary>Qué hacer con un mensaje recibido. Se traduce a la API de Service Bus en Infrastructure.</summary>
public enum Liquidacion
{
    /// <summary>CompleteMessageAsync: se procesó (o ya estaba procesado); se borra de la cola.</summary>
    Completar,
    /// <summary>AbandonMessageAsync: falla temporal; vuelve a la cola y se reintenta. Al superar MaxDeliveryCount, Service Bus lo manda solo a la DLQ.</summary>
    Reintentar,
    /// <summary>DeadLetterMessageAsync: nunca va a funcionar (mensaje inválido o regla de negocio); va directo a la DLQ para revisión.</summary>
    DeadLetter,
}

public record ResultadoProcesamiento(Liquidacion Accion, string? Motivo = null);

public record MensajeEntrante(string MensajeId, string Tipo, string CuerpoJson, int NumeroEntrega);

/// <summary>
/// Consumidor del evento PedidoCreadoV1: confirma el pedido.
/// No depende del SDK de Azure, así se prueba con pruebas unitarias.
///
/// Garantías que maneja:
///  - Entrega duplicada (at-least-once): el inbox evita procesar dos veces el mismo MensajeId.
///  - Idempotencia de negocio: Pedido.Confirmar() no falla si ya estaba confirmado.
///  - Mensaje venenoso: JSON inválido o tipo desconocido => DeadLetter sin reintentar.
///  - Falla transitoria: => Reintentar (abandon).
///  - Consistencia eventual: el pedido nace "Creado" y pasa a "Confirmado" segundos después.
/// </summary>
public class ProcesadorPedidoCreado(
    IPedidoRepositorio repositorio,
    IRegistroMensajesProcesados inbox,
    IUnidadDeTrabajo unidadDeTrabajo,
    ILogger<ProcesadorPedidoCreado> log)
{
    public const string NombreConsumidor = "confirmador-pedidos";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ResultadoProcesamiento> ProcesarAsync(MensajeEntrante m, CancellationToken ct)
    {
        if (m.Tipo != PedidoCreadoV1.Tipo)
            return new(Liquidacion.DeadLetter, $"Tipo de mensaje no soportado: {m.Tipo}");

        PedidoCreadoV1? evento;
        try { evento = JsonSerializer.Deserialize<PedidoCreadoV1>(m.CuerpoJson, Json); }
        catch (JsonException ex) { return new(Liquidacion.DeadLetter, $"JSON inválido: {ex.Message}"); }
        if (evento is null || evento.PedidoId == Guid.Empty)
            return new(Liquidacion.DeadLetter, "El mensaje no trae PedidoId.");

        try
        {
            if (await inbox.YaProcesadoAsync(m.MensajeId, ct))
            {
                log.LogInformation("Mensaje {MensajeId} duplicado; se completa sin reprocesar", m.MensajeId);
                return new(Liquidacion.Completar, "Duplicado");
            }

            var pedido = await repositorio.ObtenerAsync(evento.PedidoId, ct);
            if (pedido is null)
                return new(Liquidacion.DeadLetter, $"El pedido {evento.PedidoId} no existe.");

            pedido.Confirmar();
            inbox.Registrar(m.MensajeId, NombreConsumidor);
            await unidadDeTrabajo.GuardarAsync(ct); // pedido + inbox en la misma transacción
            return new(Liquidacion.Completar);
        }
        catch (ReglaDominioException ex)
        {
            return new(Liquidacion.DeadLetter, ex.Message);
        }
        catch (Exception ex) when (ex is FallaTransitoriaException or ConflictoConcurrenciaException or TimeoutException)
        {
            log.LogWarning(ex, "Falla transitoria con {MensajeId} (entrega {Entrega}); se reintenta", m.MensajeId, m.NumeroEntrega);
            return new(Liquidacion.Reintentar, ex.Message);
        }
    }
}
