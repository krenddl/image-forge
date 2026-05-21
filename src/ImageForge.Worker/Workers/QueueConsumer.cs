using System.Text;
using System.Text.Json;
using ImageForge.Shared.Contracts;
using ImageForge.Shared.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ImageForge.Worker.Workers;

// Long-running consumer registered as a HostedService. Connects to RabbitMQ,
// subscribes to the task queue and handles messages one at a time per worker.
// Real image processing lands in M4; for now we only log what we received.
public sealed class QueueConsumer : BackgroundService
{
    private readonly ILogger<QueueConsumer> _logger;
    private readonly RabbitMqOptions _options;
    private IConnection? _connection;
    private IModel? _channel;

    public QueueConsumer(IOptions<RabbitMqOptions> options, ILogger<QueueConsumer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            UserName = _options.User,
            Password = _options.Password
        };

        _connection = factory.CreateConnection("imageforge-worker");
        _channel = _connection.CreateModel();

        _channel.QueueDeclare(
            queue: _options.Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);

        // Fair dispatch: never push more than one un-acked message at a time
        // to this consumer. With multiple workers this makes RabbitMQ deliver
        // the next task to whichever worker is currently idle, not round-robin.
        _channel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);

        var consumer = new EventingBasicConsumer(_channel);
        consumer.Received += OnMessage;

        _channel.BasicConsume(
            queue: _options.Queue,
            autoAck: false,
            consumer: consumer);

        _logger.LogInformation("Worker consuming queue {Queue} on {Host}:{Port}",
            _options.Queue, _options.Host, _options.Port);

        // ExecuteAsync needs to keep the HostedService alive; the consumer
        // runs on RabbitMQ.Client's own threads via events.
        return Task.CompletedTask;
    }

    private void OnMessage(object? sender, BasicDeliverEventArgs ea)
    {
        try
        {
            var json = Encoding.UTF8.GetString(ea.Body.Span);
            var message = JsonSerializer.Deserialize<TaskMessage>(json);

            if (message is null)
            {
                _logger.LogWarning("Received empty or invalid message, dropping.");
                _channel!.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
                return;
            }

            _logger.LogInformation(
                "Received task {TaskId}: source={Source}, format={Format}, maxDim={MaxDim}",
                message.TaskId, message.SourcePath, message.TargetFormat, message.MaxDimension);

            // M4 will do the actual ImageSharp work here.
            _channel!.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle message {DeliveryTag}", ea.DeliveryTag);
            // Do not requeue on parse errors — would loop forever.
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
