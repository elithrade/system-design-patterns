using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ScalingReads;

public sealed class RedisCache(IConnectionMultiplexer redis, IOptions<ReadsOptions> options, Stats stats) : ICache
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly string _prefix = options.Value.KeyPrefix;

    public async Task<string?> GetAsync(string key)
    {
        stats.RedisCommand();
        return await _db.StringGetAsync(_prefix + key);
    }

    public async Task<string?[]> GetManyAsync(params string[] keys)
    {
        stats.RedisCommand();
        var values = await _db.StringGetAsync(keys.Select(k => (RedisKey)(_prefix + k)).ToArray());
        return values.Select(v => (string?)v).ToArray();
    }

    public async Task SetAsync(string key, string value, TimeSpan ttl)
    {
        stats.RedisCommand();
        await _db.StringSetAsync(_prefix + key, value, ttl);
    }

    public async Task<bool> DeleteAsync(string key)
    {
        stats.RedisCommand();
        return await _db.KeyDeleteAsync(_prefix + key);
    }

    public async Task<long> IncrementAsync(string key)
    {
        stats.RedisCommand();
        return await _db.StringIncrementAsync(_prefix + key);
    }

    public async Task<string?> EvalAsync(string script, string[] keys, params string[] args)
    {
        stats.RedisCommand();
        var result = await _db.ScriptEvaluateAsync(
            script,
            keys.Select(k => (RedisKey)(_prefix + k)).ToArray(),
            args.Select(a => (RedisValue)a).ToArray());
        return result.IsNull ? null : result.ToString();
    }
}
