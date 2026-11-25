namespace ScalingWrites;

public enum CounterKind
{
    // UPDATE videos SET views = views + 1 per view (the baseline)
    Direct,
    // stage 1: count in memory, write the sums every FlushMilliseconds
    Batched,
    // stage 2: spread one video's views over Shards rows
    Sharded,
    // stage 3: spread videos over three databases
    Partitioned,
    // stage 4: accept into a bounded queue, write from a background drainer, shed when full
    Queued,
}

public enum PartitionerKind
{
    // videoId % 3: simple, and adding a database moves almost every video
    Modulo,
    // stage 3: your consistent hash ring
    Ring,
}

// The "Writes" configuration section. Set from the command line with Writes__Counter=Batched etc.
public sealed class WritesOptions
{
    public const string Section = "Writes";

    public CounterKind Counter { get; set; } = CounterKind.Direct;

    // Stage 1: how often buffered views are written. Also how many views a crash can lose.
    public int FlushMilliseconds { get; set; } = 1000;

    // Stage 2: rows per video.
    public int Shards { get; set; } = 8;

    // Stage 3.
    public PartitionerKind Partitioner { get; set; } = PartitionerKind.Ring;
    public int VirtualNodes { get; set; } = 100;

    // Stage 4: views waiting to be written before new ones are shed.
    public int QueueCapacity { get; set; } = 50_000;

    // Stage 4: the most views one drain takes off the queue and writes together.
    public int BatchSize { get; set; } = 1000;

    // Stage 4: run the background drainer. The tests turn it off and drain by hand.
    public bool Drainer { get; set; } = true;
}
