namespace ScalingReads;

public sealed record StatsSnapshot(
    long PrimaryQueries,
    long ReplicaQueries,
    long PostReads,
    long LikeCountReads,
    long RedisCommands);

// Counters the tests assert on and GET /stats returns. PostRepository and RedisCache update them;
// your code does not need to.
public sealed class Stats
{
    private long _primaryQueries;
    private long _replicaQueries;
    private long _postReads;
    private long _likeCountReads;
    private long _redisCommands;

    // every query PostRepository sends, by the database it went to
    public long PrimaryQueries => Volatile.Read(ref _primaryQueries);
    public long ReplicaQueries => Volatile.Read(ref _replicaQueries);

    // calls to PostRepository.GetPostAsync and GetLikeCountAsync
    public long PostReads => Volatile.Read(ref _postReads);
    public long LikeCountReads => Volatile.Read(ref _likeCountReads);

    // every command RedisCache sends
    public long RedisCommands => Volatile.Read(ref _redisCommands);

    public void Query(DbTarget target)
    {
        if (target == DbTarget.Primary)
        {
            Interlocked.Increment(ref _primaryQueries);
        }
        else
        {
            Interlocked.Increment(ref _replicaQueries);
        }
    }

    public void PostRead() => Interlocked.Increment(ref _postReads);
    public void LikeCountRead() => Interlocked.Increment(ref _likeCountReads);
    public void RedisCommand() => Interlocked.Increment(ref _redisCommands);

    public StatsSnapshot Snapshot() =>
        new(PrimaryQueries, ReplicaQueries, PostReads, LikeCountReads, RedisCommands);
}
