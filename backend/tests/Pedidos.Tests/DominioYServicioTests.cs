using Microsoft.EntityFrameworkCore;
using Pedidos.Application;
using Pedidos.Domain;
using Pedidos.Infrastructure.Persistencia;

namespace Pedidos.Tests;

public class DominioTests
{
    private static readonly DateTime Ahora = new(2026, 11, 23, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("", 10)]
    [InlineData("cli-1", 0)]
    [InlineData("cli-1", -5)]
    public void Crear_con_datos_invalidos_lanza_regla_de_dominio(string cliente, decimal total) =>
        Assert.Throws<ReglaDominioException>(() => Pedido.Crear(cliente, total, Ahora));

    [Fact]
    public void Confirmar_dos_veces_es_idempotente()
    {
        var p = Pedido.Crear("cli-1", 100, Ahora);
        Assert.True(p.Confirmar());
        Assert.False(p.Confirmar());
        Assert.Equal(2, p.Version);
    }

    [Fact]
    public void No_se_puede_cancelar_un_pedido_confirmado()
    {
        var p = Pedido.Crear("cli-1", 100, Ahora);
        p.Confirmar();
        Assert.Throws<ReglaDominioException>(p.Cancelar);
    }
}

public class PedidoServiceTests
{
    [Fact]
    public async Task Crear_guarda_pedido_y_evento_del_outbox_en_la_misma_transaccion()
    {
        using var bd = new BdPrueba();
        await using var db = bd.NuevoContexto();

        var (pedido, creado) = await bd.Servicio(db).CrearAsync(new("cli-1", 150.555m), null, default);

        Assert.True(creado);
        Assert.Equal(150.56m, pedido.Total);
        await using var otra = bd.NuevoContexto();
        var outbox = await otra.Outbox.SingleAsync();
        Assert.Equal(PedidoCreadoV1.Tipo, outbox.Tipo);
        Assert.Equal(pedido.Id.ToString(), outbox.ClaveParticion);
        Assert.Null(outbox.EnviadoUtc);
    }

    [Fact]
    public async Task Misma_clave_de_idempotencia_no_duplica_el_pedido()
    {
        using var bd = new BdPrueba();
        await using var db1 = bd.NuevoContexto();
        await using var db2 = bd.NuevoContexto();

        var (primero, creado1) = await bd.Servicio(db1).CrearAsync(new("cli-1", 10), "clave-abc", default);
        var (segundo, creado2) = await bd.Servicio(db2).CrearAsync(new("cli-1", 10), "clave-abc", default);

        Assert.True(creado1);
        Assert.False(creado2);
        Assert.Equal(primero.Id, segundo.Id);
        await using var db3 = bd.NuevoContexto();
        Assert.Equal(1, await db3.Pedidos.CountAsync());
        Assert.Equal(1, await db3.Outbox.CountAsync());
    }

    [Fact]
    public async Task Cancelar_con_version_vieja_responde_conflicto()
    {
        using var bd = new BdPrueba();
        await using var db = bd.NuevoContexto();
        var (p, _) = await bd.Servicio(db).CrearAsync(new("cli-1", 10), null, default);

        await Assert.ThrowsAsync<ConflictoConcurrenciaException>(() => bd.Servicio(db).CancelarAsync(p.Id, versionEsperada: 99, default));
    }

    [Fact]
    public async Task Dos_procesos_que_modifican_a_la_vez_el_segundo_falla_por_concurrencia_optimista()
    {
        using var bd = new BdPrueba();
        Guid id;
        await using (var db = bd.NuevoContexto())
            id = (await bd.Servicio(db).CrearAsync(new("cli-1", 10), null, default)).Pedido.Id;

        await using var a = bd.NuevoContexto();
        await using var b = bd.NuevoContexto();
        var pa = await a.Pedidos.SingleAsync(p => p.Id == id); // ambos leen versión 1
        var pb = await b.Pedidos.SingleAsync(p => p.Id == id);

        pa.Confirmar();
        await new UnidadDeTrabajo(a).GuardarAsync(default); // gana A: versión 2

        pb.Cancelar();
        await Assert.ThrowsAsync<ConflictoConcurrenciaException>(() => new UnidadDeTrabajo(b).GuardarAsync(default));
    }

    [Fact]
    public async Task Listar_pagina_y_filtra_por_cliente()
    {
        using var bd = new BdPrueba();
        await using var db = bd.NuevoContexto();
        var s = bd.Servicio(db);
        for (var i = 0; i < 5; i++) await s.CrearAsync(new("cli-A", 10 + i), null, default);
        await s.CrearAsync(new("cli-B", 99), null, default);

        var pagina = await s.ListarAsync("cli-A", pagina: 2, tamano: 2, default);

        Assert.Equal(5, pagina.Total);
        Assert.Equal(2, pagina.Elementos.Count);
        Assert.All(pagina.Elementos, p => Assert.Equal("cli-A", p.ClienteId));
    }
}
