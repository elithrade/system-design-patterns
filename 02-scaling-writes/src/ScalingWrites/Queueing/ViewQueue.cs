using Microsoft.Extensions.Options;

namespace ScalingWrites;

// Stage 4. A bounded in-memory queue of views between the endpoint and the database.
// QueuedViewCounter (written) calls TryEnqueue per request; QueueDrainer (written) calls
// DrainOnceAsync in a loop.
//
// The goal, which the tests check:
//   - TryEnqueue never waits: it returns false at once when QueueCapacity views are waiting
//     (the endpoint turns that into 503 + Retry-After)
//   - DrainOnceAsync waits until at least one view is queued, takes up to BatchSize views, and
//     writes them with ONE call to videos.AddViewsAsync (the same video's views summed); it
//     returns how many views it took
//   - Depth is the number of views waiting
//
// System.Threading.Channels has most of this.
//
// Tests: dotnet test --filter-namespace "*Stage4"
public sealed class ViewQueue(IOptions<WritesOptions> options)
{
    private readonly int _capacity = options.Value.QueueCapacity;
    private readonly int _batchSize = options.Value.BatchSize;

    public int Depth => throw new NotImplementedException();

    public bool TryEnqueue(long videoId)
    {
        throw new NotImplementedException();
    }

    public Task<int> DrainOnceAsync(VideoRepository videos, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
