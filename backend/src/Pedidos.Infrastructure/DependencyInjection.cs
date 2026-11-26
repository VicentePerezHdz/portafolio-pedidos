using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pedidos.Application;
using Pedidos.Infrastructure.Ia;
using Pedidos.Infrastructure.Mensajeria;
using Pedidos.Infrastructure.Persistencia;

namespace Pedidos.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registra la infraestructura según configuración:
    ///  BaseDatos:Proveedor = "Postgres" (producción, EF Core + Npgsql) | "Sqlite" (desarrollo sin Docker)
    ///  Mensajeria:Modo     = "ServiceBus" (Azure, identidad administrada) | "EnMemoria"
    /// </summary>
    public static IServiceCollection AddInfraestructura(this IServiceCollection services, IConfiguration config)
    {
        var proveedor = config["BaseDatos:Proveedor"] ?? "Sqlite";
        var cadena = config.GetConnectionString("Pedidos") ?? "Data Source=pedidos.db";

        services.AddDbContext<PedidosDbContext>(o =>
        {
            if (proveedor.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
                o.UseNpgsql(cadena, npg => npg.EnableRetryOnFailure(maxRetryCount: 5)); // reintentos ante fallas transitorias
            else
                o.UseSqlite(cadena);
        });

        services.AddScoped<IPedidoRepositorio, PedidoRepositorio>();
        services.AddScoped<IBandejaSalida, BandejaSalida>();
        services.AddScoped<IRegistroMensajesProcesados, RegistroMensajesProcesados>();
        services.AddScoped<IUnidadDeTrabajo, UnidadDeTrabajo>();

        services.AddSingleton<IRecuperadorDocumentos, RecuperadorEnMemoria>();
        services.AddSingleton<IModeloLenguaje, ModeloExtractivo>();

        services.Configure<OpcionesMensajeria>(config.GetSection(OpcionesMensajeria.Seccion));
        var mensajeria = config.GetSection(OpcionesMensajeria.Seccion).Get<OpcionesMensajeria>() ?? new();

        if (mensajeria.Modo.Equals("ServiceBus", StringComparison.OrdinalIgnoreCase))
        {
            // Identidad administrada / DefaultAzureCredential: sin cadenas de conexión ni secretos en la config.
            // En local usa tu sesión de "az login"; en Azure usa la managed identity de la app.
            services.AddSingleton(_ => new ServiceBusClient(
                mensajeria.Namespace ?? throw new InvalidOperationException("Falta Mensajeria:Namespace"),
                new DefaultAzureCredential()));
            services.AddSingleton<IPublicadorMensajes, PublicadorServiceBus>();
            services.AddHostedService<ConsumidorServiceBusWorker>();
        }
        else
        {
            services.AddSingleton<BusEnMemoria>();
            services.AddSingleton<IPublicadorMensajes>(sp => sp.GetRequiredService<BusEnMemoria>());
            services.AddSingleton<ConsumidorBusEnMemoriaWorker>();
            services.AddHostedService(sp => sp.GetRequiredService<ConsumidorBusEnMemoriaWorker>());
        }

        services.AddSingleton<PublicadorOutboxWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<PublicadorOutboxWorker>());
        return services;
    }
}
