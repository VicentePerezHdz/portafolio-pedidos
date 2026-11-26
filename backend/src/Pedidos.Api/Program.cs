using Microsoft.EntityFrameworkCore;
using Pedidos.Api.Seguridad;
using Pedidos.Api.Errores;
using Pedidos.Application;
using Pedidos.Infrastructure;
using Pedidos.Infrastructure.Persistencia;

var builder = WebApplication.CreateBuilder(args);

// ===== Servicios =====
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ManejadorErroresGlobal>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddAplicacion();
builder.Services.AddInfraestructura(builder.Configuration);

// OAuth 2.0 / OpenID Connect: la API valida tokens JWT (Bearer) emitidos por Entra ID (o por la clave de desarrollo).
builder.Services.AddSeguridad(builder.Configuration, builder.Environment);

builder.Services.AddHealthChecks().AddDbContextCheck<PedidosDbContext>("base-datos");

builder.Services.AddCors(o => o.AddPolicy("front", p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origenes").Get<string[]>() ?? [])
    .AllowAnyHeader().AllowAnyMethod()
    .WithExposedHeaders("ETag", "Location")));

var app = builder.Build();

// En desarrollo con SQLite se crea la BD al arrancar. Con PostgreSQL se usan migraciones (ver documento).
if (app.Configuration["BaseDatos:CrearAlIniciar"] == "true")
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<PedidosDbContext>().Database.EnsureCreated();
}

// ===== Pipeline =====
app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseCors("front");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();
if (app.Environment.IsDevelopment()) app.MapTokenDesarrollo(); // POST /dev/token, solo en desarrollo

app.Run();

public partial class Program;
