using Microsoft.Extensions.Options;

namespace ScalingWrites;

// Which partition database holds a video's count.
public interface IPartitioner
{
    int PartitionFor(long videoId);
}

// The first idea: hash mod the number of databases. Even, cheap, and adding a fourth database
// moves about three videos in four.
public sealed class ModuloPartitioner(PartitionRepository partitions) : IPartitioner
{
    public int PartitionFor(long videoId) => (int)(StableHash.Of(videoId) % (ulong)partitions.Count);
}

// Stage 3: your ring, over nodes named "p0", "p1", "p2".
public sealed class RingPartitioner(PartitionRepository partitions, IOptions<WritesOptions> options) : IPartitioner
{
    private readonly ConsistentHashRing _ring = new(
        Enumerable.Range(0, partitions.Count).Select(i => $"p{i}"), options.Value.VirtualNodes);

    public int PartitionFor(long videoId) => int.Parse(_ring.NodeFor(videoId).AsSpan(1));
}
