using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pedidos.Api.Seguridad;
using Pedidos.Application;

namespace Pedidos.Api.Controllers;

[ApiController]
[Route("api/pedidos")]
[Authorize(Policy = Politicas.Leer)]
public class PedidosController(PedidoService servicio) : ControllerBase
{
    /// <summary>GET /api/pedidos?clienteId=&amp;pagina=1&amp;tamano=20 (paginado).</summary>
    [HttpGet]
    public Task<Pagina<PedidoDto>> Listar([FromQuery] string? clienteId, [FromQuery] int pagina = 1, [FromQuery] int tamano = 20,
        CancellationToken ct = default) => servicio.ListarAsync(clienteId, pagina, tamano, ct);

    /// <summary>GET /api/pedidos/{id}. Devuelve ETag con la versión para concurrencia optimista.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PedidoDto>> Obtener(Guid id, CancellationToken ct)
    {
        var p = await servicio.ObtenerAsync(id, ct);
        Response.Headers.ETag = $"\"{p.Version}\"";
        return p;
    }

    /// <summary>
    /// POST /api/pedidos con cabecera opcional Idempotency-Key.
    /// 201 si lo creó; 200 con el mismo pedido si la clave ya se había usado (reintento seguro del cliente).
    /// </summary>
    [HttpPost]
    [Authorize(Policy = Politicas.Escribir)]
    public async Task<ActionResult<PedidoDto>> Crear(CrearPedidoRequest req,
        [FromHeader(Name = "Idempotency-Key")] string? claveIdempotencia, CancellationToken ct)
    {
        if (claveIdempotencia is { Length: > 100 }) return BadRequest("Idempotency-Key demasiado larga.");
        var (pedido, creado) = await servicio.CrearAsync(req, claveIdempotencia, ct);
        Response.Headers.ETag = $"\"{pedido.Version}\"";
        return creado ? CreatedAtAction(nameof(Obtener), new { id = pedido.Id }, pedido) : Ok(pedido);
    }

    /// <summary>POST /api/pedidos/{id}/cancelar con If-Match: "versión". 409 si alguien lo cambió antes; 428 si falta If-Match.</summary>
    [HttpPost("{id:guid}/cancelar")]
    [Authorize(Policy = Politicas.Escribir)]
    public async Task<ActionResult<PedidoDto>> Cancelar(Guid id, [FromHeader(Name = "If-Match")] string? ifMatch, CancellationToken ct)
    {
        if (!int.TryParse(ifMatch?.Trim('"', ' '), out var version))
            return Problem(statusCode: StatusCodes.Status428PreconditionRequired, title: "Falta If-Match",
                detail: "Envía la cabecera If-Match con la versión (ETag) que consultaste.");
        var p = await servicio.CancelarAsync(id, version, ct);
        Response.Headers.ETag = $"\"{p.Version}\"";
        return p;
    }
}
