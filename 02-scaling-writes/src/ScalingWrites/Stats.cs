namespace ScalingWrites;

public sealed record StatsSnapshot(
    long CounterWrites,
    long CounterReads,
    long[] PartitionWrites,
    long[] PartitionReads,
    long Accepted,
    long Shed,
    long QueueDepth);

// Counters the tests assert on and GET /stats returns. The repositories and the provided counters
// update them; your code does not need to.
public sealed class Stats
{
    public const int MaxPartitions = 3;

    private long _counterWrites;
    private long _counterReads;
    private readonly long[] _partitionWrites = new long[MaxPartitions];
    private readonly long[] _partitionReads = new long[MaxPartitions];
    private long _accepted;
    private long _shed;

    // statements sent to the main database that change a view count
    public long CounterWrites => Volatile.Read(ref _counterWrites);

    // statements sent to the main database that read a video
    public long CounterReads => Volatile.Read(ref _counterReads);

    // statements sent to each partition database (stage 3)
    public long PartitionWrites(int partition) => Volatile.Read(ref _partitionWrites[partition]);
    public long PartitionReads(int partition) => Volatile.Read(ref _partitionReads[partition]);

    // views a queue accepted, and views it turned away (stage 4)
    public long Accepted => Volatile.Read(ref _accepted);
    public long Shed => Volatile.Read(ref _shed);

    // set by Program when the counter is Queued
    public Func<int>? QueueDepthProbe { get; set; }

    public void CounterWrite() => Interlocked.Increment(ref _counterWrites);
    public void CounterRead() => Interlocked.Increment(ref _counterReads);
    public void PartitionWrite(int partition) => Interlocked.Increment(ref _partitionWrites[partition]);
    public void PartitionRead(int partition) => Interlocked.Increment(ref _partitionReads[partition]);
    public void Accept() => Interlocked.Increment(ref _accepted);
    public void Shedding() => Interlocked.Increment(ref _shed);

    public StatsSnapshot Snapshot() => new(
        CounterWrites,
        CounterReads,
        Enumerable.Range(0, MaxPartitions).Select(PartitionWrites).ToArray(),
        Enumerable.Range(0, MaxPartitions).Select(PartitionReads).ToArray(),
        Accepted,
        Shed,
        QueueDepth());

    private long QueueDepth()
    {
        try
        {
            return QueueDepthProbe?.Invoke() ?? 0;
        }
        catch (NotImplementedException)
        {
            return -1;
        }
    }
}
