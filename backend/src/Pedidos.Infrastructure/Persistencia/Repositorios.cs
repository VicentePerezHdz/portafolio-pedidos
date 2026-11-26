using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pedidos.Application;
using Pedidos.Domain;

namespace Pedidos.Infrastructure.Persistencia;

public class PedidoRepositorio(PedidosDbContext db) : IPedidoRepositorio
{
    public Task<Pedido?> ObtenerAsync(Guid id, CancellationToken ct) =>
        db.Pedidos.FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Pedido?> ObtenerPorClaveIdempotenciaAsync(string clave, CancellationToken ct) =>
        db.Pedidos.FirstOrDefaultAsync(p => p.ClaveIdempotencia == clave, ct);

    public async Task<(IReadOnlyList<Pedido> Elementos, int Total)> ListarAsync(string? clienteId, int pagina, int tamano, CancellationToken ct)
    {
        // AsNoTracking: solo lectura, EF no guarda copia para detectar cambios (más rápido).
        var q = db.Pedidos.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(clienteId)) q = q.Where(p => p.ClienteId == clienteId);
        var total = await q.CountAsync(ct);
        var elementos = await q.OrderByDescending(p => p.CreadoUtc)
            .Skip((pagina - 1) * tamano).Take(tamano).ToListAsync(ct);
        return (elementos, total);
    }

    public void Agregar(Pedido pedido) => db.Pedidos.Add(pedido);
}

public class BandejaSalida(PedidosDbContext db, TimeProvider reloj) : IBandejaSalida
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Solo agrega al DbContext: se guarda en el mismo SaveChanges (misma transacción) que el pedido.
    public void Encolar<T>(string tipo, T evento, string claveParticion) => db.Outbox.Add(new OutboxMensaje
    {
        Id = Guid.NewGuid(),
        Tipo = tipo,
        CuerpoJson = JsonSerializer.Serialize(evento, Json),
        ClaveParticion = claveParticion,
        CreadoUtc = reloj.GetUtcNow().UtcDateTime,
    });
}

public class RegistroMensajesProcesados(PedidosDbContext db, TimeProvider reloj) : IRegistroMensajesProcesados
{
    public Task<bool> YaProcesadoAsync(string mensajeId, CancellationToken ct) =>
        db.MensajesProcesados.AnyAsync(m => m.MensajeId == mensajeId && m.Consumidor == ProcesadorPedidoCreado.NombreConsumidor, ct);

    public void Registrar(string mensajeId, string consumidor) => db.MensajesProcesados.Add(new MensajeProcesado
    {
        MensajeId = mensajeId, Consumidor = consumidor, ProcesadoUtc = reloj.GetUtcNow().UtcDateTime,
    });
}

/// <summary>SaveChanges = una transacción. Traduce errores de EF a excepciones de la aplicación.</summary>
public class UnidadDeTrabajo(PedidosDbContext db) : IUnidadDeTrabajo
{
    public async Task GuardarAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConflictoConcurrenciaException("Otro proceso modificó el registro. Vuelve a intentarlo. " + ex.Message);
        }
        catch (DbUpdateException ex) when (EsViolacionDeUnicidad(ex))
        {
            // Dos peticiones con la misma clave de idempotencia al mismo tiempo: gana una, la otra es conflicto.
            throw new ConflictoConcurrenciaException("Ya existe un registro con esa clave.");
        }
    }

    private static bool EsViolacionDeUnicidad(DbUpdateException ex) =>
        ex.InnerException?.GetType().Name switch
        {
            "PostgresException" => ex.InnerException.GetType().GetProperty("SqlState")?.GetValue(ex.InnerException) as string == "23505",
            "SqliteException" => ex.InnerException.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
}
