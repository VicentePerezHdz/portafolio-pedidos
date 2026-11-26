using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pedidos.Application;
using Pedidos.Domain;

namespace Pedidos.Tests;

public class ProcesadorPedidoCreadoTests
{
    private static MensajeEntrante Mensaje(Guid pedidoId, string id = "msg-1", int entrega = 1) => new(
        id, PedidoCreadoV1.Tipo,
        JsonSerializer.Serialize(new PedidoCreadoV1(pedidoId, "cli-1", 10, DateTime.UtcNow), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        entrega);

    private static async Task<Guid> CrearPedido(BdPrueba bd)
    {
        await using var db = bd.NuevoContexto();
        return (await bd.Servicio(db).CrearAsync(new("cli-1", 10), null, default)).Pedido.Id;
    }

    [Fact]
    public async Task Mensaje_valido_confirma_el_pedido_y_se_completa()
    {
        using var bd = new BdPrueba();
        var id = await CrearPedido(bd);

        await using var db = bd.NuevoContexto();
        var r = await bd.Procesador(db).ProcesarAsync(Mensaje(id), default);

        Assert.Equal(Liquidacion.Completar, r.Accion);
        await using var lectura = bd.NuevoContexto();
        Assert.Equal(EstadoPedido.Confirmado, (await lectura.Pedidos.SingleAsync()).Estado);
    }

    [Fact]
    public async Task Entrega_duplicada_se_completa_sin_reprocesar()
    {
        using var bd = new BdPrueba();
        var id = await CrearPedido(bd);
        await using (var db = bd.NuevoContexto()) await bd.Procesador(db).ProcesarAsync(Mensaje(id), default);

        await using var db2 = bd.NuevoContexto();
        var r = await bd.Procesador(db2).ProcesarAsync(Mensaje(id, entrega: 2), default);

        Assert.Equal(new ResultadoProcesamiento(Liquidacion.Completar, "Duplicado"), r);
        Assert.Equal(1, await db2.MensajesProcesados.CountAsync());
    }

    [Theory]
    [InlineData("{ esto no es json", "JSON inválido")]
    [InlineData("{}", "no trae PedidoId")]
    public async Task Mensaje_venenoso_va_a_dead_letter_sin_reintentar(string cuerpo, string motivo)
    {
        using var bd = new BdPrueba();
        await using var db = bd.NuevoContexto();

        var r = await bd.Procesador(db).ProcesarAsync(new MensajeEntrante("m", PedidoCreadoV1.Tipo, cuerpo, 1), default);

        Assert.Equal(Liquidacion.DeadLetter, r.Accion);
        Assert.Contains(motivo, r.Motivo);
    }

    [Fact]
    public async Task Tipo_desconocido_va_a_dead_letter()
    {
        using var bd = new BdPrueba();
        await using var db = bd.NuevoContexto();
        var r = await bd.Procesador(db).ProcesarAsync(new MensajeEntrante("m", "otro.evento.v9", "{}", 1), default);
        Assert.Equal(Liquidacion.DeadLetter, r.Accion);
    }

    [Fact]
    public async Task Pedido_cancelado_rompe_regla_de_negocio_y_va_a_dead_letter()
    {
        using var bd = new BdPrueba();
        var id = await CrearPedido(bd);
        await using (var db = bd.NuevoContexto()) await bd.Servicio(db).CancelarAsync(id, 1, default);

        await using var db2 = bd.NuevoContexto();
        var r = await bd.Procesador(db2).ProcesarAsync(Mensaje(id), default);

        Assert.Equal(Liquidacion.DeadLetter, r.Accion);
    }

    [Fact]
    public async Task Falla_transitoria_pide_reintento()
    {
        var procesador = new ProcesadorPedidoCreado(new RepositorioQueFalla(), new InboxVacio(), new UnidadNula(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcesadorPedidoCreado>.Instance);

        var r = await procesador.ProcesarAsync(Mensaje(Guid.NewGuid()), default);

        Assert.Equal(Liquidacion.Reintentar, r.Accion);
    }

    private sealed class RepositorioQueFalla : IPedidoRepositorio
    {
        public Task<Pedido?> ObtenerAsync(Guid id, CancellationToken ct) => throw new TimeoutException("BD lenta");
        public Task<Pedido?> ObtenerPorClaveIdempotenciaAsync(string clave, CancellationToken ct) => throw new NotImplementedException();
        public Task<(IReadOnlyList<Pedido>, int)> ListarAsync(string? c, int p, int t, CancellationToken ct) => throw new NotImplementedException();
        public void Agregar(Pedido pedido) => throw new NotImplementedException();
    }

    private sealed class InboxVacio : IRegistroMensajesProcesados
    {
        public Task<bool> YaProcesadoAsync(string mensajeId, CancellationToken ct) => Task.FromResult(false);
        public void Registrar(string mensajeId, string consumidor) { }
    }

    private sealed class UnidadNula : IUnidadDeTrabajo
    {
        public Task GuardarAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
