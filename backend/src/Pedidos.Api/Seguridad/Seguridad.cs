using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Pedidos.Api.Seguridad;

public static class Politicas
{
    public const string Leer = "Pedidos.Leer";
    public const string Escribir = "Pedidos.Escribir";
}

public static class Seguridad
{
    public const string EmisorDesarrollo = "pedidos-dev";

    /// <summary>
    /// Producción: Auth:Authority = https://login.microsoftonline.com/{tenant}/v2.0 y Auth:Audience = api://pedidos.
    ///   La API descarga las llaves públicas del emisor (OIDC discovery) y valida firma, emisor, audiencia y expiración.
    /// Desarrollo: Auth:ClaveDesarrollo (≥ 32 caracteres) firma tokens locales; se guarda con "dotnet user-secrets",
    ///   nunca en el repositorio. En Azure, los secretos van en Key Vault leído con identidad administrada.
    /// Autorización por roles de aplicación (claim "roles" de Entra ID): lector lee, operador escribe.
    /// </summary>
    public static IServiceCollection AddSeguridad(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            var authority = config["Auth:Authority"];
            var clave = config["Auth:ClaveDesarrollo"];
            o.MapInboundClaims = false; // conserva los nombres originales: "roles", "sub"

            if (!string.IsNullOrEmpty(authority))
            {
                o.Authority = authority;
                o.Audience = config["Auth:Audience"];
                o.TokenValidationParameters.RoleClaimType = "roles";
            }
            else if (!string.IsNullOrEmpty(clave) && !env.IsProduction())
            {
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = EmisorDesarrollo,
                    ValidAudience = config["Auth:Audience"] ?? "api://pedidos",
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(clave)),
                    RoleClaimType = "roles",
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            }
            else
            {
                throw new InvalidOperationException("Configura Auth:Authority (Entra ID) o Auth:ClaveDesarrollo (solo fuera de producción).");
            }
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(Politicas.Leer, p => p.RequireRole("lector", "operador"))
            .AddPolicy(Politicas.Escribir, p => p.RequireRole("operador"))
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().Build()); // todo endpoint exige token salvo [AllowAnonymous]
        return services;
    }

    /// <summary>Emite un token de desarrollo firmado con la clave local. También lo usan las pruebas.</summary>
    public static string CrearTokenDesarrollo(string clave, string audiencia, string usuario, IEnumerable<string> roles, TimeSpan? vigencia = null)
    {
        var claims = new List<Claim> { new("sub", usuario) };
        claims.AddRange(roles.Select(r => new Claim("roles", r)));
        var token = new JwtSecurityToken(
            issuer: EmisorDesarrollo, audience: audiencia, claims: claims,
            expires: DateTime.UtcNow.Add(vigencia ?? TimeSpan.FromHours(1)),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(clave)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public record SolicitudToken(string Usuario, string[] Roles);

    public static void MapTokenDesarrollo(this WebApplication app) =>
        app.MapPost("/dev/token", (SolicitudToken s, IConfiguration config) =>
        {
            var clave = config["Auth:ClaveDesarrollo"];
            if (string.IsNullOrEmpty(clave)) return Results.NotFound();
            var roles = s.Roles.Where(r => r is "lector" or "operador").ToArray();
            return Results.Ok(new { access_token = CrearTokenDesarrollo(clave, config["Auth:Audience"] ?? "api://pedidos", s.Usuario, roles) });
        }).AllowAnonymous().ExcludeFromDescription();
}
