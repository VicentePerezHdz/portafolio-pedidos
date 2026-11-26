using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pedidos.Application;
using Pedidos.Infrastructure.Persistencia;

namespace Pedidos.Tests;

/// <summary>
/// Base de datos SQLite en memoria compartida por varios DbContext (como varias peticiones a la misma BD).
/// Es relacional de verdad: respeta índices únicos, transacciones y tokens de concurrencia.
/// </summary>
public sealed class BdPrueba : IDisposable
{
    private readonly SqliteConnection _conexion = new("DataSource=:memory:");
    public FakeTimeProvider Reloj { get; } = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));

    public BdPrueba()
    {
        _conexion.Open();
        using var db = NuevoContexto();
        db.Database.EnsureCreated();
    }

    public PedidosDbContext NuevoContexto() =>
        new(new DbContextOptionsBuilder<PedidosDbContext>().UseSqlite(_conexion).Options);

    public PedidoService Servicio(PedidosDbContext db) => new(
        new PedidoRepositorio(db), new BandejaSalida(db, Reloj), new UnidadDeTrabajo(db), Reloj,
        NullLogger<PedidoService>.Instance);

    public ProcesadorPedidoCreado Procesador(PedidosDbContext db) => new(
        new PedidoRepositorio(db), new RegistroMensajesProcesados(db, Reloj), new UnidadDeTrabajo(db),
        NullLogger<ProcesadorPedidoCreado>.Instance);

    public void Dispose() => _conexion.Dispose();
}
