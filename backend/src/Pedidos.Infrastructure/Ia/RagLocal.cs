using System.Globalization;
using System.Text;
using Pedidos.Application;

namespace Pedidos.Infrastructure.Ia;

/// <summary>
/// Recuperador local por palabras clave (sustituto de Azure AI Search para correr sin nube).
/// Los documentos se "ingieren" partiéndolos en fragmentos (chunking) con roles permitidos.
/// En producción: embeddings + búsqueda híbrida (vector + BM25) + filtro de seguridad por rol.
/// </summary>
public class RecuperadorEnMemoria : IRecuperadorDocumentos
{
    private static readonly HashSet<string> Vacias = ["el", "la", "los", "las", "de", "del", "un", "una", "y", "o", "que", "en", "por", "para", "con", "se", "es", "a", "al", "como", "cual", "cuál", "qué"];

    private readonly List<Fragmento> _fragmentos =
    [
        new("pol-001", "Política de cancelación", "Un pedido solo puede cancelarse mientras su estado sea Creado. Un pedido Confirmado no se puede cancelar.", ["lector", "operador"]),
        new("pol-002", "Confirmación de pedidos", "Los pedidos se confirman de forma asíncrona cuando el consumidor procesa el evento PedidoCreado desde Service Bus.", ["lector", "operador"]),
        new("ops-001", "Dead-letter queue", "Los mensajes inválidos o que superan el máximo de entregas se mueven a la dead-letter queue y se revisan desde el runbook de operaciones.", ["operador"]),
        new("dat-001", "Capa analítica", "Los pedidos se replican a Delta Lake en Databricks mediante MERGE incremental para reportes.", ["lector", "operador"]),
    ];

    public Task<IReadOnlyList<FragmentoRecuperado>> BuscarAsync(string consulta, IReadOnlyCollection<string> rolesUsuario, int top, CancellationToken ct)
    {
        var terminos = Tokenizar(consulta);
        IReadOnlyList<FragmentoRecuperado> r = _fragmentos
            .Where(f => f.RolesPermitidos.Intersect(rolesUsuario, StringComparer.OrdinalIgnoreCase).Any()) // control de acceso
            .Select(f =>
            {
                var palabras = Tokenizar(f.Titulo + " " + f.Texto);
                var coincidencias = terminos.Count(t => palabras.Contains(t));
                return new FragmentoRecuperado(f, terminos.Count == 0 ? 0 : (double)coincidencias / terminos.Count);
            })
            .Where(x => x.Puntaje > 0)
            .OrderByDescending(x => x.Puntaje).Take(top).ToList();
        return Task.FromResult(r);
    }

    public static HashSet<string> Tokenizar(string texto)
    {
        var sinAcentos = new string(texto.ToLowerInvariant().Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return sinAcentos.Split([' ', ',', '.', '?', '¿', '!', '¡', ':', ';', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length > 2 && !Vacias.Contains(p)).ToHashSet();
    }
}

/// <summary>
/// "Modelo" extractivo determinista para desarrollo y pruebas: devuelve el primer fragmento citado.
/// Se reemplaza por Azure OpenAI / Foundry o Claude implementando IModeloLenguaje; el resto del flujo no cambia.
/// </summary>
public class ModeloExtractivo : IModeloLenguaje
{
    public Task<string> GenerarAsync(string instrucciones, string contexto, string pregunta, CancellationToken ct)
    {
        var primera = contexto.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (primera is null) return Task.FromResult("No tengo información suficiente.");
        var cierre = primera.IndexOf(')');
        var texto = cierre > 0 ? primera[(cierre + 1)..].Trim() : primera;
        return Task.FromResult($"{texto} [1]");
    }
}
