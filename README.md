# ImageForge

Учебный сервис **асинхронной обработки изображений**. Пользователь загружает
картинку через браузер, сервис ставит её в очередь, отдельные воркеры
параллельно сжимают, ресайзят и конвертируют (WebP/JPEG/PNG), а прогресс
прилетает в реальном времени через WebSocket. Демонстрирует, как реальные
веб-сервисы устроены: разделение API и фоновых воркеров, очередь сообщений
для буферизации нагрузки, горизонтальное масштабирование.

## Архитектура

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

- **API** — приём загрузок, выдача `taskId`, чтение статусов, статика, SignalR-хаб.
- **Worker** — независимый процесс, который слушает RabbitMQ, обрабатывает
  изображение через ImageSharp и пишет прогресс/результат в Redis.
- **RabbitMQ** — очередь задач, балансирует работу между воркерами.
- **Redis** — key/value-хранилище статусов + Pub/Sub-канал для пушей.

Все четыре компонента живут в Docker-контейнерах, общаются по приватной сети.

## Стек

- ASP.NET Core 8 (минимальные API + SignalR)
- .NET 8 Worker Service
- RabbitMQ 3 (`RabbitMQ.Client` 6.x)
- Redis 7 (`StackExchange.Redis` 2.x)
- SixLabors.ImageSharp 3.x (pure C#, без нативных зависимостей)
- Docker + docker-compose
- Фронт: vanilla HTML/CSS/JS, шрифты EB Garamond + Inter + JetBrains Mono

## Запуск через Docker (рекомендуемый путь)

Требуется только **Docker Desktop**.

```bash
docker compose up --build
```

Стек поднимется на:
- **http://localhost:8080** — фронт + API + Swagger (`/swagger`)
- **http://localhost:15672** — RabbitMQ Management UI (логин/пароль `guest`/`guest`)

Горизонтальное масштабирование воркеров:

```bash
docker compose up -d --scale worker=3
```

Это запустит **три копии воркера**, между которыми RabbitMQ автоматически
распределит задачи (fair dispatch через `BasicQos(prefetchCount=1)`).

Остановить и удалить всё, включая хранилище:

```bash
docker compose down -v
```

## Ручной запуск (для разработки)

Нужен .NET 8 SDK + поднятые отдельно RabbitMQ и Redis (или используйте локальные сервисы).

```bash
# В одном терминале
dotnet run --project src/ImageForge.Api

# В другом
dotnet run --project src/ImageForge.Worker
```

API стартует на `http://localhost:5102`, фронт по умолчанию читается из
относительного пути `../../frontend` (через `Frontend:Path` в `appsettings.json`).

## Эндпоинты

| Метод | URL | Что делает |
|---|---|---|
| `POST` | `/api/images` | Загрузить файл, получить `taskId`. Form-поля: `file`, `format`, `maxDimension`. |
| `GET`  | `/api/images/{taskId}` | Текущий статус (`pending` / `processing` / `done` / `failed`). |
| `GET`  | `/api/images/{taskId}/result` | Скачать обработанный файл. |
| `GET`  | `/api/images/{taskId}/source` | Скачать оригинал (для before/after). |
| `GET`  | `/api/stats` | Статистика очереди RabbitMQ (consumers / queued / in flight). |
| `WS`   | `/hub/tasks` | SignalR-хаб; вызвать `SubscribeToTask(taskId)`, ловить `statusUpdate`. |
| `GET`  | `/swagger` | OpenAPI документация. |

## Валидация и ограничения

- Принимаются только `image/jpeg`, `image/png`, `image/webp` (по MIME-type).
- Целевые форматы: `webp` (по умолчанию), `jpg`, `jpeg`, `png`.
- `maxDimension`: `0` — не ресайзить, любое положительное число — ресайз "fit
  inside box" с сохранением пропорций.
- Максимальный размер файла: **20 МБ**.
- Битый или неподдерживаемый файл → статус `failed` с понятным `Error`.

## Жизненный цикл задачи

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

При краше воркера во время обработки сообщение возвращается в очередь и
будет обработано повторно — гарантия **at-least-once delivery**.

## Структура проекта

```
ImageForge.sln
├── src/
│   ├── ImageForge.Api/         ASP.NET Core 8 + SignalR + статика
│   ├── ImageForge.Worker/      .NET 8 Worker Service + ImageSharp
│   └── ImageForge.Shared/      Контракты (TaskMessage, TaskStatus) и Redis-обёртка
├── frontend/                   index.html, styles.css, app.js
├── storage/                    Загрузки и результаты (gitignored)
├── docker-compose.yml
├── src/ImageForge.Api/Dockerfile
└── src/ImageForge.Worker/Dockerfile
```

## Out of scope (осознанно не реализовано)

- Аутентификация и пользовательские аккаунты
- Реляционная БД и долгосрочное хранение
- Облачное хранилище (S3 и т.п.)
- Распределённая трассировка
- HTTPS-терминация (предполагается реверс-прокси)
- SignalR Redis backplane (нужен при `--scale api=N`)
- Dead letter queue для poison messages

См. секцию 9 в [CLAUDE.md](CLAUDE.md).

## Полезные команды

```bash
# Логи конкретного сервиса
docker compose logs -f worker

# Подключиться к Redis в compose-стеке
docker compose exec redis redis-cli

# Подключиться к RabbitMQ CLI
docker compose exec rabbitmq rabbitmqctl list_queues name messages_ready consumers

# Пересобрать образы после изменений в коде
docker compose build

# Полная очистка с удалением volume `storage`
docker compose down -v
```

## Лицензия

Учебный проект. ImageSharp в open-source сценариях покрыт Six Labors
Split License.
