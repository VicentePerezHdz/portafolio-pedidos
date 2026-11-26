using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Pedidos.Api.Seguridad;
using Pedidos.Application;

namespace Pedidos.Tests;

/// <summary>
/// Pruebas de integración: la API completa en memoria (autenticación JWT real, EF Core con SQLite,
/// outbox worker y consumidor en memoria corriendo como en producción).
/// </summary>
public class ApiTests : IClassFixture<ApiTests.Fabrica>
{
    public class Fabrica : WebApplicationFactory<Program>
    {
        public const string Clave = "clave-de-pruebas-de-al-menos-32-caracteres!!";
        private readonly string _archivo = Path.Combine(Path.GetTempPath(), $"pedidos-test-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Pedidos", $"Data Source={_archivo}");
            builder.UseSetting("Auth:ClaveDesarrollo", Clave);
        }
    }

    private readonly Fabrica _fabrica;
    public ApiTests(Fabrica fabrica) => _fabrica = fabrica;

    private HttpClient Cliente(params string[] roles)
    {
        var c = _fabrica.CreateClient();
        if (roles.Length > 0)
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                Seguridad.CrearTokenDesarrollo(Fabrica.Clave, "api://pedidos", "usuario-prueba", roles));
        return c;
    }

    [Fact]
    public async Task Sin_token_responde_401()
    {
        var r = await Cliente().GetAsync("/api/pedidos");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Lector_no_puede_crear_403()
    {
        var r = await Cliente("lector").PostAsJsonAsync("/api/pedidos", new CrearPedidoRequest("cli-1", 10));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task Health_es_anonimo()
    {
        var r = await Cliente().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Validacion_responde_400_con_problem_details()
    {
        var r = await Cliente("operador").PostAsJsonAsync("/api/pedidos", new { clienteId = "", total = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("application/problem+json", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Crear_con_idempotency_key_dos_veces_devuelve_201_y_luego_200_con_el_mismo_pedido()
    {
        var c = Cliente("operador");
        var clave = Guid.NewGuid().ToString();
        HttpRequestMessage Req() => new(HttpMethod.Post, "/api/pedidos")
        {
            Content = JsonContent.Create(new CrearPedidoRequest("cli-idem", 50)),
            Headers = { { "Idempotency-Key", clave } },
        };

        var r1 = await c.SendAsync(Req());
        var r2 = await c.SendAsync(Req());

        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal((await r1.Content.ReadFromJsonAsync<PedidoDto>())!.Id, (await r2.Content.ReadFromJsonAsync<PedidoDto>())!.Id);
    }

    [Fact]
    public async Task El_pedido_se_confirma_de_forma_asincrona_consistencia_eventual()
    {
        var c = Cliente("operador");
        var creado = await (await c.PostAsJsonAsync("/api/pedidos", new CrearPedidoRequest("cli-eventual", 75))).Content.ReadFromJsonAsync<PedidoDto>();
        Assert.Equal("Creado", creado!.Estado);

        // outbox worker (cada 2 s) -> bus en memoria -> consumidor -> Confirmado
        PedidoDto? actual = null;
        for (var i = 0; i < 40 && actual?.Estado != "Confirmado"; i++)
        {
            await Task.Delay(250);
            actual = await c.GetFromJsonAsync<PedidoDto>($"/api/pedidos/{creado.Id}");
        }
        Assert.Equal("Confirmado", actual!.Estado);
    }

    [Fact]
    public async Task Cancelar_sin_if_match_428_y_con_version_vieja_409()
    {
        var c = Cliente("operador");
        var p = await (await c.PostAsJsonAsync("/api/pedidos", new CrearPedidoRequest("cli-conc", 20))).Content.ReadFromJsonAsync<PedidoDto>();

        var sinIfMatch = await c.PostAsync($"/api/pedidos/{p!.Id}/cancelar", null);
        Assert.Equal((HttpStatusCode)428, sinIfMatch.StatusCode);

        var vieja = new HttpRequestMessage(HttpMethod.Post, $"/api/pedidos/{p.Id}/cancelar") { Headers = { { "If-Match", "\"999\"" } } };
        Assert.Equal(HttpStatusCode.Conflict, (await c.SendAsync(vieja)).StatusCode);
    }

    [Fact]
    public async Task Pedido_inexistente_404()
    {
        var r = await Cliente("lector").GetAsync($"/api/pedidos/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}
