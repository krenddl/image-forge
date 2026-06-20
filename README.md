# ImageForge

A learning project demonstrating **asynchronous image processing**. The user uploads an image through a browser, the service places it in a queue, and separate workers concurrently compress, resize, and convert it (WebP/JPEG/PNG), while progress is streamed in real time via WebSocket. Illustrates how real-world web services are structured: separation of API and background workers, a message queue for load buffering, and horizontal scalability.

## Architecture

```
 Browser (vanilla HTML/CSS/JS)
       │  HTTP upload + SignalR
       ▼
 ASP.NET Core 8 API ───── publish ────▶ RabbitMQ
       ▲  │                                 │
SignalR│  │ pub/sub status                  │ consume
 push  │  ▼                                 ▼
       │ Redis  ◀── status + broadcast ── Worker × N
       └────────────────────────────────────┘
```

- **API** — accepts uploads, issues a `taskId`, reads statuses, serves static files, hosts the SignalR hub.
- **Worker** — an independent process that consumes from RabbitMQ, processes images via ImageSharp, and writes progress/results to Redis.
- **RabbitMQ** — the task queue, distributing work across workers.
- **Redis** — key/value store for task statuses + Pub/Sub channel for real-time pushes.

All four components run in Docker containers and communicate over a private network.

## Tech Stack

- ASP.NET Core 8 (Minimal APIs + SignalR)
- .NET 8 Worker Service
- RabbitMQ 3 (`RabbitMQ.Client` 6.x)
- Redis 7 (`StackExchange.Redis` 2.x)
- SixLabors.ImageSharp 3.x (pure C#, no native dependencies)
- Docker + docker-compose
- Frontend: vanilla HTML/CSS/JS, fonts EB Garamond + Inter + JetBrains Mono

## Running with Docker (recommended)

Only **Docker Desktop** is required.

```bash
docker compose up --build
```

The stack will be available at:

- **http://localhost:8080** — frontend + API + Swagger (`/swagger`)
- **http://localhost:15672** — RabbitMQ Management UI (login/password: `guest`/`guest`)

Horizontal scaling of workers:

```bash
docker compose up -d --scale worker=3
```

This starts **three worker instances**, with RabbitMQ automatically distributing tasks between them (fair dispatch via `BasicQos(prefetchCount=1)`).

To stop and remove everything, including stored data:

```bash
docker compose down -v
```

## Manual Run (for development)

Requires .NET 8 SDK and separately running RabbitMQ and Redis instances (or use local services).

```bash
# In one terminal
dotnet run --project src/ImageForge.Api

# In another
dotnet run --project src/ImageForge.Worker
```

The API starts at `http://localhost:5102`. The frontend is served by default from the relative path `../../frontend` (configured via `Frontend:Path` in `appsettings.json`).

## Endpoints

| Method | URL                           | Description                                                                        |
| ------ | ----------------------------- | ---------------------------------------------------------------------------------- |
| `POST` | `/api/images`                 | Upload a file, receive a `taskId`. Form fields: `file`, `format`, `maxDimension`.  |
| `GET`  | `/api/images/{taskId}`        | Current status (`pending` / `processing` / `done` / `failed`).                    |
| `GET`  | `/api/images/{taskId}/result` | Download the processed file.                                                       |
| `GET`  | `/api/images/{taskId}/source` | Download the original file (for before/after comparison).                          |
| `GET`  | `/api/stats`                  | RabbitMQ queue statistics (consumers / queued / in flight).                        |
| `WS`   | `/hub/tasks`                  | SignalR hub; call `SubscribeToTask(taskId)`, listen for `statusUpdate` events.     |
| `GET`  | `/swagger`                    | OpenAPI documentation.                                                             |

## Validation & Constraints

- Accepted MIME types: `image/jpeg`, `image/png`, `image/webp`.
- Target formats: `webp` (default), `jpg`, `jpeg`, `png`.
- `maxDimension`: `0` — no resize; any positive number — resize "fit inside box" while preserving aspect ratio.
- Maximum file size: **20 MB**.
- Corrupted or unsupported files result in a `failed` status with a descriptive `Error` message.

## Task Lifecycle

```
[Api]                                          [Worker]
  │ POST + file
  │ → save to storage/uploads/
  │ → Redis SET pending  ─── pub/sub ───┐
  │ → RabbitMQ publish                  │
  ◀ return { taskId }                   │ SignalR push "pending"
                                        │
                              consume   │
                                  ▼     │
                        [Worker]        │
                          → Redis SET processing/0      ▶ SignalR push
                          → Image.LoadAsync             ▶ progress 25
                          → Resize (if needed)          ▶ progress 60
                          → Encode + SaveAsync          ▶ progress 90
                          → Redis SET done/100          ▶ SignalR push "done"
                          → BasicAck
```

If a worker crashes during processing, the message is returned to the queue and will be reprocessed — ensuring **at-least-once delivery**.

## Project Structure

```
ImageForge.sln
├── src/
│   ├── ImageForge.Api/         ASP.NET Core 8 + SignalR + static files
│   ├── ImageForge.Worker/      .NET 8 Worker Service + ImageSharp
│   └── ImageForge.Shared/      Contracts (TaskMessage, TaskStatus) and Redis wrapper
├── frontend/                   index.html, styles.css, app.js
├── storage/                    Uploads and results (gitignored)
├── docker-compose.yml
├── src/ImageForge.Api/Dockerfile
└── src/ImageForge.Worker/Dockerfile
```

## Out of Scope (intentionally not implemented)

- Authentication and user accounts
- Relational database and long-term persistence
- Cloud storage (S3, etc.)
- Distributed tracing
- HTTPS termination (assumed to be handled by a reverse proxy)
- SignalR Redis backplane (required when running `--scale api=N`)
- Dead letter queue for poison messages

See section 9 in [CLAUDE.md](CLAUDE.md).

## Useful Commands

```bash
# Tail logs for a specific service
docker compose logs -f worker

# Connect to Redis inside the compose stack
docker compose exec redis redis-cli

# Connect to the RabbitMQ CLI
docker compose exec rabbitmq rabbitmqctl list_queues name messages_ready consumers

# Rebuild images after code changes
docker compose build

# Full cleanup including the `storage` volume
docker compose down -v
```

## License

Educational project. ImageSharp in open-source scenarios is covered by the Six Labors Split License.
