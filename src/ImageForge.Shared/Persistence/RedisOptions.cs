namespace ImageForge.Shared.Persistence;

// Redis connection settings. Both Api and Worker bind to the "Redis" section
// from their appsettings.json so they hit the same instance and key space.
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    // StackExchange.Redis connection string: "host:port,option=value,..."
    public string ConnectionString { get; init; } = "localhost:6379";

    // Keys are prefixed so the Redis instance can be shared with other apps
    // without colliding on a flat "task:xyz" key.
    public string KeyPrefix { get; init; } = "imageforge:task:";
}
