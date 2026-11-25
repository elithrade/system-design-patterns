namespace ScalingWrites;

// GET /videos/top: the most viewed videos.
public interface ITopVideos
{
    Task<IReadOnlyList<VideoViews>> TopAsync(int limit, CancellationToken ct);
}

// One database: ORDER BY views LIMIT n. Used by the Direct, Batched and Queued counters.
// (The Sharded counter has no top list; summing every video's shards to rank them is the cost.)
public sealed class DatabaseTopVideos(VideoRepository videos) : ITopVideos
{
    public Task<IReadOnlyList<VideoViews>> TopAsync(int limit, CancellationToken ct) => videos.TopAsync(limit, ct);
}

// Stage 3. With counts spread over partitions, no single database knows the top videos.
// Ask every partition for its own top `limit` (partitions.TopAsync), then merge.
//
// The goal, which the tests check:
//   - the result is the global top `limit`, most viewed first, ties broken by lower id
//   - the partitions are asked at the same time, so the slowest one sets the latency, not the sum
//
// Tests: dotnet test --filter-namespace "*Stage3"
public sealed class ScatterGatherTopVideos(PartitionRepository partitions) : ITopVideos
{
    public Task<IReadOnlyList<VideoViews>> TopAsync(int limit, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
