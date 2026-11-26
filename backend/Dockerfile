# Imagen de la API en dos etapas: se compila con el SDK y se ejecuta con la imagen ligera de ASP.NET.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
# Primero solo los .csproj: si no cambian, Docker reutiliza la capa del restore (builds más rápidos).
COPY src/Pedidos.Domain/Pedidos.Domain.csproj src/Pedidos.Domain/
COPY src/Pedidos.Application/Pedidos.Application.csproj src/Pedidos.Application/
COPY src/Pedidos.Infrastructure/Pedidos.Infrastructure.csproj src/Pedidos.Infrastructure/
COPY src/Pedidos.Api/Pedidos.Api.csproj src/Pedidos.Api/
RUN dotnet restore src/Pedidos.Api/Pedidos.Api.csproj
COPY src/ src/
RUN dotnet publish src/Pedidos.Api/Pedidos.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app .
# Usuario sin privilegios (incluido en la imagen oficial desde .NET 8).
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Pedidos.Api.dll"]
