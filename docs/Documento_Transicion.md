# Documento de transición: Portafolio "Pedidos" (C#/.NET + React + Databricks)

> Manual del proyecto: qué se construyó, dónde se aplica cada tecnología, cómo correrlo y cómo explicar cada decisión técnica.
> Fecha: 2026-11-23 · Ruta: `C:\HolaMundo\03-Portafolio-Pedidos`

## 1. Qué es

Un sistema pequeño pero completo de **pedidos**:

1. Una **API .NET 8** con Clean Architecture crea pedidos en **PostgreSQL** (EF Core + Npgsql).
2. En la misma transacción guarda un evento en un **outbox**. Un `BackgroundService` lo publica en **Azure Service Bus**.
3. Un **consumidor** lo recibe y confirma el pedido. Maneja liquidación de mensajes, reintentos, dead-letter y entregas duplicadas (**consistencia eventual**).
4. Un **shell React** con **Module Federation** carga el micro frontend remoto de **Pedidos**, desplegado por separado.
5. Un **notebook de Databricks** (PySpark) lleva los eventos a **Delta Lake** con Auto Loader, reglas de calidad y **MERGE** incremental.
6. Un **asistente RAG** responde preguntas sobre las políticas, con citas, control de acceso por rol y guardrails.
7. **CI** con GitHub Actions, Dockerfile y docker-compose.

```
 ┌───────────── Navegador ─────────────┐
 │ shell (5001)  ──Module Federation──► mfe-pedidos (5002, remoteEntry.js)
 │   AuthProvider: token (dev / MSAL)  │      PedidosApp: lista, alta (Idempotency-Key), cancelar (If-Match)
 └───────────────┬─────────────────────┘
                 │ HTTPS + Bearer JWT
 ┌───────────────▼──────────── Pedidos.Api (.NET 8, 5090) ─────────────────────────────┐
 │ JwtBearer (Entra ID / clave dev) → políticas Pedidos.Leer / Pedidos.Escribir          │
 │ PedidosController ─► PedidoService ─► IPedidoRepositorio, IBandejaSalida, IUnidadDeTrabajo │
 │ AsistenteController ─► AsistenteService ─► IRecuperadorDocumentos + IModeloLenguaje     │
 │ ManejadorErroresGlobal → ProblemDetails (400/404/409/422/428/499/500)                  │
 └───────────────┬───────────────────────────────────────────────────────────────────────┘
                 │ SaveChanges (1 transacción): pedidos + outbox
         ┌───────▼────────┐   PublicadorOutboxWorker    ┌──────────────────────────┐
         │ PostgreSQL     │ ──────────────────────────► │ Azure Service Bus        │
         │ pedidos        │                             │ cola "pedidos" (sesiones,│
         │ outbox         │ ◄── confirma + inbox ────── │ duplicate detection, DLQ)│
         │ mensajes_proc. │   ConsumidorServiceBusWorker└──────────────────────────┘
         └───────┬────────┘
                 │ exportación de eventos (JSON al lago)
 ┌───────────────▼──────────── Databricks ─────────────────────────────────────────────┐
 │ bronze (JSON) ─Auto Loader─► foreachBatch: limpiar → validar → deduplicar → MERGE    │
 │                                ├─► pedidos_silver (Delta, CDF, CHECK, CLUSTER BY)      │
 │                                └─► pedidos_rechazados (cuarentena)  → gold (agregados) │
 └───────────────────────────────────────────────────────────────────────────────────────┘
```

## 2. Tecnologías y prácticas → dónde está cada una

| Tecnología o práctica | Dónde verlo |
|---|---|
| C#, ASP.NET Core, REST | `backend/src/Pedidos.Api/Controllers/*` (GET paginado, POST 201/200, códigos correctos) |
| Inyección de dependencias | `DependencyInjection.cs` de Application e Infrastructure; constructores primarios |
| async/await y cancelación | `CancellationToken` en todos los métodos; 499 en `ManejadorErroresGlobal` |
| Background services | `PublicadorOutboxWorker`, `ConsumidorServiceBusWorker`, `ConsumidorBusEnMemoriaWorker` |
| Validación | DataAnnotations en `CrearPedidoRequest` + reglas del dominio en `Pedido.Crear` |
| Manejo consistente de errores | `Api/Errores/ManejadorErroresGlobal.cs` (ProblemDetails) |
| React, TypeScript, hooks, routing, estado | `frontend/mfe-pedidos/src/PedidosApp.tsx`, `frontend/shell/src/App.tsx`, `auth.tsx` (Context) |
| HTML, CSS, accesibilidad | tablas con `caption`/`scope`, `label`, `role="alert"`, `:focus-visible` |
| Micro frontends: Module Federation | `frontend/*/vite.config.ts` (`exposes` / `remotes`) |
| Dependencias compartidas | `shared: ['react','react-dom','react-router-dom']` |
| Routing entre shell y remoto | shell `/pedidos/*`; el remoto usa rutas relativas (`index`, `nuevo`, `..`) |
| Integración de autenticación | el shell es dueño del login; el remoto recibe `obtenerToken()` por props |
| Aislamiento de CSS | `PedidosApp.module.css` (CSS Modules); el shell solo usa selectores `.shell …` |
| Despliegues independientes | URL del remoto por variable `VITE_MFE_PEDIDOS_URL`; matriz de CI por app; `ErrorBoundary` si el remoto falla |
| Service Bus: settlement | `Infrastructure/Mensajeria/ServiceBus.cs` (`Complete` / `Abandon` / `DeadLetter`, `AutoCompleteMessages=false`) |
| Message locks | `MaxAutoLockRenewalDuration`; sesiones con `MaxConcurrentCallsPerSession = 1` |
| Retries y DLQ | `Liquidacion.Reintentar` → abandon → `MaxDeliveryCount` → DLQ; mensaje venenoso → DLQ directo |
| Entrega duplicada | `MessageId` = Id del outbox (duplicate detection) + inbox `mensajes_procesados` + `Pedido.Confirmar()` idempotente |
| Orden | `SessionId = PedidoId` (FIFO por pedido); el outbox se publica en orden y se detiene ante una falla |
| Consistencia eventual | El pedido nace `Creado` y pasa a `Confirmado`; prueba `El_pedido_se_confirma_de_forma_asincrona…` |
| Límites de servicio / recuperación | eventos versionados (`PedidoCreadoV1`), outbox con `Intentos`/`UltimoError`, health check `/health` |
| PostgreSQL, EF Core, Npgsql | `PedidosDbContext`, `UseNpgsql(... EnableRetryOnFailure)`, migración `Persistencia/Migraciones/*_Inicial.cs` |
| Transacciones | un `SaveChanges` = pedido + outbox (o pedido + inbox) atómicos |
| Índices | `(ClienteId, CreadoUtc)`, `ClaveIdempotencia` único, `(EnviadoUtc, CreadoUtc)` en outbox |
| Migraciones | `dotnet ef migrations add / database update` (herramienta local en `dotnet-tools.json`) |
| Concurrencia | `Version` como `IsConcurrencyToken` + ETag/If-Match → 409; prueba con dos `DbContext` |
| OAuth 2.0 / OpenID Connect | `Api/Seguridad/Seguridad.cs` (Authority de Entra ID, validación de JWT) |
| Autorización | políticas por rol (`lector`, `operador`) + política de respaldo que exige autenticación |
| Identidades administradas | `ServiceBusClient(namespace, new DefaultAzureCredential())`, sin cadenas de conexión |
| Manejo de secretos | sin secretos en `appsettings.json`; clave de desarrollo solo fuera de producción (user-secrets / Key Vault) |
| OLTP vs analítico | PostgreSQL (transaccional, renglón a renglón) vs Delta Lake (columnar, lotes, historia) |
| Databricks: notebooks, SQL, jobs | `data/notebooks/01_…py` (widgets, `availableNow`), `02_consultas_delta.sql` |
| PySpark | `data/pedidos_lake/transformaciones.py` |
| Delta: esquema, enforcement y evolution | `ESQUEMA_BRONZE`, `CHECK`, `schemaEvolutionMode=rescue`, `ALTER TABLE ADD COLUMN` |
| MERGE / upserts | `merge_en_silver` (solo actualiza si el evento es más nuevo) + versión SQL |
| Procesamiento incremental | Auto Loader + checkpoint; Change Data Feed para gold |
| Calidad de datos | `motivo_rechazo()` → tabla de cuarentena |
| Rendimiento | `CLUSTER BY`, `OPTIMIZE`, `DESCRIBE DETAIL`, `EXPLAIN FORMATTED`, una sola ventana para deduplicar |
| Contratos de API y datos | DTO + evento versionado; esquema explícito en bronze/silver |
| Pruebas | xUnit (32), React Testing Library + Vitest (5), pytest + Spark (9) |
| Git, Docker, CI/CD | `.gitignore`, `backend/Dockerfile`, `docker-compose.yml`, `.github/workflows/ci.yml` |
| IA: RAG, guardrails, control de acceso | `Application/Asistente.cs`, `Infrastructure/Ia/RagLocal.cs` |
| Tokens, latencia, costo | `TokensEstimados`; top-k = 3 y puntaje mínimo para limitar el contexto |
| Asistentes de código con IA | este mismo proyecto: generado con IA, compilado, probado y revisado; los hallazgos están en la sección 9 |

## 3. Cómo correrlo

```powershell
# API (SQLite + bus en memoria; no necesita Docker ni Azure)
cd C:\HolaMundo\03-Portafolio-Pedidos\backend
dotnet test                                                   # 32 pruebas
dotnet run --project src\Pedidos.Api --launch-profile http    # http://localhost:5090/swagger

# Micro frontend remoto (terminal 2) y shell (terminal 3)
cd ..\frontend\mfe-pedidos; npm install; npm test; npm run preview   # build + preview en 5002 (expone remoteEntry.js)
cd ..\shell;               npm install; npm run build; npx vite preview   # http://localhost:5001

# Datos (terminal 4)
cd ..\..\data
$env:JAVA_HOME = "C:\Program Files\Java\jdk-17"
.venv\Scripts\python -m pytest -v                             # 9 pruebas con Spark local
```

En el shell: entra como `operador` (o `lector`), ve a **Pedidos**, crea uno y presiona **Actualizar** a los 2–3 segundos. Lo verás pasar de *Creado* a *Confirmado*: eso es la consistencia eventual.

Con Swagger: `POST /dev/token` (`{"usuario":"yo","roles":["operador"]}`) → botón **Authorize** → `Bearer <token>`.

**Importante sobre Module Federation con Vite**: el remoto debe estar **construido** (`vite build` + `vite preview`). `npm run dev` del remoto no genera `remoteEntry.js`. El remoto también corre solo con `npm run dev` para desarrollarlo sin el shell.

## 4. Recorridos clave

### 4.1 Crear un pedido (POST con Idempotency-Key)
1. El remoto genera la clave **una vez** por formulario (`crypto.randomUUID()`) y la conserva si hay que reintentar.
2. La API valida el JWT y la política `Pedidos.Escribir` (rol `operador`).
3. `PedidoService.CrearAsync`: si la clave ya existe, devuelve ese pedido (**200**). Si no, `Pedido.Crear` valida las reglas; se agregan el pedido y la fila del outbox; un solo `SaveChanges` (**201** + `Location` + `ETag`).
4. Si dos peticiones con la misma clave llegan a la vez, el índice único hace fallar una; `UnidadDeTrabajo` lo traduce a 409.

### 4.2 Del outbox al consumidor
1. `PublicadorOutboxWorker` lee cada 2 s los no enviados, en orden, y los publica con `MessageId` = Id del outbox y `SessionId` = PedidoId.
2. Si se cae entre "publicar" y "marcar enviado", el reenvío lo descarta la detección de duplicados de Service Bus o, en último caso, el inbox.
3. `ConsumidorServiceBusWorker` recibe el mensaje y llama `ProcesadorPedidoCreado`:
   - tipo desconocido o JSON inválido → **DeadLetter** (no tiene caso reintentar);
   - ya procesado (inbox) → **Completar** sin hacer nada;
   - pedido cancelado → regla de negocio → **DeadLetter**;
   - BD lenta o conflicto de concurrencia → **Reintentar** (Abandon). Al pasar `MaxDeliveryCount`, Service Bus lo manda a la DLQ;
   - OK → `Confirmar()` + registro en el inbox, en una transacción → **Completar**.

### 4.3 Cancelar con concurrencia optimista
`GET` devuelve `ETag: "1"`. El remoto envía `If-Match: "1"`. Si mientras tanto el consumidor confirmó el pedido (versión 2), la API responde **409** y la pantalla lo muestra. Sin `If-Match` responde **428**. Además, EF Core agrega `WHERE version = @original` al UPDATE como segunda barrera.

### 4.4 Asistente RAG
`POST /api/asistente/preguntar` → guardrail de entrada (longitud, patrones de *prompt injection*) → recuperación filtrada por rol (un `lector` no ve el runbook de operaciones) → si nada supera el puntaje mínimo: "No tengo información suficiente" → modelo con instrucciones de usar solo el contexto y citar `[n]` → guardrail de salida (si cita fragmentos inexistentes, la respuesta se descarta). Para producción se cambian `RecuperadorEnMemoria` por Azure AI Search (híbrida) y `ModeloExtractivo` por Azure OpenAI / Foundry; el resto no cambia.

## 5. Inventario

### Backend
| Proyecto / archivo | Contenido |
|---|---|
| `Pedidos.Domain/Pedido.cs` | `Pedido` (Crear, Confirmar, Cancelar, Version), `EstadoPedido`, `ReglaDominioException` |
| `Pedidos.Application/Contratos.cs` | `CrearPedidoRequest`, `PedidoDto`, `Pagina<T>`, `PedidoCreadoV1`, excepciones |
| `Pedidos.Application/Interfaces.cs` | `IPedidoRepositorio`, `IBandejaSalida`, `IUnidadDeTrabajo`, `IRegistroMensajesProcesados`, `IPublicadorMensajes` |
| `Pedidos.Application/PedidoService.cs` | `CrearAsync`, `ObtenerAsync`, `ListarAsync`, `CancelarAsync` |
| `Pedidos.Application/ProcesadorPedidoCreado.cs` | `ProcesarAsync` → `Liquidacion` |
| `Pedidos.Application/Asistente.cs` | `AsistenteService`, `IRecuperadorDocumentos`, `IModeloLenguaje` |
| `Pedidos.Infrastructure/Persistencia/*` | `PedidosDbContext`, repositorios, `UnidadDeTrabajo`, migración |
| `Pedidos.Infrastructure/Mensajeria/*` | `PublicadorOutboxWorker`, `PublicadorServiceBus`, `ConsumidorServiceBusWorker`, `BusEnMemoria` |
| `Pedidos.Infrastructure/Ia/RagLocal.cs` | `RecuperadorEnMemoria`, `ModeloExtractivo` |
| `Pedidos.Api/Program.cs` | pipeline, DI, CORS, health checks |
| `Pedidos.Api/Seguridad/Seguridad.cs` | JWT, políticas, `/dev/token` |
| `tests/Pedidos.Tests/*` | `DominioTests`, `PedidoServiceTests`, `ProcesadorPedidoCreadoTests`, `ApiTests`, `AsistenteTests` |

### Frontend
| Archivo | Contenido |
|---|---|
| `mfe-pedidos/vite.config.ts` | `exposes: ./PedidosApp`, `shared` |
| `mfe-pedidos/src/PedidosApp.tsx` | componente expuesto: `Lista`, `Nuevo` |
| `mfe-pedidos/src/api.ts` | `crearClientePedidos(apiUrl, obtenerToken)` |
| `mfe-pedidos/src/main.tsx` | modo independiente |
| `shell/vite.config.ts` | `remotes: mfe_pedidos` |
| `shell/src/auth.tsx` | `AuthProvider`, `useAuth`, token en memoria |
| `shell/src/App.tsx` | rutas, `lazy` + `Suspense` + `LimiteDeError` |
| `shell/src/remotos.d.ts` | tipos del módulo remoto |

### Datos
| Archivo | Contenido |
|---|---|
| `pedidos_lake/transformaciones.py` | `ESQUEMA_BRONZE`, `limpiar`, `deduplicar_ultimo`, `motivo_rechazo`, `separar_validos`, `preparar_lote`, `merge_en_silver`, `resumen_gold` |
| `notebooks/01_pedidos_bronze_a_silver.py` | notebook en formato fuente de Databricks (se importa con *Workspace → Import*) |
| `notebooks/02_consultas_delta.sql` | MERGE en SQL, time travel, CDF, evolución de esquema, gold, diagnóstico |
| `tests/test_transformaciones.py` | 9 pruebas |

## 6. Preguntas frecuentes de diseño (y dónde está la respuesta)

| Pregunta | Respuesta corta |
|---|---|
| ¿Por qué outbox y no publicar directo después de `SaveChanges`? | Si la app se cae entre guardar y publicar, el evento se pierde. Con outbox, BD y evento se guardan en la misma transacción |
| ¿Cómo evitas procesar dos veces? | MessageId estable + duplicate detection + inbox + operaciones idempotentes |
| ¿Abandon o DeadLetter? | Abandon si la falla es temporal (se reintenta); DeadLetter si nunca va a funcionar |
| ¿Qué pasa si el procesamiento tarda más que el lock? | Se renueva solo (`MaxAutoLockRenewalDuration`); si se pierde, otro consumidor lo recibe y la idempotencia lo protege |
| ¿Cómo garantizas orden? | Sesiones por PedidoId y una llamada concurrente por sesión |
| ¿Optimista o pesimista? | Optimista: token de versión + 409; no bloquea filas mientras el usuario piensa |
| ¿Por qué `AsNoTracking`? | Lecturas sin seguimiento de cambios: menos memoria y más rapidez |
| ¿Por qué Module Federation? | Equipos que despliegan su parte sin redesplegar el shell, compartiendo una sola copia de React |
| ¿Cómo comparten auth los micro frontends? | El shell autentica (MSAL) y pasa `obtenerToken`; el remoto nunca ve credenciales |
| ¿Por qué MERGE con condición de fecha? | Un evento atrasado no debe pisar un estado más nuevo |
| ¿Cuándo PostgreSQL y cuándo Delta? | Transacciones de la app → PostgreSQL; análisis histórico y lotes grandes → Delta |
| ¿Cómo evitas alucinaciones en RAG? | Contexto recuperado, instrucciones estrictas, citas obligatorias y validación de la salida |
| ¿Managed identity? | La app se autentica con Azure AD sin secretos; se da a la identidad el rol "Azure Service Bus Data Sender/Receiver" |

## 7. Lo que no se pudo probar en este equipo (y cómo hacerlo)

| Pieza | Por qué no | Cómo probarla |
|---|---|---|
| PostgreSQL real | no hay Docker ni `psql` instalados | `docker compose up -d postgres` + `dotnet ef database update`; la migración ya está generada para Npgsql |
| Azure Service Bus real | requiere suscripción de Azure | crear namespace + cola `pedidos` con **sesiones** y **duplicate detection**; `Mensajeria:Modo=ServiceBus`, `Mensajeria:Namespace=...`, `az login` |
| MERGE en Delta | en Windows, Spark necesita `winutils.exe` de Hadoop para escribir archivos | correr el notebook en Databricks (Free Edition sirve); las transformaciones sí están probadas localmente |
| Carga del remoto en el navegador | se verificó que `remoteEntry.js` se sirve con CORS y que ambas apps compilan; no se abrió en un navegador | levantar los tres servicios (sección 3) y abrir `http://localhost:5001` |
| Entra ID real | requiere un tenant | registrar dos apps (API con roles `lector`/`operador` y SPA), poner `Auth:Authority` y `Auth:Audience`, y usar MSAL en el shell |

## 8. Plantilla para una HU nueva (ejemplo: "como operador quiero marcar un pedido como enviado")

1. **Dominio**: `EstadoPedido.Enviado` y `Pedido.MarcarEnviado()` con sus reglas.
2. **Aplicación**: `PedidoService.MarcarEnviadoAsync(id, version)` y, si otros sistemas deben enterarse, encolar `PedidoEnviadoV1` en el outbox.
3. **API**: `POST /api/pedidos/{id}/enviar` con `If-Match` y política `Pedidos.Escribir`.
4. **Migración** si cambia el esquema: `dotnet ef migrations add PedidoEnviado`.
5. **Consumidor** del nuevo evento, si aplica (idempotente y con inbox).
6. **Remoto**: botón en `Lista` y método en `api.ts`.
7. **Datos**: agregar `Enviado` a `ESTADOS_VALIDOS`.
8. **Pruebas** en cada capa y **documento de transición** de la HU (secciones 4 y 5).

## 9. Problemas encontrados al construirlo

| Problema | Solución |
|---|---|
| `dotnet new maui` y la restauración de paquetes grandes se cortan por red | reintentar `dotnet restore`; el CI usa caché |
| `IConfigurationSection.Get` no existe en Infrastructure | paquete `Microsoft.Extensions.Options.ConfigurationExtensions` |
| Spark en Windows: "Python worker exited unexpectedly" | las pruebas arman los datos con literales en la JVM (las transformaciones no usan UDF de Python) |
| Fechas de SQLite sin "Z" | conversión a `DateTimeKind.Utc` en el `DbContext` |
| `process` no definido en `vite.config.ts` del shell | `loadEnv(mode, '.', '')` |
| Acentos de la terminal de Windows en `curl` | mandar el JSON desde un archivo UTF-8 (`--data-binary @archivo`) |
