# CLAUDE.md — Asynchronous Image Processing Service

> This file is the project brief for the AI coding agent. Read it fully before writing any code.

---

## 0. How to work on this project (agent instructions)

**Communicate with me in Russian.** All explanations, summaries, and questions to me must be in Russian. Code, identifiers, comments in code, and commit messages stay in English.

**Explain as you go.** This is a learning project. After each meaningful step:
1. Say in 2–4 Russian sentences *what* you just did and *why*.
2. Point out the key line(s) or concept a beginner should understand (e.g. why the message goes to RabbitMQ, why status lives in Redis).
3. Only then move on.

**Work incrementally.** Follow the milestones in section 6 in order. Do not jump ahead. After each milestone, stop, summarize, and wait for me to confirm before starting the next one.

**Keep it runnable.** The project must build and run after every milestone. Never leave it in a broken state at the end of a step.

**Ask before big moves.** Before adding a new dependency, changing the architecture, or deleting files, ask me first (in Russian) and explain the trade-off.

**Prefer clarity over cleverness.** This is academic work that will be defended orally. Favor readable, conventional code over compact tricks. Add short English comments where the intent isn't obvious.

---

## 1. What we are building

A web service where a user uploads images and the system processes them **asynchronously**: compresses, resizes, and converts formats (e.g. to WebP). The user does not wait — the API responds instantly with a `taskId`, processing happens in the background, and progress is shown in real time. When done, the user gets a notification and a download link.

**Why it matters (for the defense):** sites with user-generated content must shrink large phone photos (5–10 MB) without making users wait. This service is exactly that pattern, mirroring real systems.

---

## 2. Technology stack (use these, no substitutions without asking)

- **API:** ASP.NET Core 8 Web API (C#)
- **Worker:** .NET 8 Worker Service (separate project, same solution)
- **Message queue:** RabbitMQ — client library `RabbitMQ.Client`
- **Cache / status store:** Redis — client library `StackExchange.Redis`
- **Real-time updates:** SignalR (fallback to HTTP polling if SignalR proves too slow to wire up)
- **Image processing:** ImageSharp (`SixLabors.ImageSharp`) — pure C#, no native dependencies (keeps Docker simple)
- **Frontend:** Vanilla HTML + modern CSS + JavaScript. No build step, no framework. Design language: **warm minimal / editorial** (see section 8). Reference mock: `prototype_warm_soft.html`.
- **Infrastructure:** Docker + docker-compose (containers: api, worker, rabbitmq, redis)

---

## 3. Architecture

```
  Browser (vanilla JS UI)
        │  HTTP upload + SignalR connection
        ▼
  ASP.NET Core Web API ──── publishes task ────▶ RabbitMQ (queue)
        ▲   │                                        │
 SignalR│   │ writes initial status                  │ consumed by
  push  │   ▼                                        ▼
        │  Redis  ◀──── writes progress/status ──── Worker × N
        └───────────────────────────────────────────┘
```

**Flow:**
1. Browser uploads a file → API stores it, creates `taskId`, sets status `pending` in Redis, publishes `{taskId, filePath, options}` to RabbitMQ, returns `taskId` immediately.
2. A free worker consumes the message → processes the image with ImageSharp → updates progress in Redis as it goes (`processing`, percent, then `done`).
3. Progress is pushed to the browser in real time via SignalR.
4. On completion: status `done`, result path cached in Redis, user can download.

---

## 4. Target solution structure

```
ImageForge.sln
├── src/
│   ├── ImageForge.Api/          # ASP.NET Core Web API + SignalR hub
│   ├── ImageForge.Worker/       # .NET Worker Service (queue consumer)
│   └── ImageForge.Shared/       # shared models (TaskMessage, enums, DTOs)
├── frontend/                    # index.html, styles.css, app.js (vanilla)
├── storage/                     # uploaded + processed files (gitignored)
├── docker-compose.yml
└── README.md
```

Keep shared contracts (the queue message shape, status enum) in `ImageForge.Shared` so API and Worker agree on the format.

---

## 5. Key contracts (define early, keep stable)

**Queue message** (`ImageForge.Shared`):
```
TaskMessage { string TaskId; string SourcePath; string TargetFormat; int? MaxDimension; }
```

**Status in Redis** (key: `task:{taskId}`), JSON value:
```
TaskStatus { string TaskId; string State; int Progress; string? ResultPath; string? Error; }
// State: "pending" | "processing" | "done" | "failed"
```

**API endpoints:**
- `POST /api/images` — multipart upload, returns `{ taskId }`
- `GET  /api/images/{taskId}` — returns current `TaskStatus`
- `GET  /api/images/{taskId}/result` — downloads the processed file
- SignalR hub at `/hub/tasks` — pushes `TaskStatus` updates

---

## 6. Build order (milestones — do these in order, stop after each)

**M1 — Skeleton.** Create the solution and three projects. Confirm everything builds and the API returns a hello response. *Explain how the projects relate.*

**M2 — Upload.** `POST /api/images` saves the file to `storage/`, generates a `taskId`, returns it. `GET /api/images/{taskId}` returns a stubbed status. *Explain where the file goes and what `taskId` is for.*

**M3 — RabbitMQ wiring.** API publishes a `TaskMessage`; Worker consumes and logs it. *Explain what a queue is and why we don't process inline.*

**M4 — Image processing.** Worker uses ImageSharp to resize + compress, saves result to `storage/`. *Explain the processing pipeline.*

**M5 — Redis status.** Worker writes `pending → processing → done` to Redis; `GET` reads from Redis. End-to-end works. *Explain why status lives in Redis, not the DB.*

**M6 — Format conversion + percent progress.** Add WebP conversion and percent updates written to Redis at stages. *Explain how progress is reported.*

**M7 — SignalR.** Push progress to the browser in real time. (Fallback: polling.) *Explain push vs poll.*

**M8 — Frontend.** Build the vanilla UI matching `prototype_warm_soft.html`: drag-and-drop upload, task cards, progress bars, before/after slider. Wire it to the API + SignalR. *Explain how the UI talks to the backend.*

**M9 — Docker.** Dockerfiles + `docker-compose.yml` so `docker compose up` starts api + worker + rabbitmq + redis. *Explain what each container is.*

**M10 — Worker scaling.** Verify `docker compose up --scale worker=3` distributes tasks across workers. *Explain how RabbitMQ load-balances — this is the key defense highlight.*

**M11 — Polish.** Swagger, validation (file type/size), error handling (corrupt file → status `failed`), README with run instructions.

---

## 7. Coding conventions

- C#: standard .NET conventions, `async`/`await` for all I/O, dependency injection via the built-in container, `appsettings.json` for connection strings (no hardcoded hosts).
- Configuration: RabbitMQ and Redis hostnames come from config/env so they work both locally and in Docker.
- Errors: a failed image should set status `failed` with a message, not crash the worker.
- Commits: small, one concern each, English imperative messages (e.g. `add redis status store`).

---

## 8. Frontend design language (warm minimal / editorial)

Match the reference mock `prototype_warm_soft.html`. Principles:
- Warm paper background, warm grays, **one** accent (terracotta). **No gradients, no glassmorphism, no neon.**
- Typography-led: Fraunces (display serif) + Hanken Grotesk (UI) + IBM Plex Mono (numbers/labels).
- Generous whitespace, gentle rounded corners, soft warm shadows, thin hairline rules.
- Restrained motion only (gentle fades/rises). Progress as a slim rounded bar; status as small round dots.
- Keep the before/after slider — it's the visual proof the service works.

---

## 9. Out of scope (deliberately, due to time)

Authentication, user accounts, a persistent relational database, cloud storage, and distributed tracing are **not** part of this project. If I ask for them later, treat them as new milestones. (Mentioning these as conscious exclusions is part of the oral defense.)

---

## 10. Useful commands (fill in as the project grows)

```bash
# build
dotnet build

# run API / worker locally
dotnet run --project src/ImageForge.Api
dotnet run --project src/ImageForge.Worker

# full system
docker compose up
docker compose up --scale worker=3
```
