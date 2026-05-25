using StackExchange.Redis;

namespace ImageForge.Shared.Persistence;

// Lifetime counters kept in Redis as plain integer keys:
//   imageforge:stats:processed_total — number of successful tasks
//   imageforge:stats:bytes_in         — sum of source file sizes
//   imageforge:stats:bytes_out        — sum of result file sizes
// Worker bumps them on every "done"; API reads the snapshot.
public sealed class LifetimeStats
{
    private const string KeyProcessed = "imageforge:stats:processed_total";
    private const string KeyBytesIn   = "imageforge:stats:bytes_in";
    private const string KeyBytesOut  = "imageforge:stats:bytes_out";

    private readonly IConnectionMultiplexer _redis;

    public LifetimeStats(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task RecordAsync(long bytesIn, long bytesOut)
    {
        var db = _redis.GetDatabase();
        // One round-trip via a batch instead of three sequential awaits.
        var batch = db.CreateBatch();
        var t1 = batch.StringIncrementAsync(KeyProcessed);
        var t2 = batch.StringIncrementAsync(KeyBytesIn,  bytesIn);
        var t3 = batch.StringIncrementAsync(KeyBytesOut, bytesOut);
        batch.Execute();
        await Task.WhenAll(t1, t2, t3);
    }

    public async Task<LifetimeStatsSnapshot> GetAsync()
    {
        var db = _redis.GetDatabase();
        var batch = db.CreateBatch();
        var t1 = batch.StringGetAsync(KeyProcessed);
        var t2 = batch.StringGetAsync(KeyBytesIn);
        var t3 = batch.StringGetAsync(KeyBytesOut);
        batch.Execute();
        await Task.WhenAll(t1, t2, t3);

        return new LifetimeStatsSnapshot(
            ParseLong(t1.Result),
            ParseLong(t2.Result),
            ParseLong(t3.Result));
    }

    private static long ParseLong(RedisValue v) =>
        v.IsNullOrEmpty || !long.TryParse(v.ToString(), out var n) ? 0 : n;
}

public sealed record LifetimeStatsSnapshot(long Processed, long BytesIn, long BytesOut);
