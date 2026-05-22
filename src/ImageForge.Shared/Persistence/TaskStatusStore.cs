using System.Text.Json;
using ImageForge.Shared.Contracts;
using StackExchange.Redis;

// Disambiguate from System.Threading.Tasks.TaskStatus.
using TaskStatus = ImageForge.Shared.Contracts.TaskStatus;

namespace ImageForge.Shared.Persistence;

// Reads and writes TaskStatus snapshots to Redis under "{prefix}{taskId}".
// Shared between Api (reader) and Worker (writer) so the wire shape and
// key format never drift.
//
// In addition to plain set/get, SetAndBroadcastAsync also publishes the
// status JSON to a Pub/Sub channel; this is how the worker tells the API
// "something changed for this task" so the API can push it over SignalR.
public sealed class TaskStatusStore
{
    // Keep entries around for a day, then let Redis evict them. Plenty of
    // time for the user to download the result, no unbounded growth.
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private readonly IConnectionMultiplexer _redis;
    private readonly string _prefix;
    private readonly RedisChannel _channel;

    public TaskStatusStore(IConnectionMultiplexer redis, RedisOptions options)
    {
        _redis = redis;
        _prefix = options.KeyPrefix;
        _channel = RedisChannel.Literal(options.StatusChannel);
    }

    // Persist + broadcast in one call. Order: store first, then publish, so
    // any subscriber that gets the event and then re-reads the key sees the
    // up-to-date value.
    public async Task SetAndBroadcastAsync(TaskStatus status)
    {
        var db = _redis.GetDatabase();
        var json = JsonSerializer.Serialize(status);
        await db.StringSetAsync(KeyFor(status.TaskId), json, Ttl);

        var subscriber = _redis.GetSubscriber();
        await subscriber.PublishAsync(_channel, json);
    }

    public async Task<TaskStatus?> GetAsync(string taskId)
    {
        var db = _redis.GetDatabase();
        var value = await db.StringGetAsync(KeyFor(taskId));
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        return JsonSerializer.Deserialize<TaskStatus>(value!);
    }

    // Subscribe to every status change for any task. The callback receives
    // the JSON payload; deserialize it on the consumer side.
    public Task SubscribeAsync(Func<TaskStatus, Task> handler)
    {
        var subscriber = _redis.GetSubscriber();
        return subscriber.SubscribeAsync(_channel, async (_, value) =>
        {
            if (value.IsNullOrEmpty)
            {
                return;
            }

            var status = JsonSerializer.Deserialize<TaskStatus>(value!);
            if (status is not null)
            {
                await handler(status);
            }
        });
    }

    private string KeyFor(string taskId) => _prefix + taskId;
}
