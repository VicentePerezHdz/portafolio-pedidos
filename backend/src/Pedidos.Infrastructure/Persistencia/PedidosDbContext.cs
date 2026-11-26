using Microsoft.EntityFrameworkCore;
using Pedidos.Domain;

namespace Pedidos.Infrastructure.Persistencia;

/// <summary>Fila de la bandeja de salida: el evento espera aquí hasta que el worker lo publique.</summary>
public class OutboxMensaje
{
    public Guid Id { get; set; }                 // se usa como MessageId => detección de duplicados en Service Bus
    public string Tipo { get; set; } = "";
    public string CuerpoJson { get; set; } = "";
    public string ClaveParticion { get; set; } = "";
    public DateTime CreadoUtc { get; set; }
    public DateTime? EnviadoUtc { get; set; }
    public int Intentos { get; set; }
    public string? UltimoError { get; set; }
}

/// <summary>Fila de la bandeja de entrada: mensajes ya procesados por un consumidor.</summary>
public class MensajeProcesado
{
    public string MensajeId { get; set; } = "";
    public string Consumidor { get; set; } = "";
    public DateTime ProcesadoUtc { get; set; }
}

public class PedidosDbContext(DbContextOptions<PedidosDbContext> options) : DbContext(options)
{
    public DbSet<Pedido> Pedidos => Set<Pedido>();
    public DbSet<OutboxMensaje> Outbox => Set<OutboxMensaje>();
    public DbSet<MensajeProcesado> MensajesProcesados => Set<MensajeProcesado>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Pedido>(e =>
        {
            e.ToTable("pedidos");
            e.HasKey(p => p.Id);
            e.Property(p => p.ClienteId).HasMaxLength(Pedido.MaxClienteId).IsRequired();
            e.Property(p => p.Total).HasPrecision(18, 2);
            e.Property(p => p.Estado).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.ClaveIdempotencia).HasMaxLength(100);
            // SQLite no guarda el "Kind" de DateTime: al leer se marca como UTC para que el JSON salga con "Z".
            e.Property(p => p.CreadoUtc).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

            // Concurrencia optimista: EF agrega "WHERE version = @original" al UPDATE.
            // Si no afecta filas, lanza DbUpdateConcurrencyException.
            e.Property(p => p.Version).IsConcurrencyToken();

            // Índices: búsqueda por cliente ordenada por fecha (la consulta del listado) e idempotencia única.
            e.HasIndex(p => new { p.ClienteId, p.CreadoUtc });
            e.HasIndex(p => p.ClaveIdempotencia).IsUnique();
        });

        b.Entity<OutboxMensaje>(e =>
        {
            e.ToTable("outbox");
            e.HasKey(o => o.Id);
            e.Property(o => o.Tipo).HasMaxLength(100);
            e.Property(o => o.ClaveParticion).HasMaxLength(100);
            e.HasIndex(o => new { o.EnviadoUtc, o.CreadoUtc }); // el worker busca "no enviados, los más viejos primero"
        });

        b.Entity<MensajeProcesado>(e =>
        {
            e.ToTable("mensajes_procesados");
            e.HasKey(m => new { m.MensajeId, m.Consumidor });
        });
    }
}
