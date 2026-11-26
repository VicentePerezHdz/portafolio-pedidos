namespace Pedidos.Domain;

public enum EstadoPedido { Creado = 0, Confirmado = 1, Cancelado = 2 }

/// <summary>Error de regla de negocio del dominio (la API lo traduce a 422).</summary>
public class ReglaDominioException(string mensaje) : Exception(mensaje);

/// <summary>
/// Entidad raíz (aggregate root) del dominio. No tiene setters públicos: el estado solo cambia
/// por métodos que validan las reglas. No conoce EF Core, HTTP ni Service Bus.
/// </summary>
public class Pedido
{
    public const int MaxClienteId = 64;

    public Guid Id { get; private set; }
    public string ClienteId { get; private set; } = string.Empty;
    public decimal Total { get; private set; }
    public EstadoPedido Estado { get; private set; }
    public DateTime CreadoUtc { get; private set; }

    /// <summary>Clave de idempotencia enviada por el cliente: el mismo POST repetido no crea dos pedidos.</summary>
    public string? ClaveIdempotencia { get; private set; }

    /// <summary>Token de concurrencia optimista: cambia en cada modificación.</summary>
    public int Version { get; private set; }

    private Pedido() { } // requerido por EF Core

    public static Pedido Crear(string clienteId, decimal total, DateTime ahoraUtc, string? claveIdempotencia = null)
    {
        if (string.IsNullOrWhiteSpace(clienteId) || clienteId.Length > MaxClienteId)
            throw new ReglaDominioException($"El cliente es obligatorio y de máximo {MaxClienteId} caracteres.");
        if (total <= 0)
            throw new ReglaDominioException("El total debe ser mayor a cero.");

        return new Pedido
        {
            Id = Guid.NewGuid(),
            ClienteId = clienteId.Trim(),
            Total = decimal.Round(total, 2),
            Estado = EstadoPedido.Creado,
            CreadoUtc = ahoraUtc,
            ClaveIdempotencia = claveIdempotencia,
            Version = 1,
        };
    }

    /// <summary>Confirmar es idempotente: confirmar dos veces no falla ni cambia nada.</summary>
    public bool Confirmar()
    {
        if (Estado == EstadoPedido.Confirmado) return false;
        if (Estado == EstadoPedido.Cancelado)
            throw new ReglaDominioException("No se puede confirmar un pedido cancelado.");
        Estado = EstadoPedido.Confirmado;
        Version++;
        return true;
    }

    public void Cancelar()
    {
        if (Estado == EstadoPedido.Confirmado)
            throw new ReglaDominioException("No se puede cancelar un pedido confirmado.");
        Estado = EstadoPedido.Cancelado;
        Version++;
    }
}
