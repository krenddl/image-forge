namespace ImageForge.Shared.Messaging;

// Connection settings for RabbitMQ. Both Api and Worker bind to this section
// in their appsettings.json so the broker hostname/queue name stay in sync.
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5672;
    public string User { get; init; } = "guest";
    public string Password { get; init; } = "guest";
    public string Queue { get; init; } = "image-tasks";
}
