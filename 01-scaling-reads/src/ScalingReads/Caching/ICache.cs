namespace ScalingReads;

// The few Redis commands the stages need. RedisCache adds Reads:KeyPrefix to every key and counts
// every call in Stats.RedisCommands, so pass bare keys like "post:42".
public interface ICache
{
    // GET. Null when the key does not exist.
    Task<string?> GetAsync(string key);

    // GET of several keys in one round trip (MGET); one entry per key, null for a missing key.
    Task<string?[]> GetManyAsync(params string[] keys);

    // SET with an expiry.
    Task SetAsync(string key, string value, TimeSpan ttl);

    // DEL. True if the key existed.
    Task<bool> DeleteAsync(string key);

    // INCR. Creates a missing key as 0 first, then adds 1.
    Task<long> IncrementAsync(string key);

    // EVAL: runs a Lua script atomically inside Redis. keys get the prefix; args do not.
    // Returns the script's result as a string, or null if it returned nil.
    Task<string?> EvalAsync(string script, string[] keys, params string[] args);
}
