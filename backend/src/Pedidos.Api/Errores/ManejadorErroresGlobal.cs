using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Pedidos.Application;
using Pedidos.Domain;

namespace Pedidos.Api.Errores;

/// <summary>Traduce excepciones a ProblemDetails (RFC 7807) con el código HTTP correcto.</summary>
public class ManejadorErroresGlobal(IProblemDetailsService problemDetails, ILogger<ManejadorErroresGlobal> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        var (status, titulo, detalle) = ex switch
        {
            ReglaDominioException e => (StatusCodes.Status422UnprocessableEntity, "Regla de negocio", e.Message),
            ArgumentException e => (StatusCodes.Status400BadRequest, "Solicitud inválida", e.Message),
            NoEncontradoException e => (StatusCodes.Status404NotFound, "No encontrado", e.Message),
            ConflictoConcurrenciaException e => (StatusCodes.Status409Conflict, "Conflicto", e.Message),
            OperationCanceledException => (499, "Solicitud cancelada", "El cliente canceló la solicitud."),
            _ => (StatusCodes.Status500InternalServerError, "Error interno", "Ocurrió un error inesperado."),
        };

        if (status == 500) log.LogError(ex, "Error no controlado");
        http.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = new ProblemDetails { Status = status, Title = titulo, Detail = detalle, Instance = http.Request.Path },
        });
    }
}
