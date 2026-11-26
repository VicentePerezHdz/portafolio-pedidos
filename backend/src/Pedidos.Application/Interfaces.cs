using Pedidos.Domain;

namespace Pedidos.Application;

// Puertos (interfaces) que la capa de aplicación necesita. Infrastructure los implementa.
// Así Application no depende de EF Core, Npgsql ni Azure.

public interface IPedidoRepositorio
{
    Task<Pedido?> ObtenerAsync(Guid id, CancellationToken ct);
    Task<Pedido?> ObtenerPorClaveIdempotenciaAsync(string clave, CancellationToken ct);
    Task<(IReadOnlyList<Pedido> Elementos, int Total)> ListarAsync(string? clienteId, int pagina, int tamano, CancellationToken ct);
    void Agregar(Pedido pedido);
}

/// <summary>Bandeja de salida (Transactional Outbox): el evento se guarda en la MISMA transacción que el pedido.</summary>
public interface IBandejaSalida
{
    void Encolar<T>(string tipo, T evento, string claveParticion);
}

/// <summary>Unidad de trabajo: confirma en una sola transacción todo lo que se agregó/modificó.</summary>
public interface IUnidadDeTrabajo
{
    Task GuardarAsync(CancellationToken ct);
}

/// <summary>Bandeja de entrada (Inbox): registra mensajes ya procesados para tolerar entregas duplicadas.</summary>
public interface IRegistroMensajesProcesados
{
    Task<bool> YaProcesadoAsync(string mensajeId, CancellationToken ct);
    void Registrar(string mensajeId, string consumidor);
}

/// <summary>Publica un mensaje al broker (Service Bus o el bus en memoria de desarrollo).</summary>
public interface IPublicadorMensajes
{
    Task PublicarAsync(MensajeSaliente mensaje, CancellationToken ct);
}

public record MensajeSaliente(string MensajeId, string Tipo, string CuerpoJson, string ClaveParticion);
