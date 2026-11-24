using Microsoft.Extensions.Options;

namespace ScalingReads;

// Stage 4: an in-process cache in front of Redis, so a hot post costs Redis about one read per
// L1Seconds per app instance, instead of one per request.
//
// The goal, which the tests check:
//   - 500 concurrent requests for one post send at most a handful of Redis commands
//     (concurrent misses for the same post must share one load)
//   - a value is never served more than L1Seconds after it was loaded
//
// Use the injected TimeProvider for expiry, not MemoryCache or DateTime.UtcNow: the tests move
// time forward with a fake one.
//
// Tests: dotnet test --filter Stage4
public sealed class HotKeyPostReader(CachedPostReader redis, IOptions<ReadsOptions> options, TimeProvider time)
    : IPostReader
{
    private readonly TimeSpan _l1 = TimeSpan.FromSeconds(options.Value.L1Seconds);

    public Task<Post?> GetPostAsync(long id, DbTarget target, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
