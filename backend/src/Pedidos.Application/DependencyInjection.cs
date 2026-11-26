using Microsoft.Extensions.DependencyInjection;

namespace Pedidos.Application;

public static class DependencyInjection
{
    /// <summary>Registra los casos de uso. Scoped: una instancia por petición HTTP o por mensaje.</summary>
    public static IServiceCollection AddAplicacion(this IServiceCollection services)
    {
        services.AddScoped<PedidoService>();
        services.AddScoped<ProcesadorPedidoCreado>();
        services.AddScoped<AsistenteService>();
        return services;
    }
}
