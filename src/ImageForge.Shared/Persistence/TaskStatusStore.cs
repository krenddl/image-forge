using System.Text.Json;
using ImageForge.Shared.Contracts;
using StackExchange.Redis;

// Disambiguate from System.Threading.Tasks.TaskStatus.
using TaskStatus = ImageForge.Shared.Contracts.TaskStatus;

namespace ImageForge.Shared.Persistence;

// Reads and writes TaskStatus snapshots to Redis under "{prefix}{taskId}".
// Shared between Api (reader) and Worker (writer) so the wire shape and
// key format never drift.
public sealed class TaskStatusStore
{
    // Keep entries around for a day, then let Redis evict them. Plenty of
    // time for the user to download the result, no unbounded growth.
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private readonly IConnectionMultiplexer _redis;
    private readonly string _prefix;

    public TaskStatusStore(IConnectionMultiplexer redis, RedisOptions options)
    {
        _redis = redis;
        _prefix = options.KeyPrefix;
    }

    public async Task SetAsync(TaskStatus status)
    {
        var db = _redis.GetDatabase();
        var json = JsonSerializer.Serialize(status);
        await db.StringSetAsync(KeyFor(status.TaskId), json, Ttl);
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

    private string KeyFor(string taskId) => _prefix + taskId;
}
