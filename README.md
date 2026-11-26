# Portafolio Pedidos · .NET 8 + React + Databricks

Sistema de pedidos completo, de la API a la capa de datos:

1. **API .NET 8** con Clean Architecture (`Domain`, `Application`, `Infrastructure`, `Api`) que guarda en PostgreSQL con EF Core y Npgsql.
2. **Outbox transaccional** y publicación en **Azure Service Bus** (o un bus en memoria para desarrollo). Incluye consumidor con reintentos, dead-letter, idempotencia y concurrencia optimista.
3. **Microfrontends con React**: un `shell` con login por rol y el remoto `mfe-pedidos` (Module Federation).
4. **Datos con PySpark y Delta Lake**: bronze → silver → gold, cuarentena de registros inválidos y notebooks de Databricks.
5. **Asistente RAG** con control de acceso por rol y citas.

## Cómo correrlo
```powershell
# API: http://localhost:5090/swagger
cd backend
dotnet test                                                  # 32 pruebas xUnit
dotnet run --project src\Pedidos.Api --launch-profile http

# Microfrontend remoto (puerto 5002) y shell (http://localhost:5001)
cd frontend\mfe-pedidos; npm install; npm test; npm run preview
cd frontend\shell;       npm install; npm run build; npx vite preview

# Datos (requiere JDK 17)
cd data
python -m venv .venv; .venv\Scripts\pip install -r requirements.txt
.venv\Scripts\python -m pytest -v                            # 9 pruebas con Spark local
```

También se puede levantar con `docker-compose up` (PostgreSQL y API). El CI está en `.github/workflows/ci.yml`.

La explicación completa (arquitectura, dónde está cada tecnología, decisiones de diseño) está en [`docs/Documento_Transicion.md`](docs/Documento_Transicion.md).
