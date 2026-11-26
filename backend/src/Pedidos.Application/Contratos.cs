using System.ComponentModel.DataAnnotations;
using Pedidos.Domain;

namespace Pedidos.Application;

// ===== DTO de la API =====

public record CrearPedidoRequest(
    [Required, StringLength(Pedido.MaxClienteId)] string ClienteId,
    [Range(0.01, 1_000_000)] decimal Total);

public record PedidoDto(Guid Id, string ClienteId, decimal Total, string Estado, DateTime CreadoUtc, int Version)
{
    public static PedidoDto De(Pedido p) => new(p.Id, p.ClienteId, p.Total, p.Estado.ToString(), p.CreadoUtc, p.Version);
}

public record Pagina<T>(IReadOnlyList<T> Elementos, int NumeroPagina, int Tamano, int Total);

// ===== Eventos de integración (viajan por Service Bus) =====

/// <summary>Evento publicado cuando se crea un pedido. Versionado para evolucionar el contrato sin romper consumidores.</summary>
public record PedidoCreadoV1(Guid PedidoId, string ClienteId, decimal Total, DateTime CreadoUtc)
{
    public const string Tipo = "pedidos.pedido-creado.v1";
}

// ===== Excepciones de aplicación (la API las traduce a HTTP) =====

public class NoEncontradoException(string mensaje) : Exception(mensaje);

public class ConflictoConcurrenciaException(string mensaje) : Exception(mensaje);

/// <summary>Falla temporal (BD o red): el mensaje debe reintentarse.</summary>
public class FallaTransitoriaException(string mensaje, Exception? interna = null) : Exception(mensaje, interna);
