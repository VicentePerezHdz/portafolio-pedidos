using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pedidos.Api.Seguridad;
using Pedidos.Application;

namespace Pedidos.Api.Controllers;

/// <summary>Asistente RAG: responde preguntas sobre las políticas usando solo documentos que el rol del usuario puede ver.</summary>
[ApiController]
[Route("api/asistente")]
[Authorize(Policy = Politicas.Leer)]
public class AsistenteController(AsistenteService asistente) : ControllerBase
{
    [HttpPost("preguntar")]
    public Task<RespuestaAsistente> Preguntar(PreguntaRequest req, CancellationToken ct)
    {
        var roles = User.FindAll("roles").Select(c => c.Value).ToArray();
        return asistente.PreguntarAsync(req.Pregunta, roles, ct);
    }
}
