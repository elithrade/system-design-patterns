namespace ScalingReads;

public enum RouterKind
{
    // every read goes to the primary (the baseline)
    Primary,
    // every read goes to the replica: shows the lag bug, do not keep it
    Replica,
    // stage 2
    ReadYourWrites,
}

public enum CacheKind
{
    // no cache (the baseline)
    None,
    // stage 3: Redis in front of the database
    Redis,
    // stage 4: an in-process cache in front of Redis
    RedisL1,
}

// The "Reads" configuration section. Set from the command line with Reads__Router=ReadYourWrites etc.
public sealed class ReadsOptions
{
    public const string Section = "Reads";

    public RouterKind Router { get; set; } = RouterKind.Primary;

    // Stage 2: how long after a user's write their reads stay on the primary.
    public int StickySeconds { get; set; } = 5;

    public CacheKind Cache { get; set; } = CacheKind.None;

    // Stage 3: Redis TTL, so a key nobody invalidates still goes away.
    public int CacheTtlSeconds { get; set; } = 300;

    // Stage 4: how long a value stays in the in-process cache. This is also the staleness you accept.
    public int L1Seconds { get; set; } = 1;

    // Prepended to every Redis key. The tests give each test its own prefix.
    public string KeyPrefix { get; set; } = "feed:";
}
