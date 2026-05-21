using System.Text.Json;
using ImageForge.Shared.Contracts;
using ImageForge.Shared.Messaging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ImageForge.Api.Services;

// Owns a single connection + channel to RabbitMQ for the lifetime of the
// process and exposes a simple Publish method. Registered as a singleton.
public sealed class QueuePublisher : IDisposable
{
    private readonly ILogger<QueuePublisher> _logger;
    private readonly IConnection _connection;
    private readonly IModel _channel;
    private readonly string _queue;

    public QueuePublisher(IOptions<RabbitMqOptions> options, ILogger<QueuePublisher> logger)
    {
        _logger = logger;
        var opts = options.Value;
        _queue = opts.Queue;

        var factory = new ConnectionFactory
        {
            HostName = opts.Host,
            Port = opts.Port,
            UserName = opts.User,
            Password = opts.Password
        };

        _connection = factory.CreateConnection("imageforge-api");
        _channel = _connection.CreateModel();

        // Declare the queue idempotently. "durable" means the queue survives
        // a broker restart; messages we publish with DeliveryMode=2 below
        // will be persisted to disk inside that queue.
        _channel.QueueDeclare(
            queue: _queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null);

        _logger.LogInformation("RabbitMQ publisher connected to {Host}:{Port}, queue {Queue}",
            opts.Host, opts.Port, _queue);
    }

    public void Publish(TaskMessage message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);

        var props = _channel.CreateBasicProperties();
        props.ContentType = "application/json";
        props.DeliveryMode = 2; // persistent: message survives broker restart

        // Using the default exchange ("") with routingKey = queue name means
        // the message goes directly into that queue — the simplest pattern.
        _channel.BasicPublish(
            exchange: string.Empty,
            routingKey: _queue,
            basicProperties: props,
            body: body);

        _logger.LogInformation("Published task {TaskId} to queue {Queue}", message.TaskId, _queue);
    }

    public void Dispose()
    {
        _channel.Dispose();
        _connection.Dispose();
    }
}
