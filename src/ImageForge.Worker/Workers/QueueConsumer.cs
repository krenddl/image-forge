using System.Text;
using System.Text.Json;
using ImageForge.Shared.Contracts;
using ImageForge.Shared.Messaging;
using ImageForge.Shared.Persistence;
using ImageForge.Worker.Services;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using SixLabors.ImageSharp;

// Disambiguate from System.Threading.Tasks.TaskStatus.
using TaskStatus = ImageForge.Shared.Contracts.TaskStatus;

namespace ImageForge.Worker.Workers;

// Long-running consumer registered as a HostedService. Connects to RabbitMQ,
// subscribes to the task queue and processes messages one at a time per worker.
// Updates the per-task status in Redis as it goes: processing -> done/failed.
public sealed class QueueConsumer : BackgroundService
{
    private readonly ILogger<QueueConsumer> _logger;
    private readonly RabbitMqOptions _options;
    private readonly ImageProcessor _processor;
    private readonly TaskStatusStore _statusStore;
    private readonly LifetimeStats _lifetimeStats;
    private IConnection? _connection;
    private IModel? _channel;

    public QueueConsumer(
        IOptions<RabbitMqOptions> options,
        ImageProcessor processor,
        TaskStatusStore statusStore,
        LifetimeStats lifetimeStats,
        ILogger<QueueConsumer> logger)
    {
        _options = options.Value;
        _processor = processor;
        _statusStore = statusStore;
        _lifetimeStats = lifetimeStats;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            UserName = _options.User,
            Password = _options.Password,
            DispatchConsumersAsync = true
        };

        _connection = factory.CreateConnection("imageforge-worker");
        _channel = _connection.CreateModel();

        _channel.QueueDeclare(
            queue: _options.Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);

        // Fair dispatch: only push the next message when this worker acks the
        // current one. With multiple workers this balances load by capacity.
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.Received += OnMessageAsync;

        _channel.BasicConsume(
            queue: _options.Queue,
            autoAck: false,
            consumer: consumer);

        _logger.LogInformation("Worker consuming queue {Queue} on {Host}:{Port}",
            _options.Queue, _options.Host, _options.Port);

        return Task.CompletedTask;
    }

    private async Task OnMessageAsync(object sender, BasicDeliverEventArgs ea)
    {
        TaskMessage? message = null;
        try
        {
            var json = Encoding.UTF8.GetString(ea.Body.Span);
            message = JsonSerializer.Deserialize<TaskMessage>(json);

            if (message is null)
            {
                _logger.LogWarning("Received empty or invalid message, dropping.");
                _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            _logger.LogInformation("Received task {TaskId}, starting processing", message.TaskId);

            // Flip status to "processing" so a GET right now shows the user
            // we have actually picked up their task.
            await _statusStore.SetAndBroadcastAsync(new TaskStatus(
                TaskId: message.TaskId,
                State: TaskState.Processing,
                Progress: 0,
                ResultPath: null,
                Error: null));

            // Local helper that the processor calls at each stage. Keeps the
            // processor itself unaware of Redis or status persistence.
            var taskIdLocal = message.TaskId;
            Func<int, Task> report = async percent =>
            {
                await _statusStore.SetAndBroadcastAsync(new TaskStatus(
                    TaskId: taskIdLocal,
                    State: TaskState.Processing,
                    Progress: percent,
                    ResultPath: null,
                    Error: null));
            };

            var resultPath = await _processor.ProcessAsync(message, report, CancellationToken.None);

            await _statusStore.SetAndBroadcastAsync(new TaskStatus(
                TaskId: message.TaskId,
                State: TaskState.Done,
                Progress: 100,
                ResultPath: resultPath,
                Error: null));

            // Lifetime stats: how much we processed and how many bytes saved.
            try
            {
                var bytesIn  = new FileInfo(message.SourcePath).Length;
                var bytesOut = new FileInfo(resultPath).Length;
                await _lifetimeStats.RecordAsync(bytesIn, bytesOut);
            }
            catch (Exception statsEx)
            {
                // Stats are best-effort; never let them block the ack.
                _logger.LogWarning(statsEx, "Failed to record lifetime stats for {TaskId}", message.TaskId);
            }

            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to process task {TaskId} (delivery {DeliveryTag})",
                message?.TaskId ?? "<unknown>", ea.DeliveryTag);

            if (message is not null)
            {
                // User-facing error message. Hide raw stack traces; classify
                // common failure modes into something the UI can show.
                var userMessage = ex switch
                {
                    UnknownImageFormatException => "The file is not a recognized image format.",
                    InvalidImageContentException => "The image is corrupt or truncated.",
                    NotSupportedException nse  => nse.Message,
                    _                          => "Processing failed: " + ex.Message
                };

                try
                {
                    await _statusStore.SetAndBroadcastAsync(new TaskStatus(
                        TaskId: message.TaskId,
                        State: TaskState.Failed,
                        Progress: 0,
                        ResultPath: null,
                        Error: userMessage));
                }
                catch (Exception statusEx)
                {
                    _logger.LogError(statusEx, "Also failed to write 'failed' status for {TaskId}", message.TaskId);
                }
            }

            // Do not requeue: malformed inputs would loop forever.
            _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    public override void Dispose()
    {
        _channel?.Dispose();
        _connection?.Dispose();
        base.Dispose();
    }
}
